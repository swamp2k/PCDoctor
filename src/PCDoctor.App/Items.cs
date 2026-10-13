using Avalonia.Media;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>One bubble in the chat.</summary>
public sealed class ChatItem
{
    public required string Who { get; init; }
    public required string Text { get; init; }
    public required IBrush Background { get; init; }
}

/// <summary>One line in the activity log.</summary>
public sealed class ActivityItem
{
    public ActivityItem(AgentEvent e)
    {
        TimeText = e.Time.ToString("HH:mm:ss");
        Title = e.Title.Length > 160 ? e.Title[..160] + "…" : e.Title;
        Detail = e.Detail ?? (e.Title.Length > 160 ? e.Title : "");
        Icon = e.Kind switch
        {
            AgentEventKind.UserMessage => "💬",
            AgentEventKind.AssistantMessage => "🩺",
            AgentEventKind.ToolStarted => "▶",
            AgentEventKind.ToolFinished => "✔",
            AgentEventKind.ToolDenied => "⛔",
            AgentEventKind.Error => "⚠",
            _ => "ℹ",
        };
    }

    public string TimeText { get; }
    public string Icon { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
}
