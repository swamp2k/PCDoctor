using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.App;

/// <summary>Start/stop a performance recording, then review it as gauges, a chart and a summary.</summary>
public partial class RecordingPage : UserControl
{
    readonly IPerfSampler? _sampler;
    readonly PerfRecordingStore _store;
    readonly Func<string, bool> _ask;
    readonly List<PerfSample> _live = [];
    readonly bool _gameMode;

    /// <summary>First entry of the game list: a normal recording that follows no particular game.</summary>
    static RunningProgram WholePc => new("", L.T("Hele computeren (intet bestemt spil)", "Whole computer (no specific game)",
        "Hela datorn (inget särskilt spel)", "Hele PC-en (ikke noe bestemt spill)", "Ganzer PC (kein bestimmtes Spiel)"), false);

    CancellationTokenSource? _stop;
    Task? _recording;
    PerfRecording? _shown;
    bool _filling;
    double? _lastPing;
    bool _lastLost;

    public RecordingPage() : this(null, new PerfRecordingStore(), _ => false) { }

    /// <param name="ask">Sends a question to the chat. Returns false if PC Doctor is busy.</param>
    /// <param name="gameMode">
    /// Opened as "Hvorfor hakker mit spil?": pick a running game, FPS on by default, and PC Doctor is asked automatically when the recording stops.
    /// </param>
    public RecordingPage(IPerfSampler? sampler, PerfRecordingStore store, Func<string, bool> ask, bool gameMode = false)
    {
        InitializeComponent();
        _sampler = sampler;
        _store = store;
        _ask = ask;
        _gameMode = gameMode;

        ShowTitle();
        LiveLabels();
        if (gameMode)
        {
            // The game page has a network monitor and a race view, which can be swapped for the normal graphs.
            NetCard.IsVisible = ViewToggle.IsVisible = true;
            NetGraph.Samples = _live;
            ShowRace(true);
            RaceButton.Click += (_, _) => ShowRace(true);
            GraphsButton.Click += (_, _) => ShowRace(false);
            ShowNetwork(null);
        }
        else
            Race.IsVisible = false;
        if (gameMode)
            FpsBox.IsChecked = OperatingSystem.IsWindows();
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

        if (_sampler is null)
        {
            StartButton.IsEnabled = false;
            ShowOnlyOnWindows();
        }
        FillList(select: null);

        // A new language: the texts set from code are set again, and a recording shown is described again.
        L.Changed += () =>
        {
            ShowTitle();
            if (_sampler is null)
                ShowOnlyOnWindows();
            if (_recording is null)
                RefreshGames();
            if (_shown is not null && _recording is null)
                Display(_shown);
            else
                LiveLabels();
        };
    }

    /// <summary>The race (or the normal graphs when <paramref name="race"/> is false); the button of the one shown is orange.</summary>
    void ShowRace(bool race)
    {
        RaceCard.IsVisible = race;
        ChartCard.IsVisible = !race;
        RaceButton.Classes.Set("accent", race);
        GraphsButton.Classes.Set("accent", !race);
    }

    /// <summary>The network monitor from a live sample, or from nothing (a dash). A ping that is still on its way keeps the last answer.</summary>
    void ShowNetwork(PerfSample? s)
    {
        if (s is null)
            (_lastPing, _lastLost) = (null, false);
        else if (s.PingMs is not null || s.PingLost)
            (_lastPing, _lastLost) = (s.PingMs, s.PingLost);
        DrawNetwork(s?.NetMbps is { } mbps ? NetTraffic(mbps) : "");
    }

    void DrawNetwork(string detail)
    {
        var light = _lastLost ? Light.Red : _lastPing is null ? Light.None : _lastPing >= 150 ? Light.Red : _lastPing >= 60 ? Light.Amber : Light.Green;
        NetDot.Fill = Status.Of(light);
        NetPingText.Text = _lastLost ? L.T("Intet svar", "No answer", "Inget svar", "Ikke noe svar", "Keine Antwort") : _lastPing is { } p ? $"{p:0} ms" : "–";
        NetDetail.Text = detail;
    }

    static string NetTraffic(double mbps) =>
        L.T($"Trafik lige nu: {mbps:0.0} Mbit/s", $"Traffic right now: {mbps:0.0} Mbit/s", $"Trafik just nu: {mbps:0.0} Mbit/s",
            $"Trafikk akkurat nå: {mbps:0.0} Mbit/s", $"Datenverkehr jetzt: {mbps:0.0} Mbit/s");

    void ShowOnlyOnWindows() =>
        ElapsedText.Text = L.T("Optagelse virker kun på Windows.", "Recording only works on Windows.", "Inspelning fungerar bara på Windows.",
            "Opptak virker bare på Windows.", "Aufzeichnen funktioniert nur unter Windows.");

    void ShowTitle()
    {
        TitleText.Text = _gameMode
            ? L.T("Hvorfor hakker mit spil?", "Why does my game stutter?", "Varför hackar mitt spel?", "Hvorfor hakker spillet mitt?", "Warum ruckelt mein Spiel?")
            : L.T("Optag ydelse", "Record performance", "Spela in prestanda", "Ta opp ytelse", "Leistung aufzeichnen");
        IntroText.Text = _gameMode
            ? L.T("Start spillet og vælg det her. Tryk Start, og spil til det hakker. Kom tilbage og tryk Stop for at se resultatet. Vil du have det forklaret, så tryk Spørg PC Doctor.",
                  "Start the game and pick it here. Press Start and play until it stutters. Come back and press Stop to see the result. Press Ask PC Doctor if you want it explained.",
                  "Starta spelet och välj det här. Tryck Starta och spela tills det hackar. Kom tillbaka och tryck Stopp för att se resultatet. Vill du ha det förklarat, tryck Fråga PC Doctor.",
                  "Start spillet og velg det her. Trykk Start, og spill til det hakker. Kom tilbake og trykk Stopp for å se resultatet. Vil du ha det forklart, trykk Spør PC Doctor.",
                  "Starte das Spiel und wähle es hier aus. Drücke Start und spiele, bis es ruckelt. Komm zurück und drücke Stopp, um das Ergebnis zu sehen. Möchtest du es erklärt haben, drücke PC Doctor fragen.")
            : L.T("Tryk Start, brug PC'en som normalt, og tryk Stop. Stopper selv efter 15 minutter.",
                  "Press Start, use the PC as usual, and press Stop. Stops by itself after 15 minutes.",
                  "Tryck Starta, använd datorn som vanligt och tryck Stopp. Stannar av sig själv efter 15 minuter.",
                  "Trykk Start, bruk PC-en som vanlig, og trykk Stopp. Stopper av seg selv etter 15 minutter.",
                  "Drücke Start, benutze den PC wie gewohnt und drücke Stopp. Stoppt nach 15 Minuten von selbst.");
    }

    static string GpuName => L.T("Grafikkort", "Graphics card", "Grafikkort", "Skjermkort", "Grafikkarte");

    /// <summary>The gauge names while recording (live values).</summary>
    void LiveLabels() => (CpuTile.Label, GpuTile.Label, RamTile.Label, DiskTile.Label) = ("CPU", GpuName, "RAM", "Disk");

    /// <summary>Called when the app closes. Closing mid-recording keeps what was recorded so far.</summary>
    public void Shutdown()
    {
        _stop?.Cancel();
        _recording?.Wait(TimeSpan.FromSeconds(3));
    }

    RunningProgram? SelectedGame => GameBox.SelectedItem is RunningProgram { ProcessName.Length: > 0 } g ? g : null;

    void RefreshGames()
    {
        var previous = SelectedGame?.ProcessName;
        bool wholePc = GameBox.SelectedItem is RunningProgram { ProcessName.Length: 0 };
        var list = new List<RunningProgram> { WholePc };
        try { list.AddRange(RunningGames.List()); } catch (Exception) { /* the list is a convenience; whole-PC recording still works */ }
        GameBox.ItemsSource = list;
        // Keep the choice; otherwise in game mode pick the first known game that is running.
        GameBox.SelectedItem = list.FirstOrDefault(g => g.ProcessName == previous && previous is not null)
                               ?? (_gameMode && !wholePc ? list.FirstOrDefault(g => g.KnownGame) : null)
                               ?? list[0];
    }

    async Task StartAsync()
    {
        if (_sampler is null || _recording is not null)
            return;

        var game = SelectedGame;
        if (_gameMode && game is null)
        {
            ElapsedText.Text = L.T("Vælg spillet først. Start det, og tryk på knappen ved siden af listen for at opdatere den.",
                "Pick the game first. Start it, and press the button next to the list to refresh it.",
                "Välj spelet först. Starta det och tryck på knappen bredvid listan för att uppdatera den.",
                "Velg spillet først. Start det, og trykk på knappen ved siden av listen for å oppdatere den.",
                "Wähle zuerst das Spiel. Starte es und drücke die Schaltfläche neben der Liste, um sie zu aktualisieren.");
            return;
        }
        _sampler.Game = game?.ProcessName;

        // FPS needs administrator rights, so it runs in a separate helper Windows asks permission for.
        Func<DateTimeOffset, Task<FpsResult>>? stopFps = null;
        string? fpsProblem = null;
        if (game is not null && FpsBox.IsChecked == true)
        {
            StartButton.IsEnabled = false;
            ElapsedText.Text = L.T("Venter på Windows' spørgsmål om administrator-tilladelse…", "Waiting for Windows to ask for administrator permission…",
                "Väntar på att Windows frågar om administratörsbehörighet…", "Venter på at Windows spør om administratortillatelse…",
                "Warte darauf, dass Windows nach der Administratorberechtigung fragt…");
            (stopFps, fpsProblem) = await Task.Run(() => Services.StartFps(game.ProcessName));
        }

        _live.Clear();
        _shown = null;
        Chart.Samples = _live;
        NetGraph.Samples = _live;
        ShowNetwork(null);
        Race.Reset();
        SummaryText.Text = "";
        FpsCard.IsVisible = false;
        LiveLabels();
        SetRecording(true);
        ElapsedText.Text = $"{Recording} 0:00 / {Max}";

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
                    Dispatcher.UIThread.Post(() => ElapsedText.Text = L.T("Beregner FPS…", "Calculating FPS…", "Beräknar FPS…", "Beregner FPS…", "Berechne FPS…"));
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
        NetGraph.InvalidateVisual();
        ShowNetwork(s);
        Race.Update(s.Cpu, s.Gpu, s.Ram, s.Disk, RaceScore.NetworkLag(_lastPing, _lastLost), PingText());
        CpuTile.Show(s.Cpu);
        GpuTile.Show(s.Gpu);
        RamTile.Show(s.Ram);
        DiskTile.Show(s.Disk);
        ElapsedText.Text = $"{Recording} {Time(s.Seconds)} / {Max}";
    }

    string? PingText() =>
        _lastLost ? L.T("intet svar", "no answer", "inget svar", "ikke noe svar", "keine Antwort") : _lastPing is { } p ? $"{p:0} ms" : null;

    void OnFinished(PerfRecording r, string? saveError)
    {
        _stop?.Dispose();
        _stop = null;
        _recording = null;
        SetRecording(false);
        if (r.Samples.Count == 0)
        {
            ElapsedText.Text = L.T("Intet optaget.", "Nothing recorded.", "Inget inspelat.", "Ingenting tatt opp.", "Nichts aufgezeichnet.");
            return;
        }
        if (saveError is not null)
        {
            // Still show it, it just is not kept for later.
            ElapsedText.Text = L.T("Kunne ikke gemme optagelsen: ", "Could not save the recording: ", "Kunde inte spara inspelningen: ",
                "Kunne ikke lagre opptaket: ", "Die Aufzeichnung konnte nicht gespeichert werden: ") + saveError;
            Display(r);
            return;
        }
        ElapsedText.Text = r.StopReason == "limit"
            ? L.T("Stoppet ved 15 minutter.", "Stopped at 15 minutes.", "Stoppad vid 15 minuter.", "Stoppet ved 15 minutter.", "Bei 15 Minuten gestoppt.")
            : L.T("Optagelse gemt.", "Recording saved.", "Inspelningen sparad.", "Opptaket er lagret.", "Aufzeichnung gespeichert.");
        FillList(select: r.Id);
        Display(r);
        // The analysis only starts when the user asks for it themselves.
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
        NetGraph.Samples = r.Samples;
        SummaryText.Text = Describe(r);
        AskButton.IsEnabled = _recording is null;

        // Averages, coloured by how much of the time each part was close to 100 %.
        var s = PerfSummary.From(r);
        string avg = L.T("snit", "average", "snitt", "snitt", "Schnitt");
        CpuTile.Label = $"CPU {avg}";
        GpuTile.Label = $"{GpuName} {avg}";
        RamTile.Label = $"RAM {avg}";
        DiskTile.Label = $"Disk {avg}";
        CpuTile.Show(s.Cpu.Average, Status.OfBusyTime(s.Cpu));
        GpuTile.Show(s.Gpu?.Average, Status.OfBusyTime(s.Gpu));
        RamTile.Show(s.Ram.Average, Status.OfBusyTime(s.Ram));
        DiskTile.Show(s.Disk?.Average, Status.OfBusyTime(s.Disk));

        // The race shows the averages of the recording; the network monitor shows the ping average.
        var ping = s.Ping;
        Race.Update(s.Cpu.Average, s.Gpu?.Average, s.Ram.Average, s.Disk?.Average,
            ping is null ? null : RaceScore.NetworkLag(ping.LostPercent >= 100 ? null : ping.AverageMs, ping.LostPercent >= 100),
            ping is null ? null : ping.LostPercent >= 100 ? PingText() : $"{ping.AverageMs:0} ms");
        if (_gameMode)
        {
            (_lastPing, _lastLost) = (ping is null || ping.LostPercent >= 100 ? null : ping.AverageMs, ping?.LostPercent >= 100);
            var traffic = r.Samples.Where(x => x.NetMbps is not null).Select(x => x.NetMbps!.Value).ToList();
            DrawNetwork(traffic.Count == 0 ? "" : L.T($"Gennemsnit: {traffic.Average():0.0} Mbit/s, højst {traffic.Max():0.0} Mbit/s",
                $"Average: {traffic.Average():0.0} Mbit/s, at most {traffic.Max():0.0} Mbit/s", $"Genomsnitt: {traffic.Average():0.0} Mbit/s, som mest {traffic.Max():0.0} Mbit/s",
                $"Gjennomsnitt: {traffic.Average():0.0} Mbit/s, maks {traffic.Max():0.0} Mbit/s", $"Durchschnitt: {traffic.Average():0.0} Mbit/s, höchstens {traffic.Max():0.0} Mbit/s"));
        }

        FpsCard.IsVisible = r.Fps is not null;
        if (r.Fps is { Measured: true } f)
        {
            FpsText.Text = $"{f.AverageFps:0} {avg}";
            string low = $"{f.OnePercentLowFps:0}";
            FpsDetail.Text = L.T($"{low} i 1 % low · {f.Stutters} hak", $"{low} in 1 % low · {f.Stutters} stutters", $"{low} i 1 % low · {f.Stutters} hack",
                $"{low} i 1 % low · {f.Stutters} hakk", $"{low} bei 1 % low · {f.Stutters} Ruckler");
            FpsDot.Fill = Status.Of(Status.OfFps(f.AverageFps));
        }
        else if (r.Fps is { } nf)
        {
            FpsText.Text = "–";
            FpsDetail.Text = L.T("Ikke målt", "Not measured", "Inte mätt", "Ikke målt", "Nicht gemessen");
            ToolTip.SetTip(FpsCard, nf.Problem);
            FpsDot.Fill = Status.Of(Light.None);
        }
    }

    void Ask()
    {
        if (_shown is null)
            return;
        string id = _shown.Id, when = $"{_shown.Started.LocalDateTime:dd-MM-yyyy HH:mm}", length = Time(_shown.DurationSeconds);
        string q = _shown.Game is { } g
            ? L.T($"Hvorfor hakker {g}? Kig på min optagelse {id} fra {when} ({length}), mens jeg spillede. Hvad holdt spillet tilbage, og hvad kan jeg gøre ved det?",
                  $"Why does {g} stutter? Look at my recording {id} from {when} ({length}) while I was playing. What held the game back, and what can I do about it?",
                  $"Varför hackar {g}? Titta på min inspelning {id} från {when} ({length}) medan jag spelade. Vad höll tillbaka spelet, och vad kan jag göra åt det?",
                  $"Hvorfor hakker {g}? Se på opptaket mitt {id} fra {when} ({length}) mens jeg spilte. Hva holdt spillet tilbake, og hva kan jeg gjøre med det?",
                  $"Warum ruckelt {g}? Sieh dir meine Aufzeichnung {id} vom {when} ({length}) an, während ich gespielt habe. Was hat das Spiel ausgebremst, und was kann ich dagegen tun?")
            : L.T($"Kig på min ydelsesoptagelse {id} fra {when} ({length}). Hvad begrænsede ydelsen, og hvad kan jeg gøre ved det?",
                  $"Look at my performance recording {id} from {when} ({length}). What limited the performance, and what can I do about it?",
                  $"Titta på min prestandainspelning {id} från {when} ({length}). Vad begränsade prestandan, och vad kan jag göra åt det?",
                  $"Se på ytelsesopptaket mitt {id} fra {when} ({length}). Hva begrenset ytelsen, og hva kan jeg gjøre med det?",
                  $"Sieh dir meine Leistungsaufzeichnung {id} vom {when} ({length}) an. Was hat die Leistung begrenzt, und was kann ich dagegen tun?");
        ElapsedText.Text = _ask(q) ? CommonText.QuestionSent : CommonText.Busy;
    }

    /// <summary>Plain-language summary shown under the chart, in the language picked.</summary>
    public static string Describe(PerfRecording r)
    {
        var s = PerfSummary.From(r);
        var b = new StringBuilder();
        string avg = L.T("gennemsnit", "average", "genomsnitt", "gjennomsnitt", "Durchschnitt");
        string top = L.T("top", "peak", "topp", "topp", "Spitze");
        string atMost = L.T("højst", "at most", "som mest", "maks", "höchstens");
        if (r.Game is not null)
        {
            b.AppendLine($"{L.T("Spil", "Game", "Spel", "Spill", "Spiel")}: {r.Game}");
            if (r.Fps is { Measured: true } f)
            {
                string a = $"{f.AverageFps:0}", best = $"{f.MaxFps:0}", worst = $"{f.MinFps:0}", low1 = $"{f.OnePercentLowFps:0}";
                string low01 = f.PointOnePercentLowFps.ToString("0", L.Culture), ms = $"{Math.Max(2.5 * f.MedianFrameMs, 40):0}";
                b.AppendLine(L.T(
                    $"FPS: gennemsnit {a}, bedste sekund {best}, værste sekund {worst}, 1 % low {low1}, 0,1 % low {low01}, {f.Stutters} hak (billeder over {ms} ms)",
                    $"FPS: average {a}, best second {best}, worst second {worst}, 1 % low {low1}, 0.1 % low {low01}, {f.Stutters} stutters (frames over {ms} ms)",
                    $"FPS: genomsnitt {a}, bästa sekund {best}, sämsta sekund {worst}, 1 % low {low1}, 0,1 % low {low01}, {f.Stutters} hack (bilder över {ms} ms)",
                    $"FPS: gjennomsnitt {a}, beste sekund {best}, verste sekund {worst}, 1 % low {low1}, 0,1 % low {low01}, {f.Stutters} hakk (bilder over {ms} ms)",
                    $"FPS: Durchschnitt {a}, bester Sekundenwert {best}, schlechtester Sekundenwert {worst}, 1 % low {low1}, 0,1 % low {low01}, {f.Stutters} Ruckler (Bilder über {ms} ms)"));
            }
            else if (r.Fps is { } nf)
                b.AppendLine(L.T("FPS: ikke målt.", "FPS: not measured.", "FPS: inte mätt.", "FPS: ikke målt.", "FPS: nicht gemessen.") + $" {nf.Problem}");
            if (s.GameCpu is { } gc)
                b.AppendLine(L.T("Spillets eget forbrug", "The game's own usage", "Spelets egen belastning", "Spillets eget forbruk", "Verbrauch des Spiels selbst") +
                             $": CPU {avg} {gc.Average:0} %, {top} {gc.Max:0} %" +
                             (s.GameGpu is { } gg ? $"; {GpuName.ToLower(L.Culture)} {avg} {gg.Average:0} %, {top} {gg.Max:0} %" : "") +
                             (s.MaxGameRamGb is { } gr ? $"; RAM {atMost} {gr:0.0} GB" : ""));
            else
                b.AppendLine(L.T("Spillet kørte ikke under optagelsen.", "The game was not running during the recording.", "Spelet körde inte under inspelningen.",
                    "Spillet kjørte ikke under opptaket.", "Das Spiel lief während der Aufzeichnung nicht."));
        }
        bool limit = r.StopReason == "limit";
        string length = Time(s.DurationSeconds);
        int n = s.SampleCount;
        b.AppendLine(L.T(
            $"Varighed {length} ({n} målinger), stoppet {(limit ? "ved 15-minutters grænsen" : "af dig")}.",
            $"Duration {length} ({n} samples), stopped {(limit ? "at the 15-minute limit" : "by you")}.",
            $"Längd {length} ({n} mätningar), stoppad {(limit ? "vid 15-minutersgränsen" : "av dig")}.",
            $"Varighet {length} ({n} målinger), stoppet {(limit ? "ved 15-minuttersgrensen" : "av deg")}.",
            $"Dauer {length} ({n} Messungen), gestoppt {(limit ? "beim 15-Minuten-Limit" : "von dir")}."));
        b.AppendLine(Line("CPU ", s.Cpu));
        b.AppendLine(Line("GPU ", s.Gpu));
        string ramUsed = $"{s.MaxRamUsedGb:0.0}";
        b.AppendLine(Line("RAM ", s.Ram) + L.T($" (højst {ramUsed} GB i brug)", $" (at most {ramUsed} GB in use)", $" (som mest {ramUsed} GB använt)",
            $" (maks {ramUsed} GB i bruk)", $" (höchstens {ramUsed} GB belegt)"));
        b.AppendLine(Line("Disk", s.Disk));
        if (s.CpuSpeed is { } speed)
        {
            string a = $"{speed.Average:0}", low = $"{speed.Min:0}", slowed = $"{speed.SlowedPercentOfBusyTime:0}";
            b.AppendLine(L.T($"CPU-hastighed: gennemsnit {a} % af normal, laveste {low} %", $"CPU speed: average {a} % of normal, lowest {low} %",
                             $"CPU-hastighet: genomsnitt {a} % av normal, lägsta {low} %", $"CPU-hastighet: gjennomsnitt {a} % av normal, laveste {low} %",
                             $"CPU-Takt: Durchschnitt {a} % des Normalwerts, niedrigster {low} %") +
                         (speed.SlowedPercentOfBusyTime >= 20
                             ? L.T($" (sat ned i {slowed} % af tiden den havde travlt: varme eller strømsparing?)",
                                   $" (slowed down for {slowed} % of the busy time: heat or power saving?)",
                                   $" (nedsatt under {slowed} % av tiden den var upptagen: värme eller energisparläge?)",
                                   $" (satt ned i {slowed} % av tiden den hadde mye å gjøre: varme eller strømsparing?)",
                                   $" (gedrosselt während {slowed} % der ausgelasteten Zeit: Hitze oder Energiesparen?)")
                             : ""));
        }
        if (s.MaxGpuMemoryGb is { } vram)
            b.AppendLine(L.T("Grafikhukommelse (VRAM)", "Graphics memory (VRAM)", "Grafikminne (VRAM)", "Grafikkminne (VRAM)", "Grafikspeicher (VRAM)") +
                         $": {atMost} {vram:0.0} GB " + L.T("i brug", "in use", "använt", "i bruk", "belegt"));
        if (s.Ping is { } ping)
        {
            string a = $"{ping.AverageMs:0}", max = $"{ping.MaxMs:0}", lost = $"{ping.LostPercent:0.#}";
            b.AppendLine(ping.LostPercent >= 100
                ? L.T("Internet-ping: intet svar (netværket blokerer måske ping)", "Internet ping: no answer (the network may block ping)",
                      "Internet-ping: inget svar (nätverket blockerar kanske ping)", "Internett-ping: ikke noe svar (nettverket blokkerer kanskje ping)",
                      "Internet-Ping: keine Antwort (das Netzwerk blockiert Ping vielleicht)")
                : L.T($"Internet-ping: gennemsnit {a} ms, højst {max} ms, {ping.Spikes} lag-spidser over 150 ms, {lost} % tabt",
                      $"Internet ping: average {a} ms, at most {max} ms, {ping.Spikes} lag spikes over 150 ms, {lost} % lost",
                      $"Internet-ping: genomsnitt {a} ms, som mest {max} ms, {ping.Spikes} laggtoppar över 150 ms, {lost} % förlorat",
                      $"Internett-ping: gjennomsnitt {a} ms, maks {max} ms, {ping.Spikes} lag-topper over 150 ms, {lost} % tapt",
                      $"Internet-Ping: Durchschnitt {a} ms, höchstens {max} ms, {ping.Spikes} Lag-Spitzen über 150 ms, {lost} % verloren"));
        }
        if (s.TopProcesses.Count > 0)
            b.AppendLine(L.T("Brugte mest CPU: ", "Used the most CPU: ", "Använde mest CPU: ", "Brukte mest CPU: ", "Meiste CPU: ") +
                         string.Join(", ", s.TopProcesses.Select(p => $"{p.Name} ({OfTheTime(p.PercentOfTime)})")));
        if (s.TopGpuProcesses is { Count: > 0 } gpu)
            b.AppendLine(L.T("Brugte mest grafikkort: ", "Used the graphics card the most: ", "Använde grafikkortet mest: ", "Brukte skjermkortet mest: ",
                             "Meiste Grafikkarte: ") +
                         string.Join(", ", gpu.Select(p => $"{p.Name} ({OfTheTime(p.PercentOfTime)})")));
        return b.ToString().TrimEnd();
    }

    static string OfTheTime(double percent)
    {
        string p = $"{percent:0}";
        return L.T($"{p} % af tiden", $"{p} % of the time", $"{p} % av tiden", $"{p} % av tiden", $"{p} % der Zeit");
    }

    static string Line(string name, MetricSummary? m)
    {
        if (m is null)
            return $"{name}: " + L.T("ikke tilgængelig på denne PC", "not available on this PC", "inte tillgänglig på den här datorn",
                "ikke tilgjengelig på denne PC-en", "auf diesem PC nicht verfügbar");
        string a = $"{m.Average,5:0}", top = $"{m.Max,5:0}", above = $"{m.PercentOfTimeAbove90:0}";
        return $"{name}: " + L.T($"gennemsnit {a} %, top {top} %, over 90 % i {above} % af tiden",
            $"average {a} %, peak {top} %, over 90 % for {above} % of the time",
            $"genomsnitt {a} %, topp {top} %, över 90 % under {above} % av tiden",
            $"gjennomsnitt {a} %, topp {top} %, over 90 % i {above} % av tiden",
            $"Durchschnitt {a} %, Spitze {top} %, über 90 % während {above} % der Zeit");
    }

    static string Recording => L.T("Optager…", "Recording…", "Spelar in…", "Tar opp…", "Zeichne auf…");

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
