using System.Text.RegularExpressions;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>
/// Plain-language meaning of the Linux system log (journal) messages that matter most on a family PC or Steam Deck.
/// Linux messages have no event ids, so they are recognised by their wording.
/// </summary>
public static class LinuxEvents
{
    /// <summary>Each meaning in Danish, English, Swedish, Norwegian and German.</summary>
    static readonly (Regex Pattern, string[] Meaning)[] Known =
    [
        (new(@"ring \S+ timeout|GPU reset|gpu hang|amdgpu.*(device lost|failed to initialize)|GPU fault|NVRM: Xid", RegexOptions.IgnoreCase),
            ["Grafikkortet eller dets driver holdt op med at svare (ofte driver, varme eller et spil der presser grafikken).", "The graphics card or its driver stopped responding (often the driver, heat, or a game pushing the graphics).", "Grafikkortet eller dess drivrutin slutade svara (ofta drivrutin, värme eller ett spel som pressar grafiken).", "Skjermkortet eller driveren sluttet å svare (ofte driver, varme eller et spill som presser grafikken).", "Die Grafikkarte oder ihr Treiber reagierte nicht mehr (oft Treiber, Hitze oder ein Spiel, das die Grafik stark fordert)."]),
        (new(@"Out of memory|oom-kill|oom_reaper|invoked oom-killer", RegexOptions.IgnoreCase),
            ["Computeren løb tør for hukommelse (RAM), og Linux lukkede et program for at klare sig.", "The computer ran out of memory (RAM) and Linux closed a program to cope.", "Datorn fick slut på minne (RAM) och Linux stängde ett program för att klara sig.", "Datamaskinen gikk tom for minne (RAM), og Linux lukket et program for å klare seg.", "Dem Computer ging der Arbeitsspeicher (RAM) aus, und Linux hat ein Programm beendet."]),
        (new(@"I/O error|blk_update_request|Buffer I/O error|EXT4-fs error|BTRFS (error|critical)|nvme.*(timeout|reset)|mmc\d+:.*error|critical medium error", RegexOptions.IgnoreCase),
            ["Fejl ved læsning eller skrivning på en disk eller et hukommelseskort. Tag backup; disken eller kortet kan være ved at gå i stykker.", "Error reading or writing a disk or memory card. Make a backup; the disk or card may be failing.", "Fel vid läsning eller skrivning på en disk eller ett minneskort. Ta en säkerhetskopia; disken eller kortet kan vara på väg att gå sönder.", "Feil ved lesing eller skriving på en disk eller et minnekort. Ta sikkerhetskopi; disken eller kortet kan være i ferd med å gå i stykker.", "Fehler beim Lesen oder Schreiben einer Festplatte oder Speicherkarte. Mach ein Backup; sie könnte bald ausfallen."]),
        (new(@"dumped core|segfault|general protection fault|Process \d+ \(.+\) of user", RegexOptions.IgnoreCase),
            ["Et program crashede. Beskeden nævner programmet.", "A program crashed. The message names the program.", "Ett program kraschade. Meddelandet nämner programmet.", "Et program krasjet. Meldingen nevner programmet.", "Ein Programm ist abgestürzt. Die Meldung nennt das Programm."]),
        (new(@"critical temperature|thermal (shutdown|trip)|temperature above threshold|cpu clock throttled", RegexOptions.IgnoreCase),
            ["Computeren blev for varm og satte farten ned eller slukkede. Tjek at luftindtag og blæser er fri for støv.", "The computer got too hot and slowed down or shut off. Check that the vents and fan are free of dust.", "Datorn blev för varm och sänkte farten eller stängdes av. Kontrollera att luftintag och fläkt är fria från damm.", "Datamaskinen ble for varm og satte ned farten eller slo seg av. Sjekk at luftinntak og vifte er fri for støv.", "Der Computer wurde zu heiß und hat sich verlangsamt oder abgeschaltet. Prüfe, ob Lüftungsschlitze und Lüfter staubfrei sind."]),
        (new(@"usb .*(error -71|device descriptor read|unable to enumerate)|over-current", RegexOptions.IgnoreCase),
            ["En USB-enhed (fx controller, dock eller kabel) har problemer med forbindelsen.", "A USB device (for example a controller, dock or cable) has connection problems.", "En USB-enhet (till exempel handkontroll, docka eller kabel) har anslutningsproblem.", "En USB-enhet (for eksempel kontroller, dokk eller kabel) har tilkoblingsproblemer.", "Ein USB-Gerät (zum Beispiel Controller, Dock oder Kabel) hat Verbindungsprobleme."]),
        (new(@"Bluetooth: .*(fail|error|timeout)", RegexOptions.IgnoreCase),
            ["Bluetooth havde problemer (kan give controllere eller høretelefoner der falder ud).", "Bluetooth had problems (can make controllers or headphones drop out).", "Bluetooth hade problem (kan göra att handkontroller eller hörlurar tappar anslutningen).", "Bluetooth hadde problemer (kan gjøre at kontrollere eller hodetelefoner faller ut).", "Bluetooth hatte Probleme (Controller oder Kopfhörer können dadurch die Verbindung verlieren)."]),
    ];

    /// <summary>The meaning in the language picked with the flag, or null for a message we do not know.</summary>
    public static string? Explain(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        foreach (var (pattern, m) in Known)
            if (pattern.IsMatch(message))
                return L.T(m[0], m[1], m[2], m[3], m[4]);
        return null;
    }
}
