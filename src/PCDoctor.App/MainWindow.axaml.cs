using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PCDoctor.Agent;
using PCDoctor.Core;
using PCDoctor.Storage;

namespace PCDoctor.App;

public partial class MainWindow : Window
{
    readonly ObservableCollection<ChatItem> _chat = [];
    readonly ObservableCollection<ActivityItem> _activity = [];
    readonly AgentSettings _settings = Services.LoadSettings();
    readonly ISecretStore _secrets = Services.CreateSecretStore();
    readonly List<ITool> _tools;

    PermissionMode _mode = PermissionMode.ReadOnly;
    IChatModel? _model;
    AgentLoop? _loop;
    readonly SessionStore? _store = Services.OpenHistory();
    long? _sessionId;
    CancellationTokenSource? _running;
    RecordingWindow? _recordingWindow;
    DiagnosticsWindow? _diagnosticsWindow;
    HistoryWindow? _historyWindow;

    public MainWindow()
    {
        InitializeComponent();
        _tools = Services.CreateTools(_settings);
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
        NewChatButton.Click += (_, _) => NewConversation();
        RecordButton.Click += (_, _) => OpenRecording();
        DiagnosticsButton.Click += (_, _) => OpenDiagnostics();
        HistoryButton.Click += (_, _) => OpenHistory();
        ShowRequestButton.Click += async (_, _) =>
            await new TextWindow("Seneste API-forespørgsel (hemmeligheder skjult)",
                _model?.LastRequestJson is { } json ? SecretMasker.Apply(json, _secrets.Read()) : "Ingen forespørgsel sendt endnu.").ShowDialog(this);

        SetMode(PermissionMode.ReadOnly, log: false);
        RefreshKeyState();
        StatusText.Text = ReadyText;
    }

    string ReadyText => _store is null
        ? "Klar. Historikken kunne ikke åbnes, så samtalen bliver ikke gemt."
        : "Klar. Samtalen gemmes under Historik.";

    static string ModeName(PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "Read-only",
        PermissionMode.Assisted => "Assisted",
        _ => "YOLO",
    };

    void SetMode(PermissionMode mode, bool log = true)
    {
        _mode = mode;
        (ModeText.Text, ModeBanner.Background) = mode switch
        {
            PermissionMode.ReadOnly => ("Read-only: kun indbyggede, ikke-ændrende værktøjer. Ingen fri PowerShell.", Brush.Parse("#2232CD32")),
            PermissionMode.Assisted => ("Assisted: agenten må foreslå PowerShell-kommandoer, men du skal godkende hver eneste én, før den køres.", Brush.Parse("#33FFD700")),
            _ => ("YOLO: agenten kører PowerShell-kommandoer uden at spørge, med dine almindelige Windows-rettigheder (ikke administrator). Det er ikke risikofrit.", Brush.Parse("#33FF4500")),
        };
        if (log)
            AddActivity(new AgentEvent(AgentEventKind.Info, $"Tilstand skiftet til {ModeName(mode)}"));
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
            try { Services.SaveSettings(_settings); } catch (IOException) { /* keep working with the in-memory choice */ }
            AddActivity(new AgentEvent(AgentEventKind.Info, $"Svarsprog: {language.NativeName()}"));
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
            OnAgentEvent(new AgentEvent(AgentEventKind.Error, "Uventet fejl.", ex.ToString()));
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
        StatusText.Text = running ? "Undersøger…" : ReadyText;
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

    void OpenRecording()
    {
        if (_recordingWindow is not null)
        {
            _recordingWindow.Activate();
            return;
        }
        _recordingWindow = new RecordingWindow(Services.CreatePerfSampler(), new PerfRecordingStore(), Ask);
        _recordingWindow.Closed += (_, _) => _recordingWindow = null;
        _recordingWindow.Show(this);
    }

    void OpenDiagnostics()
    {
        if (_diagnosticsWindow is not null)
        {
            _diagnosticsWindow.Activate();
            return;
        }
        _diagnosticsWindow = new DiagnosticsWindow(_tools, Ask);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show(this);
    }

    void OpenHistory()
    {
        if (_historyWindow is not null)
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow(_store);
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show(this);
    }

    /// <summary>Puts a question in the chat and sends it, as if typed. False if an investigation is running.</summary>
    bool Ask(string question)
    {
        if (_running is not null)
            return false;
        InputBox.Text = question;
        Activate();
        _ = SendAsync();
        return true;
    }

    async Task OpenSettingsAsync()
    {
        await new SettingsWindow(_settings, _secrets).ShowDialog(this);
        RefreshKeyState();
    }
}
