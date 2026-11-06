using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PCDoctor.Agent;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Storage;

namespace PCDoctor.App;

public partial class MainWindow : Window
{
    readonly ObservableCollection<ChatItem> _chat = [];
    readonly ObservableCollection<ActivityItem> _activity = [];
    readonly AgentSettings _settings = Services.LoadSettings();

    /// <summary>Recordings rebuilt at startup because the PC or PC Doctor stopped before Stop was pressed.</summary>
    IReadOnlyList<string> _recovered = [];

    /// <summary>Hardware / graphics card hangs Windows logged since the user last dismissed the notice.</summary>
    List<FaultIncident> _faultIncidents = [];
    readonly ISecretStore _secrets = Services.CreateSecretStore();
    readonly List<ITool> _tools;
    readonly LogReviewSelection _logSelection = new();

    PermissionMode _mode = PermissionMode.ReadOnly;
    IChatModel? _model;
    AgentLoop? _loop;
    readonly SessionStore? _store = Services.OpenHistory();
    long? _sessionId;
    CancellationTokenSource? _running;
    DiagnosticsPage? _diagnosticsPage;
    RecordingPage? _stutterPage, _recordingPage;
    HistoryPage? _historyPage;
    LogReviewPage? _logReviewPage;
    readonly IPerfSampler? _gaugeSampler = Services.CreatePerfSampler();
    readonly DispatcherTimer _gaugeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _sampling;

    /// <summary>
    /// On a small screen such as the Steam Deck's (1280 × 800) the normal window does not fit with its title bar,
    /// so it fills the screen instead.
    /// </summary>
    void FillSmallScreen()
    {
        if (Screens.ScreenFromWindow(this) is { } screen && screen.WorkingArea.Height / screen.Scaling < 860)
            WindowState = WindowState.Maximized;
    }

    public MainWindow()
    {
        L.Current = _settings.Language;
        InitializeComponent();
        _recovered = RecoverRecordings();
        _tools = Services.CreateTools(_settings, _logSelection);
        ChatList.ItemsSource = _chat;
        ActivityList.ItemsSource = _activity;

        SetupLanguagePicker();
        ModeBox.SelectionChanged += (_, _) => SetMode((PermissionMode)Math.Max(0, ModeBox.SelectedIndex));
        SendButton.Click += async (_, _) => await SendAsync();
        StopButton.Click += (_, _) => _running?.Cancel();
        InputBox.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                await SendAsync();
            }
        }, RoutingStrategies.Tunnel);
        SettingsButton.Click += async (_, _) => await OpenSettingsAsync();
        UpdateButton.Click += async (_, _) => await new UpdateWindow().ShowDialog(this);
        Opened += async (_, _) => await CheckForUpdateAtStartupAsync();
        Opened += async (_, _) => await CheckForFaultsAtStartupAsync();
        FaultInvestigateButton.Click += (_, _) =>
        {
            if (Ask(BuildFaultQuestion(_faultIncidents)))
                DismissFaults();
        };
        FaultDismissButton.Click += (_, _) => DismissFaults();
        Opened += (_, _) => FillSmallScreen();
        NewChatButton.Click += (_, _) => NewConversation();
        ChatNavButton.Click += (_, _) => ShowPage(ChatPage, ChatNavButton);
        RecordButton.Click += (_, _) => ShowPage(_recordingPage ??= NewRecordingPage(gameMode: false), RecordButton);
        StutterButton.Click += (_, _) => ShowPage(_stutterPage ??= NewRecordingPage(gameMode: true), StutterButton);
        DiagnosticsButton.Click += (_, _) => ShowPage(_diagnosticsPage ??= new DiagnosticsPage(_tools, Ask), DiagnosticsButton);
        LogReviewButton.Click += (_, _) => ShowPage(_logReviewPage ??= new LogReviewPage(_logSelection, Ask), LogReviewButton);
        HistoryButton.Click += (_, _) =>
        {
            ShowPage(_historyPage ??= new HistoryPage(_store), HistoryButton);
            _historyPage.Reload();
        };
        ShowRequestButton.Click += async (_, _) =>
            await new TextWindow(
                L.T("Seneste API-forespørgsel (hemmeligheder skjult)", "Latest API request (secrets hidden)", "Senaste API-förfrågan (hemligheter dolda)",
                    "Siste API-forespørsel (hemmeligheter skjult)", "Letzte API-Anfrage (Geheimnisse verborgen)"),
                _model?.LastRequestJson is { } json ? SecretMasker.Apply(json, _secrets.Read())
                    : L.T("Ingen forespørgsel sendt endnu.", "No request sent yet.", "Ingen förfrågan skickad än.", "Ingen forespørsel sendt ennå.", "Noch keine Anfrage gesendet.")).ShowDialog(this);

        SetMode(PermissionMode.ReadOnly, log: false);
        RefreshKeyState();
        StatusText.Text = ReadyText;
        // The fixed labels follow the language by themselves; these texts are set from code, so they are set again.
        L.Changed += () =>
        {
            SetMode(_mode, log: false);
            if (FaultBanner.IsVisible)
                FaultText.Text = BuildFaultBanner(_faultIncidents);
            StatusText.Text = _running is null ? ReadyText : BusyText;
            for (int i = 0; i < _chat.Count; i++)
                if (ChatItem.FromEvent(_chat[i].Event) is { } again)
                    _chat[i] = again;
        };

        GaugeRow.IsVisible = _gaugeSampler is not null;
        _gaugeTimer.Tick += async (_, _) => await UpdateGaugesAsync();
        if (_gaugeSampler is not null)
            _gaugeTimer.Start();
        Closing += (_, _) =>
        {
            _gaugeTimer.Stop();
            _diagnosticsPage?.Shutdown();
            _stutterPage?.Shutdown();
            _recordingPage?.Shutdown();
        };
    }

    /// <summary>Shows one page and marks its sidebar button. Pages are kept, so a recording keeps going while you chat.</summary>
    void ShowPage(Control page, Button nav)
    {
        if (!PageHost.Children.Contains(page))
            PageHost.Children.Add(page);
        foreach (var child in PageHost.Children)
            child.IsVisible = child == page;
        foreach (var button in new[] { ChatNavButton, DiagnosticsButton, StutterButton, RecordButton, LogReviewButton, HistoryButton })
            button.Classes.Set("active", button == nav);
    }

    RecordingPage NewRecordingPage(bool gameMode) =>
        new(Services.CreatePerfSampler(), new PerfRecordingStore(), Ask, gameMode);

    /// <summary>The live gauges on the chat page. Only measures while the chat page is shown.</summary>
    async Task UpdateGaugesAsync()
    {
        if (_gaugeSampler is null || _sampling || !ChatPage.IsVisible || !IsActive)
            return;
        _sampling = true;
        try
        {
            var s = await Task.Run(_gaugeSampler.Read);
            CpuTile.Show(s.Cpu);
            GpuTile.Show(s.Gpu);
            RamTile.Show(s.Ram);
            DiskTile.Show(s.Disk);
        }
        catch (Exception) { /* the gauges are a nicety; never let them break the app */ }
        finally { _sampling = false; }
    }

    string ReadyText => _store is null
        ? L.T("Klar. Historikken kunne ikke åbnes, så samtalen bliver ikke gemt.", "Ready. The history could not be opened, so this chat will not be saved.",
              "Klar. Historiken kunde inte öppnas, så samtalet sparas inte.", "Klar. Historikken kunne ikke åpnes, så samtalen blir ikke lagret.",
              "Bereit. Der Verlauf konnte nicht geöffnet werden, daher wird dieser Chat nicht gespeichert.")
        : L.T("Klar. Samtalen gemmes under Historik.", "Ready. The chat is saved under History.", "Klar. Samtalet sparas under Historik.",
              "Klar. Samtalen lagres under Historikk.", "Bereit. Der Chat wird unter Verlauf gespeichert.");

    static string BusyText => CommonText.Investigating;

    static string ModeName(PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "Read-only",
        PermissionMode.Assisted => "Assisted",
        _ => "YOLO",
    };

    void SetMode(PermissionMode mode, bool log = true)
    {
        _mode = mode;
        (ModeText.Text, Light light) = mode switch
        {
            PermissionMode.ReadOnly => (L.T(
                "Read-only: PC Doctor kigger kun og ændrer intet.",
                "Read-only: PC Doctor only looks and changes nothing.",
                "Read-only: PC Doctor tittar bara och ändrar ingenting.",
                "Read-only: PC Doctor ser bare og endrer ingenting.",
                "Read-only: PC Doctor schaut nur und ändert nichts."), Light.Green),
            PermissionMode.Assisted => (L.T(
                "Assisted: PC Doctor må foreslå PowerShell-kommandoer. Du godkender hver eneste én.",
                "Assisted: PC Doctor may suggest PowerShell commands. You approve every single one.",
                "Assisted: PC Doctor får föreslå PowerShell-kommandon. Du godkänner vart och ett.",
                "Assisted: PC Doctor kan foreslå PowerShell-kommandoer. Du godkjenner hver eneste en.",
                "Assisted: PC Doctor darf PowerShell-Befehle vorschlagen. Du bestätigst jeden einzelnen."), Light.Amber),
            _ => (L.T(
                "YOLO: PC Doctor kører PowerShell-kommandoer uden at spørge (ikke som administrator). Det er ikke risikofrit.",
                "YOLO: PC Doctor runs PowerShell commands without asking (not as administrator). This is not without risk.",
                "YOLO: PC Doctor kör PowerShell-kommandon utan att fråga (inte som administratör). Det är inte riskfritt.",
                "YOLO: PC Doctor kjører PowerShell-kommandoer uten å spørre (ikke som administrator). Det er ikke risikofritt.",
                "YOLO: PC Doctor führt PowerShell-Befehle aus, ohne zu fragen (nicht als Administrator). Das ist nicht ohne Risiko."), Light.Red),
        };
        ModeBanner.Background = Status.SoftOf(light);
        ModeDot.Fill = Status.Of(light);
        if (log)
            AddActivity(new AgentEvent(AgentEventKind.Info, L.T($"Tilstand skiftet til {ModeName(mode)}", $"Mode changed to {ModeName(mode)}",
                $"Läge ändrat till {ModeName(mode)}", $"Modus endret til {ModeName(mode)}", $"Modus geändert zu {ModeName(mode)}")));
    }

    void SetupLanguagePicker()
    {
        foreach (AnswerLanguage language in Enum.GetValues<AnswerLanguage>())
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Flags.Create(language));
            row.Children.Add(new TextBlock { Text = language.NativeName(), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            LanguageBox.Items.Add(new ComboBoxItem { Content = row, Tag = language });
        }
        LanguageBox.SelectedIndex = (int)_settings.Language;
        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedItem is not ComboBoxItem { Tag: AnswerLanguage language }) return;
            _settings.Language = language;
            _model?.SetLanguage(language);
            L.Current = language; // the whole app switches
            try { Services.SaveSettings(_settings); } catch (IOException) { /* keep working with the in-memory choice */ }
            AddActivity(new AgentEvent(AgentEventKind.Info, L.T("Sprog", "Language", "Språk", "Språk", "Sprache") + ": " + language.NativeName()));
        };
    }

    void RefreshKeyState()
    {
        string? key = _secrets.Read();
        KeyBanner.IsVisible = string.IsNullOrWhiteSpace(key);
        _model = null;
        _loop = null;
    }

    void NewConversation()
    {
        if (_running is not null) return;
        _model = null;
        _loop = null;
        _chat.Clear();
        _activity.Clear();
        _sessionId = null;
        StatusText.Text = ReadyText;
    }

    async Task SendAsync()
    {
        string question = InputBox.Text?.Trim() ?? "";
        if (question.Length == 0 || _running is not null)
            return;

        string? key = _secrets.Read();
        if (string.IsNullOrWhiteSpace(key))
        {
            KeyBanner.IsVisible = true;
            await OpenSettingsAsync();
            return;
        }

        if (_sessionId is null)
            StartSession(question);

        if (_loop is null)
        {
            var model = new AnthropicChatModel(key, _settings);
            long? sessionId = _sessionId;
            model.Exchanged = x => SaveToHistory(store => store.AddApiCall(sessionId!.Value, x), sessionId);
            _model = model;
            var invoker = new ToolInvoker(_tools, _settings, () => _mode, ApproveAsync);
            _loop = new AgentLoop(_model, invoker, _settings);
        }

        InputBox.Text = "";
        OnAgentEvent(new AgentEvent(AgentEventKind.UserMessage, question));

        _running = new CancellationTokenSource();
        SetRunning(true);
        try
        {
            // Run the loop off the UI thread; events are marshalled back.
            var loop = _loop;
            var ct = _running.Token;
            await Task.Run(() => loop.RunAsync(question, e => Dispatcher.UIThread.Post(() => OnAgentEvent(e)), ct));
        }
        catch (Exception ex)
        {
            OnAgentEvent(new AgentEvent(AgentEventKind.Error, L.T("Uventet fejl.", "Unexpected error.", "Oväntat fel.", "Uventet feil.", "Unerwarteter Fehler."), ex.ToString()));
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetRunning(false);
        }
    }

    void OnAgentEvent(AgentEvent e)
    {
        AddActivity(e);
        if (ChatItem.FromEvent(e) is { } item)
        {
            _chat.Add(item);
            Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    void StartSession(string question)
    {
        string settings = System.Text.Json.JsonSerializer.Serialize(new
        {
            mode = ModeName(_mode),
            _settings.MaxIterations,
            _settings.ShellTimeoutSeconds,
            _settings.MaxShellTimeoutSeconds,
            _settings.MaxOutputChars,
            _settings.MaxTokens,
            thinking = "adaptive",
            effort = "medium",
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        try
        {
            _sessionId = _store?.StartSession(new SessionInfo(
                question, AnthropicChatModel.ModelId, _settings.Language.NativeName(), settings, SessionStore.DescribeComputer()));
        }
        catch (Exception) { _sessionId = null; /* history must never block a diagnosis */ }
    }

    /// <summary>Writes to the history; a failure there never breaks a diagnosis. Safe from any thread.</summary>
    void SaveToHistory(Action<SessionStore> write, long? sessionId)
    {
        if (_store is null || sessionId is null) return;
        try { write(_store); }
        catch (Exception) { /* keep investigating even if the history file is unavailable */ }
    }

    void SetRunning(bool running)
    {
        SendButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        NewChatButton.IsEnabled = !running;
        SettingsButton.IsEnabled = !running;
        StatusText.Text = running ? BusyText : ReadyText;
    }

    void AddActivity(AgentEvent e)
    {
        var mode = _mode;
        SaveToHistory(store => store.AddEvent(_sessionId!.Value, e, mode), _sessionId);
        _activity.Add(new ActivityItem(e));
        Dispatcher.UIThread.Post(() => ActivityScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>Called from the agent (background thread) before each shell command in Assisted mode.</summary>
    Task<bool> ApproveAsync(ShellRequest request, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new ApprovalWindow(request);
            using var reg = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
            return await dialog.ShowDialog<bool>(this);
        });

    /// <summary>Puts a question in the chat and sends it, as if typed. False if an investigation is running.</summary>
    bool Ask(string question)
    {
        if (_running is not null)
            return false;
        InputBox.Text = question;
        ShowPage(ChatPage, ChatNavButton);
        Activate();
        _ = SendAsync();
        return true;
    }

    async Task OpenSettingsAsync()
    {
        await new SettingsWindow(_settings, _secrets).ShowDialog(this);
        RefreshKeyState();
    }

    /// <summary>
    /// Turns recordings left behind by a crash, freeze or restart into normal recordings.
    /// A recording that was cut short is itself a sign that something went wrong with the PC.
    /// </summary>
    static IReadOnlyList<string> RecoverRecordings()
    {
        try { return new PerfRecordingStore().RecoverInterrupted(); }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// When the app starts: if Windows logged a graphics card or hardware hang since the user was last told (or a recording
    /// was cut short), show a notice with a button to investigate. Silent when there is nothing, or the log cannot be read.
    /// </summary>
    async Task CheckForFaultsAtStartupAsync()
    {
        var since = _settings.LastFaultSeen ?? DateTimeOffset.Now.AddHours(-48);
        _faultIncidents = await Task.Run(() => Services.FindFaultIncidents(since));
        if (_faultIncidents.Count == 0 && _recovered.Count == 0)
            return;
        FaultText.Text = BuildFaultBanner(_faultIncidents);
        FaultBanner.IsVisible = true;
    }

    string BuildFaultBanner(List<FaultIncident> incidents)
    {
        var parts = new List<string>();
        if (incidents.Count > 0)
        {
            int n = incidents.Count;
            string when = incidents[^1].Start.ToString("dd-MM HH:mm", CultureInfo.InvariantCulture);
            parts.Add(L.T($"Windows har logget {n} hardware- eller grafikdriver-hang siden sidst, senest {when}.",
                $"Windows has logged {n} hardware or graphics driver hang(s) since last time, the latest at {when}.",
                $"Windows har loggat {n} hårdvaru- eller grafikdrivrutinshängning(ar) sedan sist, senast {when}.",
                $"Windows har logget {n} maskinvare- eller skjermdriver-henging(er) siden sist, siste {when}.",
                $"Windows hat seit dem letzten Mal {n} Hardware- oder Grafiktreiber-Hänger protokolliert, zuletzt {when}."));
        }
        if (_recovered.Count > 0)
            parts.Add(L.T("En optagelse blev afbrudt (PC'en eller PC Doctor stoppede) og er gemt.",
                "A recording was cut short (the PC or PC Doctor stopped) and has been saved.",
                "En inspelning avbröts (datorn eller PC Doctor stannade) och har sparats.",
                "Et opptak ble avbrutt (PC-en eller PC Doctor stoppet) og er lagret.",
                "Eine Aufzeichnung wurde abgebrochen (der PC oder PC Doctor hat aufgehört) und gespeichert."));
        return string.Join(" ", parts);
    }

    /// <summary>The question sent to PC Doctor when the user clicks Investigate on the startup notice.</summary>
    string BuildFaultQuestion(List<FaultIncident> incidents)
    {
        string recorded = _recovered.Count == 0 ? "" : " " + L.T(
            "En ydelsesoptagelse blev også afbrudt og er gemt: den slutter dér, hvor PC'en eller PC Doctor stoppede (performance_recording).",
            "A performance recording was also cut short and saved: it ends where the PC or PC Doctor stopped (performance_recording).",
            "En prestandainspelning avbröts också och har sparats: den slutar där datorn eller PC Doctor stannade (performance_recording).",
            "Et ytelsesopptak ble også avbrutt og er lagret: det slutter der PC-en eller PC Doctor stoppet (performance_recording).",
            "Eine Leistungsaufzeichnung wurde ebenfalls abgebrochen und gespeichert: sie endet dort, wo der PC oder PC Doctor aufgehört hat (performance_recording).");
        if (incidents.Count == 0)
            return L.T("En ydelsesoptagelse blev afbrudt, fordi PC'en eller PC Doctor stoppede. Se den med performance_recording, og tjek system_events omkring det tidspunkt, den slutter. Hvad skete der, og hvad skal jeg gøre?",
                "A performance recording was cut short because the PC or PC Doctor stopped. Read it with performance_recording and check system_events around the time it ends. What happened and what should I do?",
                "En prestandainspelning avbröts eftersom datorn eller PC Doctor stannade. Läs den med performance_recording och kontrollera system_events kring tiden då den slutar. Vad hände och vad ska jag göra?",
                "Et ytelsesopptak ble avbrutt fordi PC-en eller PC Doctor stoppet. Les det med performance_recording og sjekk system_events rundt tidspunktet det slutter. Hva skjedde, og hva bør jeg gjøre?",
                "Eine Leistungsaufzeichnung wurde abgebrochen, weil der PC oder PC Doctor aufgehört hat. Lies sie mit performance_recording und prüfe system_events um den Zeitpunkt, an dem sie endet. Was ist passiert und was soll ich tun?");

        var last = incidents[^1];
        int n = incidents.Count;
        string when = last.Start.ToString("dd-MM HH:mm", CultureInfo.InvariantCulture);
        string summary = last.Summary;
        return L.T($"Windows har logget {n} hardware- eller grafikdriver-hang, senest {when} ({summary}). Undersøg hvad der skete: se system_events omkring det tidspunkt, system_hardware (grafikkort og driverversion) og en eventuel ydelsesoptagelse (performance_recording). Forklar enkelt, hvad den sandsynlige årsag er, og hvad jeg skal gøre.",
                $"Windows has logged {n} hardware or graphics driver hang(s), the latest at {when} ({summary}). Find out what happened: read system_events around that time, system_hardware (graphics card and driver version) and any performance recording (performance_recording). Explain simply what the likely cause is and what I should do.",
                $"Windows har loggat {n} hårdvaru- eller grafikdrivrutinshängning(ar), senast {when} ({summary}). Ta reda på vad som hände: läs system_events kring den tiden, system_hardware (grafikkort och drivrutinsversion) och en eventuell prestandainspelning (performance_recording). Förklara enkelt vad den troliga orsaken är och vad jag ska göra.",
                $"Windows har logget {n} maskinvare- eller skjermdriver-henging(er), siste {when} ({summary}). Finn ut hva som skjedde: les system_events rundt det tidspunktet, system_hardware (skjermkort og driverversjon) og et eventuelt ytelsesopptak (performance_recording). Forklar enkelt hva den sannsynlige årsaken er og hva jeg bør gjøre.",
                $"Windows hat {n} Hardware- oder Grafiktreiber-Hänger protokolliert, zuletzt {when} ({summary}). Finde heraus, was passiert ist: lies system_events um diesen Zeitpunkt, system_hardware (Grafikkarte und Treiberversion) und eine eventuelle Leistungsaufzeichnung (performance_recording). Erkläre einfach, was die wahrscheinliche Ursache ist und was ich tun soll.")
            + recorded;
    }

    /// <summary>Hides the notice and remembers that everything up to now has been seen, so it does not come back at the next start.</summary>
    void DismissFaults()
    {
        FaultBanner.IsVisible = false;
        _settings.LastFaultSeen = DateTimeOffset.Now;
        try { Services.SaveSettings(_settings); } catch (IOException) { /* the notice may show once more at the next start */ }
    }

    /// <summary>Quietly looks for a newer version when the app starts; a window only appears if there is one.</summary>
    async Task CheckForUpdateAtStartupAsync()
    {
        if (Environment.ProcessPath is not { } exe || !Updater.CanSelfUpdate(exe))
            return; // running from a developer tool, not the real exe
        Updater.CleanUp(exe);
        var found = await new Updater().CheckAsync(Updater.CurrentVersion);
        if (found is not null) await new UpdateWindow(found).ShowDialog(this);
    }
}
