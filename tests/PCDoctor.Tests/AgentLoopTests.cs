using PCDoctor.Agent;
using PCDoctor.Core;

namespace PCDoctor.Tests;

public class AgentLoopTests
{
    static AgentLoop Create(ScriptedModel model, ITool[] tools, AgentSettings? settings = null, PermissionMode mode = PermissionMode.ReadOnly)
    {
        settings ??= new AgentSettings();
        var invoker = new ToolInvoker(tools, settings, () => mode, (_, _) => Task.FromResult(false));
        return new AgentLoop(model, invoker, settings);
    }

    [Fact]
    public async Task Runs_several_tool_calls_and_feeds_results_back_before_answering()
    {
        var hw = new RecordingTool("system_hardware", ToolKind.ReadOnly);
        var ping = new RecordingTool("network_ping", ToolKind.ReadOnly);
        var model = new ScriptedModel();
        model.Enqueue(new ModelTurn("Let me check.", [Json.Call("system_hardware", id: "a"), Json.Call("network_ping", id: "b")], "tool_use"));
        model.Enqueue(new ModelTurn("", [Json.Call("network_ping", id: "c")], "tool_use"));
        model.Enqueue(new ModelTurn("Your CPU is X. Confidence: confirmed.", [], "end_turn"));
        var events = new List<AgentEvent>();

        await Create(model, [hw, ping]).RunAsync("Hvilken CPU har jeg?", events.Add, CancellationToken.None);

        Assert.Equal(1, hw.Runs);
        Assert.Equal(2, ping.Runs);
        Assert.Equal(["a", "b", "c"], model.Results.Select(r => r.ToolCallId));
        Assert.Equal(3, events.Count(e => e.Kind == AgentEventKind.ToolStarted));
        Assert.Equal("Your CPU is X. Confidence: confirmed.", events.Last(e => e.Kind == AgentEventKind.AssistantMessage).Title);
    }

    [Fact]
    public async Task Stops_at_the_iteration_limit()
    {
        var tool = new RecordingTool("system_processes", ToolKind.ReadOnly);
        var model = new ScriptedModel();
        for (int i = 0; i < 10; i++)
            model.Enqueue(new ModelTurn("", [Json.Call("system_processes", id: $"t{i}")], "tool_use"));
        var events = new List<AgentEvent>();

        await Create(model, [tool], new AgentSettings { MaxIterations = 3 }).RunAsync("q", events.Add, CancellationToken.None);

        Assert.Equal(3, tool.Runs);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Title.Contains("3 runder"));
    }

    [Fact]
    public async Task Stop_during_a_long_tool_ends_the_loop()
    {
        var slow = new RecordingTool("system_processes", ToolKind.ReadOnly)
        {
            Behaviour = async ct => { await Task.Delay(TimeSpan.FromMinutes(5), ct); return ToolResult.Json(new { }); },
        };
        var model = new ScriptedModel();
        model.Enqueue(new ModelTurn("", [Json.Call("system_processes")], "tool_use"));
        model.Enqueue(new ModelTurn("should never be reached", [], "end_turn"));
        var events = new List<AgentEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Create(model, [slow]).RunAsync("q", events.Add, cts.Token);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains(events, e => e.Kind == AgentEventKind.Info && e.Title.StartsWith("Stoppet"));
        Assert.DoesNotContain(events, e => e.Title == "should never be reached");
    }

    [Fact]
    public async Task Refusal_and_truncation_are_reported_as_errors()
    {
        var model = new ScriptedModel();
        model.Enqueue(new ModelTurn("", [], "refusal"));
        var events = new List<AgentEvent>();
        await Create(model, []).RunAsync("q", events.Add, CancellationToken.None);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Error);
    }

    [Fact]
    public async Task Readonly_shell_refusal_goes_back_to_the_model_so_it_can_continue()
    {
        var shell = new RecordingTool("shell_execute", ToolKind.Shell);
        var model = new ScriptedModel();
        model.Enqueue(new ModelTurn("", [Json.Call("shell_execute", """{"command":"Remove-Item C:\\x"}""")], "tool_use"));
        model.Enqueue(new ModelTurn("I could not run that in Read-only mode.", [], "end_turn"));

        await Create(model, [shell]).RunAsync("q", _ => { }, CancellationToken.None);

        Assert.Equal(0, shell.Runs);
        var result = Assert.Single(model.Results);
        Assert.True(result.Result.IsError);
    }
}
