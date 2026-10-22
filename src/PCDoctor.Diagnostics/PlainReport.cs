using System.Globalization;
using System.Text;
using System.Text.Json;
using PCDoctor.Core;

namespace PCDoctor.Diagnostics;

/// <summary>
/// Turns tool results (the same JSON the agent gets) into short plain text for the Diagnostics page,
/// in the language picked with the flag, so the page and the agent always see the same facts.
/// </summary>
public static class PlainReport
{
    /// <summary>Marks a line that needs fixing. The Diagnostics page shows it with a red light instead of the sign.</summary>
    public const string Problem = "⚠";

    /// <summary>Marks a line worth a look. The Diagnostics page shows it with a yellow light instead of the sign.</summary>
    public const string Notice = "△";

    public static string Hardware(JsonElement r)
    {
        var b = new StringBuilder();
        foreach (var cpu in Rows(r, "cpu"))
        {
            string cores = Str(cpu, "NumberOfCores"), threads = Str(cpu, "NumberOfLogicalProcessors");
            b.AppendLine(L.T("Processor (CPU)", "Processor (CPU)", "Processor (CPU)", "Prosessor (CPU)", "Prozessor (CPU)") + $": {Str(cpu, "Name")} (" +
                         L.T($"{cores} kerner, {threads} tråde", $"{cores} cores, {threads} threads", $"{cores} kärnor, {threads} trådar",
                             $"{cores} kjerner, {threads} tråder", $"{cores} Kerne, {threads} Threads") + ")");
        }
        foreach (var gpu in Rows(r, "gpu"))
        {
            string line = L.T("Grafikkort (GPU)", "Graphics card (GPU)", "Grafikkort (GPU)", "Skjermkort (GPU)", "Grafikkarte (GPU)") + $": {Str(gpu, "Name")}";
            if (Str(gpu, "DriverVersion") is { Length: > 0 } v)
                line += $", {L.T("driver", "driver", "drivrutin", "driver", "Treiber")} {v}";
            bool old = false;
            if (WmiDate(Str(gpu, "DriverDate")) is { } d)
            {
                old = d < DateTime.Now.AddYears(-1);
                line += $" {L.T("fra", "from", "från", "fra", "vom")} {d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}" +
                        (old ? L.T(" (over et år gammel)", " (over a year old)", " (över ett år gammal)", " (over ett år gammel)", " (über ein Jahr alt)") : "");
            }
            b.AppendLine(old ? $"{Notice} {line}" : line);
        }
        var modules = Rows(r, "memory_modules").ToList();
        double ramGb = modules.Sum(m => Num(m, "Capacity") ?? 0) / 1073741824.0;
        if (ramGb == 0)
            ramGb = Rows(r, "computer").Select(c => Num(c, "TotalPhysicalMemory") ?? 0).FirstOrDefault() / 1073741824.0;
        if (ramGb > 0)
        {
            string speed = modules.Select(m => Num(m, "Speed")).FirstOrDefault(s => s > 0) is { } mhz ? $", {mhz:0} MHz" : "";
            b.AppendLine(L.T("Hukommelse (RAM)", "Memory (RAM)", "Minne (RAM)", "Minne (RAM)", "Arbeitsspeicher (RAM)") + $": {ramGb:0} GB" +
                         (modules.Count > 0 ? $" ({modules.Count} {L.T("moduler", "modules", "moduler", "moduler", "Module")}{speed})" : ""));
        }
        foreach (var os in Rows(r, "os"))
            b.AppendLine(Str(os, "Kernel") is { Length: > 0 } kernel
                ? $"Linux: {Str(os, "Caption")}, kernel {kernel} ({Str(os, "OSArchitecture")})"
                : $"Windows: {Str(os, "Caption")}, build {Str(os, "BuildNumber")} ({Str(os, "OSArchitecture")})");
        foreach (var c in Rows(r, "computer"))
            b.AppendLine($"{L.T("Computer", "Computer", "Dator", "Datamaskin", "Computer")}: {Str(c, "Manufacturer")} {Str(c, "Model")}".TrimEnd());
        foreach (var mb in Rows(r, "motherboard"))
            b.AppendLine($"{L.T("Bundkort", "Motherboard", "Moderkort", "Hovedkort", "Mainboard")}: {Str(mb, "Manufacturer")} {Str(mb, "Product")}".TrimEnd());

        var disks = Rows(r, "disks").ToList();
        if (disks.Count > 0)
        {
            b.AppendLine(L.T("Diske:", "Disks:", "Diskar:", "Disker:", "Festplatten:"));
            foreach (var d in disks)
            {
                double size = (Num(d, "Size") ?? 0) / 1073741824.0, free = (Num(d, "FreeSpace") ?? 0) / 1073741824.0;
                if (size <= 0) continue;
                b.AppendLine($"  {Str(d, "DeviceID")} {size:0} GB, {free:0} GB {Free} ({100 * free / size:0} %)" + (free / size < 0.10 || free < 10 ? AlmostFull : ""));
            }
        }

        // Linux only: the hottest reading of each sensor, and the battery.
        var temps = Rows(r, "temperatures").GroupBy(t => Str(t, "sensor")).Select(g => g.MaxBy(t => Num(t, "celsius") ?? 0)).ToList();
        if (temps.Count > 0)
        {
            b.AppendLine(L.T("Temperaturer:", "Temperatures:", "Temperaturer:", "Temperaturer:", "Temperaturen:"));
            foreach (var t in temps)
            {
                double c = Num(t, "celsius") ?? 0;
                string mark = c >= 90 ? $"{Problem} " : c >= 80 ? $"{Notice} " : "";
                string hot = c >= 90 ? "  " + L.T("(meget varm)", "(very hot)", "(mycket varm)", "(veldig varm)", "(sehr heiß)")
                           : c >= 80 ? "  " + L.T("(varm)", "(hot)", "(varm)", "(varm)", "(heiß)") : "";
                b.AppendLine($"  {mark}{Str(t, "sensor")}: {c:0} °C{hot}");
            }
        }
        foreach (var bat in Rows(r, "battery"))
        {
            string line = L.T("Batteri", "Battery", "Batteri", "Batteri", "Akku") + $": {Num(bat, "charge_percent"):0} %";
            if (Num(bat, "health_percent") is { } h)
            {
                line += ", " + L.T($"kan holde {h:0} % af det, det kunne som nyt", $"holds {h:0} % of what it did when new",
                    $"rymmer {h:0} % av vad det gjorde som nytt", $"holder {h:0} % av det det gjorde som nytt", $"hält {h:0} % von dem, was er neu konnte");
                if (h < 70) line = $"{Notice} {line}";
            }
            b.AppendLine(line);
        }

        var problems = Rows(r, "problem_devices").ToList();
        string devices = L.T("Enheder med problemer", "Devices with problems", "Enheter med problem", "Enheter med problemer", "Geräte mit Problemen");
        if (problems.Count == 0 && r.TryGetProperty("problem_devices", out var pd) && pd.ValueKind == JsonValueKind.Array)
            b.AppendLine($"{devices}: {L.T("ingen", "none", "inga", "ingen", "keine")}");
        else if (problems.Count > 0)
        {
            b.AppendLine($"⚠ {devices} ({problems.Count}):");
            foreach (var p in problems.Take(15))
                b.AppendLine($"  {Str(p, "Name")}: {DeviceProblem(Str(p, "ConfigManagerErrorCode"))}");
        }
        return b.ToString().TrimEnd();
    }

    static string Free => L.T("fri", "free", "ledigt", "ledig", "frei");

    static string AlmostFull => "  ⚠ " + L.T("næsten fuld", "almost full", "nästan full", "nesten full", "fast voll");

    static string DeviceProblem(string code) => code switch
    {
        "28" => L.T("mangler driver (kode 28)", "missing driver (code 28)", "saknar drivrutin (kod 28)", "mangler driver (kode 28)", "Treiber fehlt (Code 28)"),
        "22" => L.T("er slået fra (kode 22)", "is disabled (code 22)", "är inaktiverad (kod 22)", "er slått av (kode 22)", "ist deaktiviert (Code 22)"),
        "10" => L.T("kan ikke starte (kode 10)", "cannot start (code 10)", "kan inte starta (kod 10)", "kan ikke starte (kode 10)", "kann nicht starten (Code 10)"),
        "43" => L.T("er stoppet efter en fejl (kode 43)", "was stopped after an error (code 43)", "stoppades efter ett fel (kod 43)",
                    "er stoppet etter en feil (kode 43)", "wurde nach einem Fehler angehalten (Code 43)"),
        "31" => L.T("driveren virker ikke korrekt (kode 31)", "the driver is not working properly (code 31)", "drivrutinen fungerar inte korrekt (kod 31)",
                    "driveren virker ikke riktig (kode 31)", "der Treiber funktioniert nicht richtig (Code 31)"),
        _ => L.T($"fejlkode {code}", $"error code {code}", $"felkod {code}", $"feilkode {code}", $"Fehlercode {code}"),
    };

    public static string Resources(JsonElement r, JsonElement? processes = null)
    {
        var b = new StringBuilder();
        if (Num(r, "cpu_percent") is { } cpu)
        {
            string unknown = L.T("ukendt", "unknown", "okänd", "ukjent", "unbekannt");
            string gpu = Num(r, "gpu_percent") is { } g ? $"{g:0} %" : unknown;
            string disk = Num(r, "disk_active_percent") is { } d ? $"{d:0} % {L.T("aktiv", "active", "aktiv", "aktiv", "aktiv")}" : unknown;
            b.AppendLine($"CPU: {cpu:0} %    {L.T("Grafikkort", "Graphics card", "Grafikkort", "Skjermkort", "Grafikkarte")}: {gpu}    Disk: {disk}");
            string used = Gb(Num(r, "ram_used_gb")), total = Gb(Num(r, "ram_total_gb"));
            b.AppendLine($"RAM: {Num(r, "ram_percent"):0} % " + L.T($"i brug ({used} af {total} GB)", $"in use ({used} of {total} GB)",
                $"används ({used} av {total} GB)", $"i bruk ({used} av {total} GB)", $"belegt ({used} von {total} GB)"));
            if (Str(r, "top_process") is { Length: > 0 } top)
                b.AppendLine(L.T("Bruger mest CPU lige nu", "Using the most CPU right now", "Använder mest CPU just nu", "Bruker mest CPU akkurat nå",
                    "Verbraucht gerade die meiste CPU") + $": {top} ({Num(r, "top_process_cpu_percent"):0} %)");
        }
        else if (Str(r, "measurement_error") is { Length: > 0 } err)
            b.AppendLine(L.T("Kunne ikke måle CPU/RAM: ", "Could not measure CPU/RAM: ", "Kunde inte mäta CPU/RAM: ", "Kunne ikke måle CPU/RAM: ",
                "CPU/RAM konnte nicht gemessen werden: ") + err);
        else
            b.AppendLine(L.T("Måling af CPU/GPU/RAM findes kun på Windows.", "Measuring CPU/GPU/RAM is only available on Windows.",
                "Mätning av CPU/GPU/RAM finns bara på Windows.", "Måling av CPU/GPU/RAM finnes bare på Windows.", "Die Messung von CPU/GPU/RAM gibt es nur unter Windows."));

        if (Num(r, "uptime_hours") is { } up)
        {
            string on = L.T("Tændt siden sidste genstart", "On since the last restart", "Påslagen sedan senaste omstart", "På siden siste omstart",
                "Eingeschaltet seit dem letzten Neustart") + $": {Duration(up)}";
            b.AppendLine(up > 24 * 7
                ? $"{Notice} {on}" + L.T(" (en genstart kan hjælpe)", " (a restart may help)", " (en omstart kan hjälpa)", " (en omstart kan hjelpe)", " (ein Neustart kann helfen)")
                : on);
        }

        var drives = Rows(r, "drives").ToList();
        if (drives.Count > 0)
        {
            b.AppendLine(L.T("Drev:", "Drives:", "Enheter:", "Disker:", "Laufwerke:"));
            foreach (var d in drives)
            {
                string free = Gb(Num(d, "free_gb")), total = $"{Num(d, "total_gb"):0}";
                b.AppendLine($"  {Str(d, "drive")} " + L.T($"{free} GB fri af {total} GB", $"{free} GB free of {total} GB", $"{free} GB ledigt av {total} GB",
                                 $"{free} GB ledig av {total} GB", $"{free} GB frei von {total} GB") + $" ({Num(d, "free_percent"):0} %)" +
                             (Bool(d, "low_space") ? AlmostFull : ""));
            }
        }

        if (processes is { } p)
        {
            b.AppendLine();
            b.AppendLine(L.T("Programmer der bruger mest CPU:", "Programs using the most CPU:", "Program som använder mest CPU:",
                "Programmer som bruker mest CPU:", "Programme mit der höchsten CPU-Last:"));
            foreach (var row in Rows(p, "top_by_cpu").Take(8))
                b.AppendLine($"  {Num(row, "cpu_percent"),5:0.0} %   {Str(row, "name")}");
            b.AppendLine(L.T("Programmer der bruger mest hukommelse:", "Programs using the most memory:", "Program som använder mest minne:",
                "Programmer som bruker mest minne:", "Programme mit dem meisten Arbeitsspeicher:"));
            foreach (var row in Rows(p, "top_by_memory").Take(8))
                b.AppendLine($"  {Num(row, "memory_mb"),6:0} MB   {Str(row, "name")}");
        }
        return b.ToString().TrimEnd();
    }

    public static string Events(JsonElement r)
    {
        if (Rows(r, "problems").Any() && !Rows(r, "groups").Any())
            return L.T("Kunne ikke læse hændelsesloggen: ", "Could not read the event log: ", "Kunde inte läsa händelseloggen: ",
                       "Kunne ikke lese hendelsesloggen: ", "Die Ereignisanzeige konnte nicht gelesen werden: ") +
                   string.Join("; ", Rows(r, "problems").Select(p => Str(p, "error")));

        var groups = Rows(r, "groups").ToList();
        var b = new StringBuilder();
        string n = $"{Num(r, "total_events"):0}", kinds = $"{Num(r, "total_groups") ?? groups.Count:0}";
        b.AppendLine(groups.Count == 0
            ? (OperatingSystem.IsLinux()
                ? L.T("Ingen fejl i systemloggen i perioden. Fint.", "No errors in the system log in this period. Good.",
                      "Inga fel i systemloggen under perioden. Bra.", "Ingen feil i systemloggen i perioden. Fint.",
                      "Keine Fehler im Systemprotokoll in diesem Zeitraum. Gut.")
                : L.T("Ingen fejl i Windows' hændelseslog i perioden. Fint.", "No errors in the Windows event log in this period. Good.",
                      "Inga fel i Windows händelselogg under perioden. Bra.", "Ingen feil i Windows' hendelseslogg i perioden. Fint.",
                      "Keine Fehler in der Windows-Ereignisanzeige in diesem Zeitraum. Gut."))
            : L.T($"{n} fejl i perioden, samlet i {kinds} slags (nyeste først):", $"{n} errors in this period, grouped into {kinds} kinds (newest first):",
                  $"{n} fel under perioden, samlade i {kinds} sorter (nyaste först):", $"{n} feil i perioden, samlet i {kinds} typer (nyeste først):",
                  $"{n} Fehler im Zeitraum, zusammengefasst in {kinds} Arten (neueste zuerst):"));
        if (Num(r, "groups_left_out") is > 0 and var left)
        {
            int shown = groups.Count;
            b.AppendLine(L.T($"Viser de {shown} nyeste slags; {left:0} ældre slags er udeladt.", $"Showing the {shown} newest kinds; {left:0} older kinds are left out.",
                $"Visar de {shown} nyaste sorterna; {left:0} äldre sorter är utelämnade.", $"Viser de {shown} nyeste typene; {left:0} eldre typer er utelatt.",
                $"Zeige die {shown} neuesten Arten; {left:0} ältere Arten sind ausgelassen."));
        }
        foreach (var g in groups)
        {
            b.AppendLine();
            string when = DateTime.TryParse(Str(g, "last"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("dd-MM HH:mm", CultureInfo.InvariantCulture) : Str(g, "last");
            string count = Num(g, "count") is > 1 and var c ? $"  ×{c:0}" : "";
            string level = Str(g, "level") == "critical" ? L.T("KRITISK ", "CRITICAL ", "KRITISK ", "KRITISK ", "KRITISCH ") : "";
            b.AppendLine($"{(level.Length > 0 ? Problem : Notice)} {when}  {level}{Str(g, "source")}{(Str(g, "event_id") is "0" or "" ? "" : $" ({Str(g, "event_id")})")}{count}");
            if (Str(g, "meaning") is { Length: > 0 } meaning)
                b.AppendLine($"   {meaning}");
            if (Str(g, "message") is { Length: > 0 } msg)
            {
                string first = msg.Split('\n')[0].Trim();
                b.AppendLine($"   \"{(first.Length > 160 ? first[..160] + "…" : first)}\"");
            }
        }
        return b.ToString().TrimEnd();
    }

    public static string Minecraft(JsonElement r)
    {
        var installs = Rows(r, "installs").ToList();
        if (installs.Count == 0)
            return L.T("Fandt ingen Minecraft-installation med logfiler for denne bruger.",
                "Found no Minecraft installation with log files for this user.",
                "Hittade ingen Minecraft-installation med loggfiler för den här användaren.",
                "Fant ingen Minecraft-installasjon med loggfiler for denne brukeren.",
                "Keine Minecraft-Installation mit Logdateien für diesen Benutzer gefunden.");

        var b = new StringBuilder();
        b.AppendLine(L.T("Minecraft-installationer (senest brugt først):", "Minecraft installations (most recently used first):",
            "Minecraft-installationer (senast använda först):", "Minecraft-installasjoner (sist brukt først):", "Minecraft-Installationen (zuletzt benutzte zuerst):"));
        foreach (var i in installs)
        {
            string mods = Num(i, "mods") is > 0 and var m
                ? $", {m:0} {L.T("mods", "mods", "mods", "mods", "Mods")}"
                : ", " + L.T("ingen mods", "no mods", "inga mods", "ingen mods", "keine Mods");
            int crashes = Rows(i, "files").Count(f => Str(f, "kind") is "crash_report" or "jvm_crash");
            b.AppendLine($"  {Str(i, "launcher")}: {Str(i, "name")}{mods}, {Last} {Date(Str(i, "last_activity"))}" +
                         (crashes > 0 ? $", {crashes} {L.T("crash-filer", "crash files", "kraschfiler", "krasjfiler", "Absturzdateien")}" : ""));
        }

        if (r.TryGetProperty("newest_crash", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            b.AppendLine();
            b.Append(Crash(c));
        }
        else
        {
            b.AppendLine();
            b.Append(L.T("Ingen crash-rapporter fundet.", "No crash reports found.", "Inga kraschrapporter hittades.", "Ingen krasjrapporter funnet.",
                "Keine Absturzberichte gefunden."));
        }
        return b.ToString().TrimEnd();
    }

    static string Last => L.T("senest", "last", "senast", "sist", "zuletzt");

    static string Error => L.T("Fejl", "Error", "Fel", "Feil", "Fehler");

    /// <summary>One parsed crash file (the newest_crash or a 'file' read) in plain words.</summary>
    public static string Crash(JsonElement c)
    {
        var b = new StringBuilder();
        b.AppendLine($"{Notice} " + L.T("Seneste crash", "Latest crash", "Senaste krasch", "Siste krasj", "Letzter Absturz") + $": {Date(Str(c, "modified"))}");
        if (!c.TryGetProperty("content", out var x) || x.ValueKind != JsonValueKind.Object)
            return b.AppendLine(Str(c, "error")).ToString();

        if (Str(c, "kind") == "jvm_crash")
        {
            b.AppendLine(L.T("Java selv crashede (ikke Minecraft's egen fejlrapport).", "Java itself crashed (not Minecraft's own error report).",
                "Java själv kraschade (inte Minecrafts egen felrapport).", "Java selv krasjet (ikke Minecrafts egen feilrapport).",
                "Java selbst ist abgestürzt (nicht Minecrafts eigener Fehlerbericht)."));
            if (Str(x, "Error") is { Length: > 0 } e) b.AppendLine($"{Error}: {e}");
            if (Str(x, "ProblematicFrame") is { Length: > 0 } f)
                b.AppendLine(L.T("Fejlede i", "Failed in", "Fel i", "Feilet i", "Fehler in") + $": {f}");
            if (Bool(x, "GraphicsDriverSuspected"))
                b.AppendLine("⚠ " + L.T(
                    "Fejlen skete inde i grafikdriveren. Opdater grafikdriveren fra NVIDIA, AMD eller Intel.",
                    "The error happened inside the graphics driver. Update the graphics driver from NVIDIA, AMD or Intel.",
                    "Felet inträffade i grafikdrivrutinen. Uppdatera grafikdrivrutinen från NVIDIA, AMD eller Intel.",
                    "Feilen skjedde inne i skjermdriveren. Oppdater skjermdriveren fra NVIDIA, AMD eller Intel.",
                    "Der Fehler trat im Grafiktreiber auf. Aktualisiere den Grafiktreiber von NVIDIA, AMD oder Intel."));
            return b.ToString();
        }

        if (Str(x, "Description") is { Length: > 0 } d)
            b.AppendLine(L.T("Beskrivelse", "Description", "Beskrivning", "Beskrivelse", "Beschreibung") + $": {d}");
        if (Str(x, "Exception") is { Length: > 0 } ex) b.AppendLine($"{Error}: {(ex.Length > 200 ? ex[..200] + "…" : ex)}");
        var suspected = Strings(x, "SuspectedMods").Where(s => !s.Equals("NONE", StringComparison.OrdinalIgnoreCase)).ToList();
        if (suspected.Count > 0)
            b.AppendLine(L.T("Mistænkte mods", "Suspected mods", "Misstänkta mods", "Mistenkte mods", "Verdächtige Mods") + $": {string.Join(", ", suspected)}");
        var inStack = Strings(x, "ModsInStack").ToList();
        if (inStack.Count > 0)
            b.AppendLine(L.T("Mods nævnt i fejlen", "Mods named in the error", "Mods som nämns i felet", "Mods nevnt i feilen", "Im Fehler genannte Mods") +
                         $": {string.Join(", ", inStack)}");
        if (x.TryGetProperty("Details", out var det) && det.ValueKind == JsonValueKind.Object)
        {
            if (Str(det, "Minecraft Version") is { Length: > 0 } mv) b.AppendLine($"Minecraft {mv}");
            if (Str(det, "Java Version") is { Length: > 0 } jv) b.AppendLine($"Java {jv}");
        }
        return b.ToString();
    }

    // ---- network ----

    /// <summary>The facts the network verdict needs, read from the ping/DNS/TCP tool results.</summary>
    public sealed record NetworkFacts(bool GatewayFound, bool GatewayReplies, bool InternetReplies, double InternetLossPercent, double? InternetAvgMs, bool DnsWorks, bool HttpsWorks)
    {
        public static NetworkFacts From(JsonElement gatewayPing, JsonElement internetPing, JsonElement dns, JsonElement tcp)
        {
            var gw = Rows(gatewayPing, "results").ToList();
            var inet = Rows(internetPing, "results").Where(x => Num(x, "sent") > 0).ToList();
            return new NetworkFacts(
                GatewayFound: gw.Any(x => !x.TryGetProperty("error", out _)),
                GatewayReplies: gw.Any(x => Num(x, "received") > 0),
                InternetReplies: inet.Any(x => Num(x, "received") > 0),
                InternetLossPercent: inet.Count == 0 ? 100 : inet.Average(x => Num(x, "loss_percent") ?? 100),
                InternetAvgMs: inet.Select(x => Num(x, "avg_ms")).Where(v => v.HasValue).Min(),
                DnsWorks: Rows(dns, "lookups").Any(x => x.TryGetProperty("addresses", out _)),
                HttpsWorks: Bool(tcp, "connected"));
        }
    }

    public static string NetworkVerdict(NetworkFacts f)
    {
        if (!f.GatewayFound && !f.InternetReplies && !f.HttpsWorks)
            return L.T("Computeren er ikke forbundet til noget netværk. Tjek Wi-Fi eller netværkskablet.",
                "The computer is not connected to any network. Check Wi-Fi or the network cable.",
                "Datorn är inte ansluten till något nätverk. Kontrollera wifi eller nätverkskabeln.",
                "Datamaskinen er ikke koblet til noe nettverk. Sjekk Wi-Fi eller nettverkskabelen.",
                "Der Computer ist mit keinem Netzwerk verbunden. Prüfe das WLAN oder das Netzwerkkabel.");
        if (!f.InternetReplies && !f.DnsWorks && !f.HttpsWorks)
            return f.GatewayReplies
                ? L.T("Routeren svarer, men der er ingen forbindelse videre ud på internettet. Genstart routeren; hjælper det ikke, kan fejlen være hos internetudbyderen.",
                    "The router answers, but there is no connection on to the internet. Restart the router; if that does not help, the fault may be with the internet provider.",
                    "Routern svarar, men det finns ingen anslutning vidare ut på internet. Starta om routern; hjälper det inte kan felet ligga hos internetleverantören.",
                    "Ruteren svarer, men det er ingen forbindelse videre ut på internett. Start ruteren på nytt; hjelper ikke det, kan feilen være hos internettleverandøren.",
                    "Der Router antwortet, aber es gibt keine Verbindung weiter ins Internet. Starte den Router neu; hilft das nicht, liegt der Fehler vielleicht beim Internetanbieter.")
                : L.T("Hverken routeren eller internettet svarer. Tjek Wi-Fi eller kablet, og genstart routeren.",
                    "Neither the router nor the internet answers. Check Wi-Fi or the cable, and restart the router.",
                    "Varken routern eller internet svarar. Kontrollera wifi eller kabeln och starta om routern.",
                    "Verken ruteren eller internett svarer. Sjekk Wi-Fi eller kabelen, og start ruteren på nytt.",
                    "Weder der Router noch das Internet antworten. Prüfe WLAN oder Kabel und starte den Router neu.");
        if (!f.DnsWorks)
            return L.T("Der er forbindelse til internettet, men navneopslag (DNS) virker ikke, så hjemmesider og spil kan ikke findes. Genstart routeren.",
                "There is a connection to the internet, but name lookups (DNS) do not work, so websites and games cannot be found. Restart the router.",
                "Det finns anslutning till internet, men namnuppslag (DNS) fungerar inte, så webbplatser och spel kan inte hittas. Starta om routern.",
                "Det er forbindelse til internett, men navneoppslag (DNS) virker ikke, så nettsider og spill kan ikke finnes. Start ruteren på nytt.",
                "Es gibt eine Verbindung zum Internet, aber die Namensauflösung (DNS) funktioniert nicht, daher werden Websites und Spiele nicht gefunden. Starte den Router neu.");
        if (!f.HttpsWorks)
            return L.T("Internettet svarer, men forbindelsen til hjemmesider (HTTPS) fejlede. Det kan skyldes en firewall, VPN eller antivirus.",
                "The internet answers, but the connection to websites (HTTPS) failed. This can be caused by a firewall, VPN or antivirus.",
                "Internet svarar, men anslutningen till webbplatser (HTTPS) misslyckades. Det kan bero på en brandvägg, VPN eller antivirus.",
                "Internett svarer, men forbindelsen til nettsider (HTTPS) feilet. Det kan skyldes en brannmur, VPN eller antivirus.",
                "Das Internet antwortet, aber die Verbindung zu Websites (HTTPS) ist fehlgeschlagen. Das kann an einer Firewall, einem VPN oder einem Virenschutz liegen.");

        string s = L.T("Internettet virker.", "The internet works.", "Internet fungerar.", "Internett virker.", "Das Internet funktioniert.");
        if (f.InternetReplies && f.InternetLossPercent > 0)
        {
            string loss = $"{f.InternetLossPercent:0}";
            s += L.T($" Men {loss} % af ping-pakkerne gik tabt, så forbindelsen er ustabil (kan give lag og afbrydelser i spil).",
                $" But {loss} % of the ping packets were lost, so the connection is unstable (can cause lag and disconnects in games).",
                $" Men {loss} % av ping-paketen förlorades, så anslutningen är instabil (kan ge lagg och avbrott i spel).",
                $" Men {loss} % av ping-pakkene gikk tapt, så forbindelsen er ustabil (kan gi lag og brudd i spill).",
                $" Aber {loss} % der Ping-Pakete gingen verloren, die Verbindung ist also instabil (kann Lag und Verbindungsabbrüche in Spielen verursachen).");
        }
        if (f.InternetAvgMs is > 100)
        {
            string high = $"{f.InternetAvgMs:0}";
            s += L.T($" Svartiden er høj ({high} ms), hvilket kan give lag i spil.", $" The response time is high ({high} ms), which can cause lag in games.",
                $" Svarstiden är hög ({high} ms), vilket kan ge lagg i spel.", $" Svartiden er høy ({high} ms), noe som kan gi lag i spill.",
                $" Die Antwortzeit ist hoch ({high} ms), was in Spielen Lag verursachen kann.");
        }
        else if (f.InternetAvgMs is { } ms)
            s += L.T(" Svartid", " Response time", " Svarstid", " Svartid", " Antwortzeit") + $" {ms:0} ms.";
        else if (!f.InternetReplies)
            s += L.T(" (Ping bliver blokeret på dette netværk, men hjemmesider kan nås.)", " (Ping is blocked on this network, but websites can be reached.)",
                " (Ping blockeras på det här nätverket, men webbplatser kan nås.)", " (Ping blir blokkert på dette nettverket, men nettsider kan nås.)",
                " (Ping wird in diesem Netzwerk blockiert, aber Websites sind erreichbar.)");
        return s;
    }

    /// <summary>The light for the network verdict: a problem when something does not work, a notice when it is slow or unstable.</summary>
    static string VerdictMark(NetworkFacts f) =>
        !f.HttpsWorks || !f.DnsWorks ? Problem + " "
        : f.InternetReplies && (f.InternetLossPercent > 0 || f.InternetAvgMs is > 100) ? Notice + " "
        : "";

    public static string Network(JsonElement gatewayPing, JsonElement internetPing, JsonElement dns, JsonElement tcp)
    {
        var facts = NetworkFacts.From(gatewayPing, internetPing, dns, tcp);
        var b = new StringBuilder();
        b.AppendLine(VerdictMark(facts) + NetworkVerdict(facts));
        b.AppendLine();
        foreach (var x in Rows(gatewayPing, "results"))
            b.AppendLine(Ping(L.T("Router", "Router", "Router", "Ruter", "Router"), x));
        foreach (var x in Rows(internetPing, "results"))
            b.AppendLine(Ping(L.T("Internet", "Internet", "Internet", "Internett", "Internet"), x));
        string lookup = L.T("Navneopslag", "Name lookup", "Namnuppslag", "Navneoppslag", "Namensauflösung");
        string failed = L.T("FEJL", "FAILED", "FEL", "FEIL", "FEHLER");
        foreach (var x in Rows(dns, "lookups"))
            b.AppendLine(x.TryGetProperty("addresses", out _)
                ? $"{lookup} {Str(x, "name")}: OK ({Num(x, "ms"):0} ms)"
                : $"{Problem} {lookup} {Str(x, "name")}: {failed} ({Str(x, "error")})");
        string connection = L.T("Forbindelse til", "Connection to", "Anslutning till", "Forbindelse til", "Verbindung zu");
        b.AppendLine(Bool(tcp, "connected")
            ? $"{connection} {Str(tcp, "host")}:{Str(tcp, "port")}: OK ({Num(tcp, "ms"):0} ms)"
            : $"{Problem} {connection} {Str(tcp, "host")}:{Str(tcp, "port")}: {failed} ({Str(tcp, "error")})");
        return b.ToString().TrimEnd();
    }

    static string Ping(string label, JsonElement x)
    {
        if (x.TryGetProperty("error", out var e))
            return $"{label}: {e.GetString()}";
        if (Num(x, "received") is not > 0)
            return $"{label} ({Str(x, "host")}): {L.T("svarer ikke", "does not answer", "svarar inte", "svarer ikke", "antwortet nicht")} ({Str(x, "last_status")})";
        string loss = Num(x, "loss_percent") is > 0 and var l ? $", {l:0} % {L.T("tabt", "lost", "förlorat", "tapt", "verloren")}" : "";
        return $"{label} ({Str(x, "host")}): {L.T("svarer", "answers", "svarar", "svarer", "antwortet")}, {Num(x, "avg_ms"):0} ms{loss}";
    }

    // ---- Phase 4: mods, other games, online services ----

    /// <summary>The minecraft_mods result: loader, number of mods and the problems found.</summary>
    public static string Mods(JsonElement r)
    {
        if (!r.TryGetProperty("loader", out _))
            return Str(r, "note");
        var b = new StringBuilder();
        string loader = Str(r, "loader") switch
        {
            "fabric" => "Fabric", "quilt" => "Quilt", "forge" => "Forge", "neoforge" => "NeoForge",
            "vanilla" => L.T("ingen (vanilla)", "none (vanilla)", "ingen (vanilla)", "ingen (vanilla)", "keiner (Vanilla)"),
            var x => x,
        };
        b.AppendLine(L.T("Mods i", "Mods in", "Mods i", "Mods i", "Mods in") + $" {Str(r, "launcher")}: {Str(r, "name")}");
        b.AppendLine($"Mod-loader: {loader}, {Num(r, "mods"):0} {L.T("mods", "mods", "mods", "mods", "Mods")}" +
                     (Num(r, "disabled") is > 0 and var d ? $", {d:0} {L.T("slået fra", "disabled", "inaktiverade", "slått av", "deaktiviert")}" : ""));
        var problems = Rows(r, "problems").ToList();
        if (problems.Count == 0)
        {
            b.Append(L.T("Ingen problemer fundet: ingen manglende mods, ingen dubletter, alle passer til loaderen.",
                "No problems found: no missing mods, no duplicates, all fit the loader.",
                "Inga problem hittades: inga saknade mods, inga dubbletter, alla passar loadern.",
                "Ingen problemer funnet: ingen manglende mods, ingen duplikater, alle passer til loaderen.",
                "Keine Probleme gefunden: keine fehlenden Mods, keine Duplikate, alle passen zum Loader."));
            return b.ToString();
        }
        bool one = problems.Count == 1;
        b.AppendLine($"⚠ {problems.Count} " + L.T(one ? "problem" : "problemer", one ? "problem" : "problems", "problem",
            one ? "problem" : "problemer", one ? "Problem" : "Probleme") + ":");
        foreach (var p in problems.Take(20))
        {
            string kind = Str(p, "Kind") switch
            {
                "missing_dependency" => L.T("Mangler en mod", "A mod is missing", "En mod saknas", "Mangler en mod", "Ein Mod fehlt"),
                "duplicate" => L.T("Samme mod to gange", "The same mod twice", "Samma mod två gånger", "Samme mod to ganger", "Derselbe Mod doppelt"),
                "wrong_loader" => L.T("Forkert loader", "Wrong loader", "Fel loader", "Feil loader", "Falscher Loader"),
                "unreadable" => L.T("Ødelagt fil", "Damaged file", "Skadad fil", "Ødelagt fil", "Beschädigte Datei"),
                var x => x,
            };
            b.AppendLine($"  {kind}: {Str(p, "Detail")}");
        }
        return b.ToString().TrimEnd();
    }

    /// <summary>The other_game_logs result: the games found and the newest log's warning signs.</summary>
    public static string OtherGames(JsonElement r)
    {
        var games = Rows(r, "games").ToList();
        if (games.Count == 0)
            return L.T("Fandt ingen logfiler fra Fortnite, Roblox, Rocket League eller andre Unreal- og Unity-spil for denne bruger.",
                "Found no log files from Fortnite, Roblox, Rocket League or other Unreal and Unity games for this user.",
                "Hittade inga loggfiler från Fortnite, Roblox, Rocket League eller andra Unreal- och Unity-spel för den här användaren.",
                "Fant ingen loggfiler fra Fortnite, Roblox, Rocket League eller andre Unreal- og Unity-spill for denne brukeren.",
                "Keine Logdateien von Fortnite, Roblox, Rocket League oder anderen Unreal- und Unity-Spielen für diesen Benutzer gefunden.");
        var b = new StringBuilder();
        b.AppendLine(L.T("Spil med logfiler (senest spillet først):", "Games with log files (most recently played first):",
            "Spel med loggfiler (senast spelade först):", "Spill med loggfiler (sist spilt først):", "Spiele mit Logdateien (zuletzt gespielte zuerst):"));
        foreach (var g in games.Take(15))
        {
            int crashes = Rows(g, "files").Count(f => Str(f, "kind") == "crash_log");
            b.AppendLine($"  {Str(g, "name")}: {Last} {Date(Str(g, "last_activity"))}" +
                         (crashes > 0 ? $", {crashes} {L.T("crash-logs", "crash logs", "kraschloggar", "krasjlogger", "Absturzprotokolle")}" : ""));
        }
        if (r.TryGetProperty("newest_log", out var n) && n.ValueKind == JsonValueKind.Object)
        {
            b.AppendLine();
            b.AppendLine(L.T("Nyeste log", "Newest log", "Senaste logg", "Nyeste logg", "Neuestes Log") + $": {Str(n, "game")}, {Date(Str(n, "modified"))}" +
                         (Str(n, "kind") == "crash_log" ? " " + L.T("(crash-log)", "(crash log)", "(kraschlogg)", "(krasjlogg)", "(Absturzprotokoll)") : ""));
            if (n.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                var signs = Rows(c, "warning_signs").ToList();
                string lines = $"{Num(c, "problem_line_count"):0}";
                if (signs.Count == 0)
                    b.AppendLine(L.T($"Ingen kendte faresignaler. {lines} linjer med fejl i alt.", $"No known warning signs. {lines} lines with errors in total.",
                        $"Inga kända varningstecken. {lines} rader med fel totalt.", $"Ingen kjente faresignaler. {lines} linjer med feil totalt.",
                        $"Keine bekannten Warnzeichen. Insgesamt {lines} Zeilen mit Fehlern."));
                foreach (var sgn in signs)
                    b.AppendLine($"⚠ {Sign(Str(sgn, "hint"))}");
            }
            else if (Str(n, "error") is { Length: > 0 } e)
                b.AppendLine(L.T("Kunne ikke læses: ", "Could not be read: ", "Kunde inte läsas: ", "Kunne ikke leses: ", "Konnte nicht gelesen werden: ") + e);
        }
        return b.ToString().TrimEnd();
    }

    /// <summary>A warning sign from a game log (an English hint meant for the model) in plain words, in the language picked.</summary>
    public static string Sign(string hint) => hint switch
    {
        _ when hint.StartsWith("The graphics card or its driver") => L.T(
            "Grafikkortet eller dets driver holdt op med at svare (ofte driver, varme eller overclock).",
            "The graphics card or its driver stopped responding (often the driver, heat or overclocking).",
            "Grafikkortet eller dess drivrutin slutade svara (ofta drivrutinen, värme eller överklockning).",
            "Skjermkortet eller driveren sluttet å svare (ofte driver, varme eller overklokking).",
            "Die Grafikkarte oder ihr Treiber reagierte nicht mehr (oft Treiber, Hitze oder Übertaktung)."),
        _ when hint.StartsWith("The graphics card ran out") => L.T(
            "Grafikkortet løb tør for sin egen hukommelse (VRAM). Skru ned for grafik eller opløsning.",
            "The graphics card ran out of its own memory (VRAM). Lower the graphics settings or resolution.",
            "Grafikkortet fick slut på sitt eget minne (VRAM). Sänk grafikinställningarna eller upplösningen.",
            "Skjermkortet gikk tom for sitt eget minne (VRAM). Skru ned grafikken eller oppløsningen.",
            "Der Grafikkarte ging der eigene Speicher (VRAM) aus. Verringere Grafikeinstellungen oder Auflösung."),
        _ when hint.StartsWith("The game ran out of memory") => L.T(
            "Spillet løb tør for hukommelse (RAM).", "The game ran out of memory (RAM).", "Spelet fick slut på minne (RAM).",
            "Spillet gikk tom for minne (RAM).", "Dem Spiel ging der Arbeitsspeicher (RAM) aus."),
        _ when hint.StartsWith("Anti-cheat") => L.T(
            "Anti-snyd-programmet nævnes; fejl i det kan stoppe spillet eller smide spilleren af.",
            "The anti-cheat program is mentioned; errors in it can stop the game or kick the player out.",
            "Antifusk-programmet nämns; fel i det kan stoppa spelet eller kasta ut spelaren.",
            "Antijuks-programmet nevnes; feil i det kan stoppe spillet eller kaste ut spilleren.",
            "Das Anti-Cheat-Programm wird erwähnt; Fehler darin können das Spiel stoppen oder den Spieler rauswerfen."),
        _ when hint.StartsWith("Connection to the game") => L.T(
            "Forbindelsen til spillets servere blev afbrudt eller svarede ikke.",
            "The connection to the game's servers was lost or did not answer.",
            "Anslutningen till spelets servrar bröts eller svarade inte.",
            "Forbindelsen til spillets servere ble brutt eller svarte ikke.",
            "Die Verbindung zu den Servern des Spiels wurde getrennt oder antwortete nicht."),
        _ when hint.StartsWith("The game crashed with an access violation") => L.T(
            "Spillet crashede med en adgangsfejl (fejl i spillet, ødelagte spilfiler eller et program/overlay der blander sig).",
            "The game crashed with an access violation (a bug in the game, damaged game files or a program/overlay interfering).",
            "Spelet kraschade med ett åtkomstfel (fel i spelet, skadade spelfiler eller ett program/overlay som stör).",
            "Spillet krasjet med en tilgangsfeil (feil i spillet, ødelagte spillfiler eller et program/overlay som blander seg inn).",
            "Das Spiel stürzte mit einer Zugriffsverletzung ab (Fehler im Spiel, beschädigte Spieldateien oder ein störendes Programm/Overlay)."),
        _ => hint,
    };

    /// <summary>The service_status result: is the game down, or is it our connection?</summary>
    public static string Services(JsonElement r)
    {
        var b = new StringBuilder();
        bool internet = r.TryGetProperty("neutral_site", out var ns) && Bool(ns, "reachable");
        b.AppendLine(internet
            ? L.T("Internettet virker fra denne PC (google.com svarer).", "The internet works from this PC (google.com answers).",
                "Internet fungerar från den här datorn (google.com svarar).", "Internett virker fra denne PC-en (google.com svarer).",
                "Das Internet funktioniert von diesem PC aus (google.com antwortet).")
            : "⚠ " + L.T("Denne PC kan ikke nå google.com. Problemet er sandsynligvis din egen internetforbindelse, ikke spillene.",
                "This PC cannot reach google.com. The problem is probably your own internet connection, not the games.",
                "Den här datorn når inte google.com. Problemet är troligen din egen internetanslutning, inte spelen.",
                "Denne PC-en når ikke google.com. Problemet er sannsynligvis din egen internettforbindelse, ikke spillene.",
                "Dieser PC erreicht google.com nicht. Das Problem ist wahrscheinlich deine eigene Internetverbindung, nicht die Spiele."));
        b.AppendLine();
        foreach (var svc in Rows(r, "services"))
        {
            var conns = Rows(svc, "connections").ToList();
            int ok = conns.Count(c => Bool(c, "reachable")), all = conns.Count;
            string reach = ok == all ? L.T("serverne svarer", "the servers answer", "servrarna svarar", "serverne svarer", "die Server antworten")
                : ok == 0 ? "⚠ " + L.T("serverne svarer ikke", "the servers do not answer", "servrarna svarar inte", "serverne svarer ikke", "die Server antworten nicht")
                : "⚠ " + L.T($"{ok} af {all} servere svarer", $"{ok} of {all} servers answer", $"{ok} av {all} servrar svarar", $"{ok} av {all} servere svarer",
                    $"{ok} von {all} Servern antworten");
            string official = "";
            if (svc.TryGetProperty("official_status", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                string label = L.T("officiel status", "official status", "officiell status", "offisiell status", "offizieller Status");
                if (Str(st, "error") is { Length: > 0 })
                    official = L.T("statussiden kunne ikke læses", "the status page could not be read", "statussidan kunde inte läsas",
                        "statussiden kunne ikke leses", "die Statusseite konnte nicht gelesen werden");
                else
                {
                    var bad = Rows(st, "components_with_problems").ToList();
                    var incidents = Rows(st, "open_incidents").ToList();
                    official = bad.Count == 0 && incidents.Count == 0
                        ? $"{label}: " + L.T("alt kører", "all running", "allt fungerar", "alt kjører", "alles läuft")
                        : $"⚠ {label}: " + string.Join("; ", bad.Take(3).Select(c => $"{Str(c, "component")} {StatusText(Str(c, "status"))}")
                            .Concat(incidents.Take(2).Select(i => $"\"{Str(i, "title")}\"")));
                }
            }
            b.AppendLine($"{Str(svc, "name")}: {reach}{(official.Length > 0 ? ", " + official : "")}");
        }
        b.AppendLine();
        b.Append(L.T("Roblox, Minecraft, Steam og Xbox har ingen officiel status, vi kan læse automatisk; for dem testes kun forbindelsen.",
            "Roblox, Minecraft, Steam and Xbox have no official status we can read automatically; for them only the connection is tested.",
            "Roblox, Minecraft, Steam och Xbox har ingen officiell status som vi kan läsa automatiskt; för dem testas bara anslutningen.",
            "Roblox, Minecraft, Steam og Xbox har ingen offisiell status vi kan lese automatisk; for dem testes bare forbindelsen.",
            "Roblox, Minecraft, Steam und Xbox haben keinen offiziellen Status, den wir automatisch lesen können; bei ihnen wird nur die Verbindung getestet."));
        return b.ToString().TrimEnd();
    }

    static string StatusText(string s) => s switch
    {
        _ when s.StartsWith("degraded") => L.T("kører langsomt", "running slowly", "går långsamt", "går tregt", "läuft langsam"),
        _ when s.StartsWith("partial") => L.T("delvist nede", "partly down", "delvis nere", "delvis nede", "teilweise ausgefallen"),
        _ when s.StartsWith("major") => L.T("nede", "down", "nere", "nede", "ausgefallen"),
        _ when s.StartsWith("under maintenance") => L.T("under vedligehold", "under maintenance", "under underhåll", "under vedlikehold", "in Wartung"),
        _ => s,
    };

    // ---- JSON helpers: tolerant, because WMI returns numbers as strings and fields may be missing ----

    static IEnumerable<JsonElement> Rows(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    static IEnumerable<string> Strings(JsonElement r, string name) =>
        Rows(r, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!);

    static string Str(JsonElement r, string name)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    static double? Num(JsonElement r, string name)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    static bool Bool(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>WMI dates look like 20240812000000.000000-000.</summary>
    static DateTime? WmiDate(string s) =>
        s.Length >= 8 && DateTime.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    static string Date(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) : iso;

    static string Gb(double? v) => v is null ? "?" : v.Value.ToString("0.0", L.Culture);

    static string Duration(double hours)
    {
        int h = (int)hours;
        return h >= 24
            ? L.T($"{h / 24} dage og {h % 24} timer", $"{h / 24} days and {h % 24} hours", $"{h / 24} dagar och {h % 24} timmar",
                  $"{h / 24} dager og {h % 24} timer", $"{h / 24} Tage und {h % 24} Stunden")
            : L.T($"{h} timer", $"{h} hours", $"{h} timmar", $"{h} timer", $"{h} Stunden");
    }
}
