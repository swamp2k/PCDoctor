using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace PCDoctor.Core;

/// <summary>
/// Writes every sample of a running recording to disk straight away, one line at a time.
/// If the PC freezes, crashes or restarts (or PC Doctor is killed) the recording is not lost:
/// at the next start <see cref="PerfRecordingStore.RecoverInterrupted"/> turns the leftover file into a normal recording.
/// A recording that ends normally deletes its journal after the finished recording has been saved.
/// </summary>
public sealed class PerfJournal : IDisposable
{
    /// <summary>Journals being written by this process right now. They are not "leftovers" and must not be recovered.</summary>
    static readonly ConcurrentDictionary<string, byte> Active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Flush to the physical disk this often (every sample is flushed to the operating system).</summary>
    const int DiskFlushEvery = 5;

    static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    readonly object _lock = new();
    readonly FileStream _file;
    readonly StreamWriter _writer;
    int _sinceDiskFlush;
    bool _broken;

    public string Path { get; }

    PerfJournal(string path, FileStream file, StreamWriter writer)
    {
        Path = path;
        _file = file;
        _writer = writer;
    }

    /// <summary>Starts a journal in <paramref name="dir"/>. The header (start time, game) is the first line.</summary>
    public static PerfJournal Create(string dir, PerfRecording header)
    {
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"rec-{header.Started.LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.partial");
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false)) { AutoFlush = false };
        var journal = new PerfJournal(path, file, writer);
        Active[path] = 0;
        journal.WriteLine(JsonSerializer.Serialize(new PerfRecording
        {
            Started = header.Started,
            IntervalSeconds = header.IntervalSeconds,
            Game = header.Game,
            GameProcess = header.GameProcess,
        }, Options), toDisk: true);
        return journal;
    }

    public static bool IsActive(string path) => Active.ContainsKey(path);

    /// <summary>Adds one sample. Never throws: a journal that cannot be written must not stop the recording.</summary>
    public void Append(PerfSample sample)
    {
        try { WriteLine(JsonSerializer.Serialize(sample, Options), toDisk: ++_sinceDiskFlush >= DiskFlushEvery); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { _broken = true; }
    }

    void WriteLine(string line, bool toDisk)
    {
        lock (_lock)
        {
            if (_broken) return;
            _writer.WriteLine(line);
            _writer.Flush();
            if (toDisk)
            {
                _file.Flush(flushToDisk: true);
                _sinceDiskFlush = 0;
            }
        }
    }

    /// <summary>The recording ended normally and was saved: the journal is no longer needed.</summary>
    public void Complete()
    {
        Dispose();
        try { File.Delete(Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Closes the file but keeps it, so an unsaved recording can still be recovered later.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _broken = true;
            try { _writer.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
        Active.TryRemove(Path, out _);
    }

    /// <summary>
    /// Reads a journal back. A last line cut off by the crash is ignored. Null if the header is missing.
    /// </summary>
    public static PerfRecording? Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        if (reader.ReadLine() is not { } first)
            return null;
        PerfRecording? recording;
        try { recording = JsonSerializer.Deserialize<PerfRecording>(first, Options); }
        catch (JsonException) { return null; }
        if (recording is null)
            return null;
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (JsonSerializer.Deserialize<PerfSample>(line, Options) is { } sample)
                    recording.Samples.Add(sample);
            }
            catch (JsonException) { /* the line the crash cut in half */ }
        }
        return recording;
    }
}
