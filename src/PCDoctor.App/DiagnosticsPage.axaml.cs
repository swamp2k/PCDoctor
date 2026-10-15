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
/// and shows the result in plain Danish with a red, yellow or green light per line and the raw data underneath.
/// </summary>
public partial class DiagnosticsPage : UserControl
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly Dictionary<string, ITool> _tools;
    readonly Func<string, bool> _ask;
    readonly CancellationTokenSource _closing = new();

    /// <summary>A tab: what it checks, how to run it, and what to ask the agent about it.</summary>
    sealed record Check(string Header, string Intro, Func<CancellationToken, Task<(string Text, string Raw)>> Run, string Question);

    public DiagnosticsPage() : this([], _ => false) { }

    /// <param name="ask">Sends a question to the chat. Returns false if PC Doctor is busy.</param>
    public DiagnosticsPage(IEnumerable<ITool> tools, Func<string, bool> ask)
    {
        InitializeComponent();
        _tools = tools.ToDictionary(t => t.Name);
        _ask = ask;

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

            new("Spiltjenester", "Er spillet nede, eller er det vores internet? Læser de officielle statussider (Epic Games/Fortnite, Discord) og tester, om PC'en kan nå spillenes servere. Tager ca. 10 sekunder.",
                async ct =>
                {
                    var r = await Tool("service_status", "{}", ct);
                    return (PlainReport.Services(r), Raw(r));
                },
                "Er der nogen af mine spil eller Discord, der er nede lige nu, eller er det min internetforbindelse?"),
        ];

        foreach (var check in checks)
            Tabs.Items.Add(BuildTab(check));
        Tabs.Items.Insert(4, BuildGamesTab());
        Tabs.SelectionChanged += (_, _) => ((Tabs.SelectedItem as TabItem)?.Tag as Action)?.Invoke();
        AttachedToVisualTree += (_, _) => ((Tabs.SelectedItem as TabItem)?.Tag as Action)?.Invoke();
        Tabs.SelectedIndex = 0;
    }

    /// <summary>Stops running tests when the app closes.</summary>
    public void Shutdown() => _closing.Cancel();

    TabItem BuildTab(Check check)
    {
        var runButton = new Button { Content = "Kør igen" };
        var askButton = new Button { Content = "Spørg PC Doctor", IsEnabled = false };
        askButton.Classes.Add("accent");
        var bigLight = Status.Dot(Light.None, 22);
        var verdict = new TextBlock { Text = "Undersøger…" };
        verdict.Classes.Add("h2");
        var status = new TextBlock { FontSize = 13 };
        status.Classes.Add("muted");
        var rows = new StackPanel { Spacing = 2 };
        var raw = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 12 };
        var rawExpander = new Expander { Header = "Vis rådata", Content = raw, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 12, 0, 0), IsVisible = false };

        var top = new Border { Margin = new(0, 0, 0, 12) };
        top.Classes.Add("card");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(runButton);
        buttons.Children.Add(askButton);
        var head = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14,
            Children = { bigLight, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { verdict, status } } },
        });
        top.Child = head;

        var list = new Border { Padding = new(18, 10) };
        list.Classes.Add("card");
        list.Child = new ScrollViewer { Content = new StackPanel { Children = { rows, rawExpander } } };

        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(list);

        var tabLight = Status.Dot(Light.None, 8);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tabLight, new TextBlock { Text = check.Header } } };
        ToolTip.SetTip(header, check.Intro);

        bool started = false, running = false;
        List<string> flagged = [];
        void ShowVerdict(Light light, string title, string detail)
        {
            bigLight.Fill = tabLight.Fill = Status.Of(light);
            verdict.Text = title;
            status.Text = detail;
        }

        async Task RunAsync()
        {
            if (running) return;
            running = started = true;
            runButton.IsEnabled = false;
            ShowVerdict(Light.None, "Undersøger…", check.Intro);
            try
            {
                var (text, rawText) = await check.Run(_closing.Token);
                var lights = ShowRows(rows, text);
                flagged = Flagged(text);
                raw.Text = rawText;
                rawExpander.IsVisible = true;
                askButton.IsEnabled = true;
                int red = lights.Count(l => l == Light.Red), amber = lights.Count(l => l == Light.Amber);
                var parts = new List<string>();
                if (red > 0) parts.Add($"{red} skal ordnes");
                if (amber > 0) parts.Add($"{amber} bør tjekkes");
                parts.Add($"opdateret {DateTime.Now:HH:mm}");
                if (red > 0) ShowVerdict(Light.Red, "Noget skal ordnes", string.Join(" · ", parts));
                else if (amber > 0) ShowVerdict(Light.Amber, "Noget bør tjekkes", string.Join(" · ", parts));
                else ShowVerdict(Light.Green, "Ingen problemer fundet", string.Join(" · ", parts));
            }
            catch (OperationCanceledException)
            {
                return; // app closed
            }
            catch (Exception ex)
            {
                rows.Children.Clear();
                ShowVerdict(Light.Amber, "Testen kunne ikke køre", ex.Message);
            }
            finally
            {
                running = false;
                runButton.IsEnabled = true;
            }
        }

        runButton.Click += async (_, _) => await RunAsync();
        // With problems found, PC Doctor is asked about exactly those; otherwise the tab's general question.
        askButton.Click += (_, _) => status.Text = _ask(flagged.Count == 0 ? check.Question :
                $"Diagnostik ({check.Header}) fandt {(flagged.Count == 1 ? "dette problem" : "disse problemer")}:\n" +
                string.Join("\n", flagged.Select(f => "- " + f)) +
                $"\nUndersøg kun {(flagged.Count == 1 ? "det" : "dem")}: hvad betyder det, er det alvorligt, og hvad skal jeg gøre?")
            ? "Spørgsmålet er sendt til PC Doctor."
            : "PC Doctor er optaget. Vent til den er færdig, og prøv igen.";

        // Run once, the first time the tab is shown.
        return new TabItem { Header = header, Content = panel, Tag = new Action(() => { if (!started) _ = RunAsync(); }) };
    }

    /// <summary>The marked lines of a report, each with the indented lines under it (e.g. which device has a problem).</summary>
    static List<string> Flagged(string text)
    {
        var result = new List<string>();
        bool under = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("  ") && Status.OfLine(line) == Light.None)
            {
                if (under && line.Trim().Length > 0)
                    result[^1] += " " + line.Trim();
                continue;
            }
            under = Status.OfLine(line) != Light.None;
            if (under)
                result.Add(Status.WithoutMarker(line));
        }
        return result;
    }

    /// <summary>
    /// Shows a report as rows: a light per line (red for a problem, yellow for a notice, grey for a plain fact),
    /// "Heading:" lines as small headings, and indented lines indented. Returns the lights shown.
    /// </summary>
    static List<Light> ShowRows(StackPanel rows, string text)
    {
        rows.Children.Clear();
        var lights = new List<Light>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                rows.Children.Add(new Border { Height = 8 });
                continue;
            }
            bool indented = line.StartsWith("  ");
            var light = Status.OfLine(line);
            string shown = Status.WithoutMarker(line);
            if (!indented && light == Light.None && shown.EndsWith(':'))
            {
                var heading = new TextBlock { Text = shown.TrimEnd(':').ToUpperInvariant(), Margin = new(0, 10, 0, 4) };
                heading.Classes.Add("label");
                rows.Children.Add(heading);
                continue;
            }
            lights.Add(light);
            var row = new DockPanel { Margin = new(indented ? 22 : 0, 5, 0, 5) };
            var dot = Status.Dot(light, light == Light.None ? 6 : 10);
            dot.Margin = new(light == Light.None ? 2 : 0, 0, light == Light.None ? 16 : 12, 0);
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new SelectableTextBlock
            {
                Text = shown, TextWrapping = TextWrapping.Wrap, FontSize = 14,
                FontWeight = light == Light.None ? FontWeight.Normal : FontWeight.SemiBold,
            });
            rows.Children.Add(row);
        }
        return lights;
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
