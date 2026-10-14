using System.Text.Json;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Windows;

namespace PCDoctor.Tests;

public class FpsMathTests
{
    /// <summary>Present times for a steady frame rate, with a few long frames (hitches) put in.</summary>
    static List<double> Presents(double fps, double seconds, params int[] hitchAtFrame)
    {
        var times = new List<double>();
        double t = 0, frame = 1000 / fps;
        for (int i = 0; t < seconds * 1000; i++)
        {
            times.Add(Math.Round(t, 3));
            t += hitchAtFrame.Contains(i) ? 120 : frame;
        }
        return times;
    }

    [Fact]
    public void Steady_60_fps_has_lows_near_60_and_no_stutters()
    {
        var r = FpsResult.FromPresents(Presents(60, 10), 0, "DXGI");
        Assert.True(r.Measured);
        Assert.InRange(r.AverageFps, 59.5, 60.5);
        Assert.InRange(r.OnePercentLowFps, 59, 61);
        Assert.Equal(0, r.Stutters);
        Assert.InRange(r.MinFps, 59, 61);
        Assert.InRange(r.PerSecond!.Count, 10, 11);
        Assert.Equal("DXGI", r.Source);
    }

    [Fact]
    public void Hitches_pull_the_lows_down_and_are_counted()
    {
        var r = FpsResult.FromPresents(Presents(100, 20, 300, 900, 1500, 1501, 1700), 0, "DxgKrnl");
        Assert.Equal(5, r.Stutters);
        Assert.Equal(120, r.WorstFrameMs);
        Assert.InRange(r.AverageFps, 90, 100);
        Assert.True(r.PointOnePercentLowFps < 20, $"0.1 % low was {r.PointOnePercentLowFps}");
        Assert.True(r.MinFps < r.MaxFps);
    }

    [Fact]
    public void Per_second_counts_line_up_with_the_recording_start()
    {
        // The FPS helper started 2.5 seconds after the recording.
        var r = FpsResult.FromPresents(Presents(50, 4), 2500, "DXGI");
        Assert.Equal(0, r.PerSecond![0]);
        Assert.Equal(0, r.PerSecond[1]);
        Assert.Equal(25, r.PerSecond[2]);
        Assert.Equal(50, r.PerSecond[3]);
    }

    [Fact]
    public void No_frames_or_a_helper_error_is_explained()
    {
        Assert.False(FpsResult.FromPresents([], 0, "DXGI").Measured);
        var file = new FpsCaptureFile { Error = "Not running as administrator." };
        var r = file.ToResult(DateTimeOffset.Now);
        Assert.False(r.Measured);
        Assert.Contains("administrator", r.Problem);
    }

    [Fact]
    public void Capture_file_prefers_DXGI_and_falls_back_to_the_graphics_kernel()
    {
        var start = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
        var file = new FpsCaptureFile { StartedUtc = start.UtcDateTime.AddSeconds(1), Kernel = Presents(30, 5) };
        var kernel = file.ToResult(start);
        Assert.Equal("DxgKrnl", kernel.Source);
        Assert.Equal(0, kernel.PerSecond![0]); // helper started one second in

        file.Dxgi = Presents(144, 5);
        Assert.Equal("DXGI", file.ToResult(start).Source);

        string path = Path.Combine(Path.GetTempPath(), "pcd-fps-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(file));
            Assert.Equal(file.Dxgi.Count, FpsCaptureFile.Load(path)!.Dxgi.Count);
            Assert.Null(FpsCaptureFile.Load(path + ".missing"));
        }
        finally { File.Delete(path); }
    }
}

public class GameRecordingTests
{
    static PerfSample S(double sec, double? gameCpu, double? gameGpu, double? gameRam) =>
        new(sec, 60, 80, 50, 8, 5, "javaw", 30, GameCpu: gameCpu, GameGpu: gameGpu, GameRamGb: gameRam);

    [Fact]
    public async Task Game_recording_carries_the_game_its_use_and_fps_to_the_agent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pcd-game-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PerfRecordingStore(dir);
            var fpsTimes = Enumerable.Range(0, 300).Select(i => i * 1000.0 / 60).ToList(); // 5 s at 60 FPS
            var rec = new PerfRecording
            {
                Started = DateTimeOffset.Now,
                Game = "Minecraft (Java)",
                GameProcess = "javaw",
                Samples = [S(1, 25, 70, 3.1), S(2, 30, 90, 3.4), S(3, null, null, null)],
                Fps = FpsResult.FromPresents(fpsTimes, 0, "DxgKrnl"),
            };
            store.Save(rec);

            var summary = PerfSummary.From(rec);
            Assert.Equal(27.5, summary.GameCpu!.Average);
            Assert.Equal(90, summary.GameGpu!.Max);
            Assert.Equal(3.4, summary.MaxGameRamGb);

            var r = await new PerformanceRecordingTool(store).RunAsync(Json.Parse("{}"), CancellationToken.None);
            var root = JsonDocument.Parse(r.Content).RootElement;
            Assert.Equal("Minecraft (Java)", root.GetProperty("game").GetString());
            Assert.InRange(root.GetProperty("fps").GetProperty("AverageFps").GetDouble(), 59, 61);
            // The per-second list is long; the agent gets it folded into the timeline instead.
            Assert.Equal(JsonValueKind.Null, root.GetProperty("fps").GetProperty("PerSecond").ValueKind);
            Assert.Equal(60, root.GetProperty("timeline")[1].GetProperty("fps_avg").GetDouble());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Running_programs_can_be_listed()
    {
        var list = RunningGames.List();
        Assert.DoesNotContain(list, p => p.ProcessName.Equals("PCDoctor", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Minecraft (Java)", RunningGames.Known["javaw"]);
    }

    [WindowsFact]
    public void Fps_helper_starts_a_measurement_and_writes_its_result()
    {
        // GitHub's Windows runners run as administrator, like the helper after the user clicks Yes.
        string dir = Path.Combine(Path.GetTempPath(), "pcd-fpsh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string outFile = Path.Combine(dir, "out.json"), stopFile = Path.Combine(dir, "stop");
            var helper = Task.Run(() => FpsCapture.RunHelper([FpsCapture.HelperArg, "no-such-game-xyz", outFile, stopFile, Environment.ProcessId.ToString()]));
            Thread.Sleep(2000);
            File.WriteAllText(stopFile, "stop");
            Assert.True(helper.Wait(TimeSpan.FromSeconds(15)), "helper did not stop");
            Assert.Equal(0, helper.Result);

            var file = FpsCaptureFile.Load(outFile);
            Assert.NotNull(file);
            if (Microsoft.Diagnostics.Tracing.Session.TraceEventSession.IsElevated() == true)
            {
                Assert.Null(file!.Error);
                Assert.Empty(file.Dxgi); // the game does not exist, so no frames
                Assert.False(file.ToResult(DateTimeOffset.Now).Measured);
            }
            else
                Assert.Contains("administrator", file!.Error);
        }
        finally { Directory.Delete(dir, true); }
    }
}
