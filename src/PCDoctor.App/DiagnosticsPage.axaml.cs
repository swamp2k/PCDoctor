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
/// and shows the result in plain words (in the language picked) with a red, yellow or green light per line and the raw data underneath.
/// </summary>
public partial class DiagnosticsPage : UserControl
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly Dictionary<string, ITool> _tools;
    readonly Func<string, bool> _ask;
    readonly CancellationTokenSource _closing = new();

    /// <summary>
    /// A tab: its name, what it checks, how to run it, and what to ask the agent about it.
    /// The texts are functions, so they come out in the language picked when they are shown.
    /// Run returns the report as a function too, so a result already shown can switch language.
    /// </summary>
    sealed record Check(Func<string> Header, Func<string> Intro, Func<CancellationToken, Task<(Func<string> Text, string Raw)>> Run, Func<string> Question);

    public DiagnosticsPage() : this([], _ => false) { }

    /// <param name="ask">Sends a question to the chat. Returns false if PC Doctor is busy.</param>
    public DiagnosticsPage(IEnumerable<ITool> tools, Func<string, bool> ask)
    {
        InitializeComponent();
        _tools = tools.ToDictionary(t => t.Name);
        _ask = ask;

        Check[] checks =
        [
            new(() => L.T("Hardware", "Hardware", "Hårdvara", "Maskinvare", "Hardware"),
                () => L.T(
                    "Processor, grafikkort og driver, hukommelse, Windows-version, diske og enheder Windows melder fejl på.",
                    "Processor, graphics card and driver, memory, Windows version, disks and devices Windows reports errors on.",
                    "Processor, grafikkort och drivrutin, minne, Windows-version, diskar och enheter som Windows rapporterar fel på.",
                    "Prosessor, skjermkort og driver, minne, Windows-versjon, disker og enheter Windows melder feil på.",
                    "Prozessor, Grafikkarte und Treiber, Arbeitsspeicher, Windows-Version, Festplatten und Geräte, für die Windows Fehler meldet."),
                async ct =>
                {
                    var r = await Tool("system_hardware", "{}", ct);
                    return (() => PlainReport.Hardware(r), Raw(r));
                },
                () => L.T(
                    "Se min hardware igennem. Er der noget, der ser forkert eller forældet ud, fx drivere eller enheder med fejl?",
                    "Go through my hardware. Does anything look wrong or out of date, for example drivers or devices with errors?",
                    "Gå igenom min hårdvara. Ser något fel eller föråldrat ut, till exempel drivrutiner eller enheter med fel?",
                    "Se gjennom maskinvaren min. Er det noe som ser feil eller utdatert ut, for eksempel drivere eller enheter med feil?",
                    "Sieh dir meine Hardware an. Sieht etwas falsch oder veraltet aus, zum Beispiel Treiber oder Geräte mit Fehlern?")),

            new(() => L.T("Forbrug", "Usage", "Belastning", "Belastning", "Auslastung"),
                () => L.T(
                    "Hvor travlt computeren har lige nu: CPU, grafikkort, RAM og disk, de programmer der bruger mest, og ledig plads på drevene.",
                    "How busy the computer is right now: CPU, graphics card, RAM and disk, the programs using the most, and free space on the drives.",
                    "Hur mycket datorn har att göra just nu: CPU, grafikkort, RAM och disk, de program som använder mest och ledigt utrymme på enheterna.",
                    "Hvor travelt datamaskinen har det akkurat nå: CPU, skjermkort, RAM og disk, programmene som bruker mest, og ledig plass på diskene.",
                    "Wie ausgelastet der Computer gerade ist: CPU, Grafikkarte, RAM und Festplatte, die Programme, die am meisten verbrauchen, und freier Platz auf den Laufwerken."),
                async ct =>
                {
                    var resources = Tool("system_resources", "{}", ct);
                    var processes = Tool("system_processes", """{"top":10}""", ct);
                    var (r, p) = (await resources, await processes);
                    return (() => PlainReport.Resources(r, p), Raw(r) + "\n\n" + Raw(p));
                },
                () => L.T(
                    "Hvad belaster min computer lige nu, og er der noget, jeg bør gøre ved det?",
                    "What is putting load on my computer right now, and is there anything I should do about it?",
                    "Vad belastar min dator just nu, och finns det något jag borde göra åt det?",
                    "Hva belaster datamaskinen min akkurat nå, og er det noe jeg bør gjøre med det?",
                    "Was belastet meinen Computer gerade, und sollte ich etwas dagegen tun?")),

            new(() => L.T("Netværk", "Network", "Nätverk", "Nettverk", "Netzwerk"),
                () => L.T(
                    "Svarer routeren og internettet, virker navneopslag (DNS), og kan vi nå en hjemmeside? Tager ca. 10 sekunder.",
                    "Do the router and the internet answer, do name lookups (DNS) work, and can we reach a website? Takes about 10 seconds.",
                    "Svarar routern och internet, fungerar namnuppslag (DNS), och kan vi nå en webbplats? Tar ca 10 sekunder.",
                    "Svarer ruteren og internett, virker navneoppslag (DNS), og kan vi nå en nettside? Tar ca. 10 sekunder.",
                    "Antworten Router und Internet, funktioniert die Namensauflösung (DNS), und erreichen wir eine Website? Dauert etwa 10 Sekunden."),
                async ct =>
                {
                    var gw = await Tool("network_ping", """{"hosts":["gateway"],"count":2}""", ct);
                    var inet = await Tool("network_ping", """{"hosts":["1.1.1.1","8.8.8.8"],"count":4}""", ct);
                    var dns = await Tool("network_dns", "{}", ct);
                    var tcp = await Tool("network_tcp", """{"host":"www.google.com","port":443}""", ct);
                    return (() => PlainReport.Network(gw, inet, dns, tcp), string.Join("\n\n", Raw(gw), Raw(inet), Raw(dns), Raw(tcp)));
                },
                () => L.T(
                    "Er der problemer med min internetforbindelse? Undersøg både routeren, internettet og navneopslag.",
                    "Are there problems with my internet connection? Check the router, the internet and name lookups.",
                    "Finns det problem med min internetanslutning? Undersök både routern, internet och namnuppslag.",
                    "Er det problemer med internettforbindelsen min? Undersøk både ruteren, internett og navneoppslag.",
                    "Gibt es Probleme mit meiner Internetverbindung? Prüfe den Router, das Internet und die Namensauflösung.")),

            new(() => L.T("Hændelser", "Events", "Händelser", "Hendelser", "Ereignisse"),
                () => L.T(
                    "Fejl og kritiske hændelser fra Windows' hændelseslog de sidste 7 dage, fx uventede genstarter, blåskærme, programmer der crasher og grafikdriveren der genstarter.",
                    "Errors and critical events from the Windows event log for the last 7 days, for example unexpected restarts, blue screens, programs crashing and the graphics driver restarting.",
                    "Fel och kritiska händelser från Windows händelselogg de senaste 7 dagarna, till exempel oväntade omstarter, blåskärmar, program som kraschar och grafikdrivrutinen som startar om.",
                    "Feil og kritiske hendelser fra Windows' hendelseslogg de siste 7 dagene, for eksempel uventede omstarter, blåskjermer, programmer som krasjer og skjermdriveren som starter på nytt.",
                    "Fehler und kritische Ereignisse aus der Windows-Ereignisanzeige der letzten 7 Tage, zum Beispiel unerwartete Neustarts, Bluescreens, abstürzende Programme und Neustarts des Grafiktreibers."),
                async ct =>
                {
                    var r = await Tool("system_events", """{"hours":168,"max_groups":40}""", ct);
                    return (() => PlainReport.Events(r), Raw(r));
                },
                () => L.T(
                    "Kig på fejlene i Windows' hændelseslog fra de sidste 7 dage. Er der noget alvorligt, og hvad betyder det?",
                    "Look at the errors in the Windows event log from the last 7 days. Is anything serious, and what does it mean?",
                    "Titta på felen i Windows händelselogg från de senaste 7 dagarna. Är något allvarligt, och vad betyder det?",
                    "Se på feilene i Windows' hendelseslogg fra de siste 7 dagene. Er det noe alvorlig, og hva betyr det?",
                    "Sieh dir die Fehler in der Windows-Ereignisanzeige der letzten 7 Tage an. Ist etwas Ernstes dabei, und was bedeutet es?")),

            new(() => L.T("Spiltjenester", "Game services", "Speltjänster", "Spilltjenester", "Spieldienste"),
                () => L.T(
                    "Er spillet nede, eller er det vores internet? Læser de officielle statussider (Epic Games/Fortnite, Discord) og tester, om PC'en kan nå spillenes servere. Tager ca. 10 sekunder.",
                    "Is the game down, or is it our internet? Reads the official status pages (Epic Games/Fortnite, Discord) and tests whether the PC can reach the games' servers. Takes about 10 seconds.",
                    "Ligger spelet nere, eller är det vårt internet? Läser de officiella statussidorna (Epic Games/Fortnite, Discord) och testar om datorn når spelens servrar. Tar ca 10 sekunder.",
                    "Er spillet nede, eller er det internettet vårt? Leser de offisielle statussidene (Epic Games/Fortnite, Discord) og tester om PC-en når spillenes servere. Tar ca. 10 sekunder.",
                    "Ist das Spiel down, oder liegt es an unserem Internet? Liest die offiziellen Statusseiten (Epic Games/Fortnite, Discord) und testet, ob der PC die Server der Spiele erreicht. Dauert etwa 10 Sekunden."),
                async ct =>
                {
                    var r = await Tool("service_status", "{}", ct);
                    return (() => PlainReport.Services(r), Raw(r));
                },
                () => L.T(
                    "Er der nogen af mine spil eller Discord, der er nede lige nu, eller er det min internetforbindelse?",
                    "Are any of my games or Discord down right now, or is it my internet connection?",
                    "Ligger något av mina spel eller Discord nere just nu, eller är det min internetanslutning?",
                    "Er noen av spillene mine eller Discord nede akkurat nå, eller er det internettforbindelsen min?",
                    "Ist gerade eines meiner Spiele oder Discord down, oder liegt es an meiner Internetverbindung?")),
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
        var runButton = new Button();
        Tr.Bind(runButton, ContentControl.ContentProperty, "Kør igen", "Run again", "Kör igen", "Kjør igjen", "Erneut ausführen");
        var askButton = new Button { IsEnabled = false };
        Tr.Bind(askButton, ContentControl.ContentProperty, "Spørg PC Doctor", "Ask PC Doctor", "Fråga PC Doctor", "Spør PC Doctor", "PC Doctor fragen");
        askButton.Classes.Add("accent");
        var bigLight = Status.Dot(Light.None, 22);
        var verdict = new TextBlock { Text = CommonText.Investigating };
        verdict.Classes.Add("h2");
        var status = new TextBlock { FontSize = 13 };
        status.Classes.Add("muted");
        var rows = new StackPanel { Spacing = 2 };
        var raw = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 12 };
        var rawExpander = new Expander { Content = raw, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 12, 0, 0), IsVisible = false };
        Tr.Bind(rawExpander, Avalonia.Controls.Primitives.HeaderedContentControl.HeaderProperty, "Vis rådata", "Show raw data", "Visa rådata", "Vis rådata", "Rohdaten anzeigen");

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
        var headerText = new TextBlock { Text = check.Header() };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tabLight, headerText } };
        ToolTip.SetTip(header, check.Intro());

        bool started = false, running = false;
        List<string> flagged = [];
        Func<string>? report = null;  // the last result, kept so it can be shown again in another language
        Func<string>? error = null;  // a function too, so the "only on Windows" message can switch language
        DateTime updated = default;

        void ShowVerdict(Light light, string title, string detail)
        {
            bigLight.Fill = tabLight.Fill = Status.Of(light);
            verdict.Text = title;
            status.Text = detail;
        }

        // Draws the last result (or error) in the current language.
        void ShowResult()
        {
            if (error is not null)
            {
                ShowVerdict(Light.Amber, L.T("Testen kunne ikke køre", "The test could not run", "Testet kunde inte köras", "Testen kunne ikke kjøre",
                    "Der Test konnte nicht ausgeführt werden"), error());
                return;
            }
            if (report is null)
                return;
            string text = report();
            var lights = ShowRows(rows, text);
            flagged = Flagged(text);
            int red = lights.Count(l => l == Light.Red), amber = lights.Count(l => l == Light.Amber);
            var parts = new List<string>();
            if (red > 0) parts.Add(L.T($"{red} skal ordnes", $"{red} to fix", $"{red} behöver åtgärdas", $"{red} må fikses", $"{red} zu beheben"));
            if (amber > 0) parts.Add(L.T($"{amber} bør tjekkes", $"{amber} to check", $"{amber} bör kontrolleras", $"{amber} bør sjekkes", $"{amber} zu prüfen"));
            parts.Add(L.T("opdateret", "updated", "uppdaterad", "oppdatert", "aktualisiert") + $" {updated:HH:mm}");
            string detail = string.Join(" · ", parts);
            if (red > 0)
                ShowVerdict(Light.Red, L.T("Noget skal ordnes", "Something needs fixing", "Något behöver åtgärdas", "Noe må fikses", "Etwas muss behoben werden"), detail);
            else if (amber > 0)
                ShowVerdict(Light.Amber, L.T("Noget bør tjekkes", "Something should be checked", "Något bör kontrolleras", "Noe bør sjekkes", "Etwas sollte geprüft werden"), detail);
            else
                ShowVerdict(Light.Green, L.T("Ingen problemer fundet", "No problems found", "Inga problem hittades", "Ingen problemer funnet", "Keine Probleme gefunden"), detail);
        }

        async Task RunAsync()
        {
            if (running) return;
            running = started = true;
            runButton.IsEnabled = false;
            ShowVerdict(Light.None, CommonText.Investigating, check.Intro());
            try
            {
                var (text, rawText) = await check.Run(_closing.Token);
                (report, error, updated) = (text, null, DateTime.Now);
                raw.Text = rawText;
                rawExpander.IsVisible = true;
                askButton.IsEnabled = true;
                ShowResult();
            }
            catch (OperationCanceledException)
            {
                return; // app closed
            }
            catch (Exception ex)
            {
                rows.Children.Clear();
                (report, error) = (null, ex is PlatformNotSupportedException ? OnlyOnWindows : () => ex.Message);
                ShowResult();
            }
            finally
            {
                running = false;
                runButton.IsEnabled = true;
            }
        }

        // A new language: the tab name, its tip and the result shown switch too.
        L.Changed += () =>
        {
            headerText.Text = check.Header();
            ToolTip.SetTip(header, check.Intro());
            if (running)
                ShowVerdict(Light.None, CommonText.Investigating, check.Intro());
            else
                ShowResult();
        };

        runButton.Click += async (_, _) => await RunAsync();
        // With problems found, PC Doctor is asked about exactly those; otherwise the tab's general question.
        askButton.Click += (_, _) => status.Text = _ask(flagged.Count == 0 ? check.Question() : AboutProblems(check.Header(), flagged))
            ? CommonText.QuestionSent
            : CommonText.Busy;

        // Run once, the first time the tab is shown.
        return new TabItem { Header = header, Content = panel, Tag = new Action(() => { if (!started) _ = RunAsync(); }) };
    }

    /// <summary>The question for "Spørg PC Doctor" when the tab found problems: only those, in the language picked.</summary>
    static string AboutProblems(string tab, List<string> flagged)
    {
        bool one = flagged.Count == 1;
        string list = string.Join("\n", flagged.Select(f => "- " + f));
        return L.T(
            $"Diagnostik ({tab}) fandt {(one ? "dette problem" : "disse problemer")}:\n{list}\nUndersøg kun {(one ? "det" : "dem")}: hvad betyder det, er det alvorligt, og hvad skal jeg gøre?",
            $"Diagnostics ({tab}) found {(one ? "this problem" : "these problems")}:\n{list}\nLook into only {(one ? "that" : "those")}: what does it mean, is it serious, and what should I do?",
            $"Diagnostik ({tab}) hittade {(one ? "det här problemet" : "de här problemen")}:\n{list}\nUndersök bara {(one ? "det" : "dem")}: vad betyder det, är det allvarligt, och vad ska jag göra?",
            $"Diagnostikk ({tab}) fant {(one ? "dette problemet" : "disse problemene")}:\n{list}\nUndersøk bare {(one ? "det" : "dem")}: hva betyr det, er det alvorlig, og hva skal jeg gjøre?",
            $"Die Diagnose ({tab}) hat {(one ? "dieses Problem" : "diese Probleme")} gefunden:\n{list}\nUntersuche nur {(one ? "dieses" : "diese")}: Was bedeutet es, ist es ernst, und was soll ich tun?");
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
            throw new PlatformNotSupportedException(OnlyOnWindows());
        ToolResult r = await tool.RunAsync(JsonDocument.Parse(input).RootElement, ct);
        var root = JsonDocument.Parse(r.Content).RootElement.Clone();
        if (r.IsError)
            throw new InvalidOperationException(root.TryGetProperty("error", out var e) ? e.GetString() : r.Content);
        return root;
    }

    static string OnlyOnWindows() => L.T("Denne test findes kun på Windows.", "This test is only available on Windows.",
        "Det här testet finns bara på Windows.", "Denne testen finnes bare på Windows.", "Diesen Test gibt es nur unter Windows.");

    static string Raw(JsonElement e) => JsonSerializer.Serialize(e, Pretty);
}
