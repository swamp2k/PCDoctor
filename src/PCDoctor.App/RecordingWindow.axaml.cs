using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.App;

/// <summary>Start/stop a performance recording, then review it as a chart and a summary.</summary>
public partial class RecordingWindow : Window
{
    readonly IPerfSampler? _sampler;
    readonly PerfRecordingStore _store;
    readonly Func<string, bool> _ask;
    readonly List<PerfSample> _live = [];
    readonly bool _gameMode;

    /// <summary>First entry of the game list: a normal recording that follows no particular game.</summary>
    static readonly RunningProgram WholePc = new("", "Hele computeren (intet bestemt spil)", false);

    CancellationTokenSource? _stop;
    Task? _recording;
    PerfRecording? _shown;
    bool _filling;

    public RecordingWindow() : this(null, new PerfRecordingStore(), _ => false) { }

    /// <param name="ask">Sends a question to the chat. Returns false if PC Doctor is busy.</param>
    /// <param name="gameMode">
    /// Opened as "Hvorfor hakker mit spil?": pick a running game, FPS on by default, and PC Doctor is asked automatically when the recording stops.
    /// </param>
    public RecordingWindow(IPerfSampler? sampler, PerfRecordingStore store, Func<string, bool> ask, bool gameMode = false)
    {
        InitializeComponent();
        _sampler = sampler;
        _store = store;
        _ask = ask;
        _gameMode = gameMode;

        if (gameMode)
        {
            Title = "Hvorfor hakker mit spil?";
            IntroText.Text = "1. Start spillet. 2. Vælg det her (tryk \"Opdater listen\" hvis det ikke står der). 3. Tryk Start og spil, til det hakker. " +
                             "4. Kom tilbage og tryk Stop. PC Doctor kigger så på optagelsen og forklarer, hvad der holdt spillet tilbage. " +
                             "Den måler hele PC'en og spillet selv cirka én gang i sekundet, og FPS hvis du tillader det. Skærmen optages ikke.";
            FpsBox.IsChecked = OperatingSystem.IsWindows();
        }
        FpsBox.IsEnabled = OperatingSystem.IsWindows();
        RefreshGames();
        RefreshGamesButton.Click += (_, _) => RefreshGames();
        GameBox.SelectionChanged += (_, _) => FpsBox.IsEnabled = OperatingSystem.IsWindows() && SelectedGame is not null;

        StartButton.Click += async (_, _) => await StartAsync();
        StopButton.Click += (_, _) => _stop?.Cancel();
        AskButton.Click += (_, _) => Ask();
        RecordingBox.SelectionChanged += (_, _) =>
        {
            if (!_filling && RecordingBox.SelectedItem is Entry e && _store.Load(e.Id) is { } r)
                Display(r);
        };
        Closing += (_, _) =>
        {
            // Closing mid-recording keeps what was recorded so far.
            _stop?.Cancel();
            _recording?.Wait(TimeSpan.FromSeconds(3));
        };

        if (_sampler is null)
        {
            StartButton.IsEnabled = false;
            ElapsedText.Text = "Optagelse virker kun på Windows.";
        }
        FillList(select: null);
    }

    RunningProgram? SelectedGame => GameBox.SelectedItem is RunningProgram { ProcessName.Length: > 0 } g ? g : null;

    void RefreshGames()
    {
        var previous = SelectedGame?.ProcessName;
        var list = new List<RunningProgram> { WholePc };
        try { list.AddRange(RunningGames.List()); } catch (Exception) { /* the list is a convenience; whole-PC recording still works */ }
        GameBox.ItemsSource = list;
        // Keep the choice; otherwise in game mode pick the first known game that is running.
        GameBox.SelectedItem = list.FirstOrDefault(g => g.ProcessName == previous && previous is not null)
                               ?? (_gameMode ? list.FirstOrDefault(g => g.KnownGame) : null)
                               ?? WholePc;
    }

    async Task StartAsync()
    {
        if (_sampler is null || _recording is not null)
            return;

        var game = SelectedGame;
        if (_gameMode && game is null)
        {
            ElapsedText.Text = "Vælg spillet først. Start det, og tryk \"Opdater listen\".";
            return;
        }
        _sampler.Game = game?.ProcessName;

        // FPS needs administrator rights, so it runs in a separate helper Windows asks permission for.
        Func<DateTimeOffset, Task<FpsResult>>? stopFps = null;
        string? fpsProblem = null;
        if (game is not null && FpsBox.IsChecked == true)
        {
            StartButton.IsEnabled = false;
            ElapsedText.Text = "Venter på Windows' spørgsmål om administrator-tilladelse…";
            (stopFps, fpsProblem) = await Task.Run(() => Services.StartFps(game.ProcessName));
        }

        _live.Clear();
        _shown = null;
        Chart.Samples = _live;
        SummaryText.Text = "";
        SetRecording(true);
        ElapsedText.Text = $"Optager… 0:00 / {Max}";

        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        var recorder = new PerfRecorder(_sampler);
        // Sampling and saving run off the UI thread, so a game in front does not slow them down.
        _recording = Task.Run(async () =>
        {
            PerfRecording r = await recorder.RunAsync(s => Dispatcher.UIThread.Post(() => OnSample(s)), ct);
            if (game is not null)
            {
                r.Game = game.DisplayName;
                r.GameProcess = game.ProcessName;
                if (stopFps is not null)
                {
                    Dispatcher.UIThread.Post(() => ElapsedText.Text = "Beregner FPS…");
                    r.Fps = await stopFps(r.Started);
                }
                else if (fpsProblem is not null)
                    r.Fps = FpsResult.NotMeasured(fpsProblem);
            }
            string? saveError = null;
            try
            {
                if (r.Samples.Count > 0)
                    _store.Save(r);
            }
            catch (Exception e) { saveError = e.Message; }
            Dispatcher.UIThread.Post(() => OnFinished(r, saveError));
        });
    }

    void OnSample(PerfSample s)
    {
        _live.Add(s);
        Chart.InvalidateVisual();
        ElapsedText.Text = $"Optager… {Time(s.Seconds)} / {Max}";
    }

    void OnFinished(PerfRecording r, string? saveError)
    {
        _stop?.Dispose();
        _stop = null;
        _recording = null;
        SetRecording(false);
        if (r.Samples.Count == 0)
        {
            ElapsedText.Text = "Intet optaget.";
            return;
        }
        if (saveError is not null)
        {
            // Still show it, it just is not kept for later.
            ElapsedText.Text = $"Kunne ikke gemme optagelsen: {saveError}";
            Display(r);
            return;
        }
        ElapsedText.Text = r.StopReason == "limit" ? "Stoppet ved 15 minutter." : "Optagelse gemt.";
        FillList(select: r.Id);
        Display(r);
        if (_gameMode && r.Game is not null)
            Ask();
    }

    void SetRecording(bool on)
    {
        StartButton.IsEnabled = !on && _sampler is not null;
        StopButton.IsEnabled = on;
        RecordingBox.IsEnabled = !on;
        GameBox.IsEnabled = RefreshGamesButton.IsEnabled = !on;
        FpsBox.IsEnabled = !on && OperatingSystem.IsWindows() && SelectedGame is not null;
        AskButton.IsEnabled = !on && _shown is not null;
    }

    void FillList(string? select)
    {
        _filling = true;
        var entries = _store.ListIds().Select(id => new Entry(id)).ToList();
        RecordingBox.ItemsSource = entries;
        RecordingBox.SelectedItem = entries.FirstOrDefault(e => e.Id == select);
        _filling = false;
    }

    void Display(PerfRecording r)
    {
        _shown = r;
        Chart.Samples = r.Samples;
        SummaryText.Text = Describe(r);
        AskButton.IsEnabled = _recording is null;
    }

    void Ask()
    {
        if (_shown is null)
            return;
        string q = _shown.Game is { } g
            ? $"Hvorfor hakker {g}? Kig på min optagelse {_shown.Id} fra {_shown.Started.LocalDateTime:dd-MM-yyyy HH:mm} " +
              $"({Time(_shown.DurationSeconds)}), mens jeg spillede. Hvad holdt spillet tilbage, og hvad kan jeg gøre ved det?"
            : $"Kig på min ydelsesoptagelse {_shown.Id} fra {_shown.Started.LocalDateTime:dd-MM-yyyy HH:mm} " +
              $"({Time(_shown.DurationSeconds)}). Hvad begrænsede ydelsen, og hvad kan jeg gøre ved det?";
        if (_ask(q))
            ElapsedText.Text = "Spørgsmålet er sendt til PC Doctor i hovedvinduet.";
        else
            ElapsedText.Text = "PC Doctor er optaget. Vent til den er færdig, og prøv igen.";
    }

    /// <summary>Plain-language summary shown under the chart.</summary>
    public static string Describe(PerfRecording r)
    {
        var s = PerfSummary.From(r);
        var b = new StringBuilder();
        if (r.Game is not null)
        {
            b.AppendLine($"Spil: {r.Game}");
            if (r.Fps is { Measured: true } f)
                b.AppendLine($"FPS: gennemsnit {f.AverageFps:0}, bedste sekund {f.MaxFps:0}, værste sekund {f.MinFps:0}, " +
                             $"1 % low {f.OnePercentLowFps:0}, 0,1 % low {f.PointOnePercentLowFps:0}, {f.Stutters} hak (billeder over {Math.Max(2.5 * f.MedianFrameMs, 40):0} ms)");
            else if (r.Fps is { } nf)
                b.AppendLine($"FPS: ikke målt. {nf.Problem}");
            if (s.GameCpu is { } gc)
                b.AppendLine($"Spillets eget forbrug: CPU gennemsnit {gc.Average:0} %, top {gc.Max:0} %" +
                             (s.GameGpu is { } gg ? $"; grafikkort gennemsnit {gg.Average:0} %, top {gg.Max:0} %" : "") +
                             (s.MaxGameRamGb is { } gr ? $"; RAM højst {gr:0.0} GB" : ""));
            else
                b.AppendLine("Spillet kørte ikke under optagelsen.");
        }
        b.AppendLine($"Varighed {Time(s.DurationSeconds)} ({s.SampleCount} målinger), stoppet {(r.StopReason == "limit" ? "ved 15-minutters grænsen" : "af dig")}.");
        b.AppendLine(Line("CPU ", s.Cpu));
        b.AppendLine(Line("GPU ", s.Gpu));
        b.AppendLine(Line("RAM ", s.Ram) + $" (højst {s.MaxRamUsedGb:0.0} GB i brug)");
        b.AppendLine(Line("Disk", s.Disk));
        if (s.CpuSpeed is { } speed)
            b.AppendLine($"CPU-hastighed: gennemsnit {speed.Average:0} % af normal, laveste {speed.Min:0} %" +
                         (speed.SlowedPercentOfBusyTime >= 20 ? $" (sat ned i {speed.SlowedPercentOfBusyTime:0} % af tiden den havde travlt: varme eller strømsparing?)" : ""));
        if (s.MaxGpuMemoryGb is { } vram)
            b.AppendLine($"Grafikhukommelse (VRAM): højst {vram:0.0} GB i brug");
        if (s.Ping is { } ping)
            b.AppendLine(ping.LostPercent >= 100
                ? "Internet-ping: intet svar (netværket blokerer måske ping)"
                : $"Internet-ping: gennemsnit {ping.AverageMs:0} ms, højst {ping.MaxMs:0} ms, {ping.Spikes} lag-spidser over 150 ms, {ping.LostPercent:0.#} % tabt");
        if (s.TopProcesses.Count > 0)
            b.AppendLine("Brugte mest CPU: " + string.Join(", ", s.TopProcesses.Select(p => $"{p.Name} ({p.PercentOfTime:0} % af tiden)")));
        if (s.TopGpuProcesses is { Count: > 0 } gpu)
            b.AppendLine("Brugte mest grafikkort: " + string.Join(", ", gpu.Select(p => $"{p.Name} ({p.PercentOfTime:0} % af tiden)")));
        return b.ToString().TrimEnd();
    }

    static string Line(string name, MetricSummary? m) =>
        m is null
            ? $"{name}: ikke tilgængelig på denne PC"
            : $"{name}: gennemsnit {m.Average,5:0} %, top {m.Max,5:0} %, over 90 % i {m.PercentOfTimeAbove90:0} % af tiden";

    static string Max => Time(PerfRecorder.MaxDuration.TotalSeconds);

    static string Time(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    /// <summary>A saved recording in the drop-down, shown as its date and time.</summary>
    sealed record Entry(string Id)
    {
        public override string ToString() =>
            DateTime.TryParseExact(Id, "'rec-'yyyyMMdd-HHmmss'.json'", null, System.Globalization.DateTimeStyles.None, out var t)
                ? t.ToString("dd-MM-yyyy  HH:mm:ss")
                : Id;
    }
}
