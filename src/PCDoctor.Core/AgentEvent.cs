namespace PCDoctor.Core;

public enum AgentEventKind
{
    UserMessage,
    AssistantMessage,
    ToolStarted,
    ToolFinished,
    ToolDenied,
    Info,
    Error,
}

/// <summary>One line in the activity log. Detail holds the full input/output shown on click.</summary>
public sealed record AgentEvent(AgentEventKind Kind, string Title, string? Detail = null)
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
}
