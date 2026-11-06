using System.Text.RegularExpressions;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>
/// Recognises the records Windows leaves behind when the graphics card or a driver hangs.
/// A GPU hang freezes the desktop and can blank a monitor while the processor and RAM look perfectly normal,
/// and the only traces are often "information" level entries that an errors-only search never shows.
/// </summary>
public static partial class HardwareFaults
{
    /// <summary>Windows Error Reporting writes event 1001 to the Application log for every report, including kernel hangs.</summary>
    public const string WerProvider = "Windows Error Reporting";

    /// <summary>Logs "Component WATCHDOG has requested to create a Live Kernel Dump" at the moment a hang is detected.</summary>
    public const string WerKernelProvider = "Microsoft-Windows-WerKernel";

    /// <summary>Sources of graphics-driver and hardware-error events.</summary>
    public static readonly string[] DriverProviders =
        ["Display", "nvlddmkm", "amdkmdag", "amdwddmg", "igfx", "Microsoft-Windows-Dxgkrnl", "Microsoft-Windows-WHEA-Logger"];

    [GeneratedRegex(@"^\s*P1:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex FirstParameter();

    [GeneratedRegex(@"([A-Za-z0-9_\-]+\.(?:dmp|mdmp))", RegexOptions.IgnoreCase)]
    private static partial Regex DumpName();

    /// <summary>The short form <see cref="WerReport.Summary"/> writes. Tools re-read their own summaries, so this must parse too.</summary>
    [GeneratedRegex(@"^(LiveKernelEvent|BlueScreen) code (\S+)(?: \(dump ([^)]+)\))?")]
    private static partial Regex SummaryForm();

    [GeneratedRegex(@"Component\s+(\w+)\s+has requested", RegexOptions.IgnoreCase)]
    private static partial Regex Component();

    /// <summary>What a Windows Error Reporting entry is about.</summary>
    /// <param name="Kind">"LiveKernelEvent" (a hang Windows recovered from) or "BlueScreen".</param>
    /// <param name="Code">The first problem parameter. For a LiveKernelEvent this is the hang type, e.g. 141 (graphics card timeout).</param>
    public sealed record WerReport(string Kind, string? Code, string? Dump)
    {
        /// <summary>One short line: the first line of the event, which is what groups repeats together.</summary>
        public string Summary => $"{Kind} code {Code ?? "?"}" + (Dump is null ? "" : $" (dump {Dump})");
    }

    /// <summary>
    /// Reads a Windows Error Reporting event 1001 message, either the full text Windows writes or the short
    /// <see cref="WerReport.Summary"/> of it. Null when it is not a kernel hang or blue screen.
    /// </summary>
    public static WerReport? ParseWer(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        if (SummaryForm().Match(message) is { Success: true } s)
            return new WerReport(s.Groups[1].Value, s.Groups[2].Value is "?" ? null : s.Groups[2].Value, s.Groups[3].Success ? s.Groups[3].Value : null);
        string? kind = message.Contains("LiveKernelEvent", StringComparison.Ordinal) ? "LiveKernelEvent"
            : message.Contains("BlueScreen", StringComparison.Ordinal) ? "BlueScreen"
            : null;
        if (kind is null) return null;
        var code = FirstParameter().Match(message);
        var dump = DumpName().Match(message);
        return new WerReport(kind, code.Success ? code.Groups[1].Value : null, dump.Success ? dump.Groups[1].Value : null);
    }

    /// <summary>The Windows part that asked for the dump (WATCHDOG for a graphics or driver hang), or null.</summary>
    public static string? WerKernelComponent(string? message) =>
        message is not null && Component().Match(message) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// True for the entries that mean "hardware or a driver hung or failed": a kernel hang report, a live dump request,
    /// a graphics driver reset, a graphics driver error or a WHEA hardware error. Used to flag them even at information level.
    /// </summary>
    public static bool IsHardwareFault(string provider, int id, string? message)
    {
        if (provider == WerProvider && id == 1001)
            return ParseWer(message) is not null;
        if (provider == WerKernelProvider && id == 1001)
            return true;
        if (provider is "nvlddmkm" or "amdkmdag" or "amdwddmg" or "igfx")
            return true;
        if (provider == "Display" && id == 4101)
            return true;
        return provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The message shown for a Windows Error Reporting entry: short and the same for repeats of the same hang.</summary>
    public static string? MessageFor(string provider, int id, string? raw)
    {
        if (provider == WerProvider && id == 1001 && ParseWer(raw) is { } w)
            return w.Summary;
        if (provider == WerKernelProvider && id == 1001)
            return $"Component {WerKernelComponent(raw) ?? "?"} requested a live kernel dump (a driver or hardware part stopped responding).";
        return raw;
    }

    /// <summary>Plain meaning of a kernel hang report, by hang type.</summary>
    public static string ExplainWer(WerReport w)
    {
        if (w.Kind == "BlueScreen")
            return L.T("Windows crashede (blåskærm), og en rapport blev gemt.", "Windows crashed (blue screen) and a report was saved.",
                "Windows kraschade (blåskärm) och en rapport sparades.", "Windows krasjet (blåskjerm), og en rapport ble lagret.",
                "Windows ist abgestürzt (Bluescreen) und ein Bericht wurde gespeichert.");
        string code = w.Code ?? "?";
        return w.Code is "141" or "117"
            ? L.T($"Grafikkortet (GPU) holdt op med at svare (Windows-kode {code}). Skærmen kan blinke sort eller slukke, og alt hakker, mens processor og RAM ser normale ud. Typiske årsager: grafikdriveren, overlays (fx Xbox Game Bar), overclock, varme eller for lidt strøm.",
                  $"The graphics card (GPU) stopped responding (Windows code {code}). The screen may flash black or turn off and everything stutters while the processor and RAM look normal. Typical causes: the graphics driver, overlays (for example Xbox Game Bar), overclocking, heat or too little power.",
                  $"Grafikkortet (GPU) slutade svara (Windows-kod {code}). Skärmen kan blinka svart eller slockna och allt hackar medan processor och RAM ser normala ut. Vanliga orsaker: grafikdrivrutinen, överlägg (t.ex. Xbox Game Bar), överklockning, värme eller för lite ström.",
                  $"Skjermkortet (GPU) sluttet å svare (Windows-kode {code}). Skjermen kan blinke svart eller slukke, og alt hakker mens prosessor og RAM ser normale ut. Vanlige årsaker: skjermdriveren, overlegg (f.eks. Xbox Game Bar), overklokking, varme eller for lite strøm.",
                  $"Die Grafikkarte (GPU) reagierte nicht mehr (Windows-Code {code}). Der Bildschirm kann schwarz werden oder ausgehen und alles ruckelt, während Prozessor und RAM normal aussehen. Typische Ursachen: der Grafiktreiber, Overlays (z. B. Xbox Game Bar), Übertaktung, Hitze oder zu wenig Strom.")
            : L.T($"Windows opdagede, at en driver eller en hardwaredel hang (kode {code}), og gemte en hukommelsesdump. Det er ofte grafikkortet, disken eller en USB-enhed.",
                  $"Windows detected that a driver or a hardware part hung (code {code}) and saved a memory dump. It is often the graphics card, the disk or a USB device.",
                  $"Windows upptäckte att en drivrutin eller en hårdvarudel hängde sig (kod {code}) och sparade en minnesdump. Det är ofta grafikkortet, disken eller en USB-enhet.",
                  $"Windows oppdaget at en driver eller en maskinvaredel hang seg (kode {code}) og lagret en minnedump. Det er ofte skjermkortet, disken eller en USB-enhet.",
                  $"Windows hat erkannt, dass ein Treiber oder ein Hardwareteil hängt (Code {code}), und einen Speicherabzug gespeichert. Oft ist es die Grafikkarte, die Festplatte oder ein USB-Gerät.");
    }

    /// <summary>Plain meaning of the live dump request, which is logged the moment the hang is detected.</summary>
    public static string ExplainLiveDumpRequest() =>
        L.T("Windows gemte en hukommelsesdump, fordi en driver eller hardwaredel ikke svarede (ofte grafikkortet). Tidspunktet er, da hanget blev opdaget; selve dumpen er først færdigskrevet cirka et minut senere.",
            "Windows saved a memory dump because a driver or hardware part did not respond (often the graphics card). The time is when the hang was detected; the dump itself finishes writing about a minute later.",
            "Windows sparade en minnesdump eftersom en drivrutin eller hårdvarudel inte svarade (ofta grafikkortet). Tiden är när hängningen upptäcktes; själva dumpen skrivs klart ungefär en minut senare.",
            "Windows lagret en minnedump fordi en driver eller maskinvaredel ikke svarte (ofte skjermkortet). Tidspunktet er da hengingen ble oppdaget; selve dumpen er ferdigskrevet omtrent et minutt senere.",
            "Windows hat einen Speicherabzug gespeichert, weil ein Treiber oder Hardwareteil nicht reagierte (oft die Grafikkarte). Die Zeit ist der Moment, in dem der Hänger erkannt wurde; der Abzug ist erst etwa eine Minute später fertig geschrieben.");
}

/// <summary>One hardware or driver incident: related entries close together in time, counted once.</summary>
public sealed record FaultIncident(DateTimeOffset Start, DateTimeOffset End, string Summary, int Entries);

public static class FaultIncidents
{
    /// <summary>One hang writes several entries over minutes (and again at the next boot), so entries closer than this are one incident.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(15);

    /// <summary>Groups the hardware-fault entries into incidents, oldest first. Other entries are ignored.</summary>
    public static List<FaultIncident> Cluster(IEnumerable<LogEvent> events)
    {
        var faults = events.Where(e => HardwareFaults.IsHardwareFault(e.Provider, e.Id, e.Message)).OrderBy(e => e.Time).ToList();
        var result = new List<FaultIncident>();
        for (int i = 0; i < faults.Count;)
        {
            int j = i;
            while (j + 1 < faults.Count && faults[j + 1].Time - faults[j].Time <= Gap) j++;
            var group = faults.Skip(i).Take(j - i + 1).ToList();
            // Prefer the Windows report: it names the kind of hang. Otherwise whatever came first.
            var lead = group.FirstOrDefault(e => e.Provider == HardwareFaults.WerProvider) ?? group[0];
            string summary = HardwareFaults.MessageFor(lead.Provider, lead.Id, lead.Message) ?? $"{lead.Provider} {lead.Id}";
            result.Add(new FaultIncident(group[0].Time, group[^1].Time, summary, group.Count));
            i = j + 1;
        }
        return result;
    }
}
