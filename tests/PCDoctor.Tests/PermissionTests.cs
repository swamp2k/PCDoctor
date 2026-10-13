using PCDoctor.Agent;
using PCDoctor.Core;

namespace PCDoctor.Tests;

public class PermissionTests
{
    static (ToolInvoker invoker, RecordingTool shell, RecordingTool safe, List<ShellRequest> asked) Setup(PermissionMode mode, bool approve)
    {
        var shell = new RecordingTool("shell_execute", ToolKind.Shell);
        var safe = new RecordingTool("system_info", ToolKind.ReadOnly);
        var asked = new List<ShellRequest>();
        var invoker = new ToolInvoker([shell, safe], new AgentSettings(), () => mode, (req, _) =>
        {
            asked.Add(req);
            return Task.FromResult(approve);
        });
        return (invoker, shell, safe, asked);
    }

    const string Cmd = """{"command":"Get-Process | Select -First 1","working_directory":"C:\\Temp"}""";

    [Fact]
    public async Task ReadOnly_refuses_shell_and_never_runs_it()
    {
        var (invoker, shell, _, asked) = Setup(PermissionMode.ReadOnly, approve: true);
        var events = new List<AgentEvent>();

        ToolResult r = await invoker.InvokeAsync(Json.Call("shell_execute", Cmd), events.Add, CancellationToken.None);

        Assert.True(r.IsError);
        Assert.Contains("Read-only", r.Content);
        Assert.Equal(0, shell.Runs);
        Assert.Empty(asked); // not even asked: Read-only has no free shell at all
        Assert.Contains(events, e => e.Kind == AgentEventKind.ToolDenied);
    }

    [Fact]
    public async Task Assisted_shows_the_exact_command_and_decline_prevents_execution()
    {
        var (invoker, shell, _, asked) = Setup(PermissionMode.Assisted, approve: false);

        ToolResult r = await invoker.InvokeAsync(Json.Call("shell_execute", Cmd), _ => { }, CancellationToken.None);

        Assert.True(r.IsError);
        Assert.Contains("declined", r.Content);
        Assert.Equal(0, shell.Runs);
        var req = Assert.Single(asked);
        Assert.Equal("Get-Process | Select -First 1", req.Command);
        Assert.Equal(@"C:\Temp", req.WorkingDirectory);
    }

    [Fact]
    public async Task Assisted_asks_before_every_shell_call()
    {
        var (invoker, shell, _, asked) = Setup(PermissionMode.Assisted, approve: true);

        await invoker.InvokeAsync(Json.Call("shell_execute", Cmd), _ => { }, CancellationToken.None);
        await invoker.InvokeAsync(Json.Call("shell_execute", Cmd), _ => { }, CancellationToken.None);

        Assert.Equal(2, asked.Count);
        Assert.Equal(2, shell.Runs);
    }

    [Fact]
    public async Task Yolo_runs_shell_without_asking()
    {
        var (invoker, shell, _, asked) = Setup(PermissionMode.Yolo, approve: false);

        ToolResult r = await invoker.InvokeAsync(Json.Call("shell_execute", Cmd), _ => { }, CancellationToken.None);

        Assert.False(r.IsError);
        Assert.Equal(1, shell.Runs);
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData(PermissionMode.ReadOnly)]
    [InlineData(PermissionMode.Assisted)]
    [InlineData(PermissionMode.Yolo)]
    public async Task Builtin_readonly_tools_run_in_every_mode_without_a_dialog(PermissionMode mode)
    {
        var (invoker, _, safe, asked) = Setup(mode, approve: false);

        ToolResult r = await invoker.InvokeAsync(Json.Call("system_info"), _ => { }, CancellationToken.None);

        Assert.False(r.IsError);
        Assert.Equal(1, safe.Runs);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Mode_is_read_at_call_time()
    {
        var mode = PermissionMode.ReadOnly;
        var shell = new RecordingTool("shell_execute", ToolKind.Shell);
        var invoker = new ToolInvoker([shell], new AgentSettings(), () => mode, (_, _) => Task.FromResult(true));

        await invoker.InvokeAsync(Json.Call("shell_execute", Cmd), _ => { }, CancellationToken.None);
        mode = PermissionMode.Yolo;
        await invoker.InvokeAsync(Json.Call("shell_execute", Cmd), _ => { }, CancellationToken.None);

        Assert.Equal(1, shell.Runs);
    }

    [Fact]
    public async Task Unknown_tool_and_bad_arguments_return_errors_not_crashes()
    {
        var (invoker, shell, _, _) = Setup(PermissionMode.Yolo, approve: true);

        ToolResult unknown = await invoker.InvokeAsync(Json.Call("format_disk"), _ => { }, CancellationToken.None);
        ToolResult noCommand = await invoker.InvokeAsync(Json.Call("shell_execute", "{}"), _ => { }, CancellationToken.None);

        Assert.True(unknown.IsError);
        Assert.True(noCommand.IsError);
        Assert.Equal(0, shell.Runs);
    }

    [Fact]
    public async Task Tool_exception_becomes_an_error_result()
    {
        var (invoker, _, safe, _) = Setup(PermissionMode.ReadOnly, approve: false);
        safe.Behaviour = _ => throw new InvalidOperationException("WMI broke");

        ToolResult r = await invoker.InvokeAsync(Json.Call("system_info"), _ => { }, CancellationToken.None);

        Assert.True(r.IsError);
        Assert.Contains("WMI broke", r.Content);
    }

    [Fact]
    public void ShellRequest_caps_requested_timeout()
    {
        var settings = new AgentSettings { ShellTimeoutSeconds = 60, MaxShellTimeoutSeconds = 120 };
        var req = ShellRequest.Parse(Json.Parse("""{"command":"x","timeout_seconds":9999}"""), settings);
        Assert.Equal(120, req.TimeoutSeconds);
        var def = ShellRequest.Parse(Json.Parse("""{"command":"x"}"""), settings);
        Assert.Equal(60, def.TimeoutSeconds);
    }
}
