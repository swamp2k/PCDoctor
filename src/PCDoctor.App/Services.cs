using System.Text.Json;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Windows;
using PCDoctor.Storage;

namespace PCDoctor.App;

/// <summary>Wires the platform-specific pieces together. The only place that knows about Windows.</summary>
static class Services
{
    public static List<ITool> CreateTools(AgentSettings settings)
    {
        var tools = new List<ITool>();
        if (OperatingSystem.IsWindows())
            tools.Add(new HardwareTool());
        tools.Add(new ResourcesTool(CreatePerfSampler));
        tools.Add(new ProcessesTool());
        if (OperatingSystem.IsWindows())
            tools.Add(new EventLogTool());
        tools.Add(new PingTool());
        tools.Add(new DnsTool());
        tools.Add(new TcpTool());
        tools.Add(new GamingLogsTool());
        tools.Add(new MinecraftModsTool());
        tools.Add(new OtherGameLogsTool());
        tools.Add(new ServiceStatusTool());
        tools.Add(new GameRequirementsTool());
        tools.Add(new PerformanceRecordingTool(new PerfRecordingStore()));
        if (OperatingSystem.IsWindows())
            tools.Add(new PowerShellTool(settings));
        return tools;
    }

    /// <summary>Performance recording reads Windows counters; there is no sampler elsewhere.</summary>
    public static IPerfSampler? CreatePerfSampler() =>
        OperatingSystem.IsWindows() ? new WindowsPerfSampler() : null;

    /// <summary>
    /// Starts FPS measuring for a game (Windows asks for administrator rights). Returns how to stop it and get the result,
    /// or why it did not start.
    /// </summary>
    public static (Func<DateTimeOffset, Task<FpsResult>>? Stop, string? Problem) StartFps(string gameProcess)
    {
        if (!OperatingSystem.IsWindows())
            return (null, "FPS kan kun måles på Windows.");
        var (handle, problem) = FpsCapture.Start(gameProcess, AppPaths.SubDir("fps"));
        return handle is null ? (null, problem) : (handle.StopAsync, null);
    }

    public static ISecretStore CreateSecretStore() =>
        OperatingSystem.IsWindows() ? new CredentialManagerStore() : new EnvironmentSecretStore();

    /// <summary>Opens the conversation history and keeps the newest 100 conversations. Null if it cannot be opened.</summary>
    public static SessionStore? OpenHistory()
    {
        try
        {
            var secrets = CreateSecretStore();
            var store = new SessionStore(SessionStore.DefaultFile, secrets.Read);
            store.Prune(keep: 100);
            return store;
        }
        catch (Exception) { return null; }
    }

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
