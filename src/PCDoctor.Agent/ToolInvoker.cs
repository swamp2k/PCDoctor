using System.Text.Json;
using PCDoctor.Core;

namespace PCDoctor.Agent;

/// <summary>Asks the user whether a shell command may run. Returns true if approved.</summary>
public delegate Task<bool> ShellApprover(ShellRequest request, CancellationToken ct);

/// <summary>
/// The single path from a model tool call to an actual tool run.
/// Every call passes the permission check here; nothing else starts a process for the model.
/// </summary>
public sealed class ToolInvoker
{
    readonly IReadOnlyList<ITool> _tools;
    readonly AgentSettings _settings;
    readonly Func<PermissionMode> _mode;
    readonly ShellApprover _approve;

    public ToolInvoker(IReadOnlyList<ITool> tools, AgentSettings settings, Func<PermissionMode> mode, ShellApprover approve)
    {
        _tools = tools;
        _settings = settings;
        _mode = mode;
        _approve = approve;
    }

    public IReadOnlyList<ITool> Tools => _tools;

    public async Task<ToolResult> InvokeAsync(ToolCall call, Action<AgentEvent> report, CancellationToken ct)
    {
        ITool? tool = _tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null)
            return ToolResult.Error($"Unknown tool '{call.Name}'.");

        if (tool.Kind == ToolKind.Shell)
        {
            ShellRequest request;
            try { request = ShellRequest.Parse(call.Input, _settings); }
            catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }

            switch (_mode())
            {
                case PermissionMode.ReadOnly:
                    report(new AgentEvent(AgentEventKind.ToolDenied, "Shell-kommando afvist (Read-only)", request.Command));
                    return ToolResult.Error(
                        "Refused: PC Doctor is in Read-only mode, where free shell commands are not allowed. " +
                        "Use the built-in tools instead. If a shell command is truly needed, tell the user they can switch to Assisted or YOLO mode.");

                case PermissionMode.Assisted:
                    bool approved = await _approve(request, ct);
                    ct.ThrowIfCancellationRequested(); // Stop pressed while the dialog was open
                    if (!approved)
                    {
                        report(new AgentEvent(AgentEventKind.ToolDenied, "Shell-kommando afvist af brugeren", request.Command));
                        return ToolResult.Error("The user declined this command. It was not run. Do not ask to run the same command again.");
                    }
                    report(new AgentEvent(AgentEventKind.Info, "Shell-kommando godkendt af brugeren", request.Command));
                    break;

                case PermissionMode.Yolo:
                    break;
            }
        }

        try
        {
            return await tool.RunAsync(call.Input, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static string Describe(JsonElement input) =>
        JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
