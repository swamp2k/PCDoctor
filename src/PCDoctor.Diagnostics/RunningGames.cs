using System.Diagnostics;

namespace PCDoctor.Diagnostics;

/// <summary>A program with a window that could be the game to follow.</summary>
public sealed record RunningProgram(string ProcessName, string DisplayName, bool KnownGame)
{
    public override string ToString() => DisplayName;
}

/// <summary>Lists programs with a window, known games first, for the "why does my game stutter" picker.</summary>
public static class RunningGames
{
    /// <summary>Process names of popular games and what to call them.</summary>
    public static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["javaw"] = "Minecraft (Java)",
        ["Minecraft.Windows"] = "Minecraft (Bedrock)",
        ["FortniteClient-Win64-Shipping"] = "Fortnite",
        ["RobloxPlayerBeta"] = "Roblox",
        ["RocketLeague"] = "Rocket League",
        ["FallGuys_client_game"] = "Fall Guys",
        ["VALORANT-Win64-Shipping"] = "Valorant",
        ["League of Legends"] = "League of Legends",
        ["cs2"] = "Counter-Strike 2",
        ["GTA5"] = "GTA V",
        ["GTA5_Enhanced"] = "GTA V",
        ["r5apex"] = "Apex Legends",
        ["Overwatch"] = "Overwatch",
        ["Among Us"] = "Among Us",
        ["Terraria"] = "Terraria",
        ["StardewValley"] = "Stardew Valley",
        ["eurotrucks2"] = "Euro Truck Simulator 2",
        ["FC25"] = "EA Sports FC 25",
        ["FC26"] = "EA Sports FC 26",
    };

    /// <summary>Programs that have a window but are never the game.</summary>
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "PCDoctor", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost",
        "SearchHost", "StartMenuExperienceHost", "Taskmgr", "LockApp", "steamwebhelper", "EpicWebHelper",
    };

    public static List<RunningProgram> List()
    {
        if (OperatingSystem.IsLinux())
            return LinuxGames.List();
        var found = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || NotGames.Contains(p.ProcessName) || found.ContainsKey(p.ProcessName))
                        continue;
                    bool known = Known.TryGetValue(p.ProcessName, out string? name);
                    if (!known && (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)))
                        continue;
                    found[p.ProcessName] = new RunningProgram(p.ProcessName, known ? name! : $"{p.MainWindowTitle} ({p.ProcessName})", known);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return found.Values.OrderByDescending(x => x.KnownGame).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
