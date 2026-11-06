using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.Platform.Windows;

/// <summary>
/// Reads the Windows Event Log entries that show a graphics card or driver hang, at any level:
/// graphics driver and hardware (WHEA) events, Windows Error Reporting kernel reports and the live dump requests.
/// All three logs can be read without administrator rights.
/// </summary>
/// <remarks>
/// A hang we measured on a real PC left no error-level entry at all. Its only traces were the information entries
/// "LiveKernelEvent code 141" in the Application log and "Component WATCHDOG has requested to create a Live Kernel Dump"
/// in the WerKernel log, which an errors-only search never sees.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class HardwareFaultReader
{
    const int MaxRecordsPerLog = 500;
    const string WerKernelLog = "Microsoft-Windows-WerKernel/Operational";

    /// <summary>Graphics driver / hardware error events (any of error, warning or critical), plus every graphics driver reset (4101).</summary>
    public static string DriverQuery(DateTimeOffset from, DateTimeOffset to)
    {
        string providers = string.Join(" or ", HardwareFaults.DriverProviders.Select(p => $"@Name='{p}'"));
        return $"*[System[((Provider[{providers}] and Level>0 and Level<4) or EventID=4101) and {Time(from, to)}]]";
    }

    /// <summary>Every Windows Error Reporting entry; the caller keeps only the kernel hangs and blue screens.</summary>
    public static string WerQuery(DateTimeOffset from, DateTimeOffset to) =>
        $"*[System[Provider[@Name='{HardwareFaults.WerProvider}'] and EventID=1001 and {Time(from, to)}]]";

    public static string WerKernelQuery(DateTimeOffset from, DateTimeOffset to) =>
        $"*[System[Provider[@Name='{HardwareFaults.WerKernelProvider}'] and EventID=1001 and {Time(from, to)}]]";

    static string Time(DateTimeOffset from, DateTimeOffset to) =>
        $"TimeCreated[@SystemTime>='{Utc(from)}' and @SystemTime<='{Utc(to)}']";

    static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// The hardware and driver fault entries between the two times, newest first. A log that cannot be read is listed in
    /// the second result instead of stopping the others (the WerKernel log does not exist on older Windows versions and is skipped quietly).
    /// </summary>
    public static (List<LogEvent> Events, List<object> Problems) Read(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var events = new List<LogEvent>();
        var problems = new List<object>();
        Collect("System", "System", DriverQuery(from, to), events, problems, ct);
        Collect("Application", "Application", WerQuery(from, to), events, problems, ct);
        Collect(WerKernelLog, "WerKernel", WerKernelQuery(from, to), events, problems, ct);
        return (events, problems);
    }

    static void Collect(string log, string label, string query, List<LogEvent> into, List<object> problems, CancellationToken ct)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true });
            int n = 0;
            for (EventRecord? r = reader.ReadEvent(); r is not null; r = reader.ReadEvent())
            {
                using (r)
                {
                    ct.ThrowIfCancellationRequested();
                    string provider = r.ProviderName ?? "?";
                    string? raw = EventLogTool.Message(r);
                    // Windows Error Reporting also reports ordinary program crashes; those are not for this search.
                    if (log == "Application" && !HardwareFaults.IsHardwareFault(provider, r.Id, raw))
                        continue;
                    into.Add(new LogEvent(
                        label,
                        r.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.MinValue,
                        EventLogTool.LevelName(r.Level),
                        provider,
                        r.Id,
                        HardwareFaults.MessageFor(provider, r.Id, raw)));
                }
                if (++n >= MaxRecordsPerLog)
                    break;
            }
        }
        catch (EventLogNotFoundException) { /* this Windows has no such log */ }
        catch (EventLogException ex) { problems.Add(new { log, error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { problems.Add(new { log, error = ex.Message }); }
    }
}
