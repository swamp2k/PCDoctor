using System.Text.Json;
using PCDoctor.Agent;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Windows;

namespace PCDoctor.Tests;

sealed class FakeSampler(Func<int, PerfSample>? make = null) : IPerfSampler
{
    public int Reads;
    public PerfSample Read() => (make ?? (_ => S(0, 50)))(Reads++);
    public static PerfSample S(double sec, double cpu, double? gpu = 40, double ram = 60, string? top = "game") =>
        new(sec, cpu, gpu, ram, 8, 5, top, cpu / 2);
}

public sealed class PerformanceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcd-rec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Recorder_stops_by_itself_at_the_cap()
    {
        var recorder = new PerfRecorder(new FakeSampler(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));
        int seen = 0;
        var r = await recorder.RunAsync(_ => seen++, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("limit", r.StopReason);
        Assert.InRange(r.Samples.Count, 1, 10);
        Assert.Equal(r.Samples.Count, seen);
    }

    [Fact]
    public void Default_cap_is_15_minutes_at_one_sample_per_second()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), PerfRecorder.MaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), PerfRecorder.DefaultInterval);
    }

    [Fact]
    public async Task Stop_keeps_samples_and_reports_user()
    {
        using var cts = new CancellationTokenSource();
        var recorder = new PerfRecorder(new FakeSampler(), TimeSpan.FromMilliseconds(20));
        cts.CancelAfter(150);
        var r = await recorder.RunAsync(_ => { }, cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("user", r.StopReason);
        Assert.NotEmpty(r.Samples);
    }

    [Fact]
    public async Task A_failed_reading_does_not_end_the_recording()
    {
        // Read 0 primes the counters; read 2 fails.
        var sampler = new FakeSampler(i => i == 2 ? throw new InvalidOperationException("counter gone") : FakeSampler.S(0, 10));
        // Count-based, not time-based: Windows timers tick coarsely, so stop after 3 good samples.
        using var cts = new CancellationTokenSource();
        int seen = 0;
        var recorder = new PerfRecorder(sampler, TimeSpan.FromMilliseconds(10));
        var r = await recorder.RunAsync(_ => { if (++seen == 3) cts.Cancel(); }, cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, r.Samples.Count);
        Assert.True(sampler.Reads >= 4); // the failed read was skipped, recording carried on
    }

    [Fact]
    public async Task Cap_still_applies_when_every_reading_fails()
    {
        var sampler = new FakeSampler(_ => throw new InvalidOperationException("counter gone"));
        var recorder = new PerfRecorder(sampler, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));
        var r = await recorder.RunAsync(_ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("limit", r.StopReason);
        Assert.Empty(r.Samples);
    }

    [Fact]
    public void Summary_has_averages_peaks_and_time_above_90()
    {
        var r = new PerfRecording
        {
            Samples =
            [
                FakeSampler.S(1, 20, gpu: null, top: "game"),
                FakeSampler.S(2, 40, gpu: null, top: "game"),
                FakeSampler.S(3, 95, gpu: null, top: "game"),
                FakeSampler.S(4, 100, gpu: null, top: "antivirus"),
            ],
        };
        var s = PerfSummary.From(r);

        Assert.Equal(4, s.DurationSeconds);
        Assert.Equal(63.8, s.Cpu.Average);
        Assert.Equal(100, s.Cpu.Max);
        Assert.Equal(50, s.Cpu.PercentOfTimeAbove90);
        Assert.Null(s.Gpu); // no GPU counter on this PC
        Assert.Equal("game", s.TopProcesses[0].Name);
        Assert.Equal(75, s.TopProcesses[0].PercentOfTime);
    }

    [Fact]
    public void Store_saves_loads_and_keeps_only_the_newest_20()
    {
        var store = new PerfRecordingStore(_dir);
        var start = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 25; i++)
            store.Save(new PerfRecording { Started = start.AddMinutes(i), Samples = [FakeSampler.S(1, i)] });

        var ids = store.ListIds();
        Assert.Equal(PerfRecordingStore.Keep, ids.Count);
        var newest = store.Load(ids[0])!;
        Assert.Equal(24, newest.Samples[0].Cpu);
        Assert.Equal(ids[0], newest.Id);
    }

    [Fact]
    public void Store_refuses_paths_outside_its_folder()
    {
        var store = new PerfRecordingStore(_dir);
        Assert.Null(store.Load("../settings.json"));
        Assert.Null(store.Load(@"..\rec-x.json"));
        Assert.Null(store.Load("rec-missing.json"));
    }

    [Fact]
    public async Task Tool_explains_how_to_record_when_there_is_nothing()
    {
        var result = await new PerformanceRecordingTool(new PerfRecordingStore(_dir)).RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("Optag ydelse", result.Content);
    }

    [Fact]
    public async Task Tool_returns_newest_recording_with_a_short_timeline()
    {
        var store = new PerfRecordingStore(_dir);
        var start = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        store.Save(new PerfRecording { Started = start, Samples = [FakeSampler.S(1, 5)] });
        var full = new PerfRecording { Started = start.AddHours(1), StopReason = "limit" };
        for (int i = 1; i <= 900; i++)
            full.Samples.Add(FakeSampler.S(i, i % 100));
        string newest = store.Save(full);

        var result = await new PerformanceRecordingTool(store).RunAsync(Json.Parse("{}"), CancellationToken.None);
        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Content);
        var root = doc.RootElement;
        Assert.Equal(newest, root.GetProperty("id").GetString());
        Assert.Equal("15-minute limit", root.GetProperty("stopped_by").GetString());
        Assert.InRange(root.GetProperty("timeline").GetArrayLength(), 1, 60);
        Assert.Equal(2, root.GetProperty("recordings").GetArrayLength());
    }

    [WindowsFact]
    public void Windows_sampler_reads_real_values()
    {
        using var sampler = new WindowsPerfSampler();
        sampler.Read();
        Thread.Sleep(1000);
        var s = sampler.Read();

        Assert.InRange(s.Cpu, 0, 100);
        Assert.InRange(s.Ram, 1, 100);
        Assert.True(s.RamUsedGb > 0);
        if (s.Gpu is { } gpu) Assert.InRange(gpu, 0, 100);
        if (s.Disk is { } disk) Assert.InRange(disk, 0, 100);
    }
}

public class PerformanceAnalysisTests
{
    [Fact]
    public async Task Agent_can_read_a_recording_and_gets_its_numbers_back()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pcd-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PerfRecordingStore(dir);
            var rec = new PerfRecording { Started = DateTimeOffset.Now };
            for (int i = 1; i <= 900; i++)
                rec.Samples.Add(FakeSampler.S(i, 97, gpu: 30, top: "javaw"));
            string id = store.Save(rec);

            var model = new ScriptedModel();
            model.Enqueue(new ModelTurn("", [Json.Call("performance_recording")], "tool_use"));
            model.Enqueue(new ModelTurn("CPU'en er flaskehalsen.", [], "end_turn"));
            var settings = new AgentSettings();
            var invoker = new ToolInvoker([new PerformanceRecordingTool(store)], settings, () => PermissionMode.ReadOnly, (_, _) => Task.FromResult(false));

            await new AgentLoop(model, invoker, settings).RunAsync($"Kig på min ydelsesoptagelse {id}", _ => { }, CancellationToken.None);

            var result = Assert.Single(model.Results).Result;
            Assert.False(result.IsError);
            Assert.Contains(id, result.Content);
            Assert.Contains("javaw", result.Content);
            Assert.True(result.Content.Length < 20_000, $"tool result too large for the model: {result.Content.Length} chars");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
