using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>other_game_logs: logs of games besides Minecraft (Fortnite and other Unreal games, Roblox, Unity games).</summary>
public sealed class OtherGameLogsTool(OtherGamesLocator? locator = null) : ITool
{
    readonly OtherGamesLocator _locator = locator ?? new OtherGamesLocator();

    public string Name => "other_game_logs";
    public string Description =>
        "Find the logs of games other than Minecraft: Fortnite, Rocket League, Fall Guys and other Unreal Engine games (game logs and crash logs), " +
        "Roblox, and Unity games (Player.log). Without 'file' it lists the games found, newest activity first, and reads the newest log of the " +
        "most recently played game. With 'file' (a path from the list) it reads that log: error lines, warning signs (graphics driver crash, " +
        "out of memory, anti-cheat, lost connection) and the last lines before the game stopped. For Minecraft use gaming_logs.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["file"] = new JsonObject { ["type"] = "string", ["description"] = "Full path of a log from this tool's list. Other paths are refused." },
        },
    };

    public Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var games = _locator.Find();
        string? file = input.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        if (file is not null)
        {
            // Only files this tool found itself; never a path the model made up.
            string? full = LogFiles.SafeFullPath(file);
            var game = games.FirstOrDefault(g => g.Files.Any(x => string.Equals(Path.GetFullPath(x.Path), full, StringComparison.OrdinalIgnoreCase)));
            if (game is null)
                return ToolResult.Error("That file is not in the list of game logs. Call other_game_logs without 'file' to see the list.");
            return ToolResult.Json(Read(game, game.Files.First(x => string.Equals(Path.GetFullPath(x.Path), full, StringComparison.OrdinalIgnoreCase))));
        }

        if (games.Count == 0)
            return ToolResult.Json(new
            {
                games = Array.Empty<object>(),
                note = "No logs from Fortnite, other Unreal Engine games, Roblox or Unity games were found for this Windows user.",
            });

        var newest = games[0];
        // Prefer a crash log if the newest game has one that is at least as new as its game log.
        var newestFile = newest.Files.OrderByDescending(x => x.Modified).ThenBy(x => x.Kind == "crash_log" ? 0 : 1).First();
        return ToolResult.Json(new
        {
            games = games.Select(g => new
            {
                name = g.Name,
                engine = g.Format,
                folder = g.Folder,
                last_activity = Local(g.LastActivity),
                files = g.Files.OrderByDescending(x => x.Modified).Select(x => new { kind = x.Kind, path = x.Path, modified = Local(x.Modified), kb = x.Bytes / 1024 }),
            }),
            newest_log = Read(newest, newestFile),
            note = "Times are local. game_log = the log of a game start (the newest is the latest start); crash_log = written by the game's crash reporter. " +
                   "A log older than the user's problem is probably not about it. warning_signs are hints from known phrases, not proof.",
        });
    }, ct);

    static object Read(OtherGame game, GameLogFile file)
    {
        try
        {
            string text = LogFiles.ReadCapped(file.Path, fromEnd: true);
            return new { game = game.Name, file = file.Path, kind = file.Kind, modified = Local(file.Modified), content = GameLog.Parse(text, game.Format) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new { game = game.Name, file = file.Path, error = e.Message };
        }
    }

    static string Local(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss");
}
