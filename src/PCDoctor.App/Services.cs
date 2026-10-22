using System.Text.Json;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Platform.Linux;
using PCDoctor.Platform.Windows;
using PCDoctor.Storage;

namespace PCDoctor.App;

/// <summary>Wires the platform-specific pieces together. The only place that picks Windows or Linux parts.</summary>
static class Services
{
    public static List<ITool> CreateTools(AgentSettings settings, LogReviewSelection logReview)
    {
        var tools = new List<ITool>();
        if (OperatingSystem.IsWindows())
            tools.Add(new HardwareTool());
        else if (OperatingSystem.IsLinux())
            tools.Add(new LinuxHardwareTool());
        tools.Add(new ResourcesTool(CreatePerfSampler));
        tools.Add(new ProcessesTool());
        if (OperatingSystem.IsWindows())
            tools.Add(new EventLogTool());
        else if (OperatingSystem.IsLinux())
            tools.Add(new JournalTool());
        tools.Add(new PingTool());
        tools.Add(new DnsTool());
        tools.Add(new TcpTool());
        tools.Add(new GamingLogsTool());
        tools.Add(new MinecraftModsTool());
        tools.Add(new OtherGameLogsTool());
        tools.Add(new ServiceStatusTool());
        tools.Add(new GameRequirementsTool());
        tools.Add(new PerformanceRecordingTool(new PerfRecordingStore()));
        tools.Add(new ReviewLogTool(logReview));
        if (OperatingSystem.IsWindows())
            tools.Add(new PowerShellTool(settings));
        else if (OperatingSystem.IsLinux())
            tools.Add(new BashTool(settings));
        return tools;
    }

    /// <summary>Performance recording reads Windows counters, or /proc and /sys on Linux. Other systems have no sampler.</summary>
    public static IPerfSampler? CreatePerfSampler() =>
        OperatingSystem.IsWindows() ? new WindowsPerfSampler()
        : OperatingSystem.IsLinux() ? new LinuxPerfSampler()
        : null;

    /// <summary>
    /// Starts FPS measuring for a game (Windows asks for administrator rights). Returns how to stop it and get the result,
    /// or why it did not start.
    /// </summary>
    public static (Func<DateTimeOffset, Task<FpsResult>>? Stop, string? Problem) StartFps(string gameProcess)
    {
        if (!OperatingSystem.IsWindows())
            return (null, L.T("FPS kan kun måles på Windows.", "FPS can only be measured on Windows.", "FPS kan bara mätas på Windows.", "FPS kan bare måles på Windows.", "FPS kann nur unter Windows gemessen werden."));
        var (handle, problem) = FpsCapture.Start(gameProcess, AppPaths.SubDir("fps"));
        return handle is null ? (null, problem) : (handle.StopAsync, null);
    }

    public static ISecretStore CreateSecretStore() =>
        OperatingSystem.IsWindows() ? new CredentialManagerStore()
        : OperatingSystem.IsLinux() ? new FileSecretStore()
        : new EnvironmentSecretStore();

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

/// <summary>Development fallback on other systems: reads ANTHROPIC_API_KEY, keeps writes in memory only.</summary>
sealed class EnvironmentSecretStore : ISecretStore
{
    string? _value = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    public string? Read() => _value;
    public void Write(string secret) => _value = secret;
    public void Delete() => _value = null;
}
