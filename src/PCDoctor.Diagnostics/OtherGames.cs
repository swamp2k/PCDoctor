using System.Text.RegularExpressions;

namespace PCDoctor.Diagnostics;

/// <summary>A game other than Minecraft whose logs we found.</summary>
/// <param name="Format">"unreal" (Fortnite and other Unreal Engine games), "unity" or "roblox". Decides how a log is read.</param>
public sealed record OtherGame(string Name, string Format, string Folder, List<GameLogFile> Files)
{
    public DateTime LastActivity => Files.Count == 0 ? DateTime.MinValue : Files.Max(f => f.Modified);
}

/// <summary>
/// Finds logs of popular games besides Minecraft, in the places their engines always use:
/// Unreal Engine games (Fortnite, Rocket League, Fall Guys, ...) in %LOCALAPPDATA%\&lt;Game&gt;\Saved,
/// Unity games in %USERPROFILE%\AppData\LocalLow\&lt;Company&gt;\&lt;Game&gt;\Player.log, and Roblox in %LOCALAPPDATA%\Roblox\logs.
/// </summary>
public sealed class OtherGamesLocator
{
    const int FilesPerKind = 5;

    /// <summary>Folder names that are not the game's own name.</summary>
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FortniteGame"] = "Fortnite",
        ["TAGame"] = "Rocket League",
        ["FallGuys_client"] = "Fall Guys",
        ["EpicGamesLauncher"] = "Epic Games Launcher",
        ["VALORANT"] = "Valorant",
        ["ShooterGame"] = "Valorant / ARK (ShooterGame)",
    };

    readonly string _localAppData;
    readonly string _localLow;
    readonly string _programData;

    /// <param name="localAppData">%LOCALAPPDATA%. Defaults to the current user's.</param>
    /// <param name="localLow">%USERPROFILE%\AppData\LocalLow. Defaults to the current user's.</param>
    public OtherGamesLocator(string? localAppData = null, string? localLow = null, string? programData = null)
    {
        _programData = programData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _localLow = localLow ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
    }

    public List<OtherGame> Find()
    {
        var found = new List<OtherGame>();

        // Unreal Engine: <Game>\Saved\Logs\*.log, and crash folders Saved\Crashes\<crash>\*.log.
        foreach (string dir in LogFiles.SubDirs(_localAppData))
        {
            string saved = Path.Combine(dir, "Saved");
            if (!Directory.Exists(Path.Combine(saved, "Logs")) && !Directory.Exists(Path.Combine(saved, "Crashes")))
                continue;
            var files = new List<GameLogFile>();
            files.AddRange(Newest("game_log", LogFiles.SafeFiles(Path.Combine(saved, "Logs"), "*.log")));
            files.AddRange(Newest("crash_log", LogFiles.SubDirs(Path.Combine(saved, "Crashes")).SelectMany(c => LogFiles.SafeFiles(c, "*.log"))));
            if (files.Count > 0)
                found.Add(new OtherGame(Names.GetValueOrDefault(Path.GetFileName(dir)) ?? Path.GetFileName(dir), "unreal", dir, files));
        }

        // Roblox keeps one log per start; only the newest few matter.
        string roblox = Path.Combine(_localAppData, "Roblox", "logs");
        var rFiles = Newest("game_log", LogFiles.SafeFiles(roblox, "*.log")).ToList();
        if (rFiles.Count > 0)
            found.Add(new OtherGame("Roblox", "roblox", roblox, rFiles));

        // Unity: LocalLow\<Company>\<Game>\Player.log (this start) and Player-prev.log (the start before).
        foreach (string company in LogFiles.SubDirs(_localLow))
        {
            foreach (string game in LogFiles.SubDirs(company))
            {
                var files = Newest("game_log", LogFiles.SafeFiles(game, "Player*.log")).ToList();
                if (files.Count > 0)
                    found.Add(new OtherGame(Path.GetFileName(game), "unity", game, files));
            }
        }

        // Xbox app / Microsoft Store games: logs sit inside the app's package folder, and Windows keeps crash reports (WER).
        foreach (string pkg in LogFiles.SubDirs(Path.Combine(_localAppData, "Packages")))
        {
            string state = Path.Combine(pkg, "LocalState");
            foreach (string dir in new[] { state }.Concat(LogFiles.SubDirs(state)))
            {
                string saved = Path.Combine(dir, "Saved");
                var files = new List<GameLogFile>();
                files.AddRange(Newest("game_log", LogFiles.SafeFiles(Path.Combine(saved, "Logs"), "*.log")));
                files.AddRange(Newest("crash_log", LogFiles.SubDirs(Path.Combine(saved, "Crashes")).SelectMany(c => LogFiles.SafeFiles(c, "*.log"))));
                if (files.Count > 0)
                    found.Add(new OtherGame(StoreName(dir == state ? pkg : dir), "unreal", dir, files));
            }
        }
        FindStoreCrashes(found);

        return found.OrderByDescending(g => g.LastActivity).ToList();
    }

    /// <summary>"Publisher.Game_abc123" or ...\Game -> "Game".</summary>
    static string StoreName(string folder)
    {
        string n = Path.GetFileName(folder);
        n = n.Split('_')[0];
        return n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
    }

    /// <summary>Windows Error Reporting keeps a Report.wer for every crash; the ones from XboxGames / WindowsApps are Xbox app games.</summary>
    void FindStoreCrashes(List<OtherGame> found)
    {
        var byGame = new Dictionary<string, List<GameLogFile>>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Path.Combine(_localAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(_programData, "Microsoft", "Windows", "WER", "ReportArchive"),
        };
        foreach (string root in roots)
            foreach (string report in LogFiles.SubDirs(root).SelectMany(d => LogFiles.SafeFiles(d, "Report.wer")))
            {
                try
                {
                    string text = LogFiles.ReadCapped(report, fromEnd: false);
                    if (!Regex.IsMatch(text, @"\\(XboxGames|WindowsApps)\\", RegexOptions.IgnoreCase)) continue;
                    var m = Regex.Match(text, @"^AppName=(.+?)\r?$", RegexOptions.Multiline);
                    string name = m.Success ? m.Groups[1].Value.Trim() : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    if (name.Length == 0) continue;
                    var fi = new FileInfo(report);
                    if (!byGame.TryGetValue(name, out var list)) byGame[name] = list = [];
                    list.Add(new GameLogFile("crash_log", fi.FullName, fi.LastWriteTime, fi.Length));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        foreach (var (name, list) in byGame)
        {
            var files = list.OrderByDescending(f => f.Modified).Take(FilesPerKind).ToList();
            found.Add(new OtherGame(name, "wer", Path.GetDirectoryName(files[0].Path) ?? "", files));
        }
    }

    static IEnumerable<GameLogFile> Newest(string kind, IEnumerable<string> paths) =>
        paths.Select(f => new FileInfo(f))
             .OrderByDescending(f => f.LastWriteTime)
             .Take(FilesPerKind)
             .Select(f => new GameLogFile(kind, f.FullName, f.LastWriteTime, f.Length));
}

/// <summary>Reads the important lines of a game log: error lines, known warning signs, and how it ended.</summary>
public static partial class GameLog
{
    [GeneratedRegex(@"(Error:|Fatal error|Critical error|Assertion failed|LogWindows: Error|Unhandled Exception|appError)", RegexOptions.IgnoreCase)]
    private static partial Regex UnrealProblem();

    [GeneratedRegex(@"(Crash!!!|Exception|\bERROR\b|\[Error\]|Failed to|out of memory)", RegexOptions.IgnoreCase)]
    private static partial Regex UnityProblem();

    [GeneratedRegex(@"(\[FLog::Error\]|\[FLog::Crash|\bError\b|crash|Disconnect|Lost connection|timed out)", RegexOptions.IgnoreCase)]
    private static partial Regex RobloxProblem();

    [GeneratedRegex(@"^(Sig\[\d+\]\.Value|EventType|AppPath|FaultModule|ExceptionCode|Fault)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WerProblem();

    /// <summary>Phrases that, when present, point at a likely cause. The model gets the hint and decides.</summary>
    static readonly (Regex Pattern, string Hint)[] Signs =
    [
        (new(@"GPU crashed|DXGI_ERROR_DEVICE_(REMOVED|HUNG|RESET)|D3D Device (Removed|Lost)|VK_ERROR_DEVICE_LOST", RegexOptions.IgnoreCase),
            "The graphics card or its driver stopped responding (often driver, overheating or overclock)."),
        (new(@"out of video memory|OUT_OF_VIDEO_MEMORY|D3DERR_OUTOFVIDEOMEMORY|Failed to allocate.*VRAM", RegexOptions.IgnoreCase),
            "The graphics card ran out of its own memory (VRAM). Lower graphics settings or resolution."),
        (new(@"out of memory|OutOfMemory|Ran out of memory|Failed to allocate", RegexOptions.IgnoreCase),
            "The game ran out of memory (RAM)."),
        (new(@"EasyAntiCheat|BattlEye|anti-?cheat|Vanguard", RegexOptions.IgnoreCase),
            "Anti-cheat software is mentioned; anti-cheat errors stop games from starting or kick players."),
        (new(@"Disconnect|Lost connection|timed out|Connection (closed|lost|failed)|NetworkFailure|ConnectionLost", RegexOptions.IgnoreCase),
            "Connection to the game's servers was lost or timed out."),
        (new(@"Access is denied|access violation|EXCEPTION_ACCESS_VIOLATION", RegexOptions.IgnoreCase),
            "The game crashed with an access violation (a program bug, damaged game files, or a conflicting overlay/driver)."),
    ];

    /// <param name="format">"unreal", "unity" or "roblox".</param>
    public static object Parse(string text, string format, int maxProblems = 30, int tail = 25)
    {
        string[] lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        Regex problem = format switch
        {
            "unity" => UnityProblem(),
            "roblox" => RobloxProblem(),
            "wer" => WerProblem(),
            _ => UnrealProblem(),
        };
        var problems = lines.Where(l => problem.IsMatch(l)).ToList();
        var signs = Signs
            .Select(s => (s.Hint, Line: lines.FirstOrDefault(l => s.Pattern.IsMatch(l))))
            .Where(s => s.Line is not null)
            .Select(s => new { hint = s.Hint, example = Cut(s.Line!) })
            .ToList();
        return new
        {
            line_count = lines.Length,
            problem_line_count = problems.Count,
            warning_signs = signs,
            // The first problems often show the cause; later ones are frequently consequences.
            first_problems = problems.Take(maxProblems / 2).Select(Cut),
            last_problems = problems.Skip(maxProblems / 2).TakeLast(maxProblems / 2).Select(Cut),
            last_lines = lines.TakeLast(tail).Select(Cut),
        };
    }

    static string Cut(string s) => s.Length > 300 ? s[..300] + " […]" : s;
}
