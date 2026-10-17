using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PCDoctor.Core;
using PCDoctor.Storage;

namespace PCDoctor.App;

/// <summary>
/// The History page: earlier conversations from the SQLite history, with the chat, every tool call,
/// and the exact API requests and responses (secrets masked).
/// </summary>
public partial class HistoryPage : UserControl
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly SessionStore? _store;
    bool _confirmDelete;

    public HistoryPage() : this(null) { }

    public HistoryPage(SessionStore? store)
    {
        InitializeComponent();
        _store = store;
        SessionList.SelectionChanged += (_, _) => ShowSelected();
        RefreshButton.Click += (_, _) => Reload();
        DeleteButton.Click += (_, _) => DeleteSelected();
        ApiList.AddHandler(Button.ClickEvent, async (_, e) =>
        {
            if (e.Source is Button { DataContext: ApiCallItem item, Tag: string part })
                await ShowApiCall(item, part);
        });
        ResetDelete();
        Reload();
        // A new language: the list and the conversation shown are read again, so their labels switch.
        L.Changed += () =>
        {
            ResetDelete();
            Reload();
        };
    }

    /// <summary>Reads the list again, e.g. when the page is shown after a new conversation.</summary>
    public void Reload()
    {
        long? selected = (SessionList.SelectedItem as SessionListItem)?.Id;
        List<SessionListItem> items = [];
        try
        {
            if (_store is not null)
                items = [.. _store.ListSessions().Select(s => new SessionListItem(s))];
        }
        catch (Exception ex)
        {
            EmptyText.Text = L.T("Kunne ikke læse historikken: ", "Could not read the history: ", "Kunde inte läsa historiken: ",
                "Kunne ikke lese historikken: ", "Der Verlauf konnte nicht gelesen werden: ") + ex.Message;
            EmptyText.IsVisible = true;
            return;
        }
        SessionList.ItemsSource = items;
        EmptyText.Text = _store is null
            ? L.T("Historikken er ikke tilgængelig.", "The history is not available.", "Historiken är inte tillgänglig.", "Historikken er ikke tilgjengelig.",
                  "Der Verlauf ist nicht verfügbar.")
            : L.T("Ingen gemte samtaler endnu.", "No saved chats yet.", "Inga sparade samtal än.", "Ingen lagrede samtaler ennå.", "Noch keine gespeicherten Chats.");
        EmptyText.IsVisible = items.Count == 0;
        SessionList.SelectedItem = items.FirstOrDefault(i => i.Id == selected) ?? items.FirstOrDefault();
    }

    void ShowSelected()
    {
        ResetDelete();
        if (_store is null || SessionList.SelectedItem is not SessionListItem item)
        {
            DetailTabs.IsVisible = false;
            DeleteButton.IsEnabled = false;
            return;
        }
        SessionDetail? s = _store.Load(item.Id);
        if (s is null) return;

        DetailTabs.IsVisible = true;
        DeleteButton.IsEnabled = true;
        ChatList.ItemsSource = s.Events.Select(e => ChatItem.FromEvent(e.Event)).OfType<ChatItem>().ToList();
        ActivityList.ItemsSource = s.Events.Select(e => new ActivityItem(e.Event)).ToList();
        ApiList.ItemsSource = s.ApiCalls.Select((c, i) => new ApiCallItem(c, i + 1)).ToList();

        var modes = s.Events.Select(e => e.Mode.ToString()).Distinct();
        static string Pad(string label) => (label + ":").PadRight(13);
        InfoText.Text =
            $"{Pad(L.T("Startet", "Started", "Startat", "Startet", "Gestartet"))}{s.Started.LocalDateTime:dd-MM-yyyy HH:mm:ss}\n" +
            $"{Pad(L.T("Model", "Model", "Modell", "Modell", "Modell"))}{s.Info.Model}\n" +
            $"{Pad(L.T("Svarsprog", "Language", "Svarsspråk", "Svarspråk", "Sprache"))}{s.Info.Language}\n" +
            $"{Pad(L.T("Tilstande", "Modes", "Lägen", "Moduser", "Modi"))}{string.Join(", ", modes)}\n" +
            $"{Pad(L.T("Computer", "Computer", "Dator", "Datamaskin", "Computer"))}{s.Info.Computer}\n" +
            $"{Pad(L.T("Hændelser", "Events", "Händelser", "Hendelser", "Ereignisse"))}{s.Events.Count}, " +
            $"{L.T("API-kald", "API calls", "API-anrop", "API-kall", "API-Aufrufe")}: {s.ApiCalls.Count}\n\n" +
            L.T("Indstillinger da samtalen startede:", "Settings when the chat started:", "Inställningar när samtalet startade:",
                "Innstillinger da samtalen startet:", "Einstellungen beim Start des Chats:") + $"\n{s.Info.Settings}";
    }

    async Task ShowApiCall(ApiCallItem item, string part)
    {
        var x = _store?.LoadApiCall(item.Id);
        if (x is null) return;
        string text = part == "request" ? x.Request : x.Response ?? x.Error ?? "";
        string title = part == "request"
            ? L.T("Sendt til AI'en (hemmeligheder skjult)", "Sent to the AI (secrets hidden)", "Skickat till AI:n (hemligheter dolda)",
                  "Sendt til AI-en (hemmeligheter skjult)", "An die KI gesendet (Geheimnisse verborgen)")
            : L.T("Svar fra AI'en", "Answer from the AI", "Svar från AI:n", "Svar fra AI-en", "Antwort der KI");
        await new TextWindow(title, PrettyJson(text)).ShowDialog((Window)TopLevel.GetTopLevel(this)!);
    }

    void DeleteSelected()
    {
        if (_store is null || SessionList.SelectedItem is not SessionListItem item) return;
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            DeleteButton.Content = L.T("Klik igen for at slette", "Click again to delete", "Klicka igen för att radera", "Klikk igjen for å slette",
                "Zum Löschen erneut klicken");
            return;
        }
        _store.Delete(item.Id);
        ResetDelete();
        Reload();
    }

    void ResetDelete()
    {
        _confirmDelete = false;
        DeleteButton.Content = L.T("Slet samtalen", "Delete this chat", "Radera samtalet", "Slett samtalen", "Diesen Chat löschen");
    }

    static string PrettyJson(string text)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement, Pretty); }
        catch (JsonException) { return text; }
    }
}
