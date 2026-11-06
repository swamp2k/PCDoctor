namespace PCDoctor.Core;

/// <summary>
/// Why a graphics card is running slower than it could. NVIDIA reports this as a bit mask;
/// these are the names of the bits. "Idle" is left out because it is normal and says nothing.
/// </summary>
public static class GpuThrottle
{
    static readonly (ulong Bit, string Name)[] Bits =
    [
        (0x2, "app_clock_setting"),
        (0x4, "power_cap"),
        (0x8, "hardware_slowdown"),
        (0x10, "sync_boost"),
        (0x20, "software_thermal_slowdown"),
        (0x40, "hardware_thermal_slowdown"),
        (0x80, "power_brake"),
        (0x100, "display_clock_setting"),
    ];

    /// <summary>Comma-separated names of the active limits, or null when none (or only "idle") is active.</summary>
    public static string? Decode(ulong mask)
    {
        var names = Bits.Where(b => (mask & b.Bit) != 0).Select(b => b.Name).ToList();
        return names.Count == 0 ? null : string.Join(",", names);
    }
}

/// <summary>
/// Graphics card health over a recording. A hung or starved card shows up here (clock far below its top speed
/// while busy, a limit being active, the driver taking seconds to answer) even when the load percentage looks normal.
/// </summary>
/// <param name="MinClockMhzWhileBusy">Slowest core clock while the card was over 50 % busy. A sudden drop points at power, heat or a driver problem.</param>
/// <param name="ThrottleReasons">Every limit that was active at some point (see <see cref="GpuThrottle"/>).</param>
/// <param name="SlowestReplyMs">Longest time the driver took to answer a health reading. Seconds here mean the driver itself was struggling.</param>
public sealed record GpuHealthSummary(
    double? MaxTempC,
    double? MaxPowerW,
    double? MinClockMhzWhileBusy,
    double? MaxClockMhz,
    IReadOnlyList<string> ThrottleReasons,
    int? SlowestReplyMs)
{
    /// <summary>Null when the recording has no graphics card health readings (not an NVIDIA card, or an older recording).</summary>
    public static GpuHealthSummary? From(IReadOnlyList<PerfSample> samples)
    {
        if (!samples.Any(s => s.GpuTempC is not null || s.GpuClockMhz is not null || s.GpuPowerW is not null))
            return null;

        static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 1);
        var busy = samples.Where(s => s.Gpu > 50 && s.GpuClockMhz is not null).ToList();
        return new GpuHealthSummary(
            Round(samples.Max(s => s.GpuTempC)),
            Round(samples.Max(s => s.GpuPowerW)),
            busy.Count == 0 ? null : Round(busy.Min(s => s.GpuClockMhz)),
            Round(samples.Max(s => s.GpuClockMhz)),
            samples.Where(s => !string.IsNullOrEmpty(s.GpuThrottle))
                   .SelectMany(s => s.GpuThrottle!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                   .Distinct().ToList(),
            samples.Max(s => s.GpuQueryMs));
    }
}
