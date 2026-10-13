using System.Text.Json;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Windows;

namespace PCDoctor.App;

/// <summary>Wires the platform-specific pieces together. The only place that knows about Windows.</summary>
static class Services
{
    public static List<ITool> CreateTools(AgentSettings settings)
    {
        var tools = new List<ITool>();
        if (OperatingSystem.IsWindows())
            tools.Add(new HardwareTool());
        tools.Add(new ProcessesTool());
        tools.Add(new PingTool());
        tools.Add(new DnsTool());
        tools.Add(new TcpTool());
        if (OperatingSystem.IsWindows())
            tools.Add(new PowerShellTool(settings));
        return tools;
    }

    public static ISecretStore CreateSecretStore() =>
        OperatingSystem.IsWindows() ? new CredentialManagerStore() : new EnvironmentSecretStore();

    static string SettingsFile => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AgentSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(SettingsFile)) ?? new AgentSettings();
        }
        catch (Exception) { /* unreadable settings: fall back to defaults */ }
        return new AgentSettings();
    }

    public static void SaveSettings(AgentSettings s) =>
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Development fallback off Windows: reads ANTHROPIC_API_KEY, keeps writes in memory only.</summary>
sealed class EnvironmentSecretStore : ISecretStore
{
    string? _value = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    public string? Read() => _value;
    public void Write(string secret) => _value = secret;
    public void Delete() => _value = null;
}
