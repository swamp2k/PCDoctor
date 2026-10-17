using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PCDoctor.Core;
using PCDoctor.Diagnostics;

namespace PCDoctor.App;

/// <summary>
/// Log Review: pick a log file, PC Doctor recognises the game from the first 50 lines (or asks), shows how much would be
/// sent, and hands only the important lines to the AI.
/// </summary>
public partial class LogReviewPage : UserControl
{
    readonly LogReviewSelection _selection;
    readonly Func<string, bool> _ask;
    string? _path;
    LogKind? _detected;
    LogExcerpt? _smart;
    CancellationTokenSource? _work;
    bool _filling;

    public LogReviewPage() : this(new LogReviewSelection(), _ => false) { }

    /// <param name="ask">Sends a question to the chat. Returns false if PC Doctor is busy.</param>
    public LogReviewPage(LogReviewSelection selection, Func<string, bool> ask)
    {
        InitializeComponent();
        _selection = selection;
        _ask = ask;

        FillGames();
        ShowTexts();
        PickButton.Click += async (_, _) => await PickAsync();
        GameBox.SelectionChanged += (_, _) => { if (!_filling) _ = AnalyseAsync(); };
        SmartButton.Click += (_, _) => Send(whole: false);
        WholeButton.Click += (_, _) => Send(whole: true);

        DragDrop.SetAllowDrop(DropZone, true);
        DropZone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
            if (file?.TryGetLocalPath() is { } path)
                await LoadAsync(path);
        });

        L.Changed += () =>
        {
            FillGames();
            ShowTexts();
            if (_path is not null)
                _ = AnalyseAsync(redetect: false);
        };
    }

    void ShowTexts()
    {
        FileText.Text = _path is null
            ? L.T("Ingen logfil valgt endnu.", "No log file picked yet.", "Ingen loggfil vald än.", "Ingen loggfil valgt ennå.", "Noch keine Logdatei gewählt.")
            : _path;
        SmartButton.Content = L.T("Send de vigtigste linjer til PC Doctor", "Send the important lines to PC Doctor",
            "Skicka de viktigaste raderna till PC Doctor", "Send de viktigste linjene til PC Doctor", "Die wichtigsten Zeilen an PC Doctor senden");
    }

    /// <summary>The games to pick from when the log is not recognised (or the guess is wrong).</summary>
    void FillGames()
    {
        _filling = true;
        int index = GameBox.SelectedIndex;
        GameBox.Items.Clear();
        foreach (var k in LogReview.Kinds)
            GameBox.Items.Add(k.Key == "other"
                ? L.T("Andet / ved ikke", "Other / do not know", "Annat / vet inte", "Annet / vet ikke", "Anderes / weiß nicht")
                : k.Name);
        GameBox.SelectedIndex = index;
        _filling = false;
    }

    async Task PickAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Vælg en logfil", "Choose a log file", "Välj en loggfil", "Velg en loggfil", "Logdatei wählen"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(L.T("Logfiler", "Log files", "Loggfiler", "Loggfiler", "Logdateien")) { Patterns = ["*.log", "*.txt", "*.log.*"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await LoadAsync(path);
    }

    async Task LoadAsync(string path)
    {
        _path = path;
        _detected = null;
        ShowTexts();
        ReviewCard.IsVisible = true;
        _filling = true;
        GameBox.SelectedIndex = -1;
        _filling = false;
        await AnalyseAsync(redetect: true);
    }

    /// <summary>Recognises the game (first time only) and measures what would be sent.</summary>
    async Task AnalyseAsync(bool redetect = true)
    {
        if (_path is not { } path)
            return;
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        var ct = work.Token;
        SmartButton.IsEnabled = WholeButton.IsEnabled = false;
        StatusText.Text = "";

        try
        {
            if (redetect)
            {
                GameDot.Fill = Status.Of(Light.None);
                GameText.Text = L.T("Kigger i de første 50 linjer…", "Looking at the first 50 lines…", "Tittar på de första 50 raderna…",
                    "Ser på de første 50 linjene…", "Sehe mir die ersten 50 Zeilen an…");
                var kind = await Task.Run(() => LogReview.Recognise(LogReview.ReadFirstLines(path), Path.GetFileName(path)), ct);
                _detected = kind;
                if (kind is not null)
                {
                    _filling = true;
                    GameBox.SelectedIndex = Array.FindIndex(LogReview.Kinds, k => k.Key == kind.Key);
                    _filling = false;
                }
            }

            if (GameBox.SelectedIndex < 0)
            {
                GameDot.Fill = Status.Of(Light.Amber);
                GameText.Text = L.T("Jeg kan ikke se, hvilket spil det er. Vælg det herunder, så ved jeg, hvad jeg skal lede efter.",
                    "I cannot tell which game this is. Pick it below so I know what to look for.",
                    "Jag kan inte se vilket spel det är. Välj det nedan så vet jag vad jag ska leta efter.",
                    "Jeg kan ikke se hvilket spill det er. Velg det under, så vet jeg hva jeg skal lete etter.",
                    "Ich kann nicht erkennen, welches Spiel es ist. Wähle es unten aus, dann weiß ich, wonach ich suchen soll.");
                SizeText.Text = "";
                WarnText.Text = "";
                PreviewText.Text = "";
                return;
            }

            var chosen = LogReview.Kinds[GameBox.SelectedIndex];
            bool same = _detected?.Key == chosen.Key;
            GameDot.Fill = Status.Of(Light.Green);
            GameText.Text = same
                ? L.T($"Genkendt: {_detected!.Name} (ud fra de første 50 linjer)", $"Recognised: {_detected!.Name} (from the first 50 lines)",
                      $"Igenkänt: {_detected!.Name} (från de första 50 raderna)", $"Gjenkjent: {_detected!.Name} (fra de første 50 linjene)",
                      $"Erkannt: {_detected!.Name} (anhand der ersten 50 Zeilen)")
                : L.T($"Du har valgt: {GameBox.SelectedItem}", $"You picked: {GameBox.SelectedItem}", $"Du valde: {GameBox.SelectedItem}",
                      $"Du valgte: {GameBox.SelectedItem}", $"Du hast gewählt: {GameBox.SelectedItem}");
            SizeText.Text = L.T("Måler logfilen…", "Measuring the log file…", "Mäter loggfilen…", "Måler loggfilen…", "Messe die Logdatei…");

            var smart = await Task.Run(() => LogReview.Smart(path, chosen.Key, ct), ct);
            ct.ThrowIfCancellationRequested();
            _smart = smart;
            ShowSizes(smart);
            PreviewText.Text = smart.Text.Length > 30_000 ? smart.Text[..30_000] + "\n[…]" : smart.Text;
            SmartButton.IsEnabled = WholeButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GameDot.Fill = Status.Of(Light.Red);
            GameText.Text = L.T("Logfilen kunne ikke læses: ", "The log file could not be read: ", "Loggfilen kunde inte läsas: ",
                "Loggfilen kunne ikke leses: ", "Die Logdatei konnte nicht gelesen werden: ") + e.Message;
        }
    }

    void ShowSizes(LogExcerpt smart)
    {
        string size = Size(smart.FileBytes);
        string tokens = $"{smart.ApproxTokens:N0}";
        SizeText.Text = smart.ProblemLines == 0
            ? L.T($"Filen er {size} og har {smart.TotalLines:N0} linjer. Jeg fandt ingen fejl eller advarsler, så kun slutningen af loggen ({Size(smart.Text.Length)}, ca. {tokens} tokens) sendes.",
                  $"The file is {size} and has {smart.TotalLines:N0} lines. I found no errors or warnings, so only the end of the log ({Size(smart.Text.Length)}, about {tokens} tokens) is sent.",
                  $"Filen är {size} och har {smart.TotalLines:N0} rader. Jag hittade inga fel eller varningar, så bara slutet av loggen ({Size(smart.Text.Length)}, ca {tokens} tokens) skickas.",
                  $"Filen er {size} og har {smart.TotalLines:N0} linjer. Jeg fant ingen feil eller advarsler, så bare slutten av loggen ({Size(smart.Text.Length)}, ca. {tokens} tokens) sendes.",
                  $"Die Datei ist {size} groß und hat {smart.TotalLines:N0} Zeilen. Ich habe keine Fehler oder Warnungen gefunden, deshalb wird nur das Ende des Logs ({Size(smart.Text.Length)}, ca. {tokens} Tokens) gesendet.")
            : L.T($"Filen er {size} og har {smart.TotalLines:N0} linjer, heraf {smart.ProblemLines:N0} med fejl eller advarsler. Der sendes kun {Size(smart.Text.Length)} (ca. {tokens} tokens).",
                  $"The file is {size} and has {smart.TotalLines:N0} lines, {smart.ProblemLines:N0} of them with errors or warnings. Only {Size(smart.Text.Length)} (about {tokens} tokens) is sent.",
                  $"Filen är {size} och har {smart.TotalLines:N0} rader, varav {smart.ProblemLines:N0} med fel eller varningar. Bara {Size(smart.Text.Length)} (ca {tokens} tokens) skickas.",
                  $"Filen er {size} og har {smart.TotalLines:N0} linjer, hvorav {smart.ProblemLines:N0} med feil eller advarsler. Bare {Size(smart.Text.Length)} (ca. {tokens} tokens) sendes.",
                  $"Die Datei ist {size} groß und hat {smart.TotalLines:N0} Zeilen, davon {smart.ProblemLines:N0} mit Fehlern oder Warnungen. Gesendet werden nur {Size(smart.Text.Length)} (ca. {tokens} Tokens).");

        long whole = Math.Min(smart.FileBytes, LogReview.WholeLimit);
        string wholeSize = Size(whole), wholeTokens = $"{whole / 3.5:N0}";
        WholeButton.Content = L.T($"Send hele loggen ({wholeSize})", $"Send the whole log ({wholeSize})", $"Skicka hela loggen ({wholeSize})",
            $"Send hele loggen ({wholeSize})", $"Ganzes Log senden ({wholeSize})");
        bool big = smart.FileBytes > LogReview.WholeLimit;
        WarnText.Text = L.T(
            $"Vælg kun “hele loggen”, hvis de vigtigste linjer ikke er nok. Det er ca. {wholeTokens} tokens og koster mere." + (big ? $" Filen er for stor, så kun starten og slutningen sendes ({wholeSize})." : ""),
            $"Only choose “the whole log” if the important lines are not enough. It is about {wholeTokens} tokens and costs more." + (big ? $" The file is too big, so only the start and the end are sent ({wholeSize})." : ""),
            $"Välj bara “hela loggen” om de viktigaste raderna inte räcker. Det är ca {wholeTokens} tokens och kostar mer." + (big ? $" Filen är för stor, så bara början och slutet skickas ({wholeSize})." : ""),
            $"Velg bare «hele loggen» hvis de viktigste linjene ikke er nok. Det er ca. {wholeTokens} tokens og koster mer." + (big ? $" Filen er for stor, så bare starten og slutten sendes ({wholeSize})." : ""),
            $"Wähle “ganzes Log” nur, wenn die wichtigsten Zeilen nicht reichen. Das sind ca. {wholeTokens} Tokens und kostet mehr." + (big ? $" Die Datei ist zu groß, deshalb werden nur Anfang und Ende gesendet ({wholeSize})." : ""));
    }

    void Send(bool whole)
    {
        if (_path is null || GameBox.SelectedIndex < 0)
            return;
        var kind = LogReview.Kinds[GameBox.SelectedIndex];
        string name = _detected?.Key == kind.Key ? _detected.Name : GameBox.SelectedItem?.ToString() ?? kind.Name;
        _selection.Path = _path;
        _selection.GameKey = kind.Key;
        _selection.GameName = name;
        _selection.Whole = whole;

        string g = kind.Key == "other" ? "" : $" ({name})";
        bool sent = _ask(L.T($"Gennemgå den logfil jeg har valgt på Log Review-siden{g}, og fortæl hvad der er galt og hvad jeg kan gøre.",
            $"Review the log file I picked on the Log Review page{g}, and tell me what is wrong and what I can do.",
            $"Granska loggfilen jag valde på sidan Log Review{g} och berätta vad som är fel och vad jag kan göra.",
            $"Gå gjennom loggfilen jeg valgte på Log Review-siden{g}, og fortell hva som er galt og hva jeg kan gjøre.",
            $"Prüfe die Logdatei, die ich auf der Seite Log Review gewählt habe{g}, und sag mir, was nicht stimmt und was ich tun kann."));
        StatusText.Text = sent ? "" : L.T("PC Doctor er i gang med noget andet. Prøv igen om lidt.", "PC Doctor is busy with something else. Try again in a moment.",
            "PC Doctor håller på med något annat. Försök igen om en stund.", "PC Doctor holder på med noe annet. Prøv igjen om litt.",
            "PC Doctor ist mit etwas anderem beschäftigt. Versuche es gleich noch einmal.");
    }

    static string Size(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
