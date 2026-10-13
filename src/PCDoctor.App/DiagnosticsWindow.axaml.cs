using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.App;

/// <summary>
/// The Diagnostics page: runs the same read-only tools the agent uses, directly, without AI or an API key,
/// and shows the result in plain Danish with the raw data underneath.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly Dictionary<string, ITool> _tools;
    readonly Func<string, bool> _ask;
    readonly CancellationTokenSource _closing = new();

    /// <summary>A tab: what it checks, how to run it, and what to ask the agent about it.</summary>
    sealed record Check(string Header, string Intro, Func<CancellationToken, Task<(string Text, string Raw)>> Run, string Question);

    public DiagnosticsWindow() : this([], _ => false) { }

    /// <param name="ask">Sends a question to the chat. Returns false if PC Doctor is busy.</param>
    public DiagnosticsWindow(IEnumerable<ITool> tools, Func<string, bool> ask)
    {
        InitializeComponent();
        _tools = tools.ToDictionary(t => t.Name);
        _ask = ask;
        Closing += (_, _) => _closing.Cancel();

        Check[] checks =
        [
            new("Hardware", "Processor, grafikkort og driver, hukommelse, Windows-version, diske og enheder Windows melder fejl på.",
                async ct =>
                {
                    var r = await Tool("system_hardware", "{}", ct);
                    return (PlainReport.Hardware(r), Raw(r));
                },
                "Se min hardware igennem. Er der noget, der ser forkert eller forældet ud, fx drivere eller enheder med fejl?"),

            new("Forbrug", "Hvor travlt computeren har lige nu: CPU, grafikkort, RAM og disk, de programmer der bruger mest, og ledig plads på drevene.",
                async ct =>
                {
                    var resources = Tool("system_resources", "{}", ct);
                    var processes = Tool("system_processes", """{"top":10}""", ct);
                    var (r, p) = (await resources, await processes);
                    return (PlainReport.Resources(r, p), Raw(r) + "\n\n" + Raw(p));
                },
                "Hvad belaster min computer lige nu, og er der noget, jeg bør gøre ved det?"),

            new("Netværk", "Svarer routeren og internettet, virker navneopslag (DNS), og kan vi nå en hjemmeside? Tager ca. 10 sekunder.",
                async ct =>
                {
                    var gw = await Tool("network_ping", """{"hosts":["gateway"],"count":2}""", ct);
                    var inet = await Tool("network_ping", """{"hosts":["1.1.1.1","8.8.8.8"],"count":4}""", ct);
                    var dns = await Tool("network_dns", "{}", ct);
                    var tcp = await Tool("network_tcp", """{"host":"www.google.com","port":443}""", ct);
                    return (PlainReport.Network(gw, inet, dns, tcp), string.Join("\n\n", Raw(gw), Raw(inet), Raw(dns), Raw(tcp)));
                },
                "Er der problemer med min internetforbindelse? Undersøg både routeren, internettet og navneopslag."),

            new("Hændelser", "Fejl og kritiske hændelser fra Windows' hændelseslog de sidste 7 dage, fx uventede genstarter, blåskærme, programmer der crasher og grafikdriveren der genstarter.",
                async ct =>
                {
                    var r = await Tool("system_events", """{"hours":168,"max_groups":40}""", ct);
                    return (PlainReport.Events(r), Raw(r));
                },
                "Kig på fejlene i Windows' hændelseslog fra de sidste 7 dage. Er der noget alvorligt, og hvad betyder det?"),

            new("Minecraft", "Finder Minecraft (officiel launcher, CurseForge, Prism, Modrinth m.fl.) og læser den nyeste crash-rapport.",
                async ct =>
                {
                    var r = await Tool("gaming_logs", "{}", ct);
                    return (PlainReport.Minecraft(r), Raw(r));
                },
                "Hvorfor crashede Minecraft senest? Kig i crash-rapporterne og Windows' hændelseslog omkring tidspunktet."),
        ];

        foreach (var check in checks)
            Tabs.Items.Add(BuildTab(check));
        Tabs.SelectionChanged += (_, _) => ((Tabs.SelectedItem as TabItem)?.Tag as Action)?.Invoke();
        Opened += (_, _) => ((Tabs.SelectedItem as TabItem)?.Tag as Action)?.Invoke();
        Tabs.SelectedIndex = 0;
    }

    TabItem BuildTab(Check check)
    {
        var runButton = new Button { Content = "Kør igen", MinWidth = 100 };
        var askButton = new Button { Content = "Spørg PC Doctor om dette", IsEnabled = false };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0), Opacity = 0.75 };
        var result = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var raw = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 12 };
        var rawExpander = new Expander { Header = "Vis rådata (det samme som PC Doctor ser)", Content = raw, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 12, 0, 0), IsVisible = false };

        var top = new DockPanel { Margin = new(0, 8, 0, 8) };
        DockPanel.SetDock(askButton, Dock.Right);
        top.Children.Add(askButton);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(runButton);
        left.Children.Add(status);
        top.Children.Add(left);

        var body = new StackPanel();
        body.Children.Add(result);
        body.Children.Add(rawExpander);

        var panel = new DockPanel { Margin = new(0, 8, 0, 0) };
        var intro = new TextBlock { Text = check.Intro, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(intro);
        panel.Children.Add(top);
        panel.Children.Add(new Border
        {
            BorderBrush = Brushes.Gray, BorderThickness = new(1), CornerRadius = new(4), Padding = new(10),
            Child = new ScrollViewer { Content = body },
        });

        bool started = false, running = false;
        async Task RunAsync()
        {
            if (running) return;
            running = started = true;
            runButton.IsEnabled = false;
            status.Text = "Undersøger…";
            try
            {
                var (text, rawText) = await check.Run(_closing.Token);
                result.Text = text;
                raw.Text = rawText;
                rawExpander.IsVisible = true;
                askButton.IsEnabled = true;
                status.Text = $"Opdateret {DateTime.Now:HH:mm:ss}";
            }
            catch (OperationCanceledException)
            {
                return; // window closed
            }
            catch (Exception ex)
            {
                result.Text = $"Testen fejlede: {ex.Message}";
                status.Text = "";
            }
            finally
            {
                running = false;
                runButton.IsEnabled = true;
            }
        }

        runButton.Click += async (_, _) => await RunAsync();
        askButton.Click += (_, _) => status.Text = _ask(check.Question)
            ? "Spørgsmålet er sendt til PC Doctor i hovedvinduet."
            : "PC Doctor er optaget. Vent til den er færdig, og prøv igen.";

        // Run once, the first time the tab is shown.
        return new TabItem { Header = check.Header, Content = panel, Tag = new Action(() => { if (!started) _ = RunAsync(); }) };
    }

    /// <summary>Runs a built-in read-only tool directly. These never start a shell, so no permission check is needed here.</summary>
    async Task<JsonElement> Tool(string name, string input, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool) || tool.Kind != ToolKind.ReadOnly)
            throw new InvalidOperationException("Denne test findes kun på Windows.");
        ToolResult r = await tool.RunAsync(JsonDocument.Parse(input).RootElement, ct);
        var root = JsonDocument.Parse(r.Content).RootElement.Clone();
        if (r.IsError)
            throw new InvalidOperationException(root.TryGetProperty("error", out var e) ? e.GetString() : r.Content);
        return root;
    }

    static string Raw(JsonElement e) => JsonSerializer.Serialize(e, Pretty);
}
