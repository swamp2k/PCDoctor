using System.Diagnostics;

namespace PCDoctor.Diagnostics;

/// <summary>
/// The "which game to follow" list on Linux and the Steam Deck. Linux programs have no Windows "main window",
/// so instead we look for what Steam started: Steam marks every game's processes with SteamGameId in their environment.
/// Of each game's processes, the one using most memory is the game itself (the rest are Proton's helpers).
/// Minecraft (Java) is recognised from its command line.
/// </summary>
public static class LinuxGames
{
    /// <summary>Steam and Proton's own helper processes, which carry SteamGameId too but are never the game.</summary>
    static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "reaper", "pv-bwrap", "pressure-vessel", "pressure-vessel-wrap", "srt-bwrap", "steam-runtime-launcher-service", "wineserver",
        "wine", "wine64", "wine-preloader", "wine64-preloader", "steam.exe", "explorer.exe", "services.exe", "winedevice.exe", "plugplay.exe",
        "rpcss.exe", "svchost.exe", "conhost.exe", "tabtip.exe", "start.exe", "rundll32.exe", "python3", "python", "sh", "bash", "proton",
        "steam", "steamwebhelper", "gamescope", "gamescope-wl", "mangoapp", "PCDoctor", "xdg-dbus-proxy", "sleep", "timeout",
    };

    public static List<RunningProgram> List() => List(home: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static List<RunningProgram> List(string home)
    {
        var bestPerGame = new Dictionary<string, (string Process, long Ram)>();
        var found = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || Helpers.Contains(p.ProcessName)) continue;
                    string? env = ReadProc(p.Id, "environ");
                    string? appId = env is null ? null : SteamGameId(env);
                    if (appId is not null && appId != "0")
                    {
                        long ram = p.WorkingSet64;
                        if (!bestPerGame.TryGetValue(appId, out var best) || ram > best.Ram)
                            bestPerGame[appId] = (p.ProcessName, ram);
                        continue;
                    }
                    if (p.ProcessName is "java" or "javaw" && (ReadProc(p.Id, "cmdline") ?? "").Contains("minecraft", StringComparison.OrdinalIgnoreCase))
                        found[p.ProcessName] = new RunningProgram(p.ProcessName, "Minecraft (Java)", true);
                    else if (RunningGames.Known.TryGetValue(p.ProcessName, out string? known))
                        found[p.ProcessName] = new RunningProgram(p.ProcessName, known, true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        if (bestPerGame.Count > 0)
        {
            var names = SteamLibrary.Games(home).ToDictionary(g => g.AppId, g => g.Name);
            foreach (var (appId, best) in bestPerGame)
            {
                string name = names.TryGetValue(appId, out string? n) ? n : best.Process;
                found[best.Process] = new RunningProgram(best.Process, $"{name} ({best.Process})", true);
            }
        }
        return found.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The SteamGameId (or SteamAppId) value in a process environment (NUL-separated "KEY=value" entries).</summary>
    public static string? SteamGameId(string environ)
    {
        string? appId = null;
        foreach (string entry in environ.Split('\0'))
        {
            if (entry.StartsWith("SteamGameId=", StringComparison.Ordinal)) return entry[12..];
            if (entry.StartsWith("SteamAppId=", StringComparison.Ordinal)) appId = entry[11..];
        }
        return appId;
    }

    /// <summary>/proc/&lt;pid&gt;/&lt;file&gt;; null when it is another user's process or gone.</summary>
    static string? ReadProc(int pid, string file)
    {
        try { return File.ReadAllText($"/proc/{pid}/{file}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
