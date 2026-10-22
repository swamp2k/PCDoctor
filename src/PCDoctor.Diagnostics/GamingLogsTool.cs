using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>gaming_logs: finds Minecraft installs and their crash logs, and reads the important parts of one.</summary>
public sealed class GamingLogsTool(MinecraftLocator? locator = null) : ITool
{
    readonly MinecraftLocator _locator = locator ?? new MinecraftLocator();

    public string Name => "gaming_logs";
    public string Description =>
        "Find Minecraft installations (official launcher, CurseForge, Prism, Modrinth, ATLauncher, GDLauncher) and their crash logs: " +
        "Minecraft crash reports, Java crash logs (hs_err_pid) and logs/latest.log. Without 'file' it lists installs, newest activity first, " +
        "and summarises the newest crash. With 'file' (a path from the list) it reads that file's important parts: " +
        "error, stack trace, suspected mods, versions, or for a Java crash the failing module (a graphics driver file points at the driver).";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["file"] = new JsonObject { ["type"] = "string", ["description"] = "Full path of a file from this tool's list. Other paths are refused." },
        },
    };

    public Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var installs = _locator.Find();
        string? file = input.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        if (file is not null)
        {
            // Only files this tool found itself; never a path the model made up.
            var match = installs.SelectMany(i => i.Files)
                .FirstOrDefault(x => string.Equals(Path.GetFullPath(x.Path), LogFiles.SafeFullPath(file), StringComparison.OrdinalIgnoreCase));
            return match is null
                ? ToolResult.Error("That file is not in the list of Minecraft logs. Call gaming_logs without 'file' to see the list.")
                : ToolResult.Json(Read(match));
        }

        if (installs.Count == 0)
            return ToolResult.Json(new
            {
                installs = Array.Empty<object>(),
                note = "No Minecraft installation with logs was found in the usual places for this user. " +
                       "It may be installed for another user, in a custom folder, or never started yet.",
            });

        var newestCrash = installs.SelectMany(i => i.Files)
            .Where(x => x.Kind is "crash_report" or "jvm_crash")
            .MaxBy(x => x.Modified);

        return ToolResult.Json(new
        {
            installs = installs.Select(i => new
            {
                launcher = i.Launcher,
                name = i.Name,
                folder = i.Folder,
                mods = i.ModCount,
                last_activity = Local(i.LastActivity),
                files = i.Files.OrderByDescending(x => x.Modified).Select(x => new { kind = x.Kind, path = x.Path, modified = Local(x.Modified), kb = x.Bytes / 1024 }),
            }),
            newest_crash = newestCrash is null ? null : Read(newestCrash),
            note = "Times are local. crash_report = Minecraft caught the error; jvm_crash = Java itself crashed (often a graphics driver or memory); " +
                   "latest_log = the log of the newest game start. A crash file older than the user's problem is probably not the cause.",
        });
    }, ct);

    static object Read(GameLogFile file)
    {
        string text;
        try
        {
            // A crash report's key facts are at the top; a game log's are at the end.
            text = LogFiles.ReadCapped(file.Path, fromEnd: file.Kind == "latest_log");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new { file = file.Path, error = e.Message };
        }

        object content = file.Kind switch
        {
            "crash_report" => MinecraftCrashReport.Parse(text),
            "jvm_crash" => JvmCrashLog.Parse(text),
            _ => MinecraftLatestLog.Parse(text),
        };
        return new { file = file.Path, kind = file.Kind, modified = Local(file.Modified), content };
    }

    static string Local(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss");
}
