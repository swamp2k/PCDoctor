using System.Text.Json;

namespace PCDoctor.Core;

/// <summary>
/// One measurement during a performance recording. Percentages are 0-100 for the whole machine.
/// Gpu and Disk are null when Windows does not offer that counter on this PC.
/// </summary>
public sealed record PerfSample(
    double Seconds,
    double Cpu,
    double? Gpu,
    double Ram,
    double RamUsedGb,
    double? Disk,
    string? TopProcess,
    double TopProcessCpu);

/// <summary>Takes one measurement of the whole machine. The Windows version lives in Platform.Windows.</summary>
public interface IPerfSampler
{
    /// <summary>Returns the current values (Seconds is filled in by the recorder).</summary>
    PerfSample Read();
}

/// <summary>A finished recording, saved as JSON in %LOCALAPPDATA%\PCDoctor\recordings.</summary>
public sealed class PerfRecording
{
    public DateTimeOffset Started { get; set; }
    public double IntervalSeconds { get; set; }

    /// <summary>"user" (Stop pressed) or "limit" (reached the 15-minute cap).</summary>
    public string StopReason { get; set; } = "user";

    public List<PerfSample> Samples { get; set; } = [];

    public double DurationSeconds => Samples.Count == 0 ? 0 : Samples[^1].Seconds;

    /// <summary>File name without folder, e.g. "rec-20261013-212000.json". Set when saved or loaded.</summary>
    public string? Id { get; set; }
}

/// <summary>Samples the machine about once a second until stopped or until the 15-minute cap.</summary>
public sealed class PerfRecorder
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(15);

    readonly IPerfSampler _sampler;
    readonly TimeSpan _interval;
    readonly TimeSpan _max;

    public PerfRecorder(IPerfSampler sampler, TimeSpan? interval = null, TimeSpan? maxDuration = null)
    {
        _sampler = sampler;
        _interval = interval ?? DefaultInterval;
        _max = maxDuration ?? MaxDuration;
    }

    /// <summary>
    /// Records until <paramref name="ct"/> is cancelled (Stop) or the cap is reached.
    /// Never throws on Stop: it returns what was recorded so far.
    /// </summary>
    public async Task<PerfRecording> RunAsync(Action<PerfSample> onSample, CancellationToken ct)
    {
        var recording = new PerfRecording { Started = DateTimeOffset.Now, IntervalSeconds = _interval.TotalSeconds };
        int maxSamples = (int)Math.Max(1, Math.Round(_max / _interval));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Rate counters (CPU, disk, GPU) need a first reading to compare against.
        try { _sampler.Read(); } catch (Exception) { /* the first real sample will report the problem */ }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    PerfSample sample = _sampler.Read() with { Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) };
                    recording.Samples.Add(sample);
                    onSample(sample);
                }
                catch (Exception) { /* one failed reading must not end the recording */ }
                if (recording.Samples.Count >= maxSamples || clock.Elapsed >= _max)
                {
                    recording.StopReason = "limit";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            recording.StopReason = "user";
        }
        return recording;
    }
}

/// <summary>Average, peak and time spent near 100 % for one measurement.</summary>
public sealed record MetricSummary(double Average, double Max, double PercentOfTimeAbove90);

public sealed record ProcessShare(string Name, double PercentOfTime, double AverageCpu);

/// <summary>Plain numbers that describe a recording. Used by the review window and by the agent tool.</summary>
public sealed record PerfSummary(
    double DurationSeconds,
    int SampleCount,
    MetricSummary Cpu,
    MetricSummary? Gpu,
    MetricSummary Ram,
    MetricSummary? Disk,
    double MaxRamUsedGb,
    IReadOnlyList<ProcessShare> TopProcesses)
{
    public static PerfSummary From(PerfRecording r)
    {
        var s = r.Samples;
        int n = s.Count;
        return new PerfSummary(
            r.DurationSeconds,
            n,
            Summarise(s.Select(x => (double?)x.Cpu))!,
            Summarise(s.Select(x => x.Gpu)),
            Summarise(s.Select(x => (double?)x.Ram))!,
            Summarise(s.Select(x => x.Disk)),
            n == 0 ? 0 : Math.Round(s.Max(x => x.RamUsedGb), 1),
            s.Where(x => !string.IsNullOrEmpty(x.TopProcess))
             .GroupBy(x => x.TopProcess!, StringComparer.OrdinalIgnoreCase)
             .Select(g => new ProcessShare(g.Key, Math.Round(100.0 * g.Count() / n, 0), Math.Round(g.Average(x => x.TopProcessCpu), 1)))
             .OrderByDescending(p => p.PercentOfTime)
             .Take(5)
             .ToList());
    }

    static MetricSummary? Summarise(IEnumerable<double?> values)
    {
        var v = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (v.Count == 0)
            return null;
        return new MetricSummary(
            Math.Round(v.Average(), 1),
            Math.Round(v.Max(), 1),
            Math.Round(100.0 * v.Count(x => x >= 90) / v.Count, 0));
    }
}

/// <summary>Saves and loads recordings. Keeps the newest 20 so the folder does not grow forever.</summary>
public sealed class PerfRecordingStore
{
    public const int Keep = 20;
    static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    readonly string _dir;

    public PerfRecordingStore(string? dir = null) => _dir = dir ?? AppPaths.SubDir("recordings");

    public string Folder => _dir;

    public string Save(PerfRecording r)
    {
        Directory.CreateDirectory(_dir);
        string id = $"rec-{r.Started.LocalDateTime:yyyyMMdd-HHmmss}.json";
        r.Id = id;
        File.WriteAllText(Path.Combine(_dir, id), JsonSerializer.Serialize(r, Options));
        foreach (string old in ListIds().Skip(Keep))
        {
            try { File.Delete(Path.Combine(_dir, old)); } catch (IOException) { }
        }
        return id;
    }

    /// <summary>Recording ids, newest first.</summary>
    public IReadOnlyList<string> ListIds() =>
        Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "rec-*.json").Select(f => Path.GetFileName(f)).OrderDescending().ToList()
            : [];

    /// <summary>Loads a recording by id, or null if it is missing or unreadable.</summary>
    public PerfRecording? Load(string id)
    {
        // Only plain file names from ListIds; never a path the model made up.
        if (id != Path.GetFileName(id) || !id.StartsWith("rec-") || !id.EndsWith(".json"))
            return null;
        try
        {
            var r = JsonSerializer.Deserialize<PerfRecording>(File.ReadAllText(Path.Combine(_dir, id)), Options);
            if (r is not null) r.Id = id;
            return r;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
