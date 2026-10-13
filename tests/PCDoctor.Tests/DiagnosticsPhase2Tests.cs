using System.Text.Json;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Windows;

namespace PCDoctor.Tests;

static class Fixture
{
    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    public static string Text(params string[] parts) => File.ReadAllText(Path(parts));
}

public class MinecraftParsingTests
{
    [Fact]
    public void Forge_crash_report_names_the_suspected_mod()
    {
        var r = MinecraftCrashReport.Parse(Fixture.Text("minecraft", "crash-forge-suspected-mod.txt"));
        Assert.Equal("Ticking entity", r.Description);
        Assert.StartsWith("java.lang.NullPointerException", r.Exception);
        Assert.Contains("Better Pets (betterpets), Version: 1.4.2", r.SuspectedMods);
        Assert.DoesNotContain(r.SuspectedMods, s => s.Contains("Issue tracker"));
        Assert.Equal("1.20.1", r.Details["Minecraft Version"]);
        Assert.Equal("17.0.8, Microsoft", r.Details["Java Version"]);
        Assert.Equal(4, r.StackTop.Count);
    }

    [Fact]
    public void Fabric_mixin_crash_points_at_the_mod_that_owns_the_mixin()
    {
        var r = MinecraftCrashReport.Parse(Fixture.Text("minecraft", "crash-fabric-mixin.txt"));
        Assert.Equal("Initializing game", r.Description);
        Assert.Contains("fancysky", r.ModsInStack);
        Assert.Empty(r.SuspectedMods);
        Assert.Contains("InjectionError", r.CausedBy[^1]);
        Assert.Equal("3 lines (list)", r.Details["Fabric Mods"]);
    }

    [Fact]
    public void Java_crash_inside_a_graphics_driver_is_flagged()
    {
        var r = JvmCrashLog.Parse(Fixture.Text("minecraft", "hs_err_pid1234.log"));
        Assert.True(r.GraphicsDriverSuspected);
        Assert.Contains("atio6axx.dll", r.ProblematicFrame);
        Assert.StartsWith("EXCEPTION_ACCESS_VIOLATION", r.Error);
        Assert.StartsWith("JRE version:", r.JavaVersion);
    }

    [Fact]
    public void Java_crash_in_java_itself_is_not_blamed_on_the_driver()
    {
        var r = JvmCrashLog.Parse("#\n# A fatal error\n#  EXCEPTION_ACCESS_VIOLATION (0xc0000005)\n# Problematic frame:\n# V  [jvm.dll+0x3b2c1a]\n#\n");
        Assert.False(r.GraphicsDriverSuspected);
        Assert.Contains("jvm.dll", r.ProblematicFrame);
    }

    [Fact]
    public void Latest_log_keeps_errors_and_the_end()
    {
        string json = JsonSerializer.Serialize(MinecraftLatestLog.Parse(Fixture.Text("minecraft", "latest.log")));
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal(3, root.GetProperty("problem_line_count").GetInt32());
        Assert.Contains("Stopping!", root.GetProperty("last_lines").EnumerateArray().Last().GetString());
    }
}

/// <summary>Builds a fake user profile with a vanilla .minecraft and a CurseForge instance.</summary>
public sealed class FakeMinecraftHome : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "pcdoctor-mc-" + Guid.NewGuid().ToString("N"));
    public string AppData => Path.Combine(Root, "AppData", "Roaming");
    public string Home => Root;
    public string VanillaCrash = "", ModpackCrash = "", JvmCrash = "", Secret = "";

    public FakeMinecraftHome()
    {
        string vanilla = Path.Combine(AppData, ".minecraft");
        VanillaCrash = Copy("crash-forge-suspected-mod.txt", Path.Combine(vanilla, "crash-reports", "crash-2026-10-13_20.58.12-server.txt"), hoursAgo: 1);
        JvmCrash = Copy("hs_err_pid1234.log", Path.Combine(vanilla, "hs_err_pid1234.log"), hoursAgo: 30);
        Copy("latest.log", Path.Combine(vanilla, "logs", "latest.log"), hoursAgo: 1);
        Directory.CreateDirectory(Path.Combine(vanilla, "mods"));
        File.WriteAllText(Path.Combine(vanilla, "mods", "betterpets-1.4.2.jar"), "");
        File.WriteAllText(Path.Combine(vanilla, "mods", "forge.jar"), "");

        string pack = Path.Combine(Home, "curseforge", "minecraft", "Instances", "Sky Pack");
        ModpackCrash = Copy("crash-fabric-mixin.txt", Path.Combine(pack, "crash-reports", "crash-2026-10-12_18.20.05-client.txt"), hoursAgo: 50);

        Secret = Path.Combine(Root, "secret.txt");
        File.WriteAllText(Secret, "not a minecraft log");
    }

    static string Copy(string fixture, string to, int hoursAgo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(Fixture.Path("minecraft", fixture), to);
        File.SetLastWriteTime(to, DateTime.Now.AddHours(-hoursAgo));
        return to;
    }

    public GamingLogsTool Tool() => new(new MinecraftLocator(AppData, Home));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class GamingLogsToolTests
{
    [Fact]
    public void Finds_vanilla_and_launcher_installs_newest_first()
    {
        using var home = new FakeMinecraftHome();
        var installs = new MinecraftLocator(home.AppData, home.Home).Find();
        Assert.Equal(2, installs.Count);
        Assert.Equal(".minecraft", installs[0].Name);
        Assert.Equal(2, installs[0].ModCount);
        Assert.Equal(new[] { "crash_report", "jvm_crash", "latest_log" }, installs[0].Files.Select(f => f.Kind).Order());
        Assert.Equal("CurseForge", installs[1].Launcher);
        Assert.Equal("Sky Pack", installs[1].Name);
    }

    [Fact]
    public async Task List_summarises_the_newest_crash()
    {
        using var home = new FakeMinecraftHome();
        var r = await home.Tool().RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.False(r.IsError, r.Content);
        var newest = JsonDocument.Parse(r.Content).RootElement.GetProperty("newest_crash");
        Assert.Equal("crash_report", newest.GetProperty("kind").GetString());
        Assert.Equal("Ticking entity", newest.GetProperty("content").GetProperty("Description").GetString());
    }

    [Fact]
    public async Task Reads_a_listed_file()
    {
        using var home = new FakeMinecraftHome();
        var r = await home.Tool().RunAsync(Json.Parse(JsonSerializer.Serialize(new { file = home.ModpackCrash })), CancellationToken.None);
        Assert.False(r.IsError, r.Content);
        Assert.Contains("fancysky", r.Content);
    }

    [Fact]
    public async Task Refuses_files_it_did_not_find_itself()
    {
        using var home = new FakeMinecraftHome();
        var tool = home.Tool();
        string sneaky = Path.Combine(Path.GetDirectoryName(home.VanillaCrash)!, "..", "..", "..", "..", "secret.txt");
        foreach (string path in new[] { home.Secret, sneaky, "C:\\Windows\\System32\\config\\SAM", "" })
        {
            var r = await tool.RunAsync(Json.Parse(JsonSerializer.Serialize(new { file = path })), CancellationToken.None);
            Assert.True(r.IsError, path);
            Assert.DoesNotContain("not a minecraft log", r.Content);
        }
    }

    [Fact]
    public async Task A_huge_crash_report_is_read_from_the_top_so_the_cause_is_kept()
    {
        using var home = new FakeMinecraftHome();
        // A big modpack's crash report: the cause is at the top, then megabytes of mod list.
        File.AppendAllText(home.VanillaCrash, string.Concat(Enumerable.Repeat("\t\tsome-mod-1.0.jar |Some Mod |somemod |1.0 |DONE\n", 60_000)));
        Assert.True(new FileInfo(home.VanillaCrash).Length > 2_500_000);
        var r = await home.Tool().RunAsync(Json.Parse(JsonSerializer.Serialize(new { file = home.VanillaCrash })), CancellationToken.None);
        Assert.False(r.IsError, r.Content[..Math.Min(500, r.Content.Length)]);
        var content = JsonDocument.Parse(r.Content).RootElement.GetProperty("content");
        Assert.Equal("Ticking entity", content.GetProperty("Description").GetString());
    }

    [Fact]
    public async Task No_minecraft_is_an_answer_not_an_error()
    {
        string empty = Path.Combine(Path.GetTempPath(), "pcdoctor-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var r = await new GamingLogsTool(new MinecraftLocator(empty, empty)).RunAsync(Json.Parse("{}"), CancellationToken.None);
            Assert.False(r.IsError);
            Assert.Contains("No Minecraft installation", r.Content);
            Assert.StartsWith("Fandt ingen Minecraft", PlainReport.Minecraft(JsonDocument.Parse(r.Content).RootElement));
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public async Task Diagnostics_page_text_names_the_suspected_mod()
    {
        using var home = new FakeMinecraftHome();
        var r = await home.Tool().RunAsync(Json.Parse("{}"), CancellationToken.None);
        string text = PlainReport.Minecraft(JsonDocument.Parse(r.Content).RootElement);
        Assert.Contains("Minecraft Launcher: .minecraft, 2 mods", text);
        Assert.Contains("CurseForge: Sky Pack, ingen mods", text);
        Assert.Contains("Beskrivelse: Ticking entity", text);
        Assert.Contains("Mistænkte mods: Better Pets (betterpets), Version: 1.4.2", text);
    }

    [Fact]
    public async Task Diagnostics_page_text_blames_the_graphics_driver_for_a_java_crash()
    {
        using var home = new FakeMinecraftHome();
        var r = await home.Tool().RunAsync(Json.Parse(JsonSerializer.Serialize(new { file = home.JvmCrash })), CancellationToken.None);
        string text = PlainReport.Crash(JsonDocument.Parse(r.Content).RootElement);
        Assert.Contains("atio6axx.dll", text);
        Assert.Contains("grafikdriveren", text);
    }
}

public class EventLogTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 13, 20, 0, 0, TimeSpan.FromHours(2));

    static LogEvent Ev(string provider, int id, int minutes, string message, string log = "Application") =>
        new(log, T0.AddMinutes(minutes), "error", provider, id, message);

    [Fact]
    public void Repeats_are_grouped_and_newest_comes_first()
    {
        var events = new[]
        {
            Ev("Application Error", 1000, 0, "Faulting application name: javaw.exe, version: 8.0.3510.10\nFaulting module: atio6axx.dll"),
            Ev("Application Error", 1000, 30, "Faulting application name: javaw.exe, version: 8.0.3910.12\nFaulting module: atio6axx.dll"),
            Ev("Application Error", 1000, 10, "Faulting application name: Discord.exe, version: 1.0.9\nFaulting module: ntdll.dll"),
            Ev("Microsoft-Windows-Kernel-Power", 41, 60, "The system has rebooted without cleanly shutting down first.", "System"),
        };
        var groups = EventSummary.Group(events);

        Assert.Equal(3, groups.Count);
        Assert.Equal(41, groups[0].Id);
        Assert.Contains("uden at lukke korrekt ned", groups[0].Meaning);
        var java = groups.Single(g => g.Message!.Contains("javaw"));
        Assert.Equal(2, java.Count);
        Assert.Equal(T0, java.First);
        Assert.Equal(T0.AddMinutes(30), java.Last);
        Assert.Contains("8.0.3910.12", java.Message); // the newest message is kept
        Assert.Contains("crashede", java.Meaning);
    }

    [Fact]
    public void Long_messages_are_cut_and_newest_comes_first()
    {
        var events = Enumerable.Range(0, 50).Select(i => Ev($"Source{i}", i, i, new string('x', 2000))).ToList();
        var groups = EventSummary.Group(events);
        Assert.Equal(50, groups.Count);
        Assert.All(groups, g => Assert.True(g.Message!.Length < 450));
        Assert.Equal("Source49", groups[0].Provider);
    }

    [Fact]
    public void Different_devices_stay_apart_but_times_and_ids_do_not_split_groups()
    {
        var events = new[]
        {
            Ev("disk", 7, 0, @"The device, \Device\Harddisk0\DR0, has a bad block.", "System"),
            Ev("disk", 7, 1, @"The device, \Device\Harddisk1\DR1, has a bad block.", "System"),
            Ev("disk", 7, 2, @"The device, \Device\Harddisk1\DR1, has a bad block.", "System"),
            Ev("EventLog", 6008, 3, "The previous system shutdown at 10:23:11 on \u200e13-\u200e10-\u200e2026 was unexpected.", "System"),
            Ev("EventLog", 6008, 4, "The previous system shutdown at 22:01:45 on \u200e14-\u200e10-\u200e2026 was unexpected.", "System"),
            Ev("Application Error", 1000, 5, "Faulting application name: javaw.exe, version: 8.0.3510.10, time stamp: 0x64b0c5f4"),
            Ev("Application Error", 1000, 6, "Faulting application name: javaw.exe, version: 8.0.3910.12, time stamp: 0x65aa0011"),
        };
        var groups = EventSummary.Group(events);
        Assert.Equal(2, groups.Count(g => g.Id == 7));
        Assert.Equal(2, groups.Single(g => g.Id == 7 && g.Message!.Contains("Harddisk1")).Count);
        Assert.Equal(2, groups.Single(g => g.Id == 6008).Count);
        Assert.Equal(2, groups.Single(g => g.Id == 1000).Count);
    }

    [Fact]
    public void Query_asks_for_errors_by_default_and_warnings_on_request()
    {
        var from = new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.FromHours(2));
        string q = EventLogTool.BuildQuery(from, from.AddHours(1), false, null);
        Assert.Equal("*[System[(Level=1 or Level=2) and TimeCreated[@SystemTime>='2026-10-13T18:00:00.000Z' and @SystemTime<='2026-10-13T19:00:00.000Z']]]", q);
        Assert.Contains("Level=3", EventLogTool.BuildQuery(from, from, true, null));
        Assert.Contains("Provider[@Name='Application Error'] and ", EventLogTool.BuildQuery(from, from, false, "Application Error"));
    }

    [Theory]
    [InlineData("x' or '1'='1")]
    [InlineData("a]] | *[System")]
    [InlineData("<script>")]
    public async Task Provider_filter_rejects_anything_that_could_change_the_query(string provider)
    {
        var r = await new EventLogTool().RunAsync(Json.Parse(JsonSerializer.Serialize(new { provider })), CancellationToken.None);
        Assert.True(r.IsError);
    }

    [Fact]
    public void Diagnostics_page_text_explains_events()
    {
        var json = JsonDocument.Parse("""
            {"total_events":4,"groups":[
              {"log":"System","level":"critical","source":"Microsoft-Windows-Kernel-Power","event_id":41,"count":1,"first":"2026-10-13T21:00:00","last":"2026-10-13T21:00:00","meaning":"PC'en genstartede uden at lukke korrekt ned.","message":"The system has rebooted without cleanly shutting down first."},
              {"log":"Application","level":"error","source":"Application Error","event_id":1000,"count":3,"first":"2026-10-13T20:00:00","last":"2026-10-13T20:30:00","meaning":"Et program crashede.","message":"Faulting application name: javaw.exe\r\nmore"}]}
            """).RootElement;
        string text = PlainReport.Events(json);
        Assert.Contains("4 fejl i perioden, samlet i 2 slags", text);
        Assert.Contains("13-10 21:00  KRITISK Microsoft-Windows-Kernel-Power (41)", text);
        Assert.Contains("Application Error (1000)  ×3", text);
        Assert.Contains("\"Faulting application name: javaw.exe\"", text);
        Assert.DoesNotContain("udeladt", text);
        string cut = PlainReport.Events(JsonDocument.Parse("""
            {"total_events":90,"total_groups":30,"groups_left_out":28,"groups":[
              {"source":"disk","event_id":7,"count":1,"last":"2026-10-13T21:00:00"},
              {"source":"disk","event_id":51,"count":1,"last":"2026-10-13T20:00:00"}]}
            """).RootElement);
        Assert.Contains("samlet i 30 slags", cut);
        Assert.Contains("Viser de 2 nyeste slags; 28 ældre slags er udeladt.", cut);
        Assert.Contains("Ingen fejl", PlainReport.Events(JsonDocument.Parse("""{"total_events":0,"groups":[]}""").RootElement));
    }

    [WindowsFact]
    public async Task Reads_the_real_event_log()
    {
        var r = await new EventLogTool().RunAsync(Json.Parse("""{"hours":72,"include_warnings":true}"""), CancellationToken.None);
        Assert.False(r.IsError, r.Content);
        var root = JsonDocument.Parse(r.Content).RootElement;
        Assert.Equal(JsonValueKind.Array, root.GetProperty("groups").ValueKind);
        Assert.False(root.TryGetProperty("problems", out var p) && p.ValueKind == JsonValueKind.Array, r.Content);
    }

    [WindowsFact]
    public async Task Around_a_time_reads_a_small_window()
    {
        var r = await new EventLogTool().RunAsync(Json.Parse("""{"around":"2026-01-01T12:00:00","minutes":5}"""), CancellationToken.None);
        Assert.False(r.IsError, r.Content);
        Assert.Equal("2026-01-01T11:55:00", JsonDocument.Parse(r.Content).RootElement.GetProperty("from").GetString());
    }
}

public class ResourcesAndHardwareReportTests
{
    sealed class FixedSampler : IPerfSampler, IDisposable
    {
        public int Reads;
        public bool Disposed;
        public PerfSample Read()
        {
            Reads++;
            return new PerfSample(0, 42, 7, 63, 10.1, 3, "javaw", 35);
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Resources_reads_twice_and_reports_the_second_reading()
    {
        var sampler = new FixedSampler();
        var r = await new ResourcesTool(() => sampler).RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.False(r.IsError);
        Assert.Equal(2, sampler.Reads);
        Assert.True(sampler.Disposed);
        var root = JsonDocument.Parse(r.Content).RootElement;
        Assert.Equal(42, root.GetProperty("cpu_percent").GetDouble());
        Assert.Equal("javaw", root.GetProperty("top_process").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("drives").ValueKind);

        string text = PlainReport.Resources(root);
        Assert.Contains("CPU: 42 %", text);
        Assert.Contains("Bruger mest CPU lige nu: javaw (35 %)", text);
    }

    [Fact]
    public async Task Resources_without_counters_still_reports_drives_and_uptime()
    {
        var r = await new ResourcesTool(() => null).RunAsync(Json.Parse("{}"), CancellationToken.None);
        var root = JsonDocument.Parse(r.Content).RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpu_percent").ValueKind);
        Assert.True(root.GetProperty("uptime_hours").GetDouble() >= 0);
        Assert.Contains("kun på Windows", PlainReport.Resources(root));
    }

    [Fact]
    public void Hardware_text_flags_old_drivers_full_disks_and_broken_devices()
    {
        var json = JsonDocument.Parse("""
            {"cpu":[{"Name":"AMD Ryzen 5 5600X 6-Core Processor","NumberOfCores":"6","NumberOfLogicalProcessors":"12"}],
             "gpu":[{"Name":"NVIDIA GeForce GTX 1060 6GB","DriverVersion":"31.0.15.1694","DriverDate":"20220801000000.000000-000"}],
             "computer":[{"Manufacturer":"ASUS","Model":"System Product Name","TotalPhysicalMemory":"17112760320"}],
             "memory_modules":[{"Capacity":"8589934592","Speed":"3200"},{"Capacity":"8589934592","Speed":"3200"}],
             "os":[{"Caption":"Microsoft Windows 11 Home","BuildNumber":"22631","OSArchitecture":"64-bit"}],
             "disks":[{"DeviceID":"C:","Size":"511101108224","FreeSpace":"21474836480"}],
             "motherboard":[{"Manufacturer":"ASUSTeK","Product":"PRIME B550M-A"}],
             "problem_devices":[{"Name":"PCI Data Acquisition and Signal Processing Controller","ConfigManagerErrorCode":"28"}]}
            """).RootElement;
        string text = PlainReport.Hardware(json);
        Assert.Contains("AMD Ryzen 5 5600X 6-Core Processor (6 kerner, 12 tråde)", text);
        Assert.Contains("driver 31.0.15.1694 fra 01-08-2022 (over et år gammel)", text);
        Assert.Contains("Hukommelse (RAM): 16 GB (2 moduler, 3200 MHz)", text);
        Assert.Contains("C: 476 GB, 20 GB fri (4 %)  ⚠ næsten fuld", text);
        Assert.Contains("mangler driver (kode 28)", text);
    }

    [WindowsFact]
    public async Task Hardware_tool_lists_problem_devices()
    {
        var r = await new HardwareTool().RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.Contains("\"problem_devices\"", r.Content);
        Assert.False(string.IsNullOrWhiteSpace(PlainReport.Hardware(JsonDocument.Parse(r.Content).RootElement)));
    }
}

public class NetworkVerdictTests
{
    static PlainReport.NetworkFacts Facts(bool gwFound = true, bool gw = true, bool inet = true, double loss = 0, double? ms = 15, bool dns = true, bool https = true) =>
        new(gwFound, gw, inet, loss, ms, dns, https);

    [Fact]
    public void All_good() => Assert.Equal("Internettet virker. Svartid 15 ms.", PlainReport.NetworkVerdict(Facts()));

    [Fact]
    public void Not_connected_at_all() => Assert.Contains("ikke forbundet til noget netværk", PlainReport.NetworkVerdict(Facts(gwFound: false, gw: false, inet: false, loss: 100, ms: null, dns: false, https: false)));

    [Fact]
    public void Router_answers_but_internet_is_down() => Assert.Contains("Routeren svarer, men der er ingen forbindelse videre", PlainReport.NetworkVerdict(Facts(inet: false, loss: 100, ms: null, dns: false, https: false)));

    [Fact]
    public void Dns_broken() => Assert.Contains("navneopslag (DNS) virker ikke", PlainReport.NetworkVerdict(Facts(dns: false, https: false)));

    [Fact]
    public void Https_blocked() => Assert.Contains("firewall, VPN eller antivirus", PlainReport.NetworkVerdict(Facts(https: false)));

    [Fact]
    public void Unstable_and_slow()
    {
        string v = PlainReport.NetworkVerdict(Facts(loss: 25, ms: 180));
        Assert.Contains("25 % af ping-pakkerne gik tabt", v);
        Assert.Contains("Svartiden er høj (180 ms)", v);
    }

    [Fact]
    public void Ping_blocked_but_web_works() => Assert.Contains("Ping bliver blokeret", PlainReport.NetworkVerdict(Facts(gw: false, inet: false, loss: 100, ms: null)));

    [Fact]
    public void Facts_are_read_from_the_tool_results()
    {
        var gw = Json.Parse("""{"results":[{"host":"192.168.1.1","sent":2,"received":2,"loss_percent":0,"avg_ms":2}]}""");
        var inet = Json.Parse("""{"results":[{"host":"1.1.1.1","sent":4,"received":3,"loss_percent":25,"avg_ms":20},{"host":"8.8.8.8","sent":4,"received":4,"loss_percent":0,"avg_ms":18}]}""");
        var dns = Json.Parse("""{"lookups":[{"name":"www.google.com","addresses":["1.2.3.4"],"ms":12}]}""");
        var tcp = Json.Parse("""{"host":"www.google.com","port":443,"connected":true,"ms":30}""");
        var f = PlainReport.NetworkFacts.From(gw, inet, dns, tcp);
        Assert.Equal(Facts(loss: 12.5, ms: 18), f);
        string text = PlainReport.Network(gw, inet, dns, tcp);
        Assert.Contains("Router (192.168.1.1): svarer, 2 ms", text);
        Assert.Contains("Internet (1.1.1.1): svarer, 20 ms, 25 % tabt", text);
        Assert.Contains("Forbindelse til www.google.com:443: OK", text);
    }

    [Fact]
    public void No_gateway_is_read_as_not_connected()
    {
        var gw = Json.Parse("""{"results":[{"host":"gateway","error":"No default gateway found"}]}""");
        var inet = Json.Parse("""{"results":[{"host":"1.1.1.1","sent":4,"received":0,"loss_percent":100}]}""");
        var dns = Json.Parse("""{"lookups":[{"name":"www.google.com","error":"HostNotFound"}]}""");
        var tcp = Json.Parse("""{"host":"www.google.com","port":443,"connected":false,"error":"HostNotFound"}""");
        Assert.Contains("ikke forbundet til noget netværk", PlainReport.Network(gw, inet, dns, tcp));
    }
}
