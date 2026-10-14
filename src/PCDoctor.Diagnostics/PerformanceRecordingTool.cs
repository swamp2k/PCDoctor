using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>performance_recording: lets the agent read a performance recording the user made (e.g. while gaming).</summary>
public sealed class PerformanceRecordingTool(PerfRecordingStore store) : ITool
{
    /// <summary>The timeline sent to the model is cut into at most this many slices.</summary>
    const int MaxSlices = 60;

    public string Name => "performance_recording";
    public string Description =>
        "Read a performance recording the user made with PC Doctor's Record button (for example while playing a game). " +
        "It sampled CPU, GPU, RAM and disk use about once a second, for up to 15 minutes, plus which program used most CPU and most GPU, " +
        "processor speed (to spot overheating or power saving), graphics memory (VRAM) in use, and a ping to the internet (to spot lag). " +
        "Returns a summary, a timeline (average and peak per time slice) and the list of other recordings. " +
        "Without an id it returns the newest recording.";
    public ToolKind Kind => ToolKind.ReadOnly;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Recording id from the 'recordings' list, e.g. rec-20261013-212000.json. Omit for the newest." },
        },
    };

    public Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        var ids = store.ListIds();
        if (ids.Count == 0)
            return Task.FromResult(ToolResult.Error("No performance recordings yet. Ask the user to press Record (Optag ydelse), use the PC as usual (e.g. play the game), then press Stop."));

        string id = input.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : ids[0];
        PerfRecording? r = store.Load(id);
        if (r is null)
            return Task.FromResult(ToolResult.Error($"Recording '{id}' not found. Available: {string.Join(", ", ids)}"));

        return Task.FromResult(ToolResult.Json(new
        {
            id = r.Id,
            started = r.Started,
            stopped_by = r.StopReason == "limit" ? "15-minute limit" : "user",
            game = r.Game,
            game_process = r.GameProcess,
            fps = r.Fps is null ? null : r.Fps with { PerSecond = null },
            summary = PerfSummary.From(r),
            timeline = Timeline(r),
            recordings = ids,
            note = "Percentages are 0-100 for the whole machine. gpu/disk are null if Windows had no counter for them. " +
                   "PercentOfTimeAbove90 is how much of the recording that part was at 90 % or more, which suggests a bottleneck. " +
                   "TopProcesses lists which program used the most CPU, and in how much of the recording; TopGpuProcesses the same for the graphics card. " +
                   "CpuSpeed is % of the processor's normal speed: over 100 is boosting; SlowedPercentOfBusyTime high means the processor was slowed " +
                   "while busy (overheating, laptop on battery, or power-saving plan). MaxGpuMemoryGb near the card's VRAM size means it ran out of " +
                   "graphics memory (compare with system_hardware). Ping: Spikes are pings over 150 ms (felt as lag); if LostPercent is 100 the network " +
                   "may just block ping. Fields are null in recordings made before these measurements existed. " +
                   "Game recordings (\"Hvorfor hakker mit spil?\") also have game: GameCpu/GameGpu/MaxGameRamGb are the game's own use " +
                   "(GameCpu is % of the whole machine, so one busy core on an 8-core CPU is about 12 %); fps has average, best and worst second, " +
                   "1 % and 0.1 % lows (far below the average = stutter) and Stutters (visible hitches); the timeline has fps per slice, " +
                   "so you can see what CPU, GPU, disk, RAM or ping did when the FPS dropped. fps.Measured=false gives the reason.",
        }));
    }

    /// <summary>Downsamples to at most <see cref="MaxSlices"/> slices so long recordings stay small for the model.</summary>
    static object Timeline(PerfRecording r)
    {
        var s = r.Samples;
        if (s.Count == 0)
            return Array.Empty<object>();
        int per = (int)Math.Ceiling(s.Count / (double)MaxSlices);
        return s.Chunk(per).Select(c => new
        {
            from_s = c[0].Seconds,
            to_s = c[^1].Seconds,
            cpu_avg = Avg(c.Select(x => (double?)x.Cpu)),
            cpu_max = Max(c.Select(x => (double?)x.Cpu)),
            gpu_avg = Avg(c.Select(x => x.Gpu)),
            gpu_max = Max(c.Select(x => x.Gpu)),
            ram_avg = Avg(c.Select(x => (double?)x.Ram)),
            disk_max = Max(c.Select(x => x.Disk)),
            cpu_speed_min = Min(c.Select(x => x.CpuSpeed)),
            gpu_mem_gb_max = Max(c.Select(x => x.GpuMemoryGb)),
            ping_max_ms = Max(c.Select(x => x.PingMs)),
            pings_lost = c.Any(x => x.PingLost) ? c.Count(x => x.PingLost) : (int?)null,
            game_cpu_avg = Avg(c.Select(x => x.GameCpu)),
            game_gpu_avg = Avg(c.Select(x => x.GameGpu)),
            fps_avg = Fps(r, c[0].Seconds, c[^1].Seconds, min: false),
            fps_min = Fps(r, c[0].Seconds, c[^1].Seconds, min: true),
            top_process = c.Where(x => x.TopProcess is not null).GroupBy(x => x.TopProcess).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key,
        }).ToList();
    }

    static double? Avg(IEnumerable<double?> v)
    {
        var l = v.Where(x => x.HasValue).ToList();
        return l.Count == 0 ? null : Math.Round(l.Average(x => x!.Value), 1);
    }

    /// <summary>FPS in the whole seconds this slice covers, from the per-second counts.</summary>
    static double? Fps(PerfRecording r, double from, double to, bool min)
    {
        if (r.Fps?.PerSecond is not { Count: > 0 } ps)
            return null;
        var secs = Enumerable.Range((int)Math.Floor(from), Math.Max(1, (int)Math.Ceiling(to) - (int)Math.Floor(from)))
            .Where(i => i >= 0 && i < ps.Count).Select(i => ps[i]).ToList();
        return secs.Count == 0 ? null : Math.Round(min ? secs.Min() : secs.Average(), 0);
    }

    static double? Min(IEnumerable<double?> v)
    {
        var l = v.Where(x => x.HasValue).ToList();
        return l.Count == 0 ? null : Math.Round(l.Min(x => x!.Value), 1);
    }

    static double? Max(IEnumerable<double?> v)
    {
        var l = v.Where(x => x.HasValue).ToList();
        return l.Count == 0 ? null : Math.Round(l.Max(x => x!.Value), 1);
    }
}
