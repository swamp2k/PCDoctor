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
            if (key.Length == 0) { TestResult.Text = "Indsæt en nøgle først."; return; }
            try
            {
                _secrets.Write(key);
                KeyBox.Text = "";
                TestResult.Text = "Nøglen er gemt.";
            }
            catch (Exception ex) { TestResult.Text = ex.Message; }
            RefreshKeyStatus();
        };
        RemoveKeyButton.Click += (_, _) =>
        {
            _secrets.Delete();
            TestResult.Text = "Nøglen er fjernet.";
            RefreshKeyStatus();
        };
        TestButton.Click += async (_, _) =>
        {
            string? key = _secrets.Read();
            if (string.IsNullOrWhiteSpace(key)) { TestResult.Text = "Gem en nøgle først."; return; }
            TestResult.Text = "Tester…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await AnthropicChatModel.TestConnectionAsync(key, cts.Token);
                TestResult.Text = "Forbindelsen virker.";
            }
            catch (Exception ex) { TestResult.Text = $"Forbindelsen fejlede: {ex.Message}"; }
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
                LimitsResult.Text = "Gemt.";
            }
            catch (Exception ex) { LimitsResult.Text = ex.Message; }
        };
    }

    void RefreshKeyStatus() =>
        KeyStatus.Text = string.IsNullOrWhiteSpace(_secrets.Read()) ? "Status: ingen nøgle gemt." : "Status: en nøgle er gemt.";
}
