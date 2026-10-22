namespace PCDoctor.Platform.Linux;

/// <summary>Small helpers for reading Linux's /proc and /sys files, which are plain text.</summary>
static class LinuxFiles
{
    /// <summary>The file's text, trimmed; null when it is missing or cannot be read.</summary>
    public static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static long? ReadLong(string path) => long.TryParse(Read(path), out long v) ? v : null;

    /// <summary>"key=value" or "key: value" lines (os-release, meminfo, cpuinfo) as a dictionary. First value wins.</summary>
    public static Dictionary<string, string> KeyValues(string? text, char separator)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text is null) return d;
        foreach (string line in text.Split('\n'))
        {
            int at = line.IndexOf(separator);
            if (at <= 0) continue;
            string key = line[..at].Trim();
            if (!d.ContainsKey(key)) d[key] = line[(at + 1)..].Trim().Trim('"');
        }
        return d;
    }

    /// <summary>Folders under a /sys directory, or none.</summary>
    public static IEnumerable<string> Dirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path).OrderBy(x => x, StringComparer.Ordinal).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
