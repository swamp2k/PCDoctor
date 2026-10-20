using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using PCDoctor.Agent;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.Tests;

/// <summary>Builds mod jars the way Fabric, Quilt, Forge and NeoForge write them.</summary>
static class Jar
{
    public static void Write(string path, params (string Name, string Content)[] entries) =>
        File.WriteAllBytes(path, Bytes(entries));

    public static byte[] Bytes(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    /// <summary>A jar with another jar inside it (Fabric "jar-in-jar").</summary>
    public static void WriteWithNested(string path, (string Name, string Content) meta, string nestedName, byte[] nested)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry(meta.Name).Open())) w.Write(meta.Content);
        using var s = zip.CreateEntry(nestedName).Open();
        s.Write(nested);
    }

    public static string Fabric(string id, string version, params string[] depends) =>
        JsonSerializer.Serialize(new { schemaVersion = 1, id, version, name = id + " mod", depends = depends.ToDictionary(d => d, _ => "*") });
}

public sealed class MinecraftModsTests : IDisposable
{
    readonly string _game = Path.Combine(Path.GetTempPath(), "pcd-mods-" + Guid.NewGuid().ToString("N"));
    string Mods => Path.Combine(_game, "mods");

    public MinecraftModsTests() => Directory.CreateDirectory(Mods);

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch (IOException) { }
    }

    [Fact]
    public void Fabric_folder_finds_missing_duplicate_wrong_loader_and_broken_mods()
    {
        // Fabric API bundles its modules as nested jars.
        Jar.WriteWithNested(Path.Combine(Mods, "fabric-api-0.92.jar"), ("fabric.mod.json", Jar.Fabric("fabric-api", "0.92")),
            "META-INF/jars/fabric-lifecycle-events-v1.jar", Jar.Bytes(("fabric.mod.json", Jar.Fabric("fabric-lifecycle-events-v1", "2.2"))));
        Jar.Write(Path.Combine(Mods, "skymod.jar"), ("fabric.mod.json", Jar.Fabric("skymod", "1.0", "minecraft", "fabricloader", "fabric-api", "cloth-config")));
        Jar.Write(Path.Combine(Mods, "events.jar"), ("fabric.mod.json", Jar.Fabric("eventsmod", "1.0", "fabric-lifecycle-events-v1", "fabric-networking-api-v1")));
        Jar.Write(Path.Combine(Mods, "sodium-0.5.jar"), ("fabric.mod.json", Jar.Fabric("sodium", "0.5")));
        Jar.Write(Path.Combine(Mods, "sodium-0.6.jar"), ("fabric.mod.json", Jar.Fabric("sodium", "0.6")));
        Jar.Write(Path.Combine(Mods, "jei-forge.jar"), ("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"jei\"\nversion=\"15.2\"\n"));
        File.WriteAllText(Path.Combine(Mods, "broken.jar"), "this is not a zip");
        Jar.Write(Path.Combine(Mods, "old.jar.disabled"), ("fabric.mod.json", Jar.Fabric("oldmod", "0.1", "nothing-here")));

        var r = MinecraftMods.Read(_game);

        Assert.Equal("fabric", r.Loader);
        Assert.Equal(1, r.DisabledCount);
        Assert.Contains(r.Problems, p => p.Kind == "missing_dependency" && p.Mod == "skymod" && p.Detail.Contains("cloth-config"));
        // fabric-api itself, its nested modules and Fabric modules we cannot see are all fine.
        Assert.DoesNotContain(r.Problems, p => p.Kind == "missing_dependency" && p.Detail.Contains("fabric"));
        Assert.Contains(r.Problems, p => p.Kind == "duplicate" && p.Mod == "sodium" && p.Detail.Contains("sodium-0.5.jar") && p.Detail.Contains("sodium-0.6.jar"));
        Assert.Contains(r.Problems, p => p.Kind == "wrong_loader" && p.Mod == "jei-forge.jar");
        Assert.Contains(r.Problems, p => p.Kind == "unreadable" && p.Mod == "broken.jar");
        // A switched-off mod is ignored by the game, so its missing dependency is not a problem.
        Assert.DoesNotContain(r.Problems, p => p.Detail.Contains("nothing-here"));
    }

    [Fact]
    public void Forge_and_NeoForge_tomls_give_ids_versions_and_required_mods()
    {
        Jar.Write(Path.Combine(Mods, "create.jar"),
            ("META-INF/mods.toml", """
                modLoader="javafml"
                loaderVersion="[47,)"
                [[mods]]
                modId="create"
                version="${file.jarVersion}"
                displayName="Create"
                description='''
                [[dependencies.fake]]
                modId="ignored"
                '''
                [[dependencies.create]]
                    modId="forge"
                    mandatory=true
                [[dependencies.create]]
                    modId="flywheel"
                    mandatory=true
                [[dependencies.create]]
                    modId="jei"
                    mandatory=false
                """),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nImplementation-Version: 0.5.1.f\r\n"));
        Jar.Write(Path.Combine(Mods, "neo.jar"),
            ("META-INF/neoforge.mods.toml", """
                [[mods]]
                modId = "neomod" # comment
                version = "2.0"
                [[dependencies.neomod]]
                modId = "geckolib"
                type = "required"
                [[dependencies.neomod]]
                modId = "jade"
                type = "optional"
                """));

        var create = MinecraftMods.ReadJar(Path.Combine(Mods, "create.jar"));
        Assert.Equal(("create", "Create", "0.5.1.f", "forge"), (create.Id, create.Name, create.Version, create.Loader));
        Assert.Equal(["forge", "flywheel"], create.Requires);

        var neo = MinecraftMods.ReadJar(Path.Combine(Mods, "neo.jar"));
        Assert.Equal(("neomod", "2.0", "neoforge"), (neo.Id, neo.Version, neo.Loader));
        Assert.Equal(["geckolib"], neo.Requires);

        var r = MinecraftMods.Read(_game);
        Assert.Contains(r.Problems, p => p.Kind == "missing_dependency" && p.Detail.Contains("flywheel"));
        Assert.Contains(r.Problems, p => p.Kind == "missing_dependency" && p.Detail.Contains("geckolib"));
        Assert.DoesNotContain(r.Problems, p => p.Detail.Contains("'forge'") || p.Detail.Contains("jade") || p.Detail.Contains("ignored"));
    }

    [Fact]
    public void Quilt_loads_fabric_mods_and_old_forge_mcmod_info_is_read()
    {
        Jar.Write(Path.Combine(Mods, "q.jar"), ("quilt.mod.json",
            """{"schema_version":1,"quilt_loader":{"id":"qmod","version":"1.0","metadata":{"name":"Q Mod"},"depends":["quilt_loader",{"id":"qsl","optional":true},{"id":"needed"}]}}"""));
        Jar.Write(Path.Combine(Mods, "f.jar"), ("fabric.mod.json", Jar.Fabric("fmod", "1.0")));
        Jar.Write(Path.Combine(Mods, "needed.jar"), ("fabric.mod.json", Jar.Fabric("needed", "1.0")));

        var r = MinecraftMods.Read(_game);
        Assert.Equal("quilt", r.Loader);
        Assert.Empty(r.Problems);
        Assert.Equal("Q Mod", r.Mods.Single(m => m.Id == "qmod").Name);

        string old = Path.Combine(Mods, "oldforge.jar");
        Jar.Write(old, ("mcmod.info", """[{"modid":"ic2","name":"IndustrialCraft 2","version":"2.8","requiredMods":["forge@[14.23,)"]}]"""));
        var info = MinecraftMods.ReadJar(old);
        Assert.Equal(("ic2", "IndustrialCraft 2", "forge"), (info.Id, info.Name, info.Loader));
    }

    [Fact]
    public async Task Tool_checks_the_newest_install_with_mods_and_refuses_other_folders()
    {
        using var home = new FakeMinecraftHome();
        var tool = new MinecraftModsTool(new MinecraftLocator(home.AppData, home.Home));

        var r = await tool.RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.False(r.IsError);
        var root = JsonDocument.Parse(r.Content).RootElement;
        Assert.Equal(".minecraft", root.GetProperty("name").GetString());
        // The fake install's jars are empty files, so both are reported as damaged.
        Assert.Equal(2, root.GetProperty("problems").EnumerateArray().Count(p => p.GetProperty("Kind").GetString() == "unreadable"));
        Assert.Contains("Ødelagt fil", PlainReport.Mods(root));

        var refused = await tool.RunAsync(Json.Parse(JsonSerializer.Serialize(new { folder = home.Root })), CancellationToken.None);
        Assert.True(refused.IsError);
    }
}

public sealed class OtherGameLogsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "pcd-games-" + Guid.NewGuid().ToString("N"));
    string Local => Path.Combine(_root, "Local");
    string LocalLow => Path.Combine(_root, "LocalLow");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    string Copy(string fixture, string to, int hoursAgo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(Fixture.Path("games", fixture), to);
        File.SetLastWriteTime(to, DateTime.Now.AddHours(-hoursAgo));
        return to;
    }

    [Fact]
    public async Task Finds_Fortnite_Roblox_and_Unity_logs_and_reads_the_newest()
    {
        string fortnite = Copy("fortnite-gpu-crash.log", Path.Combine(Local, "FortniteGame", "Saved", "Logs", "FortniteGame.log"), 1);
        Copy("roblox-disconnect.log", Path.Combine(Local, "Roblox", "logs", "0.650_20261012T180001Z_Player_1a2b_last.log"), 20);
        string unity = Copy("unity-out-of-memory.log", Path.Combine(LocalLow, "Cozy Studio", "Cozy Farm", "Player.log"), 5);
        Directory.CreateDirectory(Path.Combine(Local, "Google", "Chrome")); // not a game
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "private");

        var tool = new OtherGameLogsTool(new OtherGamesLocator(Local, LocalLow));
        var r = await tool.RunAsync(Json.Parse("{}"), CancellationToken.None);
        var root = JsonDocument.Parse(r.Content).RootElement;

        Assert.Equal(["Fortnite", "Cozy Farm", "Roblox"], root.GetProperty("games").EnumerateArray().Select(g => g.GetProperty("name").GetString()));
        var newest = root.GetProperty("newest_log");
        Assert.Equal("Fortnite", newest.GetProperty("game").GetString());
        Assert.Contains("graphics card", newest.GetProperty("content").GetProperty("warning_signs")[0].GetProperty("hint").GetString());
        Assert.Contains("Grafikkortet", PlainReport.OtherGames(root));

        var read = await tool.RunAsync(Json.Parse(JsonSerializer.Serialize(new { file = unity })), CancellationToken.None);
        Assert.Contains("ran out of memory", read.Content);

        var refused = await tool.RunAsync(Json.Parse(JsonSerializer.Serialize(new { file = Path.Combine(_root, "secret.txt") })), CancellationToken.None);
        Assert.True(refused.IsError);
        Assert.DoesNotContain("private", refused.Content);
        _ = fortnite;
    }

    [Fact]
    public async Task Xbox_app_games_are_found_by_package_logs_and_crash_reports()
    {
        string logs = Path.Combine(Local, "Packages", "Contoso.CozyQuest_8wekyb3d8bbwe", "LocalState", "Saved", "Logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "Game.log"), "LogWindows: Error: boom");
        string wer = Path.Combine(Local, "Microsoft", "Windows", "WER", "ReportArchive", "AppCrash_x");
        Directory.CreateDirectory(wer);
        File.WriteAllText(Path.Combine(wer, "Report.wer"), "AppName=Halo.exe\r\nAppPath=D:\\XboxGames\\Halo\\Content\\Halo.exe\r\n");

        var games = new OtherGamesLocator(Local, LocalLow, Path.Combine(_root, "pd")).Find();
        Assert.Contains(games, g => g.Name == "CozyQuest" && g.Format == "unreal");
        Assert.Contains(games, g => g.Name == "Halo" && g.Format == "wer");
    }

    [Fact]
    public void Games_in_a_custom_named_xbox_folder_are_found()
    {
        string gamingRoot = Path.Combine(_root, "xbox games");
        string logs = Path.Combine(gamingRoot, "RuneScape- Dragonwilds", "Content", "RSDragonwilds", "Saved", "Logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "RSDragonwilds.log"), "LogWindows: Error: boom");

        var games = new OtherGamesLocator(Local, LocalLow, Path.Combine(_root, "pd"), [gamingRoot]).Find();
        Assert.Contains(games, g => g.Name == "RuneScape- Dragonwilds" && g.Files.Count == 1);
    }

    [Fact]
    public async Task No_games_gives_a_plain_note()
    {
        var r = await new OtherGameLogsTool(new OtherGamesLocator(Local, LocalLow)).RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.False(r.IsError);
        Assert.Contains("No logs", r.Content);
    }
}

/// <summary>Answers HTTP requests from a table of URL prefixes; anything else is a 404.</summary>
sealed class FakeWeb(Dictionary<string, string> pages) : HttpMessageHandler
{
    public readonly List<string> Requested = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.ToString();
        Requested.Add(url);
        var hit = pages.FirstOrDefault(p => url.StartsWith(p.Key));
        return Task.FromResult(hit.Key is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hit.Value, Encoding.UTF8, "application/json") });
    }
}

public class ServiceStatusTests
{
    static ServiceStatusTool Tool(Func<string, bool> reachable, out FakeWeb web)
    {
        web = new FakeWeb(new() { ["https://status.epicgames.com/api/v2/summary.json"] = Fixture.Text("status", "epic-summary.json") });
        return new ServiceStatusTool(new HttpClient(web), (host, _) => Task.FromResult((reachable(host), 12L, reachable(host) ? null : "TimedOut")));
    }

    [Fact]
    public async Task Fortnite_outage_is_read_from_Epics_status_page()
    {
        var tool = Tool(_ => true, out var web);
        var r = await tool.RunAsync(Json.Parse("""{"service":"fortnite"}"""), CancellationToken.None);
        var svc = JsonDocument.Parse(r.Content).RootElement.GetProperty("services")[0];
        var official = svc.GetProperty("official_status");

        Assert.False(official.GetProperty("all_checked_components_ok").GetBoolean());
        var bad = official.GetProperty("components_with_problems").EnumerateArray().Select(c => c.GetProperty("component").GetString()).ToList();
        Assert.Contains("Login", bad);
        Assert.Contains("Matchmaking", bad);
        Assert.Equal("Fortnite login issues", official.GetProperty("open_incidents")[0].GetProperty("title").GetString());
        Assert.Single(web.Requested);
    }

    [Fact]
    public async Task Rocket_League_on_the_same_page_is_fine()
    {
        var r = await Tool(_ => true, out _).RunAsync(Json.Parse("""{"service":"rocket-league"}"""), CancellationToken.None);
        var official = JsonDocument.Parse(r.Content).RootElement.GetProperty("services")[0].GetProperty("official_status");
        Assert.True(official.GetProperty("all_checked_components_ok").GetBoolean());
        Assert.Empty(official.GetProperty("open_incidents").EnumerateArray());
    }

    [Fact]
    public async Task All_services_read_the_shared_page_once_and_tell_our_internet_from_theirs()
    {
        var tool = Tool(host => host != "www.google.com", out var web);
        var r = await tool.RunAsync(Json.Parse("{}"), CancellationToken.None);
        var root = JsonDocument.Parse(r.Content).RootElement;

        Assert.Equal(ServiceStatusTool.Services.Length, root.GetProperty("services").GetArrayLength());
        Assert.False(root.GetProperty("neutral_site").GetProperty("reachable").GetBoolean());
        // Epic's page is shared by four services; Discord's is not in the fake web and fails politely.
        Assert.Equal(1, web.Requested.Count(u => u.Contains("epicgames")));
        var discord = root.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("service").GetString() == "discord");
        Assert.Contains("404", discord.GetProperty("official_status").GetProperty("error").GetString());
        var roblox = root.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("service").GetString() == "roblox");
        Assert.Equal(JsonValueKind.Null, roblox.GetProperty("official_status").ValueKind);

        string text = PlainReport.Services(root);
        Assert.Contains("din egen internetforbindelse", text);
        Assert.Contains("Fortnite: serverne svarer, ⚠ officiel status", text);
    }

    [Fact]
    public async Task Unknown_service_is_refused()
    {
        var r = await Tool(_ => true, out _).RunAsync(Json.Parse("""{"service":"evil.example.com"}"""), CancellationToken.None);
        Assert.True(r.IsError);
    }
}

public class GameRequirementsTests
{
    static PcFacts Pc(double ramGb, double freeGb = 200) => new(ramGb, freeGb, "C:\\", 8, true, "Windows 11");

    static async Task<JsonElement> Run(GameRequirementsTool tool, string game)
    {
        var r = await tool.RunAsync(Json.Parse(JsonSerializer.Serialize(new { game })), CancellationToken.None);
        Assert.False(r.IsError);
        return JsonDocument.Parse(r.Content).RootElement;
    }

    static string Verdict(JsonElement root, string part) =>
        root.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("part").GetString() == part).GetProperty("verdict").GetString()!;

    [Theory]
    [InlineData(4, "below minimum")]
    [InlineData(7.9, "meets minimum, below recommended")]
    [InlineData(15.8, "meets recommended")]
    public async Task Fortnite_RAM_is_compared_with_the_built_in_requirements(double ram, string verdict)
    {
        var web = new FakeWeb([]);
        var root = await Run(new GameRequirementsTool(new HttpClient(web), () => Pc(ram)), "fortnite");
        Assert.Equal("Fortnite", root.GetProperty("game").GetString());
        Assert.Equal(verdict, Verdict(root, "RAM"));
        Assert.StartsWith("https://www.epicgames.com/", root.GetProperty("source").GetString());
        Assert.Empty(web.Requested); // built in: no lookup
    }

    [Fact]
    public async Task Roblox_storage_in_MB_is_understood()
    {
        var root = await Run(new GameRequirementsTool(new HttpClient(new FakeWeb([])), () => Pc(8, freeGb: 0.1)), "Roblox");
        Assert.Equal("not enough free space", Verdict(root, "Free disk space"));
        Assert.Equal("meets minimum", Verdict(root, "RAM"));
    }

    const string SteamDetails = """
        {"1091500":{"success":true,"data":{"name":"Cyberpunk 2077","pc_requirements":{
          "minimum":"<strong>Minimum:</strong><br><ul class=\"bb_ul\"><li>Requires a 64-bit processor and operating system<br></li><li><strong>OS:</strong> 64-bit Windows 10<br></li><li><strong>Memory:</strong> 12 GB RAM<br></li><li><strong>Graphics:</strong> NVIDIA GeForce GTX 1060 6GB<br></li><li><strong>Storage:</strong> 70 GB available space</li></ul>",
          "recommended":"<strong>Recommended:</strong><br><ul class=\"bb_ul\"><li><strong>Memory:</strong> 16 GB RAM<br></li><li><strong>Graphics:</strong> NVIDIA GeForce RTX 2060 SUPER &amp; better</li></ul>"}}}}
        """;

    [Fact]
    public async Task Other_games_are_looked_up_on_Steam()
    {
        var web = new FakeWeb(new()
        {
            ["https://store.steampowered.com/api/storesearch/"] = """{"total":2,"items":[{"type":"app","name":"Cyberpunk 2077","id":1091500},{"type":"app","name":"Cyberpunk 2077: Phantom Liberty","id":2138330}]}""",
            ["https://store.steampowered.com/api/appdetails?appids=1091500"] = SteamDetails,
        });
        var root = await Run(new GameRequirementsTool(new HttpClient(web), () => Pc(16, freeGb: 50)), "cyberpunk 2077");

        Assert.Equal("Cyberpunk 2077", root.GetProperty("game").GetString());
        var min = root.GetProperty("minimum").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("Memory: 12 GB RAM", min);
        Assert.Contains("Storage: 70 GB available space", min);
        Assert.Contains("Graphics: NVIDIA GeForce RTX 2060 SUPER & better", root.GetProperty("recommended").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("meets recommended", Verdict(root, "RAM"));
        Assert.Equal("not enough free space", Verdict(root, "Free disk space"));
        Assert.Equal("ok", Verdict(root, "64-bit Windows"));
    }

    [Fact]
    public async Task A_game_without_requirements_or_not_found_says_so()
    {
        var web = new FakeWeb(new()
        {
            ["https://store.steampowered.com/api/storesearch/"] = """{"total":1,"items":[{"type":"app","name":"Tiny Game","id":5}]}""",
            ["https://store.steampowered.com/api/appdetails?appids=5"] = """{"5":{"success":true,"data":{"name":"Tiny Game","pc_requirements":[]}}}""",
        });
        var root = await Run(new GameRequirementsTool(new HttpClient(web), () => Pc(8)), "Tiny Game");
        Assert.False(root.GetProperty("found").GetBoolean());

        var none = await Run(new GameRequirementsTool(new HttpClient(new FakeWeb([])), () => Pc(8)), "No Such Game");
        Assert.False(none.GetProperty("found").GetBoolean());
    }
}

public class ExtendedPerformanceTests
{
    static PerfSample S(double sec, double cpu, double? speed, double? ping, bool lost = false, string? gpuTop = "FortniteClient", double? vram = 3.2) =>
        new(sec, cpu, 50, 60, 8, 5, "game", cpu / 2, speed, vram, gpuTop, ping, lost);

    [Fact]
    public void Summary_shows_slowed_processor_lag_spikes_and_graphics_memory()
    {
        var r = new PerfRecording
        {
            Samples =
            [
                S(1, 90, 60, 20), S(2, 90, 60, 25), S(3, 90, 110, 300), S(4, 20, 50, null, lost: true),
                S(5, 95, 70, 30, vram: 5.9), S(6, 10, 100, 22, gpuTop: "obs64"),
            ],
        };
        var s = PerfSummary.From(r);

        Assert.Equal(50, s.CpuSpeed!.Min);
        Assert.Equal(75, s.CpuSpeed.SlowedPercentOfBusyTime); // 3 of the 4 busy samples ran under 80 %
        Assert.Equal(5.9, s.MaxGpuMemoryGb);
        Assert.Equal("FortniteClient", s.TopGpuProcesses![0].Name);
        Assert.Equal(1, s.Ping!.Spikes);
        Assert.Equal(300, s.Ping.MaxMs);
        Assert.Equal(16.7, s.Ping.LostPercent);
        Assert.Equal(6, s.Ping.Pings);
    }

    [Fact]
    public void Recordings_from_before_phase_4_still_load_and_summarise()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pcd-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "rec-20261013-120000.json"),
                """{"Started":"2026-10-13T12:00:00+02:00","IntervalSeconds":1,"StopReason":"user","Samples":[{"Seconds":1,"Cpu":50,"Gpu":null,"Ram":40,"RamUsedGb":6,"Disk":2,"TopProcess":"javaw","TopProcessCpu":30}]}""");
            var r = new PerfRecordingStore(dir).Load("rec-20261013-120000.json");
            Assert.NotNull(r);
            Assert.Null(r!.Samples[0].CpuSpeed);
            Assert.False(r.Samples[0].PingLost);
            var s = PerfSummary.From(r);
            Assert.Null(s.CpuSpeed);
            Assert.Null(s.Ping);
            Assert.Null(s.MaxGpuMemoryGb);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Recording_tool_timeline_carries_the_new_measurements()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pcd-tl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PerfRecordingStore(dir);
            store.Save(new PerfRecording { Started = DateTimeOffset.Now, Samples = [S(1, 90, 60, 20), S(2, 90, 120, 180)] });
            var r = await new PerformanceRecordingTool(store).RunAsync(Json.Parse("{}"), CancellationToken.None);
            var timeline = JsonDocument.Parse(r.Content).RootElement.GetProperty("timeline");
            Assert.Equal(60, timeline[0].GetProperty("cpu_speed_min").GetDouble());
            Assert.Equal(180, timeline[1].GetProperty("ping_max_ms").GetDouble());
            Assert.Equal(3.2, timeline[1].GetProperty("gpu_mem_gb_max").GetDouble());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class Phase4WiringTests
{
    [Fact]
    public void System_prompt_names_the_new_tools()
    {
        string prompt = SystemPrompt.For(AnswerLanguage.Danish);
        foreach (string tool in new[] { "minecraft_mods", "other_game_logs", "service_status", "game_requirements" })
            Assert.Contains(tool, prompt);
    }

    [Fact]
    public void New_tools_are_read_only()
    {
        ITool[] tools = [new MinecraftModsTool(), new OtherGameLogsTool(), new ServiceStatusTool(), new GameRequirementsTool()];
        Assert.All(tools, t => Assert.Equal(ToolKind.ReadOnly, t.Kind));
        Assert.All(tools, t => Assert.Matches("^[a-z_]+$", t.Name));
    }
}
