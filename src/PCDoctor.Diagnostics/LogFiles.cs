namespace PCDoctor.Diagnostics;

/// <summary>Reads (part of) a log file, also while the game still has it open.</summary>
static class LogFiles
{
    /// <summary>At most this much of a file is read: the end of a log, the start of a crash file.</summary>
    public const int MaxReadBytes = 2_000_000;

    public static string ReadCapped(string path, bool fromEnd)
    {
        // A running game holds its log open; allow it to keep writing.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > MaxReadBytes && fromEnd)
            fs.Seek(-MaxReadBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs);
        var buffer = new char[MaxReadBytes];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    public static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public static string[] SubDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
