using System.Text.Json;
using PCDoctor.Core;

namespace PCDoctor.Agent;

/// <summary>A tool call the model asked for.</summary>
public sealed record ToolCall(string Id, string Name, JsonElement Input);

/// <summary>One model reply: optional text, zero or more tool calls, and why it stopped.</summary>
public sealed record ModelTurn(string Text, IReadOnlyList<ToolCall> ToolCalls, string StopReason);

/// <summary>A tool result going back to the model.</summary>
public sealed record ToolCallResult(string ToolCallId, ToolResult Result);

/// <summary>
/// The conversation with the language model. The Anthropic adapter is the only
/// implementation; the interface keeps the API code swappable and testable.
/// The implementation owns the message history so model-specific blocks
/// (such as thinking blocks) are passed back unchanged.
/// </summary>
public interface IChatModel
{
    void AddUserText(string text);

    /// <summary>
    /// Switch the answer language. Takes effect from the next question. History is never edited:
    /// mid-conversation, the switch is sent as a note with the next question.
    /// </summary>
    void SetLanguage(AnswerLanguage language);
    void AddToolResults(IReadOnlyList<ToolCallResult> results);
    Task<ModelTurn> NextTurnAsync(IReadOnlyList<ITool> tools, CancellationToken ct);

    /// <summary>The JSON of the last request sent, with no secrets, for the technical detail view.</summary>
    string? LastRequestJson { get; }
}
