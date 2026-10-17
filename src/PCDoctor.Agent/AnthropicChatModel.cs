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
    readonly string _systemPrompt;
    AnswerLanguage _language;
    bool _languageNoteDue;

    public AnthropicChatModel(string apiKey, AgentSettings settings)
        : this(new AnthropicClient { ApiKey = apiKey }, settings) { }

    /// <summary>Lets tests pass a client with a fake HTTP handler.</summary>
    public AnthropicChatModel(AnthropicClient client, AgentSettings settings)
    {
        _client = client;
        _settings = settings;
        _language = settings.Language;
        // The system prompt is fixed for the whole conversation so earlier turns stay unchanged.
        _systemPrompt = SystemPrompt.For(_language);
    }

    public void SetLanguage(AnswerLanguage language)
    {
        if (language == _language) return;
        _language = language;
        _languageNoteDue = true;
    }

    public string? LastRequestJson { get; private set; }

    /// <summary>Called after every API call with the request and the response (or error), for the history.</summary>
    public Action<ApiExchange>? Exchanged { get; set; }

    public void AddUserText(string text)
    {
        // If the previous run was stopped mid-way, every tool call still needs a result
        // before the conversation can continue.
        foreach (string id in _unansweredToolIds)
            _pendingUser.Add(new ToolResultBlockParam { ToolUseID = id, Content = "Stopped by the user before this tool ran.", IsError = true });
        _unansweredToolIds.Clear();
        if (_languageNoteDue)
        {
            _pendingUser.Add(new TextBlockParam { Text = $"[PC Doctor setting changed: from now on, write all answers in {_language.PromptName()}.]" });
            _languageNoteDue = false;
        }
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
            System = _systemPrompt,
            Tools = [.. tools.Select(ToApiTool)],
            Messages = messages,
        };
        string request = SafeSerialize(parameters);
        LastRequestJson = request;

        var started = DateTimeOffset.Now;
        Message response;
        try
        {
            response = await _client.Messages.Create(parameters, ct);
        }
        catch (Exception ex)
        {
            Exchanged?.Invoke(new ApiExchange(started, request, null, Seconds(started),
                ex is OperationCanceledException ? L.T("Stoppet af brugeren", "Stopped by the user", "Stoppad av användaren", "Stoppet av brukeren", "Vom Benutzer gestoppt") : $"{ex.GetType().Name}: {ex.Message}"));
            throw;
        }
        Exchanged?.Invoke(new ApiExchange(started, request, SafeSerialize(response), Seconds(started)));

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

    static double Seconds(DateTimeOffset started) => (DateTimeOffset.Now - started).TotalSeconds;

    static readonly JsonSerializerOptions Readable = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The JSON as sent or received, readable (Danish letters and quotes unescaped). For a request this is
    /// the body only: the API key travels in a header, never in the body, so the body is safe to show.
    /// </summary>
    static string SafeSerialize(object p)
    {
        try
        {
            string json = p.ToString() ?? "";
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("BodyData", out JsonElement body))
                root = body;
            return JsonSerializer.Serialize(root, Readable);
        }
        catch (JsonException) { return p.ToString() ?? ""; }
        catch (Exception ex) { return $"(could not serialize: {ex.Message})"; }
    }
}
