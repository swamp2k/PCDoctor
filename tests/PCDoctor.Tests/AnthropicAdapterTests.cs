using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using PCDoctor.Agent;
using PCDoctor.Core;

namespace PCDoctor.Tests;

/// <summary>
/// Checks the exact request bodies sent to the Messages API, using a fake HTTP handler
/// (no network, no API key needed).
/// </summary>
public class AnthropicAdapterTests
{
    sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Queue<string> Responses = new();
        public readonly List<JsonElement> Requests = [];
        public readonly List<HttpRequestMessage> Raw = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Raw.Add(request);
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    static string Reply(string stop, params object[] content) => JsonSerializer.Serialize(new
    {
        id = "msg_1", type = "message", role = "assistant", model = "claude-haiku-5-5",
        content, stop_reason = stop, stop_sequence = (string?)null,
        usage = new { input_tokens = 10, output_tokens = 5 },
    });

    static (AnthropicChatModel model, FakeHandler handler) Create(AgentSettings? settings = null)
    {
        var handler = new FakeHandler();
        var client = new AnthropicClient { ApiKey = "sk-test-secret", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        return (new AnthropicChatModel(client, settings ?? new AgentSettings()), handler);
    }

    [Fact]
    public async Task Answers_in_danish_by_default_and_the_chosen_language_otherwise()
    {
        var (dk, dkHandler) = Create();
        dkHandler.Responses.Enqueue(Reply("end_turn", new { type = "text", text = "ok" }));
        dk.AddUserText("q");
        await dk.NextTurnAsync(Tools, CancellationToken.None);
        string dkSystem = dkHandler.Requests[0].GetProperty("system").GetRawText();
        Assert.Contains("Danish (dansk", dkSystem);
        Assert.Contains("not Norwegian", dkSystem);

        var (de, deHandler) = Create(new AgentSettings { Language = AnswerLanguage.German });
        deHandler.Responses.Enqueue(Reply("end_turn", new { type = "text", text = "ok" }));
        de.AddUserText("q");
        await de.NextTurnAsync(Tools, CancellationToken.None);
        Assert.Contains("German", deHandler.Requests[0].GetProperty("system").GetRawText());
    }

    [Fact]
    public async Task Switching_language_mid_conversation_adds_a_note_and_never_edits_earlier_turns()
    {
        var (model, handler) = Create();
        handler.Responses.Enqueue(Reply("end_turn", new { type = "text", text = "ok" }));
        handler.Responses.Enqueue(Reply("end_turn", new { type = "text", text = "ok" }));

        model.AddUserText("first");
        await model.NextTurnAsync(Tools, CancellationToken.None);
        model.SetLanguage(AnswerLanguage.English);
        model.AddUserText("second");
        await model.NextTurnAsync(Tools, CancellationToken.None);

        var first = handler.Requests[0];
        var second = handler.Requests[1];
        Assert.Equal(first.GetProperty("system").GetRawText(), second.GetProperty("system").GetRawText());
        Assert.Equal(first.GetProperty("messages")[0].GetRawText(), second.GetProperty("messages")[0].GetRawText());
        var last = second.GetProperty("messages")[2].GetProperty("content");
        Assert.Contains("English", last[0].GetProperty("text").GetString());
        Assert.Equal("second", last[1].GetProperty("text").GetString());
    }

    static readonly ITool[] Tools = [new RecordingTool("system_hardware", ToolKind.ReadOnly)];

    [Fact]
    public async Task Sends_haiku_with_adaptive_thinking_medium_effort_and_tools()
    {
        var (model, handler) = Create();
        handler.Responses.Enqueue(Reply("end_turn", new { type = "text", text = "Hej" }));

        model.AddUserText("Hvilken CPU har jeg?");
        ModelTurn turn = await model.NextTurnAsync(Tools, CancellationToken.None);

        var req = handler.Requests[0];
        Assert.Equal("claude-haiku-5-5", req.GetProperty("model").GetString());
        Assert.Equal("adaptive", req.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("medium", req.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(req.TryGetProperty("temperature", out _));
        Assert.Equal("system_hardware", req.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("Hej", turn.Text);
        Assert.Equal("end_turn", turn.StopReason);
    }

    [Fact]
    public async Task Thinking_blocks_and_tool_use_are_sent_back_unchanged_with_results()
    {
        var (model, handler) = Create();
        handler.Responses.Enqueue(Reply("tool_use",
            new { type = "thinking", thinking = "", signature = "sig-abc" },
            new { type = "text", text = "Jeg tjekker." },
            new { type = "tool_use", id = "toolu_1", name = "system_hardware", input = new { } }));
        handler.Responses.Enqueue(Reply("end_turn", new { type = "text", text = "Svar" }));

        model.AddUserText("q");
        ModelTurn first = await model.NextTurnAsync(Tools, CancellationToken.None);
        Assert.Equal("toolu_1", Assert.Single(first.ToolCalls).Id);
        model.AddToolResults([new ToolCallResult("toolu_1", ToolResult.Json(new { cpu = "X" }))]);
        await model.NextTurnAsync(Tools, CancellationToken.None);

        var messages = handler.Requests[1].GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var assistant = messages[1].GetProperty("content");
        Assert.Equal("thinking", assistant[0].GetProperty("type").GetString());
        Assert.Equal("sig-abc", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", assistant[2].GetProperty("type").GetString());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task After_a_stop_the_next_question_answers_the_open_tool_call_first()
    {
        var (model, handler) = Create();
        handler.Responses.Enqueue(Reply("tool_use", new { type = "tool_use", id = "toolu_9", name = "system_hardware", input = new { } }));
        handler.Responses.Enqueue(Reply("end_turn", new { type = "text", text = "ok" }));

        model.AddUserText("first");
        await model.NextTurnAsync(Tools, CancellationToken.None);
        // User pressed Stop: no tool results were added. Then asks something new.
        model.AddUserText("second");
        await model.NextTurnAsync(Tools, CancellationToken.None);

        var last = handler.Requests[1].GetProperty("messages")[2].GetProperty("content");
        Assert.Equal("tool_result", last[0].GetProperty("type").GetString());
        Assert.Equal("toolu_9", last[0].GetProperty("tool_use_id").GetString());
        Assert.True(last[0].GetProperty("is_error").GetBoolean());
        Assert.Equal("second", last[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Api_key_goes_in_a_header_and_never_in_the_body_or_debug_view()
    {
        var (model, handler) = Create();
        handler.Responses.Enqueue(Reply("end_turn", new { type = "text", text = "ok" }));
        model.AddUserText("q");
        await model.NextTurnAsync(Tools, CancellationToken.None);

        Assert.DoesNotContain("sk-test-secret", handler.Requests[0].GetRawText());
        Assert.DoesNotContain("sk-test-secret", model.LastRequestJson);
        Assert.Contains("claude-haiku-5-5", model.LastRequestJson);
    }
}
