using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCDoctor.Core;

/// <summary>What kind of permission a tool needs before it may run.</summary>
public enum ToolKind
{
    /// <summary>A built-in tool that only reads information. Runs in every mode without a dialog.</summary>
    ReadOnly,

    /// <summary>Free shell access. Blocked in Read-only, needs approval in Assisted, free in YOLO.</summary>
    Shell,
}

/// <summary>A diagnostic tool the agent can call.</summary>
public interface ITool
{
    /// <summary>Tool ID sent to the model, e.g. "system_hardware". Letters, digits, _ and - only.</summary>
    string Name { get; }

    /// <summary>Plain description the model reads to decide when to use the tool.</summary>
    string Description { get; }

    /// <summary>JSON schema (type "object") describing the tool's arguments.</summary>
    JsonObject InputSchema { get; }

    ToolKind Kind { get; }

    Task<ToolResult> RunAsync(JsonElement input, CancellationToken ct);
}

/// <summary>Result of a tool call. Content is structured JSON whenever possible.</summary>
public sealed record ToolResult(string Content, bool IsError = false)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static ToolResult Json(object value) => new(JsonSerializer.Serialize(value, Options));

    public static ToolResult Error(string message) => new(JsonSerializer.Serialize(new { error = message }, Options), true);
}
