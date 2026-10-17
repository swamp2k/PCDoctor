using PCDoctor.Core;
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

    /// <summary>
    /// Values that change between otherwise identical events: versions and dates/times (digits joined by . : / -,
    /// with the invisible marks Windows puts in dates), hex values and long numbers such as process ids.
    /// Short standalone numbers (Harddisk0 vs Harddisk1) are kept, because they name different devices.
    /// </summary>
    [GeneratedRegex(@"0x[0-9a-fA-F]+|\d+(?:[.:/\-\u200e\u200f]+\d+)+|\d{5,}")]
    private static partial Regex Volatile();

    /// <summary>All groups, newest first. Version numbers and times in the first line do not split a group.</summary>
    public static List<EventGroup> Group(IEnumerable<LogEvent> events) =>
        events
            .GroupBy(e => (e.Log, e.Provider, e.Id, Key: Volatile().Replace(FirstLine(e.Message), "#")))
            .Select(g =>
            {
                var newest = g.MaxBy(e => e.Time)!;
                return new EventGroup(
                    g.Key.Log, newest.Level, g.Key.Provider, g.Key.Id, g.Count(),
                    g.Min(e => e.Time), newest.Time,
                    Cut(newest.Message), KnownEvents.Explain(g.Key.Provider, g.Key.Id));
            })
            .OrderByDescending(g => g.Last)
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
    /// <summary>Each meaning in Danish, English, Swedish, Norwegian and German.</summary>
    static readonly Dictionary<(string Provider, int Id), string[]> ById = new()
    {
        [("Microsoft-Windows-Kernel-Power", 41)] = ["PC'en genstartede uden at lukke korrekt ned (strømsvigt, nedfrysning, overophedning eller hård genstart).", "The PC restarted without shutting down properly (power cut, freeze, overheating or hard reset).", "Datorn startade om utan att stängas av korrekt (strömavbrott, frysning, överhettning eller hård omstart).", "PC-en startet på nytt uten å slå seg av riktig (strømbrudd, frys, overoppheting eller hard omstart).", "Der PC wurde neu gestartet, ohne richtig herunterzufahren (Stromausfall, Einfrieren, Überhitzung oder harter Neustart)."],
        [("EventLog", 6008)] = ["Den forrige nedlukning var uventet.", "The previous shutdown was unexpected.", "Den föregående avstängningen var oväntad.", "Den forrige avslutningen var uventet.", "Das vorherige Herunterfahren war unerwartet."],
        [("Microsoft-Windows-WER-SystemErrorReporting", 1001)] = ["Blåskærm (Windows-nedbrud). Beskeden nævner fejlkoden.", "Blue screen (Windows crash). The message names the error code.", "Blåskärm (Windows-krasch). Meddelandet nämner felkoden.", "Blåskjerm (Windows-krasj). Meldingen nevner feilkoden.", "Bluescreen (Windows-Absturz). Die Meldung nennt den Fehlercode."],
        [("BugCheck", 1001)] = ["Blåskærm (Windows-nedbrud). Beskeden nævner fejlkoden.", "Blue screen (Windows crash). The message names the error code.", "Blåskärm (Windows-krasch). Meddelandet nämner felkoden.", "Blåskjerm (Windows-krasj). Meldingen nevner feilkoden.", "Bluescreen (Windows-Absturz). Die Meldung nennt den Fehlercode."],
        [("Application Error", 1000)] = ["Et program crashede. Beskeden nævner programmet og den fil (modul), der fejlede.", "A program crashed. The message names the program and the file (module) that failed.", "Ett program kraschade. Meddelandet nämner programmet och den fil (modul) som fallerade.", "Et program krasjet. Meldingen nevner programmet og filen (modulen) som feilet.", "Ein Programm ist abgestürzt. Die Meldung nennt das Programm und die Datei (Modul), die versagt hat."],
        [("Application Hang", 1002)] = ["Et program holdt op med at svare og blev lukket.", "A program stopped responding and was closed.", "Ett program slutade svara och stängdes.", "Et program sluttet å svare og ble lukket.", "Ein Programm reagierte nicht mehr und wurde geschlossen."],
        [(".NET Runtime", 1026)] = ["Et .NET-program crashede.", "A .NET program crashed.", "Ett .NET-program kraschade.", "Et .NET-program krasjet.", "Ein .NET-Programm ist abgestürzt."],
        [("Display", 4101)] = ["Grafikdriveren holdt op med at svare og blev genstartet (skærmen blinker sort).", "The graphics driver stopped responding and was restarted (the screen flashes black).", "Grafikdrivrutinen slutade svara och startades om (skärmen blinkar svart).", "Skjermdriveren sluttet å svare og ble startet på nytt (skjermen blinker svart).", "Der Grafiktreiber reagierte nicht mehr und wurde neu gestartet (der Bildschirm wird kurz schwarz)."],
        [("disk", 7)] = ["Disken har en dårlig blok. Tag backup; disken kan være ved at gå i stykker.", "The disk has a bad block. Make a backup; the disk may be about to fail.", "Disken har ett dåligt block. Ta en säkerhetskopia; disken kan vara på väg att gå sönder.", "Disken har en dårlig blokk. Ta sikkerhetskopi; disken kan være i ferd med å gå i stykker.", "Die Festplatte hat einen fehlerhaften Block. Mach ein Backup; die Festplatte könnte bald ausfallen."],
        [("disk", 51)] = ["Fejl under skrivning til disken (sidefil). Kan være kabel- eller diskproblem.", "Error while writing to the disk (page file). May be a cable or disk problem.", "Fel vid skrivning till disken (växlingsfil). Kan vara kabel- eller diskproblem.", "Feil under skriving til disken (sidefil). Kan være kabel- eller diskproblem.", "Fehler beim Schreiben auf die Festplatte (Auslagerungsdatei). Kann ein Kabel- oder Festplattenproblem sein."],
        [("disk", 153)] = ["Disken var længe om at svare og forsøgte igen.", "The disk was slow to answer and tried again.", "Disken var långsam att svara och försökte igen.", "Disken brukte lang tid på å svare og prøvde igjen.", "Die Festplatte hat lange nicht geantwortet und es erneut versucht."],
        [("Ntfs", 55)] = ["Filsystemet på disken er beskadiget. Kør diskkontrol (chkdsk).", "The file system on the disk is damaged. Run a disk check (chkdsk).", "Filsystemet på disken är skadat. Kör diskkontroll (chkdsk).", "Filsystemet på disken er skadet. Kjør diskkontroll (chkdsk).", "Das Dateisystem auf der Festplatte ist beschädigt. Führe eine Datenträgerprüfung (chkdsk) aus."],
        [("Ntfs", 98)] = ["Windows fandt fejl i filsystemet på disken.", "Windows found errors in the file system on the disk.", "Windows hittade fel i filsystemet på disken.", "Windows fant feil i filsystemet på disken.", "Windows hat Fehler im Dateisystem der Festplatte gefunden."],
        [("Microsoft-Windows-Ntfs", 55)] = ["Filsystemet på disken er beskadiget. Kør diskkontrol (chkdsk).", "The file system on the disk is damaged. Run a disk check (chkdsk).", "Filsystemet på disken är skadat. Kör diskkontroll (chkdsk).", "Filsystemet på disken er skadet. Kjør diskkontroll (chkdsk).", "Das Dateisystem auf der Festplatte ist beschädigt. Führe eine Datenträgerprüfung (chkdsk) aus."],
        [("Microsoft-Windows-Ntfs", 98)] = ["Windows fandt fejl i filsystemet på disken.", "Windows found errors in the file system on the disk.", "Windows hittade fel i filsystemet på disken.", "Windows fant feil i filsystemet på disken.", "Windows hat Fehler im Dateisystem der Festplatte gefunden."],
        [("Microsoft-Windows-Resource-Exhaustion-Detector", 2004)] = ["Windows var ved at løbe tør for hukommelse.", "Windows was running out of memory.", "Windows höll på att få slut på minne.", "Windows holdt på å gå tom for minne.", "Windows ging der Arbeitsspeicher aus."],
        [("Microsoft-Windows-DNS-Client", 1014)] = ["Et navneopslag (DNS) fik ikke svar i tide.", "A name lookup (DNS) got no answer in time.", "Ett namnuppslag (DNS) fick inget svar i tid.", "Et navneoppslag (DNS) fikk ikke svar i tide.", "Eine Namensauflösung (DNS) bekam nicht rechtzeitig eine Antwort."],
        [("DistributedCOM", 10016)] = ["Meget almindelig og harmløs. Kan ignoreres.", "Very common and harmless. Can be ignored.", "Mycket vanlig och ofarlig. Kan ignoreras.", "Svært vanlig og harmløs. Kan ignoreres.", "Sehr häufig und harmlos. Kann ignoriert werden."],
        [("DistributedCOM", 10010)] = ["En baggrundstjeneste svarede ikke i tide. Normalt harmløs.", "A background service did not answer in time. Usually harmless.", "En bakgrundstjänst svarade inte i tid. Normalt ofarlig.", "En bakgrunnstjeneste svarte ikke i tide. Vanligvis harmløs.", "Ein Hintergrunddienst hat nicht rechtzeitig geantwortet. Normalerweise harmlos."],
    };

    static readonly Dictionary<string, string[]> ByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft-Windows-WHEA-Logger"] = ["Hardwarefejl rapporteret af processor, hukommelse eller PCIe (WHEA). Kan skyldes overclock, varme eller defekt hardware.", "Hardware error reported by the processor, memory or PCIe (WHEA). Can be caused by overclocking, heat or faulty hardware.", "Hårdvarufel rapporterat av processor, minne eller PCIe (WHEA). Kan bero på överklockning, värme eller defekt hårdvara.", "Maskinvarefeil rapportert av prosessor, minne eller PCIe (WHEA). Kan skyldes overklokking, varme eller defekt maskinvare.", "Hardwarefehler, gemeldet von Prozessor, Arbeitsspeicher oder PCIe (WHEA). Kann an Übertaktung, Hitze oder defekter Hardware liegen."],
        ["nvlddmkm"] = ["NVIDIA-grafikdriveren rapporterede en fejl.", "The NVIDIA graphics driver reported an error.", "NVIDIA-grafikdrivrutinen rapporterade ett fel.", "NVIDIA-skjermdriveren rapporterte en feil.", "Der NVIDIA-Grafiktreiber hat einen Fehler gemeldet."],
        ["amdkmdag"] = ["AMD-grafikdriveren rapporterede en fejl.", "The AMD graphics driver reported an error.", "AMD-grafikdrivrutinen rapporterade ett fel.", "AMD-skjermdriveren rapporterte en feil.", "Der AMD-Grafiktreiber hat einen Fehler gemeldet."],
        ["amdkmdap"] = ["AMD-grafikdriveren rapporterede en fejl.", "The AMD graphics driver reported an error.", "AMD-grafikdrivrutinen rapporterade ett fel.", "AMD-skjermdriveren rapporterte en feil.", "Der AMD-Grafiktreiber hat einen Fehler gemeldet."],
        ["igfx"] = ["Intel-grafikdriveren rapporterede en fejl.", "The Intel graphics driver reported an error.", "Intel-grafikdrivrutinen rapporterade ett fel.", "Intel-skjermdriveren rapporterte en feil.", "Der Intel-Grafiktreiber hat einen Fehler gemeldet."],
        ["Service Control Manager"] = ["En Windows-tjeneste kunne ikke starte eller stoppede uventet.", "A Windows service could not start or stopped unexpectedly.", "En Windows-tjänst kunde inte starta eller stoppade oväntat.", "En Windows-tjeneste kunne ikke starte eller stoppet uventet.", "Ein Windows-Dienst konnte nicht starten oder wurde unerwartet beendet."],
    };

    /// <summary>The meaning in the language picked with the flag, or null for an event we do not know.</summary>
    public static string? Explain(string provider, int id) =>
        ById.TryGetValue((provider, id), out string[]? m) || ByProvider.TryGetValue(provider, out m)
            ? L.T(m[0], m[1], m[2], m[3], m[4])
            : null;
}
