using PCDoctor.Core;

namespace PCDoctor.Platform.Linux;

/// <summary>
/// Keeps the Anthropic API key on Linux in a file only the current user may read (~/.local/share/PCDoctor/api-key, mode 600),
/// the same way tools like ssh keep their keys. Linux has no one key store that works everywhere, and SteamOS often has none running.
/// </summary>
public sealed class FileSecretStore(string? file = null) : ISecretStore
{
    readonly string _file = file ?? Path.Combine(AppPaths.DataDir, "api-key");

    public string? Read()
    {
        try { return File.Exists(_file) ? File.ReadAllText(_file).Trim() is { Length: > 0 } s ? s : null : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Write(string secret)
    {
        string tmp = _file + ".tmp";
        // Create it private from the start, so the key is never readable by others, not even for a moment.
        using (var fs = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var w = new StreamWriter(fs))
            w.Write(secret);
        File.Move(tmp, _file, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(_file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
