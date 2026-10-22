using System.Reflection;
using System.Text.Json;

namespace PCDoctor.Core;

/// <summary>A newer version found on the project's GitHub Releases page.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl);

/// <summary>
/// Looks for a newer PC Doctor on the project's own GitHub Releases page, downloads it, and swaps the exe.
/// It only ever talks to github.com/swamp2k/PCDoctor.
/// </summary>
public sealed class Updater
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/swamp2k/PCDoctor/releases/latest";
    public const string DownloadPrefix = "https://github.com/swamp2k/PCDoctor/releases/download/";
    const string ExeName = "PCDoctor.exe";

    readonly HttpClient _http;

    public Updater(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PCDoctor-updater");
    }

    /// <summary>The version of the running app ("1.0.23"). A build without a version number reads as 1.0.0.</summary>
    public static Version CurrentVersion
    {
        get
        {
            string? text = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return TryParseVersion(text, out var v) ? v : new Version(1, 0, 0);
        }
    }

    /// <summary>Reads "v1.0.23", "1.0.23" or "1.0.23+abc123" as a version.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) text = text[..cut];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = parsed;
        return true;
    }

    /// <summary>
    /// Asks GitHub for the latest release. Returns null when we are up to date, or when anything goes wrong
    /// (no internet, GitHub busy, unexpected answer) - the app simply carries on.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestReleaseUrl, ct);
            if (!response.IsSuccessStatusCode) return null;
            return ParseRelease(await response.Content.ReadAsStringAsync(ct), current);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Picks the exe out of a GitHub release answer. Null if it is not newer, or the link is not ours.</summary>
    public static UpdateInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tag) || !TryParseVersion(tag.GetString(), out var latest)) return null;
        if (latest <= current) return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != ExeName) continue;
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (url is not null && url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                return new UpdateInfo(latest, url);
        }
        return null;
    }

    /// <summary>Downloads the new exe next to the running one (PCDoctor.exe.new). Returns that path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, string exePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!update.DownloadUrl.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Not an official PC Doctor download link.");
        string target = exePath + ".new";
        try
        {
            using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var file = File.Create(target))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total.Value);
                }
            }
            // An exe is always far bigger than this; a tiny file means an error page or a cut-off download.
            if (new FileInfo(target).Length < 1_000_000) throw new IOException("The download is incomplete.");
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Swaps the files: the running exe is renamed to PCDoctor.exe.old (Windows allows that while it runs)
    /// and the new one takes its place. The caller then starts the new exe and closes the old one.
    /// </summary>
    public static void Install(string exePath, string downloadedPath)
    {
        string old = exePath + ".old";
        TryDelete(old);
        File.Move(exePath, old);
        try { File.Move(downloadedPath, exePath); }
        catch { File.Move(old, exePath); throw; } // could not put the new one in place: go back to the old
    }

    /// <summary>
    /// True when the app runs as its own exe and can replace itself. The file may have any name
    /// ("PCDoctor (1).exe" after a second download); only a developer run through dotnet is excluded.
    /// </summary>
    public static bool CanSelfUpdate(string? exePath) =>
        exePath is not null
        && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !exePath.Replace('\\', '/').EndsWith("/dotnet.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Called at startup: removes the leftovers of an earlier update.</summary>
    public static void CleanUp(string exePath)
    {
        TryDelete(exePath + ".old");
        TryDelete(exePath + ".new");
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
