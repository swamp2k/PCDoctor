using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PCDoctor.Core;
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
        var status = new TextBlock { Text = LookingForGames };
        status.Classes.Add("muted");
        var refresh = new Button();
        CommonText.BindRefresh(refresh, ContentControl.ContentProperty);
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
        var headerText = new TextBlock();
        Tr.Bind(headerText, TextBlock.TextProperty, "Seneste spil", "Recent games", "Senaste spel", "Siste spill", "Zuletzt gespielt");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tabLight, headerText } };
        string Tip() => L.T("De spil der er spillet senest. Tryk Undersøg ud for et spil.", "The games played most recently. Press Investigate next to a game.",
            "De spel som spelats senast. Tryck på Undersök vid ett spel.", "Spillene som er spilt sist. Trykk Undersøk ved et spill.",
            "Die zuletzt gespielten Spiele. Klicke bei einem Spiel auf Untersuchen.");
        ToolTip.SetTip(header, Tip());

        bool started = false, running = false;
        async Task LoadAsync()
        {
            if (running) return;
            running = started = true;
            refresh.IsEnabled = false;
            status.Text = LookingForGames;
            list.Children.Clear();
            try
            {
                var games = await FindGamesAsync(_closing.Token);
                int n = games.Count;
                status.Text = n == 0
                    ? L.T("Fandt ingen spil med logfiler (Minecraft, Fortnite, Roblox, Rocket League, Unity- og Unreal-spil).",
                          "Found no games with log files (Minecraft, Fortnite, Roblox, Rocket League, Unity and Unreal games).",
                          "Hittade inga spel med loggfiler (Minecraft, Fortnite, Roblox, Rocket League, Unity- och Unreal-spel).",
                          "Fant ingen spill med loggfiler (Minecraft, Fortnite, Roblox, Rocket League, Unity- og Unreal-spill).",
                          "Keine Spiele mit Logdateien gefunden (Minecraft, Fortnite, Roblox, Rocket League, Unity- und Unreal-Spiele).")
                    : L.T($"{n} spil, senest spillet først.", $"{n} games, most recently played first.", $"{n} spel, senast spelade först.",
                          $"{n} spill, sist spilt først.", $"{n} Spiele, zuletzt gespielte zuerst.");
                foreach (var g in games)
                    list.Children.Add(GameCard(g));
                tabLight.Fill = Status.Of(games.Count == 0 ? Light.None : Light.Green);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                status.Text = L.T("Kunne ikke finde spil: ", "Could not look for games: ", "Kunde inte söka efter spel: ", "Kunne ikke lete etter spill: ",
                    "Spiele konnten nicht gesucht werden: ") + ex.Message;
            }
            finally
            {
                running = false;
                refresh.IsEnabled = true;
            }
        }
        refresh.Click += async (_, _) => await LoadAsync();
        // A new language: the list is read again, so every text on it switches (quick looks already made are cleared).
        L.Changed += () =>
        {
            ToolTip.SetTip(header, Tip());
            if (running)
                status.Text = LookingForGames;
            else if (started)
                _ = LoadAsync();
        };
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
        var sub = new TextBlock
        {
            Text = $"{L.T("Spillet", "Played", "Spelat", "Spilt", "Gespielt")} {game.LastPlayed:dd-MM-yyyy HH:mm} · {game.Detail}", FontSize = 13,
        };
        sub.Classes.Add("muted");
        var investigate = new Button { Content = L.T("Undersøg", "Investigate", "Undersök", "Undersøk", "Untersuchen"), IsEnabled = game.NewestFile is not null };
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
            body.Children.Add(Muted(L.T("Kigger i den nyeste logfil…", "Looking at the newest log file…", "Tittar i den senaste loggfilen…",
                "Ser i den nyeste loggfilen…", "Sehe mir die neueste Logdatei an…")));
            var (verdict, findings) = await QuickLookAsync(game);
            light.Fill = Status.Of(verdict);
            body.Children.Clear();
            foreach (var f in findings)
                body.Children.Add(new DockPanel
                {
                    Children = { Status.Dot(Status.OfLine(f) == Light.None ? Light.None : Status.OfLine(f), 8), Line(Status.WithoutMarker(f)) },
                });
            body.Children.Add(Muted(L.T("Hvad driller?", "What is wrong?", "Vad krånglar?", "Hva er problemet?", "Was ist los?")));
            body.Children.Add(FollowUps(game, verdict, findings));
            investigate.IsEnabled = true;
            investigate.Content = L.T("Kig igen", "Look again", "Titta igen", "Se igjen", "Erneut ansehen");
        };
        return card;
    }

    /// <summary>The follow-up choices. Each sends PC Doctor a question that already carries what the log showed.</summary>
    Control FollowUps(Game game, Light verdict, List<string> findings)
    {
        string seen = findings.Count == 0 ? "" :
            L.T("\nDet hurtige kig i den nyeste logfil viste:\n", "\nThe quick look at the newest log file showed:\n",
                "\nDen snabba titten i den senaste loggfilen visade:\n", "\nDen raske titten i den nyeste loggfilen viste:\n",
                "\nDer kurze Blick in die neueste Logdatei zeigte:\n") +
            string.Join("\n", findings.Select(f => "- " + Status.WithoutMarker(f)));
        bool clean = verdict != Light.Red;
        string g = game.Name;
        var choices = new (string Label, string Question)[]
        {
            (L.T("Det crasher", "It crashes", "Det kraschar", "Det krasjer", "Es stürzt ab"),
             L.T($"{g} crasher. Find årsagen og sig, hvad jeg skal gøre.",
                 $"{g} crashes. Find the cause and tell me what to do.",
                 $"{g} kraschar. Hitta orsaken och säg vad jag ska göra.",
                 $"{g} krasjer. Finn årsaken og si hva jeg skal gjøre.",
                 $"{g} stürzt ab. Finde die Ursache und sag mir, was ich tun soll.") + seen),
            (L.T("Det hakker / er langsomt", "It stutters / is slow", "Det hackar / är långsamt", "Det hakker / er tregt", "Es ruckelt / ist langsam"),
             (clean
                ? L.T($"{g} hakker eller kører langsomt, og loggen viser ingen fejl. Lav en grundig undersøgelse af ydelsen: " +
                      "hardware og drivere, hvad der belaster PC'en, spillets krav, og min seneste optagelse hvis der er en.",
                      $"{g} stutters or runs slowly, and the log shows no errors. Do a thorough check of the performance: " +
                      "hardware and drivers, what is putting load on the PC, the game's requirements, and my latest recording if there is one.",
                      $"{g} hackar eller går långsamt, och loggen visar inga fel. Gör en grundlig undersökning av prestandan: " +
                      "hårdvara och drivrutiner, vad som belastar datorn, spelets krav och min senaste inspelning om det finns en.",
                      $"{g} hakker eller går tregt, og loggen viser ingen feil. Gjør en grundig undersøkelse av ytelsen: " +
                      "maskinvare og drivere, hva som belaster PC-en, spillets krav, og det siste opptaket mitt hvis det finnes et.",
                      $"{g} ruckelt oder läuft langsam, und das Log zeigt keine Fehler. Untersuche die Leistung gründlich: " +
                      "Hardware und Treiber, was den PC belastet, die Anforderungen des Spiels und meine letzte Aufzeichnung, falls es eine gibt.")
                : L.T($"{g} hakker eller kører langsomt. Start med det, loggen viste, og undersøg derefter ydelsen.",
                      $"{g} stutters or runs slowly. Start with what the log showed, then look into the performance.",
                      $"{g} hackar eller går långsamt. Börja med det loggen visade och undersök sedan prestandan.",
                      $"{g} hakker eller går tregt. Start med det loggen viste, og undersøk deretter ytelsen.",
                      $"{g} ruckelt oder läuft langsam. Fang mit dem an, was das Log gezeigt hat, und untersuche danach die Leistung.")) + seen),
            (L.T("Det lagger", "It lags", "Det laggar", "Det lagger", "Es laggt"),
             L.T($"{g} lagger online (forsinkelse, folk hopper rundt). Undersøg internetforbindelsen: ping, pakketab, " +
                 "wifi eller kabel, og om noget andet bruger nettet. Er det vores net eller spillets servere?",
                 $"{g} lags online (delay, people jumping around). Check the internet connection: ping, packet loss, " +
                 "wifi or cable, and whether something else is using the network. Is it our network or the game's servers?",
                 $"{g} laggar online (fördröjning, folk hoppar runt). Undersök internetanslutningen: ping, paketförlust, " +
                 "wifi eller kabel, och om något annat använder nätet. Är det vårt nät eller spelets servrar?",
                 $"{g} lagger online (forsinkelse, folk hopper rundt). Undersøk internettforbindelsen: ping, pakketap, " +
                 "wifi eller kabel, og om noe annet bruker nettet. Er det nettet vårt eller spillets servere?",
                 $"{g} laggt online (Verzögerung, Leute springen herum). Prüfe die Internetverbindung: Ping, Paketverlust, " +
                 "WLAN oder Kabel, und ob etwas anderes das Netz nutzt. Liegt es an unserem Netz oder an den Servern des Spiels?") + seen),
            (L.T("Kan ikke komme online", "Cannot get online", "Kommer inte online", "Kommer ikke på nett", "Kommt nicht online"),
             L.T($"{g} kan ikke forbinde eller logge ind. Er spillet nede, eller er det vores internet?",
                 $"{g} cannot connect or log in. Is the game down, or is it our internet?",
                 $"{g} kan inte ansluta eller logga in. Ligger spelet nere, eller är det vårt internet?",
                 $"{g} kan ikke koble til eller logge inn. Er spillet nede, eller er det internettet vårt?",
                 $"{g} kann keine Verbindung herstellen oder sich nicht anmelden. Ist das Spiel down, oder liegt es an unserem Internet?") + seen),
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
                    findings.Add($"{PlainReport.Problem} " + L.T("Den nyeste logfil er en crash-log", "The newest log file is a crash log",
                        "Den senaste loggfilen är en kraschlogg", "Den nyeste loggfilen er en krasjlogg", "Die neueste Logdatei ist ein Absturzprotokoll") +
                        $" ({Str(r, "modified")}).");
                if (r.TryGetProperty("content", out var c))
                    findings.AddRange(Rows(c, "warning_signs").Select(s => $"{PlainReport.Problem} {PlainReport.Sign(Str(s, "hint"))}"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (Light.Amber, [$"{PlainReport.Notice} " + L.T("Logfilen kunne ikke læses: ", "The log file could not be read: ",
                "Loggfilen kunde inte läsas: ", "Loggfilen kunne ikke leses: ", "Die Logdatei konnte nicht gelesen werden: ") + ex.Message]);
        }
        var verdict = findings.Any(f => Status.OfLine(f) == Light.Red) ? Light.Red
            : findings.Any(f => Status.OfLine(f) == Light.Amber) ? Light.Amber : Light.Green;
        if (findings.Count == 0)
            findings.Add(L.T("Ingen fejl i den nyeste logfil.", "No errors in the newest log file.", "Inga fel i den senaste loggfilen.",
                "Ingen feil i den nyeste loggfilen.", "Keine Fehler in der neuesten Logdatei."));
        return (verdict, findings);
    }

    static string LookingForGames => L.T("Leder efter spil…", "Looking for games…", "Letar efter spel…", "Leter etter spill…", "Suche nach Spielen…");

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
