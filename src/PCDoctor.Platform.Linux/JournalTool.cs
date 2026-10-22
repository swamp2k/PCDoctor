using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.Platform.Linux;

/// <summary>
/// system_events on Linux: errors (and optionally warnings) from the system log (the systemd journal), grouped and explained.
/// The answer has the same shape as the Windows tool, so the Diagnostics page and the model read both the same way.
/// </summary>
public sealed partial class JournalTool : ITool
{
    const int MaxRecords = 4000;

    [GeneratedRegex(@"^[A-Za-z0-9 ._@\-]{1,100}$")]
    private static partial Regex ProviderRegex();

    public string Name => "system_events";
    public string Description =>
        "Read errors and critical messages from the Linux system log (the systemd journal, including kernel messages), grouped so repeats are counted once, " +
        "with a plain explanation for well-known ones (graphics driver hangs, out of memory, disk and memory card errors, program crashes, overheating, USB problems). " +
        "Default: errors from the last 48 hours. Use 'around' with a time (for example a crash time from a game log) to see what happened just before and after.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["hours"] = new JsonObject { ["type"] = "integer", ["description"] = "How far back to look, 1-720. Default 48. Ignored when 'around' is given." },
            ["around"] = new JsonObject { ["type"] = "string", ["description"] = "A local date and time, e.g. 2026-10-13T21:04:00. Shows messages from 'minutes' before to 'minutes' after." },
            ["minutes"] = new JsonObject { ["type"] = "integer", ["description"] = "Window size for 'around', 1-240. Default 30." },
            ["include_warnings"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also include warnings. Default false (errors and critical only)." },
            ["provider"] = new JsonObject { ["type"] = "string", ["description"] = "Only messages from this program, e.g. 'kernel' or 'steam'." },
            ["max_groups"] = new JsonObject { ["type"] = "integer", ["description"] = "Max rows returned, 5-60. Default 25." },
        },
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        bool warnings = input.TryGetProperty("include_warnings", out var w) && w.ValueKind == JsonValueKind.True;
        int maxGroups = input.TryGetProperty("max_groups", out var mg) && mg.TryGetInt32(out int m) ? Math.Clamp(m, 5, 60) : 25;
        string? provider = input.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (provider is not null && !ProviderRegex().IsMatch(provider))
            return ToolResult.Error("Provider may only contain letters, digits, spaces, '.', '_', '@' and '-'.");

        DateTimeOffset from, to;
        if (input.TryGetProperty("around", out var a) && a.ValueKind == JsonValueKind.String)
        {
            if (!DateTimeOffset.TryParse(a.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                return ToolResult.Error("'around' must be a date and time like 2026-10-13T21:04:00.");
            int minutes = input.TryGetProperty("minutes", out var mi) && mi.TryGetInt32(out int mm) ? Math.Clamp(mm, 1, 240) : 30;
            (from, to) = (at.AddMinutes(-minutes), at.AddMinutes(minutes));
        }
        else
        {
            int hours = input.TryGetProperty("hours", out var h) && h.TryGetInt32(out int hh) ? Math.Clamp(hh, 1, 720) : 48;
            (from, to) = (DateTimeOffset.Now.AddHours(-hours), DateTimeOffset.Now);
        }

        var args = new List<string>
        {
            "--no-pager", "--output=json", "--reverse", $"--lines={MaxRecords}",
            $"--priority={(warnings ? 4 : 3)}",
            $"--since=@{from.ToUnixTimeSeconds()}", $"--until=@{to.ToUnixTimeSeconds()}",
        };
        if (provider is not null)
            args.Add(provider == "kernel" ? "--dmesg" : $"--identifier={provider}");

        ProcessOutcome outcome;
        try
        {
            outcome = await ProcessRunner.RunAsync("journalctl", args, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                60, 50_000_000, ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ToolResult.Json(new { groups = Array.Empty<object>(), problems = new[] { new { log = "journal", error = "journalctl is not available on this system: " + e.Message } } });
        }

        var events = Parse(outcome.StdOut);
        var problems = new List<object>();
        if (events.Count == 0 && outcome.ExitCode is not 0 && outcome.StdErr.Trim().Length > 0)
            problems.Add(new { log = "journal", error = outcome.StdErr.Trim() });
        else if (outcome.StdErr.Contains("not seeing messages from other users", StringComparison.OrdinalIgnoreCase))
            problems.Add(new { log = "journal", error = "Only this user's own messages could be read; system messages need membership of the 'systemd-journal' or 'wheel' group." });

        var allGroups = EventSummary.Group(events);
        var groups = allGroups.Take(maxGroups).ToList();
        return ToolResult.Json(new
        {
            from = from.ToString("yyyy-MM-ddTHH:mm:ss"),
            to = to.ToString("yyyy-MM-ddTHH:mm:ss"),
            levels = warnings ? "critical, error, warning" : "critical, error",
            total_events = events.Count,
            total_groups = allGroups.Count,
            groups_left_out = allGroups.Count - groups.Count,
            groups = groups.Select(g => new
            {
                log = g.Log,
                level = g.Level,
                source = g.Provider,
                event_id = 0,
                count = g.Count,
                first = g.First.ToString("yyyy-MM-ddTHH:mm:ss"),
                last = g.Last.ToString("yyyy-MM-ddTHH:mm:ss"),
                meaning = LinuxEvents.Explain(g.Message),
                message = g.Message,
            }),
            stopped_early = events.Count >= MaxRecords ? true : (bool?)null,
            problems = problems.Count > 0 ? problems : null,
            note = "Times are local. Rows are grouped by program and message, newest first. log 'kernel' = the Linux kernel and its drivers. " +
                   "Many errors on a healthy PC are harmless noise; weigh them by meaning, count and timing. " +
                   "If groups_left_out is above 0, older kinds were left out: ask again with a shorter period, a provider, or a higher max_groups.",
        });
    }

    /// <summary>Reads journalctl's one-JSON-object-per-line output. Lines that are not valid are skipped.</summary>
    public static List<LogEvent> Parse(string jsonLines)
    {
        var list = new List<LogEvent>();
        foreach (string line in jsonLines.Split('\n'))
        {
            if (line.Length < 2 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                string? message = Text(r, "MESSAGE");
                if (message is null) continue;
                DateTimeOffset time = long.TryParse(Text(r, "__REALTIME_TIMESTAMP"), out long us)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000).ToLocalTime() : DateTimeOffset.MinValue;
                int priority = int.TryParse(Text(r, "PRIORITY"), out int pr) ? pr : 3;
                bool kernel = Text(r, "_TRANSPORT") == "kernel";
                string source = kernel ? "kernel" : Text(r, "SYSLOG_IDENTIFIER") ?? Text(r, "_COMM") ?? "?";
                string level = priority <= 2 ? "critical" : priority == 3 ? "error" : priority == 4 ? "warning" : "information";
                list.Add(new LogEvent(kernel ? "kernel" : "system", time, level, source, 0, message));
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>A field as text. The journal stores odd messages as a list of bytes; those are decoded.</summary>
    static string? Text(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Array => System.Text.Encoding.UTF8.GetString(v.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Number).Select(b => (byte)b.GetInt32()).ToArray()),
            _ => null,
        };
    }
}
