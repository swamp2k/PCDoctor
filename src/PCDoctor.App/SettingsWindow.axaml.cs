using Avalonia.Controls;
using PCDoctor.Agent;
using PCDoctor.Core;

namespace PCDoctor.App;

public partial class SettingsWindow : Window
{
    readonly AgentSettings _settings;
    readonly ISecretStore _secrets;

    public SettingsWindow() : this(new AgentSettings(), new EnvironmentSecretStore()) { }

    public SettingsWindow(AgentSettings settings, ISecretStore secrets)
    {
        InitializeComponent();
        _settings = settings;
        _secrets = secrets;

        ModelInfo.Text = $"{AnthropicChatModel.ModelId} (Claude Haiku 5.5), adaptive thinking, effort: medium.";
        IterationsBox.Value = settings.MaxIterations;
        TimeoutBox.Value = settings.ShellTimeoutSeconds;
        MaxTimeoutBox.Value = settings.MaxShellTimeoutSeconds;
        OutputBox.Value = settings.MaxOutputChars;
        RefreshKeyStatus();

        SaveKeyButton.Click += (_, _) =>
        {
            string key = KeyBox.Text?.Trim() ?? "";
            if (key.Length == 0) { TestResult.Text = L.T("Indsæt en nøgle først.", "Paste a key first.", "Klistra in en nyckel först.", "Lim inn en nøkkel først.", "Füge zuerst einen Schlüssel ein."); return; }
            try
            {
                _secrets.Write(key);
                KeyBox.Text = "";
                TestResult.Text = L.T("Nøglen er gemt.", "The key is saved.", "Nyckeln är sparad.", "Nøkkelen er lagret.", "Der Schlüssel ist gespeichert.");
            }
            catch (Exception ex) { TestResult.Text = ex.Message; }
            RefreshKeyStatus();
        };
        RemoveKeyButton.Click += (_, _) =>
        {
            _secrets.Delete();
            TestResult.Text = L.T("Nøglen er fjernet.", "The key is removed.", "Nyckeln är borttagen.", "Nøkkelen er fjernet.", "Der Schlüssel wurde entfernt.");
            RefreshKeyStatus();
        };
        TestButton.Click += async (_, _) =>
        {
            string? key = _secrets.Read();
            if (string.IsNullOrWhiteSpace(key)) { TestResult.Text = L.T("Gem en nøgle først.", "Save a key first.", "Spara en nyckel först.", "Lagre en nøkkel først.", "Speichere zuerst einen Schlüssel."); return; }
            TestResult.Text = L.T("Tester…", "Testing…", "Testar…", "Tester…", "Teste…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await AnthropicChatModel.TestConnectionAsync(key, cts.Token);
                TestResult.Text = L.T("Forbindelsen virker.", "The connection works.", "Anslutningen fungerar.", "Forbindelsen virker.", "Die Verbindung funktioniert.");
            }
            catch (Exception ex) { TestResult.Text = L.T("Forbindelsen fejlede: ", "The connection failed: ", "Anslutningen misslyckades: ", "Forbindelsen feilet: ", "Die Verbindung ist fehlgeschlagen: ") + ex.Message; }
        };
        SaveLimitsButton.Click += (_, _) =>
        {
            _settings.MaxIterations = (int)(IterationsBox.Value ?? 25);
            _settings.ShellTimeoutSeconds = (int)(TimeoutBox.Value ?? 60);
            _settings.MaxShellTimeoutSeconds = Math.Max(_settings.ShellTimeoutSeconds, (int)(MaxTimeoutBox.Value ?? 600));
            _settings.MaxOutputChars = (int)(OutputBox.Value ?? 20000);
            try
            {
                Services.SaveSettings(_settings);
                LimitsResult.Text = L.T("Gemt.", "Saved.", "Sparat.", "Lagret.", "Gespeichert.");
            }
            catch (Exception ex) { LimitsResult.Text = ex.Message; }
        };
    }

    void RefreshKeyStatus() =>
        KeyStatus.Text = string.IsNullOrWhiteSpace(_secrets.Read())
            ? L.T("Status: ingen nøgle gemt.", "Status: no key saved.", "Status: ingen nyckel sparad.", "Status: ingen nøkkel lagret.", "Status: kein Schlüssel gespeichert.")
            : L.T("Status: en nøgle er gemt.", "Status: a key is saved.", "Status: en nyckel är sparad.", "Status: en nøkkel er lagret.", "Status: ein Schlüssel ist gespeichert.");
}
