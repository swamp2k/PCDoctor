using System.Text.RegularExpressions;

namespace PCDoctor.Diagnostics;

/// <summary>A game installed through Steam.</summary>
/// <param name="Library">The Steam library folder it is in (the folder holding "steamapps").</param>
public sealed record SteamGame(string AppId, string Name, string Library)
{
    /// <summary>
    /// Proton's pretend Windows drive for this game (…/steamapps/compatdata/&lt;appid&gt;/pfx/drive_c).
    /// Windows games running on Linux keep their logs and saves in there, exactly where they would on Windows.
    /// </summary>
    public string ProtonDriveC => Path.Combine(Library, "steamapps", "compatdata", AppId, "pfx", "drive_c");

    /// <summary>The Windows user folder inside Proton's drive (Proton always calls the user "steamuser").</summary>
    public string ProtonUser => Path.Combine(ProtonDriveC, "users", "steamuser");
}

/// <summary>
/// Finds Steam and the games installed with it on Linux and the Steam Deck: the normal install, the Flatpak one,
/// and every extra library (a second disk or the memory card) listed in libraryfolders.vdf.
/// </summary>
public static partial class SteamLibrary
{
    [GeneratedRegex(@"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PathEntry();

    [GeneratedRegex(@"""(appid|name)""\s+""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestEntry();

    /// <summary>Where Steam itself can be installed, for a given home folder.</summary>
    public static IEnumerable<string> SteamRoots(string home) =>
        new[]
        {
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "debian-installation"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        }.Where(Directory.Exists);

    /// <summary>All library folders (each holds a "steamapps" folder), without repeats.</summary>
    public static List<string> Libraries(string home)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string dir)
        {
            if (!Directory.Exists(Path.Combine(dir, "steamapps"))) return;
            string key;
            try { key = new DirectoryInfo(dir).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(dir); }
            catch (IOException) { key = Path.GetFullPath(dir); }
            if (seen.Add(key.TrimEnd('/'))) found.Add(dir);
        }
        foreach (string root in SteamRoots(home))
        {
            Add(root);
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            foreach (string path in ReadLibraryFolders(vdf))
                Add(path);
        }
        return found;
    }

    /// <summary>The "path" entries of Steam's libraryfolders.vdf.</summary>
    public static IEnumerable<string> ReadLibraryFolders(string vdfFile)
    {
        string text;
        try { text = File.Exists(vdfFile) ? File.ReadAllText(vdfFile) : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (Match m in PathEntry().Matches(text))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    /// <summary>Every game with an appmanifest in every library.</summary>
    public static List<SteamGame> Games(string home)
    {
        var games = new List<SteamGame>();
        foreach (string lib in Libraries(home))
            foreach (string manifest in LogFiles.SafeFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                if (ReadManifest(manifest, lib) is { } g)
                    games.Add(g);
        return games;
    }

    /// <summary>Reads appid and name from an appmanifest_&lt;id&gt;.acf file.</summary>
    public static SteamGame? ReadManifest(string file, string library)
    {
        try
        {
            string? id = null, name = null;
            foreach (Match m in ManifestEntry().Matches(File.ReadAllText(file)))
            {
                if (m.Groups[1].Value.Equals("appid", StringComparison.OrdinalIgnoreCase)) id ??= m.Groups[2].Value;
                else name ??= m.Groups[2].Value;
            }
            return id is null ? null : new SteamGame(id, name ?? id, library);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Tools Steam installs like games (Proton itself, the Linux runtime, redistributables). They are never what a child plays.
    /// </summary>
    public static bool IsTool(SteamGame g) =>
        g.AppId == "228980" // Steamworks Common Redistributables
        || g.Name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
        || g.Name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
        || g.Name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase);
}
