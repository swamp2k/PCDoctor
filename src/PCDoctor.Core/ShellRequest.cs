using System.Text.Json;

namespace PCDoctor.Core;

/// <summary>
/// The arguments of a free shell call. Parsed in one place so the permission
/// dialog shows exactly what will be executed.
/// </summary>
public sealed record ShellRequest(string Command, string WorkingDirectory, int TimeoutSeconds)
{
    public static ShellRequest Parse(JsonElement input, AgentSettings settings)
    {
        string command = input.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()!
            : throw new ArgumentException("Missing 'command'.");
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("'command' is empty.");

        string workDir = input.TryGetProperty("working_directory", out var w) && w.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(w.GetString())
            ? w.GetString()!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        int timeout = settings.ShellTimeoutSeconds;
        if (input.TryGetProperty("timeout_seconds", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int requested) && requested > 0)
            timeout = Math.Min(requested, settings.MaxShellTimeoutSeconds);

        return new ShellRequest(command, workDir, timeout);
    }
}
