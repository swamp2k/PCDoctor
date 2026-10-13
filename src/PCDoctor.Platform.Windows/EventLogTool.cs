using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.Platform.Windows;

/// <summary>system_events: errors (and optionally warnings) from the Windows Event Log, grouped and explained.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class EventLogTool : ITool
{
    /// <summary>Never read more raw entries than this per log, however long the period.</summary>
    const int MaxRecordsPerLog = 2000;

    static readonly string[] AllowedLogs = ["System", "Application"];

    [GeneratedRegex(@"^[A-Za-z0-9 ._\-]{1,100}$")]
    private static partial Regex ProviderRegex();

    public string Name => "system_events";
    public string Description =>
        "Read errors and critical events from the Windows Event Log (System and Application), grouped so repeats are counted once, " +
        "with a plain explanation for well-known ones (unexpected restarts, blue screens, program crashes, graphics driver resets, disk errors). " +
        "Default: errors from the last 48 hours. Use 'around' with a time (for example a crash time from gaming_logs) to see what happened just before and after.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["hours"] = new JsonObject { ["type"] = "integer", ["description"] = "How far back to look, 1-720. Default 48. Ignored when 'around' is given." },
            ["around"] = new JsonObject { ["type"] = "string", ["description"] = "A local date and time, e.g. 2026-10-13T21:04:00. Shows events from 'minutes' before to 'minutes' after." },
            ["minutes"] = new JsonObject { ["type"] = "integer", ["description"] = "Window size for 'around', 1-240. Default 30." },
            ["include_warnings"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also include warnings. Default false (errors and critical only)." },
            ["logs"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("System", "Application") },
                ["description"] = "Which logs. Default both.",
            },
            ["provider"] = new JsonObject { ["type"] = "string", ["description"] = "Only events from this source, e.g. 'Application Error' or 'Microsoft-Windows-Kernel-Power'." },
            ["max_groups"] = new JsonObject { ["type"] = "integer", ["description"] = "Max rows returned, 5-60. Default 25." },
        },
    };

    public Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        bool warnings = input.TryGetProperty("include_warnings", out var w) && w.ValueKind == JsonValueKind.True;
        int maxGroups = input.TryGetProperty("max_groups", out var mg) && mg.TryGetInt32(out int m) ? Math.Clamp(m, 5, 60) : 25;

        string? provider = input.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (provider is not null && !ProviderRegex().IsMatch(provider))
            return ToolResult.Error("Provider may only contain letters, digits, spaces, '.', '_' and '-'.");

        var logs = new List<string>();
        if (input.TryGetProperty("logs", out var l) && l.ValueKind == JsonValueKind.Array)
            logs.AddRange(l.EnumerateArray().Select(x => x.GetString()).OfType<string>()
                .Select(x => AllowedLogs.FirstOrDefault(a => a.Equals(x, StringComparison.OrdinalIgnoreCase)))
                .OfType<string>().Distinct());
        if (logs.Count == 0)
            logs.AddRange(AllowedLogs);

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

        var events = new List<LogEvent>();
        var problems = new List<object>();
        var truncatedLogs = new List<string>();
        foreach (string log in logs)
        {
            try
            {
                if (Read(log, BuildQuery(from, to, warnings, provider), events, ct))
                    truncatedLogs.Add(log);
            }
            catch (EventLogException ex)
            {
                problems.Add(new { log, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                problems.Add(new { log, error = ex.Message });
            }
        }

        var groups = EventSummary.Group(events, maxGroups);
        return ToolResult.Json(new
        {
            from = from.ToString("yyyy-MM-ddTHH:mm:ss"),
            to = to.ToString("yyyy-MM-ddTHH:mm:ss"),
            levels = warnings ? "critical, error, warning" : "critical, error",
            total_events = events.Count,
            groups = groups.Select(g => new
            {
                log = g.Log,
                level = g.Level,
                source = g.Provider,
                event_id = g.Id,
                count = g.Count,
                first = g.First.ToString("yyyy-MM-ddTHH:mm:ss"),
                last = g.Last.ToString("yyyy-MM-ddTHH:mm:ss"),
                meaning = g.Meaning,
                message = g.Message,
            }),
            stopped_early_in = truncatedLogs.Count > 0 ? truncatedLogs : null,
            problems = problems.Count > 0 ? problems : null,
            note = "Times are local. Rows are grouped by source, event id and message, newest first. " +
                   "Many errors on a healthy PC are harmless noise; weigh them by meaning, count and timing.",
        });
    }, ct);

    /// <summary>Reads newest first. Returns true if it stopped at the per-log cap.</summary>
    static bool Read(string log, string query, List<LogEvent> into, CancellationToken ct)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true });
        int n = 0;
        for (EventRecord? r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
        {
            using (r)
            {
                ct.ThrowIfCancellationRequested();
                into.Add(new LogEvent(
                    log,
                    r.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.MinValue,
                    LevelName(r.Level),
                    r.ProviderName ?? "?",
                    r.Id,
                    Message(r)));
            }
            if (++n >= MaxRecordsPerLog)
                return true;
        }
        return false;
    }

    static string? Message(EventRecord r)
    {
        try
        {
            string? text = r.FormatDescription();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        catch (EventLogException) { /* the source's message file is missing */ }
        // No message template: fall back to the raw values, which still name the program or driver.
        return r.Properties.Count == 0 ? null : string.Join(" | ", r.Properties.Select(p => p.Value?.ToString()).Take(12));
    }

    static string LevelName(byte? level) => level switch
    {
        1 => "critical",
        2 => "error",
        3 => "warning",
        4 => "information",
        _ => "other",
    };

    /// <summary>XPath for the Event Log. Only validated values go in; the provider passed the strict regex above.</summary>
    public static string BuildQuery(DateTimeOffset from, DateTimeOffset to, bool includeWarnings, string? provider)
    {
        string levels = includeWarnings ? "Level=1 or Level=2 or Level=3" : "Level=1 or Level=2";
        string time = $"TimeCreated[@SystemTime>='{Utc(from)}' and @SystemTime<='{Utc(to)}']";
        string prov = provider is null ? "" : $"Provider[@Name='{provider}'] and ";
        return $"*[System[{prov}({levels}) and {time}]]";
    }

    static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
