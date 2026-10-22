using System.Text.Json;
using System.Text.Json.Nodes;
using PCDoctor.Agent;
using PCDoctor.Core;

namespace PCDoctor.Tests;

/// <summary>A tool that records whether it ran. Used to prove the permission gate blocks or allows.</summary>
sealed class RecordingTool(string name, ToolKind kind) : ITool
{
    public int Runs;
    public Func<CancellationToken, Task<ToolResult>>? Behaviour;
    public string Name => name;
    public string Description => "test tool";
    public ToolKind Kind => kind;
    public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public async Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct)
    {
        Runs++;
        return Behaviour is null ? ToolResult.Json(new { ok = true }) : await Behaviour(ct);
    }
}

/// <summary>A scripted model: returns the queued turns in order and records what it was sent.</summary>
sealed class ScriptedModel : IChatModel
{
    public readonly Queue<Func<CancellationToken, Task<ModelTurn>>> Turns = new();
    public readonly List<string> UserTexts = [];
    public readonly List<ToolCallResult> Results = [];
    public string? LastRequestJson => null;

    public void AddUserText(string text) => UserTexts.Add(text);
    public void SetLanguage(AnswerLanguage language) { }
    public void AddToolResults(IReadOnlyList<ToolCallResult> results) => Results.AddRange(results);

    public Task<ModelTurn> NextTurnAsync(IReadOnlyList<ITool> tools, CancellationToken ct) =>
        Turns.Count > 0 ? Turns.Dequeue()(ct) : Task.FromResult(new ModelTurn("done", [], "end_turn"));

    public void Enqueue(ModelTurn turn) => Turns.Enqueue(_ => Task.FromResult(turn));
}

static class Json
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    public static ToolCall Call(string name, string input = "{}", string id = "t1") => new(id, name, Parse(input));
}

/// <summary>Runs only on Windows; skipped elsewhere (e.g. in the Linux build container).</summary>
sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}

/// <summary>Runs only on Linux; skipped elsewhere (e.g. in the Windows build).</summary>
sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only";
    }
}
