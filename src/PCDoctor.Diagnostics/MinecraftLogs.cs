using System.Text.RegularExpressions;

namespace PCDoctor.Diagnostics;

/// <summary>A log file that matters for a game crash.</summary>
/// <param name="Kind">"crash_report" (Minecraft's own), "jvm_crash" (Java itself died, hs_err_pid) or "latest_log".</param>
public sealed record GameLogFile(string Kind, string Path, DateTime Modified, long Bytes);

/// <summary>One Minecraft game folder: the vanilla .minecraft or one launcher instance.</summary>
public sealed record MinecraftInstall(string Launcher, string Name, string Folder, int ModCount, List<GameLogFile> Files)
{
    public DateTime LastActivity => Files.Count == 0 ? DateTime.MinValue : Files.Max(f => f.Modified);
}

/// <summary>Finds Minecraft game folders from the common launchers and the crash logs inside them.</summary>
public sealed class MinecraftLocator
{
    /// <summary>Newest files of each kind kept per install; old crashes are rarely relevant.</summary>
    const int FilesPerKind = 5;

    readonly string _appData;
    readonly string _home;

    /// <param name="appData">Roaming AppData on Windows. Defaults to the current user's.</param>
    /// <param name="home">The user's profile folder. Defaults to the current user's.</param>
    public MinecraftLocator(string? appData = null, string? home = null)
    {
        _appData = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public List<MinecraftInstall> Find()
    {
        var found = new List<MinecraftInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string launcher, string name, string folder)
        {
            if (!Directory.Exists(folder) || !seen.Add(System.IO.Path.GetFullPath(folder)))
                return;
            var install = Scan(launcher, name, folder);
            if (install.Files.Count > 0 || install.ModCount > 0 || Directory.Exists(System.IO.Path.Combine(folder, "logs")))
                found.Add(install);
        }

        // Vanilla launcher (and TLauncher, which shares the folder). ~/.minecraft is where Linux keeps it.
        Add("Minecraft Launcher", ".minecraft", System.IO.Path.Combine(_appData, ".minecraft"));
        Add("Minecraft Launcher", ".minecraft", System.IO.Path.Combine(_home, ".minecraft"));

        // Launchers that keep one folder per modpack/instance.
        foreach (string dir in SubDirs(System.IO.Path.Combine(_home, "curseforge", "minecraft", "Instances")))
            Add("CurseForge", System.IO.Path.GetFileName(dir), dir);
        foreach (string dir in SubDirs(System.IO.Path.Combine(_appData, "PrismLauncher", "instances")))
        {
            // Prism keeps the game in "minecraft" (newer) or ".minecraft" (older) inside the instance.
            string game = Directory.Exists(System.IO.Path.Combine(dir, "minecraft")) ? System.IO.Path.Combine(dir, "minecraft") : System.IO.Path.Combine(dir, ".minecraft");
            Add("Prism Launcher", System.IO.Path.GetFileName(dir), game);
        }
        foreach (string dir in SubDirs(System.IO.Path.Combine(_appData, "ModrinthApp", "profiles")))
            Add("Modrinth", System.IO.Path.GetFileName(dir), dir);
        foreach (string dir in SubDirs(System.IO.Path.Combine(_appData, "com.modrinth.theseus", "profiles")))
            Add("Modrinth", System.IO.Path.GetFileName(dir), dir);
        foreach (string dir in SubDirs(System.IO.Path.Combine(_appData, "ATLauncher", "instances")))
            Add("ATLauncher", System.IO.Path.GetFileName(dir), dir);
        foreach (string dir in SubDirs(System.IO.Path.Combine(_appData, "gdlauncher_next", "instances")))
            Add("GDLauncher", System.IO.Path.GetFileName(dir), dir);

        return found.OrderByDescending(i => i.LastActivity).ToList();
    }

    static MinecraftInstall Scan(string launcher, string name, string folder)
    {
        var files = new List<GameLogFile>();
        files.AddRange(Newest("crash_report", System.IO.Path.Combine(folder, "crash-reports"), "crash-*.txt"));
        files.AddRange(Newest("jvm_crash", folder, "hs_err_pid*.log"));
        files.AddRange(Newest("latest_log", System.IO.Path.Combine(folder, "logs"), "latest.log"));

        string mods = System.IO.Path.Combine(folder, "mods");
        int modCount = Directory.Exists(mods) ? SafeFiles(mods, "*.jar").Length : 0;
        return new MinecraftInstall(launcher, name, folder, modCount, files);
    }

    static IEnumerable<GameLogFile> Newest(string kind, string dir, string pattern) =>
        Directory.Exists(dir)
            ? SafeFiles(dir, pattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(FilesPerKind)
                .Select(f => new GameLogFile(kind, f.FullName, f.LastWriteTime, f.Length))
            : [];

    static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static string[] SubDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>The useful parts of a Minecraft crash report (crash-reports/crash-*.txt), Forge, NeoForge, Fabric and Quilt alike.</summary>
public sealed record CrashReportInfo(
    string? Time,
    string? Description,
    string? Exception,
    List<string> StackTop,
    List<string> CausedBy,
    List<string> SuspectedMods,
    List<string> ModsInStack,
    Dictionary<string, string> Details);

public static partial class MinecraftCrashReport
{
    /// <summary>System Details lines worth showing; the rest is noise for a diagnosis.</summary>
    static readonly string[] InterestingDetails =
    [
        "Minecraft Version", "Minecraft Version ID", "Operating System", "Java Version", "Memory", "JVM Flags",
        "Is Modded", "Launched Version", "Graphics card #0 name", "Graphics card #0 versionInfo", "Backend API", "Backend library",
        "ModLauncher", "Fabric Mods", "Loaded Shaderpack", "Suspected Mods",
    ];

    [GeneratedRegex(@"from mod ([A-Za-z0-9_\-]+)")]
    private static partial Regex FromMod();

    [GeneratedRegex(@"\[([A-Za-z0-9_\-]+)\.mixins?\.json")]
    private static partial Regex MixinConfig();

    [GeneratedRegex(@"^\s+([^:]{2,60}):\s?(.*)$")]
    private static partial Regex DetailLine();

    public static CrashReportInfo Parse(string text)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        string? time = null, description = null, exception = null;
        var stack = new List<string>();
        var causedBy = new List<string>();
        var suspected = new List<string>();
        var details = new Dictionary<string, string>();
        var modsInStack = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        int i = 0;
        for (; i < lines.Length; i++)
        {
            string line = lines[i];
            if (time is null && line.StartsWith("Time: ")) time = line[6..].Trim();
            else if (description is null && line.StartsWith("Description: "))
            {
                description = line[13..].Trim();
                // The exception follows after a blank line, then its stack trace.
                int j = i + 1;
                while (j < lines.Length && lines[j].Trim().Length == 0) j++;
                if (j < lines.Length) exception = lines[j].Trim();
                for (j++; j < lines.Length && lines[j].Trim().Length > 0; j++)
                {
                    string s = lines[j].Trim();
                    if (s.StartsWith("Caused by:")) causedBy.Add(s);
                    else if (stack.Count < 12 && s.StartsWith("at ")) stack.Add(s);
                }
                i = j;
            }
            else if (line.StartsWith("Suspected Mod"))
            {
                // Forge/NeoForge: "Suspected Mods: NONE" or "Suspected Mod:" followed by indented mod lines.
                string rest = line[(line.IndexOf(':') + 1)..].Trim();
                if (rest.Length > 0) suspected.Add(rest);
                for (int j = i + 1; j < lines.Length && lines[j].StartsWith('\t') && suspected.Count < 10; j++)
                {
                    string s = lines[j].Trim();
                    if (!s.StartsWith("Issue tracker URL") && !s.StartsWith("at ")) suspected.Add(s);
                }
            }

            var d = DetailLine().Match(line);
            if (d.Success)
            {
                string key = d.Groups[1].Value.Trim();
                if (!details.ContainsKey(key) && InterestingDetails.Contains(key))
                {
                    string value = d.Groups[2].Value.Trim();
                    // Multi-line values (mod lists): count the indented lines that follow instead of copying them.
                    if (value.Length == 0)
                    {
                        int n = 0;
                        for (int j = i + 1; j < lines.Length && lines[j].StartsWith("\t\t"); j++) n++;
                        value = n > 0 ? $"{n} lines (list)" : "";
                    }
                    details[key] = value.Length > 300 ? value[..300] + " […]" : value;
                }
            }
        }

        // Mixin errors name the mod that owns the failing code; scan every line, including the exception block skipped above.
        foreach (string line in lines)
        {
            foreach (Match m in FromMod().Matches(line)) modsInStack.Add(m.Groups[1].Value);
            foreach (Match m in MixinConfig().Matches(line)) modsInStack.Add(m.Groups[1].Value);
        }

        // Some loaders also write "Caused by" deeper down; keep the last (root) cause, which is usually the real one.
        return new CrashReportInfo(time, description, exception, stack, causedBy.TakeLast(3).ToList(),
            suspected.Distinct().ToList(), modsInStack.Take(15).ToList(), details);
    }
}

/// <summary>The useful parts of a Java crash log (hs_err_pid*.log), written when Java itself crashes.</summary>
public sealed record JvmCrashInfo(string? Error, string? ProblematicFrame, string? JavaVersion, bool GraphicsDriverSuspected, List<string> Header);

public static class JvmCrashLog
{
    /// <summary>Graphics driver files: a Java crash inside one of these points at the driver, not at Minecraft.</summary>
    static readonly string[] GraphicsDlls =
    [
        "atio6axx", "atioglxx", "atig6pxx", "amdxx", "amdvlk",         // AMD
        "nvoglv64", "nvoglv32", "nvwgf2um", "nvd3dum",                 // NVIDIA
        "ig9icd64", "ig75icd64", "ig7icd64", "ig8icd64", "igxelpicd64", "igvk64", "ig4icd64", "igdumdim", // Intel
        "opengl32", "libgl", "libnvidia", "radeonsi", "iris_dri",      // generic OpenGL / Linux drivers
    ];

    public static JvmCrashInfo Parse(string text)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        var header = lines.TakeWhile(l => l.StartsWith('#')).Take(20).Select(l => l.TrimEnd()).Where(l => l.Trim('#', ' ').Length > 0).ToList();

        string? error = header.Select(l => l.Trim('#', ' ')).FirstOrDefault(l => l.StartsWith("EXCEPTION_") || l.StartsWith("SIG") || l.Contains("Out of Memory") || l.Contains("insufficient memory"));
        string? java = header.Select(l => l.Trim('#', ' ')).FirstOrDefault(l => l.StartsWith("JRE version:"));
        string? frame = null;
        int at = Array.FindIndex(lines, l => l.Contains("Problematic frame:"));
        if (at >= 0 && at + 1 < lines.Length)
            frame = lines[at + 1].Trim('#', ' ');

        bool gpu = frame is not null && GraphicsDlls.Any(d => frame.Contains(d, StringComparison.OrdinalIgnoreCase));
        return new JvmCrashInfo(error, frame, java, gpu, header);
    }
}

/// <summary>The useful parts of logs/latest.log: errors and the last lines before the game stopped.</summary>
public static partial class MinecraftLatestLog
{
    [GeneratedRegex(@"(/ERROR\]|/FATAL\]|\[ERROR\]|\[FATAL\]|Exception|Caused by:|Error:)")]
    private static partial Regex Problem();

    public static object Parse(string text, int maxProblems = 30, int tail = 30)
    {
        string[] lines = text.Replace("\r", "").Split('\n').Where(l => l.Length > 0).ToArray();
        var problems = lines.Where(l => Problem().IsMatch(l)).ToList();
        return new
        {
            line_count = lines.Length,
            problem_line_count = problems.Count,
            // The first problems often show the cause; later ones are frequently consequences.
            first_problems = problems.Take(maxProblems / 2).Select(Cut),
            last_problems = problems.Skip(maxProblems / 2).TakeLast(maxProblems / 2).Select(Cut),
            last_lines = lines.TakeLast(tail).Select(Cut),
        };
    }

    static string Cut(string s) => s.Length > 300 ? s[..300] + " […]" : s;
}
