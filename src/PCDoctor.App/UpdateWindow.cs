using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>
/// The "update" window. Shows the installed and the new version, asks before doing anything,
/// then downloads the new PCDoctor.exe, swaps it in and restarts.
/// </summary>
public sealed class UpdateWindow : Window
{
    readonly Updater _updater = new();
    readonly string? _exePath = Environment.ProcessPath;
    readonly TextBlock _message = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    readonly Button _install = new() { IsVisible = false };
    readonly Button _close = new() { Content = L.T("Luk", "Close", "Stäng", "Lukk", "Schließen") };
    UpdateInfo? _update;

    public UpdateWindow() : this(null) { }

    /// <param name="found">An update the startup check already found; null means: check now.</param>
    public UpdateWindow(UpdateInfo? found)
    {
        Title = L.T("Opdatering", "Update", "Uppdatering", "Oppdatering", "Aktualisierung");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _install.Content = L.T("Opdater nu", "Update now", "Uppdatera nu", "Oppdater nå", "Jetzt aktualisieren");
        _install.Click += async (_, _) => await InstallAsync();
        _close.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_install);
        buttons.Children.Add(_close);
        Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children = { _message, _progress, buttons } };

        if (found is not null) Show(found);
        else Opened += async (_, _) => await CheckAsync();
    }

    async Task CheckAsync()
    {
        _message.Text = L.T("Søger efter ny version…", "Looking for a new version…", "Söker efter ny version…", "Søker etter ny versjon…", "Suche nach neuer Version…");
        var found = await _updater.CheckAsync(Updater.CurrentVersion);
        if (found is not null) { Show(found); return; }
        _message.Text = L.T(
            $"Du har den nyeste version ({Updater.CurrentVersion}), eller også er der ingen forbindelse til GitHub lige nu.",
            $"You have the newest version ({Updater.CurrentVersion}), or GitHub cannot be reached right now.",
            $"Du har den senaste versionen ({Updater.CurrentVersion}), eller så går det inte att nå GitHub just nu.",
            $"Du har den nyeste versjonen ({Updater.CurrentVersion}), eller så er det ikke kontakt med GitHub akkurat nå.",
            $"Du hast die neueste Version ({Updater.CurrentVersion}), oder GitHub ist gerade nicht erreichbar.");
    }

    void Show(UpdateInfo update)
    {
        _update = update;
        _message.Text = L.T(
            $"Der er en ny version af PC Doctor.\n\nDu har: {Updater.CurrentVersion}\nNy: {update.Version}\n\nProgrammet hentes fra projektets egen GitHub-side og genstarter bagefter.",
            $"There is a new version of PC Doctor.\n\nYou have: {Updater.CurrentVersion}\nNew: {update.Version}\n\nIt is downloaded from the project's own GitHub page and the app restarts afterwards.",
            $"Det finns en ny version av PC Doctor.\n\nDu har: {Updater.CurrentVersion}\nNy: {update.Version}\n\nDen hämtas från projektets egen GitHub-sida och appen startar om efteråt.",
            $"Det finnes en ny versjon av PC Doctor.\n\nDu har: {Updater.CurrentVersion}\nNy: {update.Version}\n\nDen lastes ned fra prosjektets egen GitHub-side, og appen starter på nytt etterpå.",
            $"Es gibt eine neue Version von PC Doctor.\n\nDu hast: {Updater.CurrentVersion}\nNeu: {update.Version}\n\nSie wird von der GitHub-Seite des Projekts geladen, danach startet die App neu.");
        _install.IsVisible = Updater.CanSelfUpdate(_exePath);
    }

    async Task InstallAsync()
    {
        if (_update is null || _exePath is null) return;
        _install.IsEnabled = _close.IsEnabled = false;
        _progress.IsVisible = true;
        _message.Text = L.T("Henter den nye version…", "Downloading the new version…", "Hämtar den nya versionen…", "Laster ned den nye versjonen…", "Lade die neue Version herunter…");
        try
        {
            string file = await _updater.DownloadAsync(_update, _exePath, new Progress<double>(p => _progress.Value = p));
            Updater.Install(_exePath, file);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_exePath) { UseShellExecute = true });
            Environment.Exit(0);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException)
        {
            _progress.IsVisible = false;
            _install.IsEnabled = _close.IsEnabled = true;
            _message.Text = L.T(
                "Opdateringen mislykkedes. Programmet er uændret. Prøv igen senere.",
                "The update failed. The app is unchanged. Try again later.",
                "Uppdateringen misslyckades. Appen är oförändrad. Försök igen senare.",
                "Oppdateringen mislyktes. Appen er uendret. Prøv igjen senere.",
                "Die Aktualisierung ist fehlgeschlagen. Die App ist unverändert. Versuche es später erneut.");
        }
    }
}
