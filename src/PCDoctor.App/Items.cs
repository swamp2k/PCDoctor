using Avalonia.Layout;
using Avalonia.Media;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>One bubble in the chat. Your own messages sit to the right, PC Doctor's to the left.</summary>
public sealed class ChatItem
{
    public required string Who { get; init; }
    public required string Text { get; init; }
    public required IBrush Background { get; init; }
    public HorizontalAlignment Align { get; init; } = HorizontalAlignment.Left;

    /// <summary>The chat bubble for an activity event, or null if the event is not shown in the chat.</summary>
    public static ChatItem? FromEvent(AgentEvent e) => e.Kind switch
    {
        AgentEventKind.UserMessage => new() { Who = "DIG", Text = e.Title, Background = Status.Brush("UserBubbleBrush"), Align = HorizontalAlignment.Right },
        AgentEventKind.AssistantMessage => new() { Who = "PC DOCTOR", Text = e.Title, Background = Status.Brush("CardHighBrush") },
        AgentEventKind.Error => new() { Who = "FEJL", Text = e.Detail is null ? e.Title : $"{e.Title}\n\n{e.Detail}", Background = Status.Brush("RedSoftBrush") },
        AgentEventKind.Info when e.Title.StartsWith("Stoppet") => new() { Who = "PC DOCTOR", Text = e.Title, Background = Status.Brush("CardHighBrush") },
        _ => null,
    };
}

/// <summary>One line in the activity log.</summary>
public sealed class ActivityItem
{
    public ActivityItem(AgentEvent e)
    {
        TimeText = e.Time.ToString("HH:mm:ss");
        Title = e.Title.Length > 160 ? e.Title[..160] + "…" : e.Title;
        Detail = e.Detail ?? (e.Title.Length > 160 ? e.Title : "");
        Dot = e.Kind switch
        {
            AgentEventKind.ToolStarted => Status.Brush("AccentBrush"),
            AgentEventKind.ToolFinished => Status.Of(Light.Green),
            AgentEventKind.ToolDenied or AgentEventKind.Error => Status.Of(Light.Red),
            _ => Status.Of(Light.None),
        };
    }

    public string TimeText { get; }
    /// <summary>Orange while a tool runs, green when it finished, red when it was refused or failed.</summary>
    public IBrush Dot { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>One conversation in the history list.</summary>
public sealed class SessionListItem(PCDoctor.Storage.SessionSummary s)
{
    public long Id { get; } = s.Id;
    public string Title { get; } = s.Title;
    public string Subtitle { get; } =
        $"{s.Started.LocalDateTime:dd-MM-yyyy HH:mm} · {s.ToolCalls} værktøjskald · {s.ApiCalls} API-kald{(s.HadError ? " · fejl" : "")}";
}

/// <summary>One API call in the history's technical view.</summary>
public sealed class ApiCallItem(PCDoctor.Storage.ApiCallSummary c, int number)
{
    public long Id { get; } = c.Id;
    public bool HasResponse { get; } = c.ResponseChars > 0;
    public string Text { get; } =
        $"#{number}  {c.Time.LocalDateTime:HH:mm:ss}  {c.Seconds:0.0} s  ·  sendt {Size(c.RequestChars)}" +
        (c.Error is null ? $", modtaget {Size(c.ResponseChars)}" : $"  ·  ⚠ {c.Error}");

    static string Size(int chars) => chars < 1024 ? $"{chars} tegn" : $"{chars / 1024.0:0.#} K tegn";
}
