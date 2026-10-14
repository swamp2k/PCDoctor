using System.Text.Json;

namespace PCDoctor.Core;

/// <summary>
/// Frames per second measured during a game recording, from the moments the game handed each finished frame to Windows
/// (the same "time between presents" PresentMon and most FPS overlays use).
/// </summary>
/// <param name="Measured">False when FPS could not be measured; Problem says why.</param>
/// <param name="AverageFps">Frames divided by time.</param>
/// <param name="MaxFps">Best single second.</param>
/// <param name="MinFps">Worst single second.</param>
/// <param name="OnePercentLowFps">FPS of the slowest 1 % of frames (from the 99th-percentile frame time). Far below the average = stutter.</param>
/// <param name="PointOnePercentLowFps">The same for the slowest 0.1 % of frames.</param>
/// <param name="Stutters">Frames that took over 2.5 times as long as a normal frame and at least 40 ms: hitches you can see.</param>
/// <param name="PerSecond">FPS for each second, counted from the start of the recording, so it lines up with the CPU/GPU timeline.</param>
/// <param name="Source">"DXGI" (DirectX games) or "DxgKrnl" (others, e.g. OpenGL in Minecraft Java).</param>
public sealed record FpsResult(
    bool Measured,
    string? Problem,
    int Frames = 0,
    double AverageFps = 0,
    double MaxFps = 0,
    double MinFps = 0,
    double OnePercentLowFps = 0,
    double PointOnePercentLowFps = 0,
    double MedianFrameMs = 0,
    double WorstFrameMs = 0,
    int Stutters = 0,
    List<double>? PerSecond = null,
    string? Source = null)
{
    public static FpsResult NotMeasured(string problem) => new(false, problem);

    /// <param name="presentMs">When each frame was handed over, in ms, oldest first.</param>
    /// <param name="offsetMs">How long after the recording started the first time counts from (ms 0 of presentMs).</param>
    public static FpsResult FromPresents(IReadOnlyList<double> presentMs, double offsetMs, string source)
    {
        if (presentMs.Count < 10)
            return NotMeasured(presentMs.Count == 0
                ? "The game showed no frames while measuring. It may have been minimised, or it draws in a way Windows does not report."
                : "Too few frames to measure.");

        var frames = new double[presentMs.Count - 1];
        for (int i = 1; i < presentMs.Count; i++)
            frames[i - 1] = presentMs[i] - presentMs[i - 1];
        // Gaps over 5 seconds are pauses (alt-tab, loading screen with nothing drawn), not frames.
        var real = frames.Where(f => f > 0 && f < 5000).ToArray();
        if (real.Length < 10)
            return NotMeasured("Too few frames to measure.");

        var sorted = real.Order().ToArray();
        double median = Percentile(sorted, 50);
        double total = real.Sum();

        // Count frames per whole second of the recording.
        var perSecond = new List<double>();
        foreach (double t in presentMs)
        {
            int sec = (int)Math.Floor((t + offsetMs) / 1000);
            if (sec < 0) continue;
            while (perSecond.Count <= sec) perSecond.Add(0);
            perSecond[sec]++;
        }
        // The first and last seconds are usually partial; leave them out of max/min when there is enough data.
        var full = perSecond.Count > 4 ? perSecond.Skip(1).SkipLast(1).Where(x => x > 0).ToList() : perSecond.Where(x => x > 0).ToList();

        return new FpsResult(
            true, null,
            real.Length,
            Math.Round(1000 * real.Length / total, 1),
            full.Count == 0 ? 0 : full.Max(),
            full.Count == 0 ? 0 : full.Min(),
            Math.Round(1000 / Percentile(sorted, 99), 1),
            Math.Round(1000 / Percentile(sorted, 99.9), 1),
            Math.Round(median, 2),
            Math.Round(sorted[^1], 1),
            real.Count(f => f > Math.Max(2.5 * median, 40)),
            perSecond,
            source);
    }

    static double Percentile(double[] sorted, double p)
    {
        double rank = p / 100 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}

/// <summary>
/// What the FPS helper (PCDoctor.exe started with administrator rights) writes when it stops:
/// the game's present times in ms after <see cref="StartedUtc"/>, per source.
/// </summary>
public sealed class FpsCaptureFile
{
    public DateTime StartedUtc { get; set; }
    public string? Error { get; set; }
    public List<double> Dxgi { get; set; } = [];
    public List<double> Kernel { get; set; } = [];

    /// <summary>Turns the helper's file into a result lined up with a recording that started at <paramref name="recordingStarted"/>.</summary>
    public FpsResult ToResult(DateTimeOffset recordingStarted)
    {
        if (Error is not null)
            return FpsResult.NotMeasured(Error);
        double offset = (StartedUtc - recordingStarted.UtcDateTime).TotalMilliseconds;
        // DirectX games report through DXGI, which is exact per frame; others only through the graphics kernel.
        return Dxgi.Count >= 10 ? FpsResult.FromPresents(Dxgi, offset, "DXGI") : FpsResult.FromPresents(Kernel, offset, "DxgKrnl");
    }

    public static FpsCaptureFile? Load(string path)
    {
        try { return JsonSerializer.Deserialize<FpsCaptureFile>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
