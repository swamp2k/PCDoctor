using PCDoctor.Core;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PCDoctor.App;

/// <summary>
/// The "Overblik" tab: one short list of the problems the other tabs found, so nobody has to open them one by one.
/// It starts the other tests the first time it is shown, and updates by itself as each one finishes.
/// </summary>
public partial class DiagnosticsPage
{
    TabItem BuildOverviewTab()
    {
        var runButton = new Button();
        Tr.Bind(runButton, ContentControl.ContentProperty, "Kør alle igen", "Run all again", "Kör alla igen", "Kjør alle igjen", "Alle erneut ausführen");
        var bigLight = Status.Dot(Light.None, 22);
        var verdict = new TextBlock { Text = CommonText.Investigating };
        verdict.Classes.Add("h2");
        var rows = new StackPanel { Spacing = 2 };

        var top = new Border { Margin = new(0, 0, 0, 12) };
        top.Classes.Add("card");
        var head = new DockPanel();
        DockPanel.SetDock(runButton, Dock.Right);
        runButton.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(runButton);
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14,
            Children = { bigLight, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { verdict } } },
        });
        top.Child = head;

        var list = new Border { Padding = new(18, 10) };
        list.Classes.Add("card");
        list.Child = new ScrollViewer { Content = rows };

        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(list);

        var tabLight = Status.Dot(Light.None, 8);
        var headerText = new TextBlock();
        Tr.Bind(headerText, TextBlock.TextProperty, "Overblik", "Overview", "Översikt", "Oversikt", "Übersicht");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tabLight, headerText } };
        ToolTip.SetTip(header, L.T("Alle problemer fundet i de andre faner, samlet ét sted.", "All problems found in the other tabs, in one place.",
            "Alla problem som hittats i de andra flikarna, samlade på ett ställe.", "Alle problemer funnet i de andre fanene, samlet på ett sted.",
            "Alle in den anderen Tabs gefundenen Probleme an einem Ort."));

        void Refresh()
        {
            rows.Children.Clear();
            int red = 0, amber = 0, waiting = 0;
            foreach (var source in _sources)
            {
                var problems = source.Problems();
                var title = new TextBlock { Text = source.Header().ToUpperInvariant(), Margin = new(0, 10, 0, 4) };
                title.Classes.Add("label");
                rows.Children.Add(title);
                if (problems is null)
                {
                    waiting++;
                    rows.Children.Add(OverviewRow(Light.None, CommonText.Investigating));
                }
                else if (problems.Count == 0)
                    rows.Children.Add(OverviewRow(Light.Green, L.T("Ingen problemer", "No problems", "Inga problem", "Ingen problemer", "Keine Probleme")));
                else
                    foreach (var (light, text) in problems)
                    {
                        if (light == Light.Red) red++; else amber++;
                        rows.Children.Add(OverviewRow(light, text));
                    }
            }
            var (overall, title2) = waiting > 0 ? (Light.None, CommonText.Investigating)
                : red > 0 ? (Light.Red, L.T($"{red} skal ordnes", $"{red} to fix", $"{red} behöver åtgärdas", $"{red} må fikses", $"{red} zu beheben")
                    + (amber > 0 ? " · " + L.T($"{amber} bør tjekkes", $"{amber} to check", $"{amber} bör kontrolleras", $"{amber} bør sjekkes", $"{amber} zu prüfen") : ""))
                : amber > 0 ? (Light.Amber, L.T($"{amber} bør tjekkes", $"{amber} to check", $"{amber} bör kontrolleras", $"{amber} bør sjekkes", $"{amber} zu prüfen"))
                : (Light.Green, L.T("Ingen problemer fundet", "No problems found", "Inga problem hittades", "Ingen problemer funnet", "Keine Probleme gefunden"));
            bigLight.Fill = tabLight.Fill = Status.Of(overall);
            verdict.Text = title2;
        }

        bool started = false;
        async Task RunAllAsync()
        {
            started = true;
            runButton.IsEnabled = false;
            try
            {
                foreach (var source in _sources)
                    await source.Run();
            }
            finally { runButton.IsEnabled = true; }
        }

        ResultsChanged += Refresh;
        L.Changed += Refresh;
        runButton.Click += async (_, _) => await RunAllAsync();
        Refresh();

        return new TabItem { Header = header, Content = panel, Tag = new Action(() => { if (!started) _ = RunAllAsync(); }) };
    }

    static DockPanel OverviewRow(Light light, string text)
    {
        var row = new DockPanel { Margin = new(0, 5, 0, 5) };
        var dot = Status.Dot(light, light == Light.None ? 6 : 10);
        dot.Margin = new(light == Light.None ? 2 : 0, 0, light == Light.None ? 16 : 12, 0);
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(new SelectableTextBlock
        {
            Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14,
            FontWeight = light is Light.Red or Light.Amber ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
        });
        return row;
    }
}
