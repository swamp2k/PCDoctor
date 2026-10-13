using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PCDoctor.Agent;
using PCDoctor.Core;

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
    SessionLog _log = new();
    CancellationTokenSource? _running;
    RecordingWindow? _recordingWindow;
    DiagnosticsWindow? _diagnosticsWindow;

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
        ShowRequestButton.Click += async (_, _) =>
            await new TextWindow("Seneste API-forespørgsel (uden API-nøgle)", _model?.LastRequestJson ?? "Ingen forespørgsel sendt endnu.").ShowDialog(this);

        SetMode(PermissionMode.ReadOnly, log: false);
        RefreshKeyState();
        StatusText.Text = $"Sessionslog: {_log.FilePath}";
    }

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
        _log = new SessionLog();
        StatusText.Text = $"Sessionslog: {_log.FilePath}";
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

        if (_loop is null)
        {
            _model = new AnthropicChatModel(key, _settings);
            var invoker = new ToolInvoker(_tools, _settings, () => _mode, ApproveAsync);
            _loop = new AgentLoop(_model, invoker, _settings);
        }

        InputBox.Text = "";
        AddChat("Dig", question, "#223399FF");
        AddActivity(new AgentEvent(AgentEventKind.UserMessage, question));

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
        if (e.Kind == AgentEventKind.AssistantMessage)
            AddChat("PC Doctor", e.Title, "#2200A86B");
        else if (e.Kind == AgentEventKind.Error)
            AddChat("Fejl", e.Detail is null ? e.Title : $"{e.Title}\n\n{e.Detail}", "#33FF0000");
        else if (e.Kind == AgentEventKind.Info && e.Title.StartsWith("Stoppet"))
            AddChat("PC Doctor", e.Title, "#22808080");
    }

    void SetRunning(bool running)
    {
        SendButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        NewChatButton.IsEnabled = !running;
        SettingsButton.IsEnabled = !running;
        if (running) StatusText.Text = "Undersøger…";
        else StatusText.Text = $"Klar. Sessionslog: {_log.FilePath}";
    }

    void AddChat(string who, string text, string color)
    {
        _chat.Add(new ChatItem { Who = who, Text = text, Background = Brush.Parse(color) });
        Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    void AddActivity(AgentEvent e)
    {
        _log.Write(e, _mode);
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
