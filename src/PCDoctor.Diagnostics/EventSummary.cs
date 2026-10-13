using System.Text.RegularExpressions;

namespace PCDoctor.Diagnostics;

/// <summary>One entry from a system event log (Windows Event Log now, the Linux journal later).</summary>
public sealed record LogEvent(string Log, DateTimeOffset Time, string Level, string Provider, int Id, string? Message);

/// <summary>Events that repeat (same source, same id, same first line) folded into one row.</summary>
public sealed record EventGroup(
    string Log,
    string Level,
    string Provider,
    int Id,
    int Count,
    DateTimeOffset First,
    DateTimeOffset Last,
    string? Message,
    string? Meaning);

/// <summary>Turns hundreds of raw log entries into a short list the model and the user can read.</summary>
public static partial class EventSummary
{
    const int MaxMessageChars = 400;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>Groups repeats, newest first. Version numbers and times in the first line do not split a group.</summary>
    public static List<EventGroup> Group(IEnumerable<LogEvent> events, int maxGroups) =>
        events
            .GroupBy(e => (e.Log, e.Provider, e.Id, Key: Digits().Replace(FirstLine(e.Message), "#")))
            .Select(g =>
            {
                var newest = g.MaxBy(e => e.Time)!;
                return new EventGroup(
                    g.Key.Log, newest.Level, g.Key.Provider, g.Key.Id, g.Count(),
                    g.Min(e => e.Time), newest.Time,
                    Cut(newest.Message), KnownEvents.Explain(g.Key.Provider, g.Key.Id));
            })
            .OrderByDescending(g => g.Last)
            .Take(maxGroups)
            .ToList();

    static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        int nl = message.IndexOfAny(['\r', '\n']);
        string line = nl < 0 ? message : message[..nl];
        return line.Length > 160 ? line[..160] : line;
    }

    static string? Cut(string? message)
    {
        if (message is null) return null;
        message = message.Trim();
        return message.Length <= MaxMessageChars ? message : message[..MaxMessageChars] + " […]";
    }
}

/// <summary>Plain-language meaning of the event log entries that matter most on a family PC.</summary>
public static class KnownEvents
{
    static readonly Dictionary<(string Provider, int Id), string> ById = new()
    {
        [("Microsoft-Windows-Kernel-Power", 41)] = "PC'en genstartede uden at lukke korrekt ned (strømsvigt, nedfrysning, overophedning eller hård genstart).",
        [("EventLog", 6008)] = "Den forrige nedlukning var uventet.",
        [("Microsoft-Windows-WER-SystemErrorReporting", 1001)] = "Blåskærm (Windows-nedbrud). Beskeden nævner fejlkoden.",
        [("BugCheck", 1001)] = "Blåskærm (Windows-nedbrud). Beskeden nævner fejlkoden.",
        [("Application Error", 1000)] = "Et program crashede. Beskeden nævner programmet og den fil (modul), der fejlede.",
        [("Application Hang", 1002)] = "Et program holdt op med at svare og blev lukket.",
        [(".NET Runtime", 1026)] = "Et .NET-program crashede.",
        [("Display", 4101)] = "Grafikdriveren holdt op med at svare og blev genstartet (skærmen blinker sort).",
        [("disk", 7)] = "Disken har en dårlig blok. Tag backup; disken kan være ved at gå i stykker.",
        [("disk", 51)] = "Fejl under skrivning til disken (sidefil). Kan være kabel- eller diskproblem.",
        [("disk", 153)] = "Disken var længe om at svare og forsøgte igen.",
        [("Ntfs", 55)] = "Filsystemet på disken er beskadiget. Kør diskkontrol (chkdsk).",
        [("Ntfs", 98)] = "Windows fandt fejl i filsystemet på disken.",
        [("Microsoft-Windows-Ntfs", 55)] = "Filsystemet på disken er beskadiget. Kør diskkontrol (chkdsk).",
        [("Microsoft-Windows-Ntfs", 98)] = "Windows fandt fejl i filsystemet på disken.",
        [("Microsoft-Windows-Resource-Exhaustion-Detector", 2004)] = "Windows var ved at løbe tør for hukommelse.",
        [("Microsoft-Windows-DNS-Client", 1014)] = "Et navneopslag (DNS) fik ikke svar i tide.",
        [("DistributedCOM", 10016)] = "Meget almindelig og harmløs. Kan ignoreres.",
        [("DistributedCOM", 10010)] = "En baggrundstjeneste svarede ikke i tide. Normalt harmløs.",
    };

    static readonly Dictionary<string, string> ByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft-Windows-WHEA-Logger"] = "Hardwarefejl rapporteret af processor, hukommelse eller PCIe (WHEA). Kan skyldes overclock, varme eller defekt hardware.",
        ["nvlddmkm"] = "NVIDIA-grafikdriveren rapporterede en fejl.",
        ["amdkmdag"] = "AMD-grafikdriveren rapporterede en fejl.",
        ["amdkmdap"] = "AMD-grafikdriveren rapporterede en fejl.",
        ["igfx"] = "Intel-grafikdriveren rapporterede en fejl.",
        ["Service Control Manager"] = "En Windows-tjeneste kunne ikke starte eller stoppede uventet.",
    };

    public static string? Explain(string provider, int id) =>
        ById.TryGetValue((provider, id), out string? m) ? m
        : ByProvider.TryGetValue(provider, out m) ? m
        : null;
}
