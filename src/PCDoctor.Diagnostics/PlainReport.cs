using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PCDoctor.Diagnostics;

/// <summary>
/// Turns tool results (the same JSON the agent gets) into short Danish text for the Diagnostics page,
/// so the page and the agent always see the same facts.
/// </summary>
public static class PlainReport
{
    static readonly CultureInfo Da = CultureInfo.GetCultureInfo("da-DK");

    public static string Hardware(JsonElement r)
    {
        var b = new StringBuilder();
        foreach (var cpu in Rows(r, "cpu"))
            b.AppendLine($"Processor (CPU): {Str(cpu, "Name")} ({Str(cpu, "NumberOfCores")} kerner, {Str(cpu, "NumberOfLogicalProcessors")} tråde)");
        foreach (var gpu in Rows(r, "gpu"))
        {
            string line = $"Grafikkort (GPU): {Str(gpu, "Name")}";
            if (Str(gpu, "DriverVersion") is { Length: > 0 } v)
                line += $", driver {v}";
            if (WmiDate(Str(gpu, "DriverDate")) is { } d)
                line += $" fra {d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}" + (d < DateTime.Now.AddYears(-1) ? " (over et år gammel)" : "");
            b.AppendLine(line);
        }
        var modules = Rows(r, "memory_modules").ToList();
        double ramGb = modules.Sum(m => Num(m, "Capacity") ?? 0) / 1073741824.0;
        if (ramGb == 0)
            ramGb = Rows(r, "computer").Select(c => Num(c, "TotalPhysicalMemory") ?? 0).FirstOrDefault() / 1073741824.0;
        if (ramGb > 0)
        {
            string speed = modules.Select(m => Num(m, "Speed")).FirstOrDefault(s => s > 0) is { } mhz ? $", {mhz:0} MHz" : "";
            b.AppendLine($"Hukommelse (RAM): {ramGb:0} GB" + (modules.Count > 0 ? $" ({modules.Count} moduler{speed})" : ""));
        }
        foreach (var os in Rows(r, "os"))
            b.AppendLine($"Windows: {Str(os, "Caption")}, build {Str(os, "BuildNumber")} ({Str(os, "OSArchitecture")})");
        foreach (var c in Rows(r, "computer"))
            b.AppendLine($"Computer: {Str(c, "Manufacturer")} {Str(c, "Model")}".TrimEnd());
        foreach (var mb in Rows(r, "motherboard"))
            b.AppendLine($"Bundkort: {Str(mb, "Manufacturer")} {Str(mb, "Product")}".TrimEnd());

        var disks = Rows(r, "disks").ToList();
        if (disks.Count > 0)
        {
            b.AppendLine("Diske:");
            foreach (var d in disks)
            {
                double size = (Num(d, "Size") ?? 0) / 1073741824.0, free = (Num(d, "FreeSpace") ?? 0) / 1073741824.0;
                if (size <= 0) continue;
                b.AppendLine($"  {Str(d, "DeviceID")} {size:0} GB, {free:0} GB fri ({100 * free / size:0} %)" + (free / size < 0.10 || free < 10 ? "  ⚠ næsten fuld" : ""));
            }
        }

        var problems = Rows(r, "problem_devices").ToList();
        if (problems.Count == 0 && r.TryGetProperty("problem_devices", out var pd) && pd.ValueKind == JsonValueKind.Array)
            b.AppendLine("Enheder med problemer: ingen");
        else if (problems.Count > 0)
        {
            b.AppendLine($"⚠ Enheder med problemer ({problems.Count}):");
            foreach (var p in problems.Take(15))
                b.AppendLine($"  {Str(p, "Name")}: {DeviceProblem(Str(p, "ConfigManagerErrorCode"))}");
        }
        return b.ToString().TrimEnd();
    }

    static string DeviceProblem(string code) => code switch
    {
        "28" => "mangler driver (kode 28)",
        "22" => "er slået fra (kode 22)",
        "10" => "kan ikke starte (kode 10)",
        "43" => "er stoppet efter en fejl (kode 43)",
        "31" => "driveren virker ikke korrekt (kode 31)",
        _ => $"fejlkode {code}",
    };

    public static string Resources(JsonElement r, JsonElement? processes = null)
    {
        var b = new StringBuilder();
        if (Num(r, "cpu_percent") is { } cpu)
        {
            string gpu = Num(r, "gpu_percent") is { } g ? $"{g:0} %" : "ukendt";
            string disk = Num(r, "disk_active_percent") is { } d ? $"{d:0} % aktiv" : "ukendt";
            b.AppendLine($"CPU: {cpu:0} %    Grafikkort: {gpu}    Disk: {disk}");
            b.AppendLine($"RAM: {Num(r, "ram_percent"):0} % i brug ({Gb(Num(r, "ram_used_gb"))} af {Gb(Num(r, "ram_total_gb"))} GB)");
            if (Str(r, "top_process") is { Length: > 0 } top)
                b.AppendLine($"Bruger mest CPU lige nu: {top} ({Num(r, "top_process_cpu_percent"):0} %)");
        }
        else if (Str(r, "measurement_error") is { Length: > 0 } err)
            b.AppendLine($"Kunne ikke måle CPU/RAM: {err}");
        else
            b.AppendLine("Måling af CPU/GPU/RAM findes kun på Windows.");

        if (Num(r, "uptime_hours") is { } up)
            b.AppendLine($"Tændt siden sidste genstart: {Duration(up)}" + (up > 24 * 7 ? " (en genstart kan hjælpe)" : ""));

        var drives = Rows(r, "drives").ToList();
        if (drives.Count > 0)
        {
            b.AppendLine("Drev:");
            foreach (var d in drives)
                b.AppendLine($"  {Str(d, "drive")} {Gb(Num(d, "free_gb"))} GB fri af {Num(d, "total_gb"):0} GB ({Num(d, "free_percent"):0} %)" +
                             (Bool(d, "low_space") ? "  ⚠ næsten fuld" : ""));
        }

        if (processes is { } p)
        {
            b.AppendLine();
            b.AppendLine("Programmer der bruger mest CPU:");
            foreach (var row in Rows(p, "top_by_cpu").Take(8))
                b.AppendLine($"  {Num(row, "cpu_percent"),5:0.0} %   {Str(row, "name")}");
            b.AppendLine("Programmer der bruger mest hukommelse:");
            foreach (var row in Rows(p, "top_by_memory").Take(8))
                b.AppendLine($"  {Num(row, "memory_mb"),6:0} MB   {Str(row, "name")}");
        }
        return b.ToString().TrimEnd();
    }

    public static string Events(JsonElement r)
    {
        if (Rows(r, "problems").Any() && !Rows(r, "groups").Any())
            return "Kunne ikke læse hændelsesloggen: " + string.Join("; ", Rows(r, "problems").Select(p => Str(p, "error")));

        var groups = Rows(r, "groups").ToList();
        var b = new StringBuilder();
        b.AppendLine(groups.Count == 0
            ? "Ingen fejl i Windows' hændelseslog i perioden. Fint."
            : $"{Num(r, "total_events"):0} fejl i perioden, samlet i {groups.Count} slags (nyeste først):");
        foreach (var g in groups)
        {
            b.AppendLine();
            string when = DateTime.TryParse(Str(g, "last"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("dd-MM HH:mm", CultureInfo.InvariantCulture) : Str(g, "last");
            string count = Num(g, "count") is > 1 and var n ? $"  ×{n:0}" : "";
            string level = Str(g, "level") == "critical" ? "KRITISK " : "";
            b.AppendLine($"{when}  {level}{Str(g, "source")} ({Str(g, "event_id")}){count}");
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
            return "Fandt ingen Minecraft-installation med logfiler for denne Windows-bruger.";

        var b = new StringBuilder();
        b.AppendLine("Minecraft-installationer (senest brugt først):");
        foreach (var i in installs)
        {
            string mods = Num(i, "mods") is > 0 and var m ? $", {m:0} mods" : ", ingen mods";
            int crashes = Rows(i, "files").Count(f => Str(f, "kind") is "crash_report" or "jvm_crash");
            b.AppendLine($"  {Str(i, "launcher")}: {Str(i, "name")}{mods}, senest {Date(Str(i, "last_activity"))}" +
                         (crashes > 0 ? $", {crashes} crash-filer" : ""));
        }

        if (r.TryGetProperty("newest_crash", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            b.AppendLine();
            b.Append(Crash(c));
        }
        else
        {
            b.AppendLine();
            b.Append("Ingen crash-rapporter fundet.");
        }
        return b.ToString().TrimEnd();
    }

    /// <summary>One parsed crash file (the newest_crash or a 'file' read) in plain words.</summary>
    public static string Crash(JsonElement c)
    {
        var b = new StringBuilder();
        b.AppendLine($"Seneste crash: {Date(Str(c, "modified"))}");
        if (!c.TryGetProperty("content", out var x) || x.ValueKind != JsonValueKind.Object)
            return b.AppendLine(Str(c, "error")).ToString();

        if (Str(c, "kind") == "jvm_crash")
        {
            b.AppendLine("Java selv crashede (ikke Minecraft's egen fejlrapport).");
            if (Str(x, "Error") is { Length: > 0 } e) b.AppendLine($"Fejl: {e}");
            if (Str(x, "ProblematicFrame") is { Length: > 0 } f) b.AppendLine($"Fejlede i: {f}");
            if (Bool(x, "GraphicsDriverSuspected"))
                b.AppendLine("⚠ Fejlen skete inde i grafikdriveren. Opdater grafikdriveren fra NVIDIA, AMD eller Intel.");
            return b.ToString();
        }

        if (Str(x, "Description") is { Length: > 0 } d) b.AppendLine($"Beskrivelse: {d}");
        if (Str(x, "Exception") is { Length: > 0 } ex) b.AppendLine($"Fejl: {(ex.Length > 200 ? ex[..200] + "…" : ex)}");
        var suspected = Strings(x, "SuspectedMods").Where(s => !s.Equals("NONE", StringComparison.OrdinalIgnoreCase)).ToList();
        if (suspected.Count > 0) b.AppendLine($"Mistænkte mods: {string.Join(", ", suspected)}");
        var inStack = Strings(x, "ModsInStack").ToList();
        if (inStack.Count > 0) b.AppendLine($"Mods nævnt i fejlen: {string.Join(", ", inStack)}");
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
            return "Computeren er ikke forbundet til noget netværk. Tjek Wi-Fi eller netværkskablet.";
        if (!f.InternetReplies && !f.DnsWorks && !f.HttpsWorks)
            return f.GatewayReplies
                ? "Routeren svarer, men der er ingen forbindelse videre ud på internettet. Genstart routeren; hjælper det ikke, kan fejlen være hos internetudbyderen."
                : "Hverken routeren eller internettet svarer. Tjek Wi-Fi eller kablet, og genstart routeren.";
        if (!f.DnsWorks)
            return "Der er forbindelse til internettet, men navneopslag (DNS) virker ikke, så hjemmesider og spil kan ikke findes. Genstart routeren.";
        if (!f.HttpsWorks)
            return "Internettet svarer, men forbindelsen til hjemmesider (HTTPS) fejlede. Det kan skyldes en firewall, VPN eller antivirus.";

        string s = "Internettet virker.";
        if (f.InternetReplies && f.InternetLossPercent > 0)
            s += $" Men {f.InternetLossPercent:0} % af ping-pakkerne gik tabt, så forbindelsen er ustabil (kan give lag og afbrydelser i spil).";
        if (f.InternetAvgMs is > 100)
            s += $" Svartiden er høj ({f.InternetAvgMs:0} ms), hvilket kan give lag i spil.";
        else if (f.InternetAvgMs is { } ms)
            s += $" Svartid {ms:0} ms.";
        else if (!f.InternetReplies)
            s += " (Ping bliver blokeret på dette netværk, men hjemmesider kan nås.)";
        return s;
    }

    public static string Network(JsonElement gatewayPing, JsonElement internetPing, JsonElement dns, JsonElement tcp)
    {
        var facts = NetworkFacts.From(gatewayPing, internetPing, dns, tcp);
        var b = new StringBuilder();
        b.AppendLine(NetworkVerdict(facts));
        b.AppendLine();
        foreach (var x in Rows(gatewayPing, "results"))
            b.AppendLine(Ping("Router", x));
        foreach (var x in Rows(internetPing, "results"))
            b.AppendLine(Ping("Internet", x));
        foreach (var x in Rows(dns, "lookups"))
            b.AppendLine(x.TryGetProperty("addresses", out _)
                ? $"Navneopslag {Str(x, "name")}: OK ({Num(x, "ms"):0} ms)"
                : $"Navneopslag {Str(x, "name")}: FEJL ({Str(x, "error")})");
        b.AppendLine(Bool(tcp, "connected")
            ? $"Forbindelse til {Str(tcp, "host")}:{Str(tcp, "port")}: OK ({Num(tcp, "ms"):0} ms)"
            : $"Forbindelse til {Str(tcp, "host")}:{Str(tcp, "port")}: FEJL ({Str(tcp, "error")})");
        return b.ToString().TrimEnd();
    }

    static string Ping(string label, JsonElement x)
    {
        if (x.TryGetProperty("error", out var e))
            return $"{label}: {e.GetString()}";
        if (Num(x, "received") is not > 0)
            return $"{label} ({Str(x, "host")}): svarer ikke ({Str(x, "last_status")})";
        string loss = Num(x, "loss_percent") is > 0 and var l ? $", {l:0} % tabt" : "";
        return $"{label} ({Str(x, "host")}): svarer, {Num(x, "avg_ms"):0} ms{loss}";
    }

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

    static string Gb(double? v) => v is null ? "?" : v.Value.ToString("0.0", Da);

    static string Duration(double hours)
    {
        int h = (int)hours;
        return h >= 24 ? $"{h / 24} dage og {h % 24} timer" : $"{h} timer";
    }
}
