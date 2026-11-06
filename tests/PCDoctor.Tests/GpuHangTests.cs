using System.Text.Json;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Windows;

namespace PCDoctor.Tests;

/// <summary>
/// A graphics card hang froze a real PC and blanked a monitor while CPU and RAM looked normal; the only traces were
/// information-level Windows entries. These tests use the real text of those entries.
/// </summary>
[Collection(nameof(LanguageCollection))] // some tests switch language
public sealed class GpuHangTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcd-gpu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        L.Current = AnswerLanguage.Danish;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // Windows Error Reporting event 1001 as written for the hang (trimmed).
    const string Wer141 =
        "Fault bucket , type 0\r\nEvent Name: LiveKernelEvent\r\nResponse: Not available\r\nCab Id: 0\r\n\r\nProblem signature:\r\n" +
        "P1: 141\r\nP2: aaaaaaaaaaaaaaaa\r\nP3: aaaaaaaaaaaaaaaa\r\nP6: 10_0_26200\r\n\r\nAttached files:\r\n" +
        "\\\\?\\C:\\WINDOWS\\LiveKernelReports\\WATCHDOG\\WATCHDOG-20261106-1312.dmp\r\n\\\\?\\C:\\WINDOWS\\SystemTemp\\WER-705961250-0.sysdata.xml\r\n";

    const string WerCrash = "Fault bucket 123, type 4\r\nEvent Name: APPCRASH\r\nResponse: Not available\r\nProblem signature:\r\nP1: Wobbly Life.exe\r\n";

    const string WatchdogRequest =
        "Component WATCHDOG has requested to create a Live Kernel Dump and the request has been completed. RequestedType Kernel Dump, GrantedType Kernel Dump.";

    static readonly DateTimeOffset T0 = new(2026, 11, 6, 13, 12, 8, TimeSpan.FromHours(1));

    static LogEvent Wer(int minutes, int seconds = 0, string message = Wer141) =>
        new("Application", T0.AddMinutes(minutes).AddSeconds(seconds), "information", HardwareFaults.WerProvider, 1001,
            HardwareFaults.MessageFor(HardwareFaults.WerProvider, 1001, message));

    static LogEvent Watchdog(int minutes = 0) =>
        new("WerKernel", T0.AddMinutes(minutes), "information", HardwareFaults.WerKernelProvider, 1001,
            HardwareFaults.MessageFor(HardwareFaults.WerKernelProvider, 1001, WatchdogRequest));

    // ---- recognising the entries ----

    [Fact]
    public void Hang_report_gives_the_hang_type_and_the_dump_name()
    {
        var w = HardwareFaults.ParseWer(Wer141)!;
        Assert.Equal("LiveKernelEvent", w.Kind);
        Assert.Equal("141", w.Code);
        Assert.Equal("WATCHDOG-20261106-1312.dmp", w.Dump);
        Assert.Equal("LiveKernelEvent code 141 (dump WATCHDOG-20261106-1312.dmp)", w.Summary);
    }

    [Fact]
    public void Ordinary_program_crashes_are_not_hardware_faults()
    {
        Assert.Null(HardwareFaults.ParseWer(WerCrash));
        Assert.False(HardwareFaults.IsHardwareFault(HardwareFaults.WerProvider, 1001, WerCrash));
        Assert.False(HardwareFaults.IsHardwareFault("Application Error", 1000, "Faulting application name: x.exe"));
        Assert.False(HardwareFaults.IsHardwareFault("Microsoft-Windows-Kernel-Power", 41, "rebooted"));
    }

    [Theory]
    [InlineData("Windows Error Reporting", 1001, true)]
    [InlineData("Microsoft-Windows-WerKernel", 1001, true)]
    [InlineData("Display", 4101, true)]
    [InlineData("nvlddmkm", 13, true)]
    [InlineData("amdkmdag", 0, true)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 17, true)]
    [InlineData("Display", 4102, false)]
    [InlineData("disk", 7, false)]
    public void Which_entries_count_as_hardware_faults(string provider, int id, bool expected)
    {
        string? message = provider == HardwareFaults.WerProvider ? Wer141 : "x";
        Assert.Equal(expected, HardwareFaults.IsHardwareFault(provider, id, message));
    }

    [Fact]
    public void Live_dump_request_names_the_component()
    {
        Assert.Equal("WATCHDOG", HardwareFaults.WerKernelComponent(WatchdogRequest));
        Assert.Contains("Component WATCHDOG requested a live kernel dump", Watchdog().Message);
    }

    // ---- grouping and explaining (what system_events returns) ----

    [Fact]
    public void The_same_hang_logged_several_times_is_one_flagged_group_with_a_plain_meaning()
    {
        // Windows logs the report four times (at the hang, and again while the report is processed after the restart).
        var events = new[] { Wer(1), Wer(1), Wer(3), Wer(6), Watchdog() };
        var groups = EventSummary.Group(events);

        Assert.Equal(2, groups.Count);
        var report = groups.Single(g => g.Provider == HardwareFaults.WerProvider);
        Assert.Equal(4, report.Count);
        Assert.True(report.HardwareFault);
        Assert.Contains("Grafikkortet (GPU) holdt op med at svare", report.Meaning);
        Assert.Contains("141", report.Meaning);

        var request = groups.Single(g => g.Provider == HardwareFaults.WerKernelProvider);
        Assert.True(request.HardwareFault);
        Assert.Contains("hukommelsesdump", request.Meaning);
    }

    [Fact]
    public void Other_hang_types_and_blue_screens_get_their_own_meaning()
    {
        string other = Wer141.Replace("P1: 141", "P1: 1a8");
        Assert.Contains("kode 1a8", KnownEvents.Explain(HardwareFaults.WerProvider, 1001, HardwareFaults.MessageFor(HardwareFaults.WerProvider, 1001, other)));
        string blue = "Event Name: BlueScreen\r\nP1: 116\r\n";
        Assert.Contains("blåskærm", KnownEvents.Explain(HardwareFaults.WerProvider, 1001, HardwareFaults.MessageFor(HardwareFaults.WerProvider, 1001, blue)));
        // A program crash reported through the same event id gets no hardware explanation.
        Assert.Null(KnownEvents.Explain(HardwareFaults.WerProvider, 1001, WerCrash));
    }

    [Fact]
    public void The_explanations_exist_in_all_five_languages()
    {
        var seen = new HashSet<string>();
        foreach (var language in Enum.GetValues<AnswerLanguage>())
        {
            L.Current = language;
            string gpu = KnownEvents.Explain(HardwareFaults.WerProvider, 1001, Wer141)!;
            string dump = KnownEvents.Explain(HardwareFaults.WerKernelProvider, 1001, WatchdogRequest)!;
            Assert.Contains("141", gpu);
            Assert.False(string.IsNullOrWhiteSpace(dump));
            Assert.True(seen.Add(gpu), $"{language} repeats another language's text");
            Assert.True(seen.Add(dump), $"{language} repeats another language's text");
        }
    }

    [Fact]
    public void The_events_report_marks_hardware_faults_as_problems()
    {
        var group = EventSummary.Group([Wer(1)]).Single();
        string json = JsonSerializer.Serialize(new
        {
            total_events = 1,
            total_groups = 1,
            groups = new[]
            {
                new { level = "information", source = group.Provider, event_id = group.Id, count = group.Count, last = "2026-11-06T13:13:11",
                      meaning = group.Meaning, hardware_fault = true, message = group.Message },
            },
        });
        string text = PlainReport.Events(JsonDocument.Parse(json).RootElement);
        Assert.Contains(PlainReport.Problem, text); // shown in red on the Diagnostics page, even though the level is "information"
        Assert.Contains("HARDWARE/DRIVER", text);
        Assert.Contains("Grafikkortet", text);
    }

    // ---- the startup notice ----

    [Fact]
    public void Entries_close_together_are_one_incident_and_the_report_names_it()
    {
        var events = new[]
        {
            Watchdog(0), Wer(1), Wer(1, 3), Wer(3, 22), Wer(6, 14),           // one hang, logged over 6 minutes
            Wer(240),                                                          // a second one, hours later
            new LogEvent("System", T0.AddMinutes(2), "error", "Application Error", 1000, "program crash"), // not a hardware fault
        };
        var incidents = FaultIncidents.Cluster(events);

        Assert.Equal(2, incidents.Count);
        Assert.Equal(T0, incidents[0].Start);
        Assert.Equal(T0.AddMinutes(6).AddSeconds(14), incidents[0].End);
        Assert.Equal(5, incidents[0].Entries);
        Assert.Contains("code 141", incidents[0].Summary);
        Assert.Equal(T0.AddMinutes(240), incidents[1].Start);
        Assert.Empty(FaultIncidents.Cluster([]));
    }

    [Fact]
    public void Event_queries_cover_all_levels_and_use_fixed_invariant_times()
    {
        var from = new DateTimeOffset(2026, 11, 6, 13, 0, 0, TimeSpan.FromHours(1));
        var to = from.AddHours(1);
        string drivers = HardwareFaultReader.DriverQuery(from, to);
        Assert.Contains("@Name='nvlddmkm'", drivers);
        Assert.Contains("Level>0 and Level<4", drivers);
        Assert.Contains("EventID=4101", drivers);
        Assert.Contains("2026-11-06T12:00:00.000Z", drivers); // UTC, with ':' regardless of the PC's regional settings
        Assert.Contains("EventID=1001", HardwareFaultReader.WerQuery(from, to));
        Assert.Contains("Microsoft-Windows-WerKernel", HardwareFaultReader.WerKernelQuery(from, to));
    }

    [Fact]
    public void Reading_the_real_event_log_never_throws()
    {
        if (!OperatingSystem.IsWindows()) return; // the event log only exists on Windows
        var (events, _) = HardwareFaultReader.Read(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now, CancellationToken.None);
        Assert.NotNull(events);
    }

    // ---- crash-safe recordings ----

    static PerfSample Sample(double seconds, double cpu = 30) => new(seconds, cpu, 40, 60, 8, 5, "game", cpu / 2);

    [Fact]
    public void A_journal_that_was_never_completed_comes_back_as_an_interrupted_recording()
    {
        var store = new PerfRecordingStore(_dir);
        var started = new DateTimeOffset(2026, 11, 6, 13, 0, 0, TimeSpan.FromHours(1));
        var journal = PerfJournal.Create(_dir, new PerfRecording { Started = started, IntervalSeconds = 1, Game = "Minecraft", GameProcess = "javaw" });
        for (int i = 1; i <= 4; i++) journal.Append(Sample(i, 10 * i));
        journal.Dispose(); // the process ended here: no Complete(), no saved recording

        Assert.Empty(store.ListIds());
        var ids = store.RecoverInterrupted();

        var id = Assert.Single(ids);
        var r = store.Load(id)!;
        Assert.Equal("interrupted", r.StopReason);
        Assert.Equal(4, r.Samples.Count);
        Assert.Equal(40, r.Samples[^1].Cpu);
        Assert.Equal("Minecraft", r.Game);
        Assert.Equal("javaw", r.GameProcess);
        Assert.Equal(started, r.Started);
        Assert.Empty(Directory.GetFiles(_dir, "*.partial")); // recovered once only
        Assert.Empty(store.RecoverInterrupted());
    }

    [Fact]
    public void A_line_cut_in_half_by_the_crash_is_ignored()
    {
        var store = new PerfRecordingStore(_dir);
        var journal = PerfJournal.Create(_dir, new PerfRecording { Started = DateTimeOffset.Now, IntervalSeconds = 1 });
        journal.Append(Sample(1));
        journal.Append(Sample(2));
        journal.Dispose();
        File.AppendAllText(journal.Path, "{\"Seconds\":3,\"Cpu\":9"); // power lost mid-write

        var r = store.Load(Assert.Single(store.RecoverInterrupted()))!;
        Assert.Equal(2, r.Samples.Count);
    }

    [Fact]
    public void A_running_recording_is_not_mistaken_for_a_leftover()
    {
        var store = new PerfRecordingStore(_dir);
        using var journal = PerfJournal.Create(_dir, new PerfRecording { Started = DateTimeOffset.Now, IntervalSeconds = 1 });
        journal.Append(Sample(1));

        Assert.Empty(store.RecoverInterrupted());
        Assert.True(File.Exists(journal.Path));
    }

    [Fact]
    public void A_recording_that_ends_normally_leaves_nothing_behind_and_empty_journals_are_dropped()
    {
        var store = new PerfRecordingStore(_dir);
        var journal = PerfJournal.Create(_dir, new PerfRecording { Started = DateTimeOffset.Now, IntervalSeconds = 1 });
        journal.Append(Sample(1));
        journal.Complete();
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));

        PerfJournal.Create(_dir, new PerfRecording { Started = DateTimeOffset.Now.AddHours(-1), IntervalSeconds = 1 }).Dispose(); // header only
        Assert.Empty(store.RecoverInterrupted());
        Assert.Empty(store.ListIds());
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }

    // ---- graphics card health ----

    [Theory]
    [InlineData(0x0UL, null)]
    [InlineData(0x1UL, null)] // idle is normal
    [InlineData(0x4UL, "power_cap")]
    [InlineData(0x44UL, "power_cap,hardware_thermal_slowdown")]
    [InlineData(0x8UL | 0x1UL, "hardware_slowdown")]
    public void Throttle_bits_become_names(ulong mask, string? expected) => Assert.Equal(expected, GpuThrottle.Decode(mask));

    [Fact]
    public void Gpu_health_shows_a_clock_that_fell_while_busy_and_a_stalling_driver()
    {
        var samples = new[]
        {
            Sample(1) with { Gpu = 95, GpuTempC = 70, GpuPowerW = 160, GpuClockMhz = 1900, GpuQueryMs = 20 },
            Sample(2) with { Gpu = 95, GpuTempC = 74, GpuPowerW = 170, GpuClockMhz = 1936, GpuQueryMs = 30 },
            Sample(3) with { Gpu = 99, GpuTempC = 75, GpuPowerW = 90, GpuClockMhz = 300, GpuThrottle = "power_cap", GpuQueryMs = 6100 },
            Sample(4) with { Gpu = 2, GpuTempC = 60, GpuPowerW = 30, GpuClockMhz = 139, GpuThrottle = "power_cap,hardware_slowdown", GpuQueryMs = 25 },
        };
        var h = GpuHealthSummary.From(samples)!;

        Assert.Equal(75, h.MaxTempC);
        Assert.Equal(170, h.MaxPowerW);
        Assert.Equal(1936, h.MaxClockMhz);
        Assert.Equal(300, h.MinClockMhzWhileBusy); // 139 MHz was while idle, which says nothing
        Assert.Equal(["power_cap", "hardware_slowdown"], h.ThrottleReasons);
        Assert.Equal(6100, h.SlowestReplyMs);
    }

    [Fact]
    public void Recordings_without_gpu_health_have_none_and_the_summary_carries_it_when_present()
    {
        Assert.Null(GpuHealthSummary.From([Sample(1), Sample(2)]));
        Assert.Null(PerfSummary.From(new PerfRecording { Samples = [Sample(1)] }).GpuHealth);

        var r = new PerfRecording { Samples = [Sample(1) with { GpuTempC = 61, GpuClockMhz = 1500 }] };
        Assert.Equal(61, PerfSummary.From(r).GpuHealth!.MaxTempC);
    }

    [Fact]
    public async Task The_recording_tool_reports_interrupted_recordings_and_gpu_health_to_the_model()
    {
        var store = new PerfRecordingStore(_dir);
        store.Save(new PerfRecording
        {
            Started = DateTimeOffset.Now,
            StopReason = "interrupted",
            Samples = [Sample(1) with { Gpu = 90, GpuTempC = 70, GpuClockMhz = 1800, GpuQueryMs = 5000 }, Sample(2) with { Gpu = 90, GpuTempC = 71, GpuClockMhz = 200 }],
        });
        var result = await new PerformanceRecordingTool(store).RunAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content);
        var root = doc.RootElement;

        Assert.StartsWith("NOT the user", root.GetProperty("stopped_by").GetString());
        Assert.Equal(71, root.GetProperty("summary").GetProperty("GpuHealth").GetProperty("MaxTempC").GetDouble());
        Assert.Equal(200, root.GetProperty("timeline")[1].GetProperty("gpu_clock_min").GetDouble());
        Assert.Contains("GpuHealth", root.GetProperty("note").GetString());
    }

    [Fact]
    public async Task A_full_length_recording_with_gpu_health_still_fits_in_what_the_model_can_receive()
    {
        var store = new PerfRecordingStore(_dir);
        var rec = new PerfRecording { Started = DateTimeOffset.Now };
        for (int i = 1; i <= 900; i++)
            rec.Samples.Add(Sample(i, 90) with
            {
                Gpu = 95, GpuTempC = 70 + i % 5, GpuPowerW = 150 + i % 20, GpuClockMhz = 1900 - i % 50,
                GpuThrottle = i % 7 == 0 ? "power_cap,hardware_slowdown" : null, GpuQueryMs = 10 + i % 30,
            });
        store.Save(rec);

        var result = await new PerformanceRecordingTool(store).RunAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Contains("gpu_clock_min", result.Content);
        Assert.True(result.Content.Length < 20_000, $"tool result too large for the model: {result.Content.Length} chars");
    }

    [Fact]
    public void Old_recordings_without_gpu_fields_still_load()
    {
        Directory.CreateDirectory(_dir);
        // Written by an earlier version: no Gpu* properties at all.
        File.WriteAllText(Path.Combine(_dir, "rec-20260101-120000.json"),
            """{"Started":"2026-01-01T12:00:00+01:00","IntervalSeconds":1,"StopReason":"user","Samples":[{"Seconds":1,"Cpu":5,"Gpu":null,"Ram":50,"RamUsedGb":8,"Disk":1,"TopProcess":"x","TopProcessCpu":2}]}""");
        var r = new PerfRecordingStore(_dir).Load("rec-20260101-120000.json")!;
        Assert.Single(r.Samples);
        Assert.Null(r.Samples[0].GpuTempC);
        Assert.Null(PerfSummary.From(r).GpuHealth);
    }
}
