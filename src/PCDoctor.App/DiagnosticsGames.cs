using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PCDoctor.Diagnostics;

namespace PCDoctor.App;

/// <summary>
/// The "Seneste spil" tab: the games played most recently (those that leave log files), newest first.
/// "Undersøg" takes a quick look at that game's newest log, then asks what is wrong and hands it to PC Doctor.
/// </summary>
public partial class DiagnosticsPage
{
    const int MaxGames = 20;

    /// <summary>A game found on the PC, with the tool and log file a quick look reads.</summary>
    sealed record Game(string Name, string Detail, DateTime LastPlayed, string Tool, string? NewestFile, string? NewestKind, bool Minecraft);

    TabItem BuildGamesTab()
    {
        var list = new StackPanel { Spacing = 10 };
        var status = new TextBlock { Text = "Leder efter spil…" };
        status.Classes.Add("muted");
        var refresh = new Button { Content = "Opdater" };
        var head = new DockPanel { Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(refresh, Dock.Right);
        head.Children.Add(refresh);
        status.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(status);

        var panel = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        panel.Children.Add(new ScrollViewer { Content = list });

        var tabLight = Status.Dot(Light.None, 8);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tabLight, new TextBlock { Text = "Seneste spil" } } };
        ToolTip.SetTip(header, "De spil der er spillet senest. Tryk Undersøg ud for et spil.");

        bool started = false, running = false;
        async Task LoadAsync()
        {
            if (running) return;
            running = started = true;
            refresh.IsEnabled = false;
            status.Text = "Leder efter spil…";
            list.Children.Clear();
            try
            {
                var games = await FindGamesAsync(_closing.Token);
                status.Text = games.Count == 0
                    ? "Fandt ingen spil med logfiler (Minecraft, Fortnite, Roblox, Rocket League, Unity- og Unreal-spil)."
                    : $"{games.Count} spil, senest spillet først.";
                foreach (var g in games)
                    list.Children.Add(GameCard(g));
                tabLight.Fill = Status.Of(games.Count == 0 ? Light.None : Light.Green);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { status.Text = $"Kunne ikke finde spil: {ex.Message}"; }
            finally
            {
                running = false;
                refresh.IsEnabled = true;
            }
        }
        refresh.Click += async (_, _) => await LoadAsync();
        return new TabItem { Header = header, Content = panel, Tag = new Action(() => { if (!started) _ = LoadAsync(); }) };
    }

    async Task<List<Game>> FindGamesAsync(CancellationToken ct)
    {
        var games = new List<Game>();
        var minecraft = Tool("gaming_logs", "{}", ct);
        var others = Tool("other_game_logs", "{}", ct);

        foreach (var i in Rows(await minecraft, "installs"))
        {
            var newest = Rows(i, "files").FirstOrDefault();
            string mods = i.TryGetProperty("mods", out var m) && m.TryGetInt32(out int n) && n > 0 ? $", {n} mods" : "";
            games.Add(new Game("Minecraft", $"{Str(i, "launcher")}: {Str(i, "name")}{mods}", Time(Str(i, "last_activity")),
                "gaming_logs", NullIfEmpty(Str(newest, "path")), Str(newest, "kind"), true));
        }
        foreach (var g in Rows(await others, "games"))
        {
            var newest = Rows(g, "files").FirstOrDefault();
            games.Add(new Game(Str(g, "name"), Str(g, "engine"), Time(Str(g, "last_activity")),
                "other_game_logs", NullIfEmpty(Str(newest, "path")), Str(newest, "kind"), false));
        }
        return [.. games.OrderByDescending(g => g.LastPlayed).Take(MaxGames)];
    }

    /// <summary>One game: name and when it was last played, "Undersøg", and room for the quick look and the follow-up.</summary>
    Control GameCard(Game game)
    {
        var light = Status.Dot(Light.None, 12);
        var title = new TextBlock { Text = game.Name };
        title.Classes.Add("h2");
        var sub = new TextBlock { Text = $"Spillet {game.LastPlayed:dd-MM-yyyy HH:mm} · {game.Detail}", FontSize = 13 };
        sub.Classes.Add("muted");
        var investigate = new Button { Content = "Undersøg", IsEnabled = game.NewestFile is not null };
        investigate.Classes.Add("accent");
        var body = new StackPanel { Spacing = 8, IsVisible = false, Margin = new(26, 12, 0, 0) };

        var row = new DockPanel();
        DockPanel.SetDock(investigate, Dock.Right);
        row.Children.Add(investigate);
        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14,
            Children = { light, new StackPanel { Children = { title, sub } } },
        });

        var card = new Border { Child = new StackPanel { Children = { row, body } } };
        card.Classes.Add("card");

        investigate.Click += async (_, _) =>
        {
            investigate.IsEnabled = false;
            body.IsVisible = true;
            body.Children.Clear();
            body.Children.Add(Muted("Kigger i den nyeste logfil…"));
            var (verdict, findings) = await QuickLookAsync(game);
            light.Fill = Status.Of(verdict);
            body.Children.Clear();
            foreach (var f in findings)
                body.Children.Add(new DockPanel
                {
                    Children = { Status.Dot(Status.OfLine(f) == Light.None ? Light.None : Status.OfLine(f), 8), Line(Status.WithoutMarker(f)) },
                });
            body.Children.Add(Muted("Hvad driller?"));
            body.Children.Add(FollowUps(game, verdict, findings));
            investigate.IsEnabled = true;
            investigate.Content = "Kig igen";
        };
        return card;
    }

    /// <summary>The follow-up choices. Each sends PC Doctor a question that already carries what the log showed.</summary>
    Control FollowUps(Game game, Light verdict, List<string> findings)
    {
        string seen = findings.Count == 0 ? "" :
            "\nDet hurtige kig i den nyeste logfil viste:\n" + string.Join("\n", findings.Select(f => "- " + Status.WithoutMarker(f)));
        bool clean = verdict != Light.Red;
        var choices = new (string Label, string Question)[]
        {
            ("Det crasher", $"{game.Name} crasher. Find årsagen og sig, hvad jeg skal gøre.{seen}"),
            ("Det hakker / er langsomt", clean
                ? $"{game.Name} hakker eller kører langsomt, og loggen viser ingen fejl. Lav en grundig undersøgelse af ydelsen: " +
                  $"hardware og drivere, hvad der belaster PC'en, spillets krav, og min seneste optagelse hvis der er en.{seen}"
                : $"{game.Name} hakker eller kører langsomt. Start med det, loggen viste, og undersøg derefter ydelsen.{seen}"),
            ("Kan ikke komme online", $"{game.Name} kan ikke forbinde eller logge ind. Er spillet nede, eller er det vores internet?{seen}"),
        };
        var buttons = new WrapPanel();
        foreach (var (label, question) in choices)
        {
            var b = new Button { Content = label, Margin = new(0, 0, 8, 8) };
            b.Click += (_, _) => _ask(question);
            buttons.Children.Add(b);
        }
        return buttons;
    }

    /// <summary>Reads the game's newest log file (for Minecraft also the mods) and returns a light and short findings, marked like PlainReport.</summary>
    async Task<(Light, List<string>)> QuickLookAsync(Game game)
    {
        var findings = new List<string>();
        try
        {
            var r = await Tool(game.Tool, JsonSerializer.Serialize(new { file = game.NewestFile }), _closing.Token);
            bool crashFile = game.NewestKind is "crash_report" or "jvm_crash" or "crash_log";
            if (game.Minecraft)
            {
                if (crashFile)
                    findings.AddRange(PlainReport.Crash(r).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Take(6));
                var mods = await Tool("minecraft_mods", "{}", _closing.Token);
                findings.AddRange(PlainReport.Mods(mods).Split('\n').Select(l => l.TrimEnd('\r'))
                    .Where(l => Status.OfLine(l) != Light.None || l.StartsWith("  ")).Take(6));
            }
            else
            {
                if (crashFile)
                    findings.Add($"{PlainReport.Problem} Den nyeste logfil er en crash-log ({Str(r, "modified")}).");
                if (r.TryGetProperty("content", out var c))
                    findings.AddRange(Rows(c, "warning_signs").Select(s => $"{PlainReport.Problem} {PlainReport.SignDa(Str(s, "hint"))}"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (Light.Amber, [$"{PlainReport.Notice} Logfilen kunne ikke læses: {ex.Message}"]);
        }
        var verdict = findings.Any(f => Status.OfLine(f) == Light.Red) ? Light.Red
            : findings.Any(f => Status.OfLine(f) == Light.Amber) ? Light.Amber : Light.Green;
        if (findings.Count == 0)
            findings.Add("Ingen fejl i den nyeste logfil.");
        return (verdict, findings);
    }

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, Margin = new(0, 4, 0, 0) };
        t.Classes.Add("muted");
        return t;
    }

    static SelectableTextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new(10, 0, 0, 0) };

    static IEnumerable<JsonElement> Rows(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static DateTime Time(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.MinValue;
}
