using System.Text.Json;
using PCDoctor.Agent;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Linux;

namespace PCDoctor.Tests;

/// <summary>The Linux / Steam Deck parts. Most read pretend /proc, /sys and Steam folders, so they run on any system.</summary>
public sealed class LinuxTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("pcdoctor-linux").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Cpu_line_gives_busy_and_total_time()
    {
        var r = LinuxPerfSampler.ParseCpuLine("cpu  100 0 50 800 50 0 0 0 0 0\ncpu0 1 2 3 4\n");
        Assert.Equal((150L, 1000L), r);
    }

    [Fact]
    public void Disk_activity_uses_whole_disks_only()
    {
        string stats = """
               8       0 sda 1 2 3 4 5 6 7 8 9 500 11
               8       1 sda1 1 2 3 4 5 6 7 8 9 9999 11
             259       0 nvme0n1 1 2 3 4 5 6 7 8 9 700 11
               7       0 loop0 1 2 3 4 5 6 7 8 9 8888 11
            """;
        Assert.Equal(700, LinuxPerfSampler.BusiestDiskMs(stats));
    }

    [Fact]
    public void Network_bytes_leave_out_loopback()
    {
        string dev = """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo: 5000 10 0 0 0 0 0 0 5000 10 0 0 0 0 0 0
              wlan0: 1000 10 0 0 0 0 0 0 200 10 0 0 0 0 0 0
            """;
        Assert.Equal(1200, LinuxPerfSampler.NetworkBytes(dev));
    }

    [Fact]
    public void Sampler_measures_cpu_ram_and_amd_graphics_from_proc_and_sys()
    {
        Write("proc/meminfo", "MemTotal:       16000000 kB\nMemFree: 1000 kB\nMemAvailable:    4000000 kB\n");
        Write("sys/class/drm/card0/device/gpu_busy_percent", "63\n");
        Write("sys/class/drm/card0/device/mem_info_vram_used", "1073741824\n");
        Write("proc/stat", "cpu  100 0 0 900 0 0 0 0\n");
        using var sampler = new LinuxPerfSampler(_root);
        sampler.Read();
        Write("proc/stat", "cpu  400 0 0 1000 0 0 0 0\n"); // 300 busy out of 400 = 75 %
        var s = sampler.Read();
        Assert.Equal(75, s.Cpu);
        Assert.Equal(75, s.Ram);
        Assert.Equal(63, s.Gpu);
        Assert.Equal(1, s.GpuMemoryGb);
    }

    [Fact]
    public async Task Hardware_recognises_a_steam_deck_with_temperatures_and_battery()
    {
        Write("proc/cpuinfo", "processor\t: 0\nmodel name\t: AMD Custom APU 0405\nphysical id\t: 0\ncore id\t: 0\n\nprocessor\t: 1\nmodel name\t: AMD Custom APU 0405\nphysical id\t: 0\ncore id\t: 0\n");
        Write("proc/meminfo", "MemTotal:       16000000 kB\n");
        Write("sys/class/dmi/id/sys_vendor", "Valve\n");
        Write("sys/class/dmi/id/product_name", "Jupiter\n");
        Write("etc/os-release", "NAME=\"SteamOS\"\nPRETTY_NAME=\"SteamOS\"\nVERSION_ID=3.6\n");
        Write("proc/sys/kernel/osrelease", "6.5.0-valve\n");
        Write("sys/class/hwmon/hwmon0/name", "k10temp\n");
        Write("sys/class/hwmon/hwmon0/temp1_input", "93000\n");
        Write("sys/class/power_supply/BAT1/type", "Battery\n");
        Write("sys/class/power_supply/BAT1/capacity", "80\n");
        Write("sys/class/power_supply/BAT1/energy_full", "30000000\n");
        Write("sys/class/power_supply/BAT1/energy_full_design", "40000000\n");

        var r = await new LinuxHardwareTool(_root).RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.False(r.IsError);
        string report = PlainReport.Hardware(Json.Parse(r.Content));
        Assert.Contains("Steam Deck (LCD)", report);
        Assert.Contains("Linux: SteamOS, kernel 6.5.0-valve", report);
        Assert.Contains("k10temp: 93 °C", report);
        Assert.Contains(PlainReport.Problem, report); // 93 °C is very hot
        Assert.Contains("75 %", report); // battery health
        Assert.Contains("1 kerner, 2 tråde", report);
    }

    [Fact]
    public void Journal_lines_become_events_with_meanings()
    {
        string lines =
            """{"__REALTIME_TIMESTAMP":"1760000000000000","PRIORITY":"3","_TRANSPORT":"kernel","MESSAGE":"amdgpu 0000:04:00.0: amdgpu: ring gfx_0.0.0 timeout, signaled seq=1"}""" + "\n" +
            """{"__REALTIME_TIMESTAMP":"1760000001000000","PRIORITY":"2","SYSLOG_IDENTIFIER":"systemd-coredump","MESSAGE":"Process 4242 (GameThread) of user 1000 dumped core."}""" + "\n" +
            """{"__REALTIME_TIMESTAMP":"1760000002000000","PRIORITY":"3","SYSLOG_IDENTIFIER":"odd","MESSAGE":[104,105]}""" + "\n" +
            "not json\n";
        var events = JournalTool.Parse(lines);
        Assert.Equal(3, events.Count);
        Assert.Equal("kernel", events[0].Provider);
        Assert.Equal("critical", events[1].Level);
        Assert.Equal("hi", events[2].Message);
        L.Current = AnswerLanguage.English;
        Assert.Contains("graphics card", LinuxEvents.Explain(events[0].Message));
        Assert.Contains("program crashed", LinuxEvents.Explain(events[1].Message));
        Assert.Null(LinuxEvents.Explain("all is well"));
        L.Current = AnswerLanguage.Danish;
    }

    /// <summary>A pretend home folder with Steam, a second library (memory card) and one Proton game that wrote a Unity log.</summary>
    string FakeSteamHome()
    {
        string home = Path.Combine(_root, "home");
        string card = Path.Combine(_root, "card");
        Write("home/.local/share/Steam/steamapps/libraryfolders.vdf",
            $$"""
            "libraryfolders"
            {
                "0" { "path" "{{Path.Combine(home, ".local", "share", "Steam")}}" }
                "1" { "path" "{{card}}" }
            }
            """);
        Write("card/steamapps/appmanifest_1234.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"1234\"\n\t\"name\"\t\t\"Cozy Farm\"\n}\n");
        Write("home/.local/share/Steam/steamapps/appmanifest_1493710.acf", "\"AppState\" { \"appid\" \"1493710\" \"name\" \"Proton Experimental\" }");
        Write("card/steamapps/compatdata/1234/pfx/drive_c/users/steamuser/AppData/LocalLow/CozyCo/CozyFarm/Player.log",
            "Initialize engine\nNullReferenceException: boom\n");
        return home;
    }

    [Fact]
    public void Steam_libraries_and_games_are_found_including_the_memory_card()
    {
        string home = FakeSteamHome();
        var games = SteamLibrary.Games(home);
        Assert.Contains(games, g => g.AppId == "1234" && g.Name == "Cozy Farm" && g.Library == Path.Combine(_root, "card"));
        Assert.True(SteamLibrary.IsTool(games.Single(g => g.AppId == "1493710")));
    }

    [Fact]
    public void Proton_game_logs_are_found_and_named_after_the_steam_game()
    {
        string home = FakeSteamHome();
        string empty = Path.Combine(_root, "empty");
        var found = new OtherGamesLocator(empty, empty, empty, [], linuxHome: home).Find();
        var game = Assert.Single(found);
        Assert.Equal("Cozy Farm", game.Name);
        Assert.Equal("unity", game.Format);
    }

    [Fact]
    public void Steam_game_id_is_read_from_a_process_environment()
    {
        Assert.Equal("1234", LinuxGames.SteamGameId("HOME=/home/deck\0SteamAppId=99\0SteamGameId=1234\0"));
        Assert.Equal("99", LinuxGames.SteamGameId("SteamAppId=99\0"));
        Assert.Null(LinuxGames.SteamGameId("HOME=/home/deck\0"));
    }

    [Fact]
    public void Linux_prompt_talks_about_linux_and_bash_not_windows()
    {
        string prompt = SystemPrompt.For(AnswerLanguage.Danish, linux: true);
        Assert.Contains("Steam Deck", prompt);
        Assert.Contains("shell_execute runs bash", prompt);
        Assert.DoesNotContain("PowerShell", prompt);
        Assert.DoesNotContain("Windows PC", prompt);
        Assert.Contains("PowerShell", SystemPrompt.For(AnswerLanguage.Danish, linux: false));
    }

    [LinuxFact]
    public void Api_key_file_is_private_to_the_user()
    {
        string file = Path.Combine(_root, "api-key");
        var store = new FileSecretStore(file);
        Assert.Null(store.Read());
        store.Write("sk-ant-test");
        Assert.Equal("sk-ant-test", store.Read());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        store.Delete();
        Assert.Null(store.Read());
    }

    [LinuxFact]
    public async Task Bash_tool_runs_a_command_exactly_as_written()
    {
        var tool = new BashTool(new AgentSettings());
        var r = await tool.RunAsync(Json.Parse("""{"command":"echo \"a b\" | tr a-z A-Z"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(r.Content);
        Assert.Equal(0, doc.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal("A B", doc.RootElement.GetProperty("stdout").GetString()!.Trim());
    }

    [LinuxFact]
    public void Real_sampler_reads_this_machine()
    {
        using var sampler = new LinuxPerfSampler();
        sampler.Read();
        Thread.Sleep(200);
        var s = sampler.Read();
        Assert.InRange(s.Cpu, 0, 100);
        Assert.True(s.RamUsedGb > 0);
    }

    [LinuxFact]
    public async Task Real_hardware_and_system_log_give_readable_reports()
    {
        var hw = await new LinuxHardwareTool().RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.Contains("Linux:", PlainReport.Hardware(Json.Parse(hw.Content)));
        var events = await new JournalTool().RunAsync(Json.Parse("""{"hours":24}"""), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(PlainReport.Events(Json.Parse(events.Content))));
    }
}
