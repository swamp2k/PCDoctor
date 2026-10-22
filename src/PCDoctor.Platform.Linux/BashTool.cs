using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Core;

namespace PCDoctor.Platform.Linux;

/// <summary>
/// shell_execute on Linux: runs a bash command as the current user (never with sudo). Kind = Shell, so every call
/// goes through the permission check in ToolInvoker before this code is reached.
/// </summary>
public sealed class BashTool(AgentSettings settings) : ITool
{
    public string Name => "shell_execute";
    public string Description =>
        "Run a bash command on this Linux PC as the current user (never as root, never with sudo) and return exit code, stdout and stderr. " +
        "Refused in Read-only mode; in Assisted mode the user sees and must approve the exact command first. " +
        "Prefer the built-in tools when they can answer the question.";
    public ToolKind Kind => ToolKind.Shell;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string", ["description"] = "The bash command or script to run." },
            ["working_directory"] = new JsonObject { ["type"] = "string", ["description"] = "Folder to run in. Default: the user's home folder." },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Time limit in seconds. Default 60." },
        },
        ["required"] = new JsonArray("command"),
    };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        ShellRequest request = ShellRequest.Parse(input, settings);
        if (!Directory.Exists(request.WorkingDirectory))
            return ToolResult.Error($"Working directory does not exist: {request.WorkingDirectory}");

        string shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        // The command is one argument, passed exactly as written; no quoting rules can be broken.
        ProcessOutcome outcome = await ProcessRunner.RunAsync(shell, ["-c", request.Command], request.WorkingDirectory,
            request.TimeoutSeconds, settings.MaxOutputChars, ct);

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
}
