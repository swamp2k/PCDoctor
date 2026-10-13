using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>Start/stop a performance recording, then review it as a chart and a summary.</summary>
public partial class RecordingWindow : Window
{
    readonly IPerfSampler? _sampler;
    readonly PerfRecordingStore _store;
    readonly Func<string, bool> _ask;
    readonly List<PerfSample> _live = [];

    CancellationTokenSource? _stop;
    Task? _recording;
    PerfRecording? _shown;
    bool _filling;

    public RecordingWindow() : this(null, new PerfRecordingStore(), _ => false) { }

    /// <param name="ask">Sends a question to the chat. Returns false if PC Doctor is busy.</param>
    public RecordingWindow(IPerfSampler? sampler, PerfRecordingStore store, Func<string, bool> ask)
    {
        InitializeComponent();
        _sampler = sampler;
        _store = store;
        _ask = ask;

        StartButton.Click += (_, _) => Start();
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

    void Start()
    {
        if (_sampler is null || _recording is not null)
            return;

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
            if (r.Samples.Count > 0)
                _store.Save(r);
            Dispatcher.UIThread.Post(() => OnFinished(r));
        });
    }

    void OnSample(PerfSample s)
    {
        _live.Add(s);
        Chart.InvalidateVisual();
        ElapsedText.Text = $"Optager… {Time(s.Seconds)} / {Max}";
    }

    void OnFinished(PerfRecording r)
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
        ElapsedText.Text = r.StopReason == "limit" ? "Stoppet ved 15 minutter." : "Optagelse gemt.";
        FillList(select: r.Id);
        Display(r);
    }

    void SetRecording(bool on)
    {
        StartButton.IsEnabled = !on && _sampler is not null;
        StopButton.IsEnabled = on;
        RecordingBox.IsEnabled = !on;
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
        string q = $"Kig på min ydelsesoptagelse {_shown.Id} fra {_shown.Started.LocalDateTime:dd-MM-yyyy HH:mm} " +
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
        b.AppendLine($"Varighed {Time(s.DurationSeconds)} ({s.SampleCount} målinger), stoppet {(r.StopReason == "limit" ? "ved 15-minutters grænsen" : "af dig")}.");
        b.AppendLine(Line("CPU ", s.Cpu));
        b.AppendLine(Line("GPU ", s.Gpu));
        b.AppendLine(Line("RAM ", s.Ram) + $" (højst {s.MaxRamUsedGb:0.0} GB i brug)");
        b.AppendLine(Line("Disk", s.Disk));
        if (s.TopProcesses.Count > 0)
            b.Append("Brugte mest CPU: " + string.Join(", ", s.TopProcesses.Select(p => $"{p.Name} ({p.PercentOfTime:0} % af tiden)")));
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
