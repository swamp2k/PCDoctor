using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using PCDoctor.Core;

namespace PCDoctor.Agent;

/// <summary>
/// Talks to the Anthropic Messages API: Claude Haiku 5.5, adaptive thinking, medium effort.
/// History is append-only, and assistant content (including thinking blocks) is sent back unchanged.
/// </summary>
public sealed class AnthropicChatModel : IChatModel
{
    public const string ModelId = "claude-haiku-5-5";

    readonly AnthropicClient _client;
    readonly AgentSettings _settings;
    readonly List<MessageParam> _history = [];
    readonly List<ContentBlockParam> _pendingUser = [];
    readonly HashSet<string> _unansweredToolIds = [];

    public AnthropicChatModel(string apiKey, AgentSettings settings)
        : this(new AnthropicClient { ApiKey = apiKey }, settings) { }

    /// <summary>Lets tests pass a client with a fake HTTP handler.</summary>
    public AnthropicChatModel(AnthropicClient client, AgentSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public string? LastRequestJson { get; private set; }

    public void AddUserText(string text)
    {
        // If the previous run was stopped mid-way, every tool call still needs a result
        // before the conversation can continue.
        foreach (string id in _unansweredToolIds)
            _pendingUser.Add(new ToolResultBlockParam { ToolUseID = id, Content = "Stopped by the user before this tool ran.", IsError = true });
        _unansweredToolIds.Clear();
        _pendingUser.Add(new TextBlockParam { Text = text });
    }

    public void AddToolResults(IReadOnlyList<ToolCallResult> results)
    {
        foreach (var r in results)
        {
            _unansweredToolIds.Remove(r.ToolCallId);
            _pendingUser.Add(new ToolResultBlockParam { ToolUseID = r.ToolCallId, Content = r.Result.Content, IsError = r.Result.IsError });
        }
    }

    public async Task<ModelTurn> NextTurnAsync(IReadOnlyList<ITool> tools, CancellationToken ct)
    {
        // Tool results must come before any text in the user message.
        var userBlocks = _pendingUser.OrderBy(b => b.TryPickToolResult(out _) ? 0 : 1).ToList();
        var userMessage = new MessageParam { Role = Role.User, Content = userBlocks };
        List<MessageParam> messages = [.. _history, userMessage];

        var parameters = new MessageCreateParams
        {
            Model = ModelId,
            MaxTokens = _settings.MaxTokens,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = Effort.Medium },
            System = SystemPrompt.For(_settings.Language),
            Tools = [.. tools.Select(ToApiTool)],
            Messages = messages,
        };
        LastRequestJson = SafeSerialize(parameters);

        Message response = await _client.Messages.Create(parameters, ct);

        // Only commit to history once the call succeeded, so a cancelled call leaves no trace.
        _history.Add(userMessage);
        _pendingUser.Clear();

        var assistantContent = new List<ContentBlockParam>();
        var text = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();
        foreach (ContentBlock block in response.Content)
        {
            if (block.TryPickText(out TextBlock? t))
            {
                assistantContent.Add(new TextBlockParam { Text = t.Text });
                text.Append(t.Text);
            }
            else if (block.TryPickThinking(out ThinkingBlock? th))
            {
                assistantContent.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? rt))
            {
                assistantContent.Add(new RedactedThinkingBlockParam { Data = rt.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? tu))
            {
                assistantContent.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                calls.Add(new ToolCall(tu.ID, tu.Name, JsonSerializer.SerializeToElement(tu.Input)));
                _unansweredToolIds.Add(tu.ID);
            }
        }
        if (assistantContent.Count > 0)
            _history.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

        string stop = response.StopReason?.Raw() ?? "unknown";
        return new ModelTurn(text.ToString(), calls, stop);
    }

    /// <summary>Sends a tiny request to check that the key and the connection work.</summary>
    public static async Task TestConnectionAsync(string apiKey, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        await client.Messages.Create(new MessageCreateParams
        {
            Model = ModelId,
            MaxTokens = 256,
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = [new() { Role = Role.User, Content = "Reply with the single word OK." }],
        }, ct);
    }

    static ToolUnion ToApiTool(ITool tool)
    {
        var props = new Dictionary<string, JsonElement>();
        if (tool.InputSchema["properties"] is System.Text.Json.Nodes.JsonObject p)
            foreach (var kv in p)
                props[kv.Key] = JsonSerializer.SerializeToElement(kv.Value);
        var required = tool.InputSchema["required"] is System.Text.Json.Nodes.JsonArray r
            ? r.Select(x => x!.GetValue<string>()).ToList()
            : [];
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
    }

    static string SafeSerialize(MessageCreateParams p)
    {
        // The API key travels in a header, never in the body, so the body is safe to show.
        try { return p.ToString() ?? ""; }
        catch (Exception ex) { return $"(could not serialize request: {ex.Message})"; }
    }
}
