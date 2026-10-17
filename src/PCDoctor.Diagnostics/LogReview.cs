using System.Text;
using System.Text.RegularExpressions;

namespace PCDoctor.Diagnostics;

/// <summary>The kind of log, which decides what counts as a problem line.</summary>
/// <param name="Key">"minecraft", "unreal", "unity", "roblox" or "other".</param>
public sealed record LogKind(string Key, string Name);

/// <summary>What would be sent to the AI for one log file.</summary>
/// <param name="Text">The lines that are sent.</param>
/// <param name="ProblemLines">How many problem lines the whole file has (also those left out because they repeat or do not fit).</param>
public sealed record LogExcerpt(long FileBytes, long TotalLines, int ProblemLines, string Text, bool Cut)
{
    /// <summary>A rough token count (about 3.5 characters per token) so the user can see the size.</summary>
    public int ApproxTokens => (int)(Text.Length / 3.5);
}

/// <summary>
/// Log Review: works out which game a log belongs to from its first lines, and cuts a log of any size down to the lines
/// worth sending to the AI (errors, warnings and crash traces with a little context), without reading it all into memory.
/// </summary>
public static partial class LogReview
{
    public const int RecognitionLines = 50;

    /// <summary>The most characters the "important lines" excerpt may hold (about 35,000 tokens).</summary>
    public const int SmartLimit = 120_000;

    /// <summary>The most characters "send everything" may hold: the start and the end of the file (about 170,000 tokens).</summary>
    public const int WholeLimit = 600_000;

    /// <summary>The choices offered when the game is not recognised, and what each one searches for.</summary>
    public static readonly LogKind[] Kinds =
    [
        new("minecraft", "Minecraft"),
        new("unreal", "Fortnite / Unreal Engine"),
        new("unity", "Unity"),
        new("roblox", "Roblox"),
        new("other", "Other / do not know"),
    ];

    // ---- Recognising the game ----

    static readonly (Regex Pattern, string Key, string? Name)[] Signatures =
    [
        (MinecraftSign(), "minecraft", "Minecraft"),
        (RobloxSign(), "roblox", "Roblox"),
        (UnitySign(), "unity", null),
        (UnrealSign(), "unreal", null),
    ];

    [GeneratedRegex(@"\[(main|Render thread|Server thread|Worker-Main-\d+|Netty[^\]]*)/(INFO|WARN|ERROR|DEBUG)\]|Minecraft (Version|Launcher)|net\.minecraft|Loading Minecraft|Fabric Loader|Forge Mod Loader|ModLauncher|Quilt Loader", RegexOptions.IgnoreCase)]
    private static partial Regex MinecraftSign();

    [GeneratedRegex(@"\[FLog::|Roblox(Player|StudioBeta)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex RobloxSign();

    [GeneratedRegex(@"Initialize engine version:|UnityEngine|Unity Subsystems|Mono path\[0\]|Player connection \[", RegexOptions.IgnoreCase)]
    private static partial Regex UnitySign();

    [GeneratedRegex(@"^Log file open|LogInit:|LogWindows:|Unreal Engine|LogD3D|LogPlatformFile|LogConfig:", RegexOptions.IgnoreCase)]
    private static partial Regex UnrealSign();

    [GeneratedRegex(@"(Fortnite|TAGame|Rocket League|FallGuys|Fall Guys|VALORANT|ShooterGame|Epic Games Launcher)", RegexOptions.IgnoreCase)]
    private static partial Regex UnrealGameName();

    /// <summary>
    /// Looks at the first lines (and the file name) and says which game the log is from, or null if it cannot tell.
    /// A Unity log's own name is not in its first lines, so it is called "Unity game".
    /// </summary>
    public static LogKind? Recognise(IEnumerable<string> firstLines, string fileName)
    {
        string head = string.Join('\n', firstLines.Take(RecognitionLines));
        foreach (var (pattern, key, name) in Signatures)
        {
            if (!pattern.IsMatch(head))
                continue;
            if (key == "unreal")
            {
                var game = UnrealGameName().Match(head + "\n" + fileName);
                return new LogKind(key, game.Success ? NiceName(game.Value) : "Unreal Engine game");
            }
            return new LogKind(key, name ?? (key == "unity" ? "Unity game" : key));
        }
        // The file name alone: Player.log is Unity, latest.log / hs_err_pid are Minecraft/Java.
        if (fileName.StartsWith("Player", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return new LogKind("unity", "Unity game");
        if (fileName.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("crash-", StringComparison.OrdinalIgnoreCase))
            return new LogKind("minecraft", "Minecraft");
        return null;
    }

    static string NiceName(string raw) => raw.ToLowerInvariant() switch
    {
        "tagame" or "rocket league" => "Rocket League",
        "fallguys" or "fall guys" => "Fall Guys",
        "valorant" => "Valorant",
        "shootergame" => "Valorant / ARK",
        "epic games launcher" => "Epic Games Launcher",
        _ => "Fortnite",
    };

    /// <summary>Reads the first lines of a file (also while a game still has it open).</summary>
    public static List<string> ReadFirstLines(string path, int count = RecognitionLines)
    {
        var lines = new List<string>();
        using var reader = OpenReader(path);
        while (lines.Count < count && reader.ReadLine() is { } line)
            lines.Add(line.Length > 400 ? line[..400] : line);
        return lines;
    }

    // ---- Cutting the log down ----

    [GeneratedRegex(@"\[(ERROR|WARN|FATAL)\]|/(ERROR|WARN|FATAL)\]|\bException\b|\bCaused by:|\bFailed to\b|\bcrash(ed)?\b|\bStacktrace\b|\bat [\w.$]+\(.*\)|Description: ", RegexOptions.IgnoreCase)]
    private static partial Regex MinecraftProblem();

    [GeneratedRegex(@"(Error:|Fatal error|Critical error|Assertion failed|LogWindows: Error|Unhandled Exception|appError|\bWarning:|LogStreaming: Warning|Ensure condition failed)", RegexOptions.IgnoreCase)]
    private static partial Regex UnrealProblem();

    [GeneratedRegex(@"(Crash!!!|Exception|\bERROR\b|\[Error\]|Failed to|out of memory|\bWARNING\b|Assertion)", RegexOptions.IgnoreCase)]
    private static partial Regex UnityProblem();

    [GeneratedRegex(@"(\[FLog::Error\]|\[FLog::Crash|\bError\b|crash|Disconnect|Lost connection|timed out|Warning)", RegexOptions.IgnoreCase)]
    private static partial Regex RobloxProblem();

    [GeneratedRegex(@"\b(error|fatal|exception|crash(ed)?|fail(ed|ure)?|warn(ing)?|denied|timed? ?out|out of memory|not found|corrupt|unable to|cannot|can't|device (lost|removed)|invalid)\b|0x[0-9a-f]{8}", RegexOptions.IgnoreCase)]
    private static partial Regex GenericProblem();

    static Regex ProblemPattern(string key) => key switch
    {
        "minecraft" => MinecraftProblem(),
        "unreal" => UnrealProblem(),
        "unity" => UnityProblem(),
        "roblox" => RobloxProblem(),
        _ => GenericProblem(),
    };

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>Lines that look like part of a stack trace right after a problem line.</summary>
    static bool IsTraceLine(string line)
    {
        string t = line.TrimStart();
        return line.Length != t.Length || t.StartsWith("at ", StringComparison.Ordinal) || t.StartsWith("Caused by", StringComparison.Ordinal) || t.StartsWith("...", StringComparison.Ordinal);
    }

    const int ContextBefore = 2;
    const int TraceAfter = 8;
    const int SameLineLimit = 3;
    const int LineCut = 400;

    /// <summary>
    /// The lines worth sending: every problem line with two lines before it and its stack trace after it.
    /// A line that repeats (same text apart from numbers) is kept 3 times, then only counted.
    /// If it is still too long, the first part (often the cause) and the last part (what happened last) are kept.
    /// If the file has no problem lines at all, the end of the file is sent instead.
    /// </summary>
    public static LogExcerpt Smart(string path, string kindKey, CancellationToken ct = default)
    {
        var problem = ProblemPattern(kindKey);
        var blocks = new List<string>();
        var before = new Queue<string>();
        var seen = new Dictionary<string, int>();
        var suppressed = new Dictionary<string, int>();
        var current = new StringBuilder();
        int traceLeft = 0, problems = 0;
        long lines = 0;
        var tail = new Queue<string>();

        using var reader = OpenReader(path);
        long bytes = reader.BaseStream.Length;
        while (reader.ReadLine() is { } raw)
        {
            if ((++lines & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();
            if (raw.Trim().Length == 0)
                continue;
            string line = raw.Length > LineCut ? raw[..LineCut] + " […]" : raw;

            tail.Enqueue(line);
            if (tail.Count > 60)
                tail.Dequeue();

            if (traceLeft > 0 && IsTraceLine(line))
            {
                current.AppendLine(line);
                traceLeft--;
                before.Clear();
                continue;
            }
            traceLeft = 0;

            if (problem.IsMatch(line))
            {
                problems++;
                string key = Digits().Replace(line, "#");
                int count = seen.GetValueOrDefault(key) + 1;
                seen[key] = count;
                if (count > SameLineLimit)
                {
                    suppressed[key] = suppressed.GetValueOrDefault(key) + 1;
                    FlushBlock();
                    before.Clear();
                    continue;
                }
                if (current.Length == 0)
                    foreach (string b in before)
                        current.AppendLine(b);
                before.Clear();
                current.AppendLine(line);
                traceLeft = TraceAfter;
            }
            else
            {
                FlushBlock();
                before.Enqueue(line);
                if (before.Count > ContextBefore)
                    before.Dequeue();
            }
        }
        FlushBlock();

        var repeated = suppressed.Count == 0 ? "" :
            "\n(Lines that repeated many times were kept 3 times and then only counted: " +
            string.Join("; ", suppressed.OrderByDescending(s => s.Value).Take(10).Select(s => $"{s.Value} more of \"{Short(s.Key)}\"")) + ")";

        if (blocks.Count == 0)
        {
            string last = "(No error or warning lines were found. This is the end of the log.)\n" + string.Join('\n', tail);
            return new LogExcerpt(bytes, lines, 0, last, true);
        }

        string text = string.Join("\n--\n", blocks);
        bool cut = false;
        if (text.Length > SmartLimit)
        {
            cut = true;
            int head = SmartLimit * 4 / 10, end = SmartLimit - head;
            text = text[..head] + "\n\n[… the middle of the problem lines was left out to keep the size down …]\n\n" + text[^end..];
        }
        return new LogExcerpt(bytes, lines, problems, text + repeated, cut);

        void FlushBlock()
        {
            if (current.Length == 0) return;
            blocks.Add(current.ToString().TrimEnd());
            current.Clear();
        }
    }

    /// <summary>
    /// "Send everything": the whole file if it is small enough, otherwise the first part and the last part
    /// (a log's start shows what was loaded, its end shows what went wrong).
    /// </summary>
    public static LogExcerpt Whole(string path, CancellationToken ct = default)
    {
        using var fs = OpenStream(path);
        long bytes = fs.Length;
        long lines = 0;
        using (var counter = new StreamReader(fs, leaveOpen: true))
            while (counter.ReadLine() is not null)
                if ((++lines & 0xFFFF) == 0)
                    ct.ThrowIfCancellationRequested();

        fs.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        if (bytes <= WholeLimit)
            return new LogExcerpt(bytes, lines, 0, reader.ReadToEnd(), false);

        int head = WholeLimit / 4, end = WholeLimit - head;
        var buffer = new char[head];
        string start = new(buffer, 0, reader.ReadBlock(buffer, 0, head));
        fs.Seek(-end, SeekOrigin.End);
        using var tailReader = new StreamReader(fs);
        tailReader.ReadLine(); // the first line is usually cut in two
        string last = tailReader.ReadToEnd();
        return new LogExcerpt(bytes, lines, 0, start + "\n\n[… the middle of the log was left out to keep the size down …]\n\n" + last, true);
    }

    static string Short(string s) => s.Length > 80 ? s[..80] + "…" : s;

    // A running game holds its log open; allow it to keep writing.
    static FileStream OpenStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);

    static StreamReader OpenReader(string path) => new(OpenStream(path));
}

/// <summary>
/// The log the user picked on the Log Review page. The review_log tool reads it from here, so the AI can only look at
/// the file the user chose and never at a path it made up.
/// </summary>
public sealed class LogReviewSelection
{
    public string? Path { get; set; }
    public string GameKey { get; set; } = "other";
    public string GameName { get; set; } = "";
    public bool Whole { get; set; }
}
