using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PCDoctor.Storage;

namespace PCDoctor.App;

/// <summary>
/// The History page: earlier conversations from the SQLite history, with the chat, every tool call,
/// and the exact API requests and responses (secrets masked).
/// </summary>
public partial class HistoryWindow : Window
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly SessionStore? _store;
    bool _confirmDelete;

    public HistoryWindow() : this(null) { }

    public HistoryWindow(SessionStore? store)
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
        Reload();
    }

    void Reload()
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
            EmptyText.Text = $"Kunne ikke læse historikken: {ex.Message}";
            EmptyText.IsVisible = true;
            return;
        }
        SessionList.ItemsSource = items;
        EmptyText.Text = _store is null ? "Historikken er ikke tilgængelig." : "Ingen gemte samtaler endnu.";
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
        InfoText.Text =
            $"Startet:     {s.Started.LocalDateTime:dd-MM-yyyy HH:mm:ss}\n" +
            $"Model:       {s.Info.Model}\n" +
            $"Svarsprog:   {s.Info.Language}\n" +
            $"Tilstande:   {string.Join(", ", modes)}\n" +
            $"Computer:    {s.Info.Computer}\n" +
            $"Hændelser:   {s.Events.Count}, API-kald: {s.ApiCalls.Count}\n\n" +
            $"Indstillinger da samtalen startede:\n{s.Info.Settings}";
    }

    async Task ShowApiCall(ApiCallItem item, string part)
    {
        var x = _store?.LoadApiCall(item.Id);
        if (x is null) return;
        string text = part == "request" ? x.Request : x.Response ?? x.Error ?? "";
        string title = part == "request" ? "Sendt til AI'en (hemmeligheder skjult)" : "Svar fra AI'en";
        await new TextWindow(title, PrettyJson(text)).ShowDialog(this);
    }

    void DeleteSelected()
    {
        if (_store is null || SessionList.SelectedItem is not SessionListItem item) return;
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            DeleteButton.Content = "Klik igen for at slette";
            return;
        }
        _store.Delete(item.Id);
        ResetDelete();
        Reload();
    }

    void ResetDelete()
    {
        _confirmDelete = false;
        DeleteButton.Content = "Slet samtalen";
    }

    static string PrettyJson(string text)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement, Pretty); }
        catch (JsonException) { return text; }
    }
}
