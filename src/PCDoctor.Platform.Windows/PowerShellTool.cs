using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Platform.Windows;

/// <summary>
/// shell_execute: runs Windows PowerShell as the current user. Kind = Shell, so every call
/// goes through the permission check in ToolInvoker before this code is reached.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerShellTool : ITool
{
    readonly AgentSettings _settings;

    public PowerShellTool(AgentSettings settings) => _settings = settings;

    public string Name => "shell_execute";
    public string Description =>
        "Run a Windows PowerShell command as the current user (never as administrator) and return exit code, stdout and stderr. " +
        "Refused in Read-only mode; in Assisted mode the user sees and must approve the exact command first. " +
        "Prefer the built-in tools when they can answer the question.";
    public ToolKind Kind => ToolKind.Shell;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "The PowerShell command or script to run." },
            ["working_directory"] = new JsonObject { ["type"] = "string", ["description"] = "Folder to run in. Default: the user's home folder." },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Time limit in seconds. Default 60." },
        },
        ["required"] = new JsonArray("command"),
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        ShellRequest request = ShellRequest.Parse(input, _settings);
        if (!Directory.Exists(request.WorkingDirectory))
            return ToolResult.Error($"Working directory does not exist: {request.WorkingDirectory}");

        ProcessOutcome outcome = await ProcessRunner.RunAsync(
            "powershell.exe", BuildArguments(request.Command), request.WorkingDirectory,
            request.TimeoutSeconds, _settings.MaxOutputChars, ct);

        var result = new
        {
            exit_code = outcome.ExitCode,
            stdout = outcome.StdOut,
            stderr = outcome.StdErr,
            timed_out = outcome.TimedOut,
            truncated = outcome.Truncated,
            full_output_file = outcome.FullOutputFile,
            duration_seconds = outcome.DurationSeconds,
            working_directory = request.WorkingDirectory,
        };
        return new ToolResult(JsonSerializer.Serialize(result), outcome.TimedOut);
    }

    /// <summary>
    /// The command is passed Base64-encoded (-EncodedCommand), so quotes, pipes and other special
    /// characters reach PowerShell exactly as written; no escaping rules can be broken.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string command)
    {
        string script = "$ProgressPreference = 'SilentlyContinue'\n" +
                        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
                        command;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded];
    }
}
