using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using PCDoctor.Agent;
using PCDoctor.Core;
using PCDoctor.Diagnostics;
using PCDoctor.Storage;

namespace PCDoctor.Tests;

/// <summary>A history database in a temp file, deleted afterwards.</summary>
sealed class TempHistory : IDisposable
{
    public readonly string File = Path.Combine(Path.GetTempPath(), $"pcdoctor-history-{Guid.NewGuid():N}.db");
    public readonly SessionStore Store;

    public TempHistory(string? secret = null) => Store = new SessionStore(File, () => secret);

    public long NewSession(string title = "Hvorfor crasher Minecraft?") =>
        Store.StartSession(new SessionInfo(title, AnthropicChatModel.ModelId, "Dansk", "{}", SessionStore.DescribeComputer()));

    public void Dispose()
    {
        try { System.IO.File.Delete(File); } catch (IOException) { }
    }
}

/// <summary>A fake Anthropic API: answers with queued JSON replies and records every request.</summary>
sealed class FakeAnthropicApi : HttpMessageHandler
{
    public readonly Queue<(HttpStatusCode Status, string Body)> Replies = new();
    public readonly List<JsonElement> Bodies = [];
    public readonly List<string> ApiKeyHeaders = [];

    public void Reply(string stop, params object[] content) => Replies.Enqueue((HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        id = $"msg_{Replies.Count}", type = "message", role = "assistant", model = AnthropicChatModel.ModelId,
        content, stop_reason = stop, stop_sequence = (string?)null,
        usage = new { input_tokens = 10, output_tokens = 5 },
    })));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
        if (request.Headers.TryGetValues("x-api-key", out var keys))
            ApiKeyHeaders.AddRange(keys);
        var (status, body) = Replies.Dequeue();
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public AnthropicChatModel Model(string apiKey, AgentSettings settings) =>
        new(new AnthropicClient { ApiKey = apiKey, HttpClient = new HttpClient(this), MaxRetries = 0 }, settings);
}

public class SecretMaskerTests
{
    [Theory]
    [InlineData("key is sk-ant-api03-AbCdEfGh_123-xyz here", "key is [skjult] here")]
    [InlineData("Authorization: Bearer abcdefgh12345678", "Authorization: Bearer [skjult]")]
    [InlineData("""{"x-api-key":"whatever-1234"}""", """{"x-api-key":"[skjult]"}""")]
    [InlineData("password=hunter22", "password=[skjult]")]
    [InlineData("""{"max_tokens":16000,"input_tokens":10}""", """{"max_tokens":16000,"input_tokens":10}""")]
    [InlineData("Minecraft crashed: Ticking entity", "Minecraft crashed: Ticking entity")]
    public void Hides_secrets_and_leaves_normal_text_alone(string input, string expected) =>
        Assert.Equal(expected, SecretMasker.Apply(input));

    [Fact]
    public void Hides_the_users_own_key_whatever_its_shape() =>
        Assert.Equal("found [skjult] in env", SecretMasker.Apply("found my-own-odd-key-0000 in env", "my-own-odd-key-0000"));
}

public class SessionStoreTests
{
    [Fact]
    public void Saves_and_reopens_a_whole_conversation()
    {
        using var h = new TempHistory();
        long id = h.NewSession();
        var t0 = DateTimeOffset.Now;
        h.Store.AddEvent(id, new AgentEvent(AgentEventKind.UserMessage, "Hvorfor crasher Minecraft?") { Time = t0 }, PermissionMode.ReadOnly);
        h.Store.AddEvent(id, new AgentEvent(AgentEventKind.ToolStarted, "Kører gaming_logs", "{}"), PermissionMode.ReadOnly);
        h.Store.AddEvent(id, new AgentEvent(AgentEventKind.ToolFinished, "gaming_logs færdig (0.1 s)", "Resultat: ..."), PermissionMode.Assisted);
        h.Store.AddEvent(id, new AgentEvent(AgentEventKind.AssistantMessage, "Sandsynlig årsag: Better Pets."), PermissionMode.Assisted);
        h.Store.AddApiCall(id, new ApiExchange(t0, """{"model":"claude-haiku-5-5"}""", """{"stop_reason":"end_turn"}""", 1.25));

        var s = h.Store.Load(id)!;
        Assert.Equal("Hvorfor crasher Minecraft?", s.Info.Title);
        Assert.Equal(AnthropicChatModel.ModelId, s.Info.Model);
        Assert.Equal([AgentEventKind.UserMessage, AgentEventKind.ToolStarted, AgentEventKind.ToolFinished, AgentEventKind.AssistantMessage], s.Events.Select(e => e.Event.Kind));
        Assert.Equal(t0.ToUnixTimeMilliseconds(), s.Events[0].Event.Time.ToUnixTimeMilliseconds());
        Assert.Equal("{}", s.Events[1].Event.Detail);
        Assert.Equal(PermissionMode.Assisted, s.Events[2].Mode);

        var call = Assert.Single(s.ApiCalls);
        Assert.Equal(1.25, call.Seconds);
        var full = h.Store.LoadApiCall(call.Id)!;
        Assert.Contains("claude-haiku-5-5", full.Request);
        Assert.Contains("end_turn", full.Response);

        var listed = Assert.Single(h.Store.ListSessions());
        Assert.Equal((1, 1, false), (listed.ToolCalls, listed.ApiCalls, listed.HadError));
    }

    [Fact]
    public void Lists_newest_first_deletes_and_keeps_only_the_newest()
    {
        using var h = new TempHistory();
        long a = h.NewSession("a"), b = h.NewSession("b"), c = h.NewSession("c");
        h.Store.AddEvent(a, new AgentEvent(AgentEventKind.Error, "boom"), PermissionMode.ReadOnly);
        Assert.Equal(["c", "b", "a"], h.Store.ListSessions().Select(s => s.Title));
        Assert.True(h.Store.ListSessions().Single(s => s.Id == a).HadError);

        h.Store.Delete(b);
        Assert.Null(h.Store.Load(b));
        Assert.Equal(["c", "a"], h.Store.ListSessions().Select(s => s.Title));

        h.Store.Prune(keep: 1);
        Assert.Equal(["c"], h.Store.ListSessions().Select(s => s.Title));
        Assert.Null(h.Store.Load(a));
    }

    [Fact]
    public void Secrets_never_reach_the_database_file()
    {
        const string key = "sk-ant-api03-REALLOOKINGKEY-1234567890";
        using var h = new TempHistory(secret: key);
        long id = h.NewSession($"my key is {key}");
        h.Store.AddEvent(id, new AgentEvent(AgentEventKind.ToolFinished, "shell_execute færdig", $"$env:ANTHROPIC_API_KEY = {key}"), PermissionMode.Yolo);
        h.Store.AddApiCall(id, new ApiExchange(DateTimeOffset.Now, $"{{\"note\":\"{key}\"}}", null, 0.1, $"401 for key {key}"));

        string raw = Encoding.UTF8.GetString(File.ReadAllBytes(h.File));
        Assert.DoesNotContain("REALLOOKINGKEY", raw);
        Assert.Contains(SecretMasker.Mask, h.Store.Load(id)!.Events[0].Event.Detail);
    }

    [Fact]
    public void Writes_from_several_threads_at_once_all_land()
    {
        using var h = new TempHistory();
        long id = h.NewSession();
        Parallel.For(0, 40, i => h.Store.AddEvent(id, new AgentEvent(AgentEventKind.Info, $"e{i}"), PermissionMode.ReadOnly));
        Assert.Equal(40, h.Store.Load(id)!.Events.Count);
    }
}

/// <summary>
/// End-to-end checks with everything real except the network: the Anthropic adapter (against a fake API),
/// the agent loop, the permission gate, the Minecraft tool on fixture files, and the SQLite history.
/// </summary>
public class IntegrationTests
{
    const string Key = "sk-ant-api03-INTEGRATIONTESTKEY-0000";

    [Fact]
    public async Task Crash_log_fixture_the_agent_reads_the_evidence_and_the_session_is_traceable()
    {
        using var home = new FakeMinecraftHome();
        using var h = new TempHistory(Key);
        var api = new FakeAnthropicApi();
        api.Reply("tool_use",
            new { type = "thinking", thinking = "", signature = "sig-1" },
            new { type = "tool_use", id = "toolu_1", name = "gaming_logs", input = new { } });
        api.Reply("end_turn", new
        {
            type = "text",
            text = "Sandsynlig årsag: modden Better Pets (betterpets 1.4.2). Crashrapporten peger på den. Konfidens: sandsynlig.",
        });

        var settings = new AgentSettings();
        long id = h.NewSession();
        var model = api.Model(Key, settings);
        model.Exchanged = x => h.Store.AddApiCall(id, x);
        var invoker = new ToolInvoker([home.Tool(), new RecordingTool("shell_execute", ToolKind.Shell)], settings, () => PermissionMode.ReadOnly, (_, _) => Task.FromResult(false));
        await new AgentLoop(model, invoker, settings).RunAsync("Hvorfor crasher Minecraft?", e => h.Store.AddEvent(id, e, PermissionMode.ReadOnly), CancellationToken.None);

        // The model is told not to claim a cause without evidence...
        string system = api.Bodies[0].GetProperty("system").GetRawText();
        Assert.Contains("Never claim to have found the cause without evidence", system);
        Assert.Contains("Only blame a mod or driver the evidence names", system);

        // ...and it actually received the evidence from the fixture crash report.
        var messages = api.Bodies[1].GetProperty("messages");
        Assert.Equal("thinking", messages[1].GetProperty("content")[0].GetProperty("type").GetString());
        var toolResult = messages[2].GetProperty("content")[0];
        Assert.Equal("toolu_1", toolResult.GetProperty("tool_use_id").GetString());
        string evidence = toolResult.GetProperty("content").GetRawText();
        Assert.Contains("Ticking entity", evidence);
        Assert.Contains("betterpets", evidence);

        // The key went in the header, as it must, but nowhere else.
        Assert.Equal([Key, Key], api.ApiKeyHeaders);
        Assert.DoesNotContain("INTEGRATIONTESTKEY", Encoding.UTF8.GetString(File.ReadAllBytes(h.File)));

        // The whole session can be reopened from the history and every step traced.
        var s = h.Store.Load(id)!;
        Assert.Contains(s.Events, e => e.Event.Kind == AgentEventKind.ToolStarted && e.Event.Title.Contains("gaming_logs"));
        var finished = Assert.Single(s.Events, e => e.Event.Kind == AgentEventKind.ToolFinished);
        Assert.Contains("betterpets", finished.Event.Detail);
        Assert.Contains("Better Pets", s.Events.Last(e => e.Event.Kind == AgentEventKind.AssistantMessage).Event.Title);

        Assert.Equal(2, s.ApiCalls.Count);
        var first = h.Store.LoadApiCall(s.ApiCalls[0].Id)!;
        Assert.Contains("claude-haiku-5-5", first.Request);
        Assert.Contains("Hvorfor crasher Minecraft?", first.Request);
        Assert.Contains("family's Windows PC", first.Request); // readable, not \u0027-escaped
        Assert.Contains("gaming_logs", first.Response);
        // Both are real JSON, so the History page can show them nicely formatted.
        Assert.Equal("claude-haiku-5-5", JsonDocument.Parse(first.Request).RootElement.GetProperty("model").GetString());
        Assert.Equal("tool_use", JsonDocument.Parse(first.Response!).RootElement.GetProperty("stop_reason").GetString());
        Assert.Contains("toolu_1", h.Store.LoadApiCall(s.ApiCalls[1].Id)!.Request);
    }

    [Fact]
    public async Task A_failed_api_call_is_saved_with_its_error()
    {
        using var h = new TempHistory(Key);
        var api = new FakeAnthropicApi();
        api.Replies.Enqueue((HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
        var settings = new AgentSettings();
        long id = h.NewSession();
        var model = api.Model(Key, settings);
        model.Exchanged = x => h.Store.AddApiCall(id, x);
        var events = new List<AgentEvent>();
        var invoker = new ToolInvoker([], settings, () => PermissionMode.ReadOnly, (_, _) => Task.FromResult(false));

        await new AgentLoop(model, invoker, settings).RunAsync("q", events.Add, CancellationToken.None);

        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Title.Contains("API-nøglen"));
        var call = Assert.Single(h.Store.Load(id)!.ApiCalls);
        Assert.NotNull(call.Error);
        Assert.Equal(0, call.ResponseChars);
    }

    [Theory]
    [InlineData(PermissionMode.ReadOnly, false, "afvist (Read-only)", 0)]
    [InlineData(PermissionMode.Assisted, false, "afvist af brugeren", 0)]
    [InlineData(PermissionMode.Assisted, true, "godkendt af brugeren", 1)]
    public async Task Shell_refusals_and_approvals_are_recorded_with_the_command(PermissionMode mode, bool approve, string expected, int runs)
    {
        using var h = new TempHistory();
        var shell = new RecordingTool("shell_execute", ToolKind.Shell);
        var model = new ScriptedModel();
        model.Enqueue(new ModelTurn("", [Json.Call("shell_execute", """{"command":"Get-Process minecraft"}""")], "tool_use"));
        model.Enqueue(new ModelTurn("Done.", [], "end_turn"));
        var settings = new AgentSettings();
        long id = h.NewSession();
        var invoker = new ToolInvoker([shell], settings, () => mode, (_, _) => Task.FromResult(approve));

        await new AgentLoop(model, invoker, settings).RunAsync("q", e => h.Store.AddEvent(id, e, mode), CancellationToken.None);

        Assert.Equal(runs, shell.Runs);
        var e = Assert.Single(h.Store.Load(id)!.Events, e => e.Event.Title.Contains(expected));
        Assert.Equal("Get-Process minecraft", e.Event.Detail);
        Assert.Equal(mode, e.Mode);
    }

    /// <summary>
    /// Real Claude Haiku 5.5 against the crash fixture. Costs a few cents, so it only runs when
    /// PCDOCTOR_LIVE_TEST_KEY is set; CI and normal test runs skip it.
    /// </summary>
    [LiveFact]
    public async Task Live_haiku_uses_the_crash_log_and_states_its_confidence()
    {
        string key = Environment.GetEnvironmentVariable(LiveFactAttribute.Variable)!;
        using var home = new FakeMinecraftHome();
        var settings = new AgentSettings { MaxIterations = 8 };
        var events = new List<AgentEvent>();
        var invoker = new ToolInvoker([home.Tool()], settings, () => PermissionMode.ReadOnly, (_, _) => Task.FromResult(false));

        await new AgentLoop(new AnthropicChatModel(key, settings), invoker, settings)
            .RunAsync("Minecraft crashede i går aftes. Hvorfor?", events.Add, CancellationToken.None);

        Assert.Contains(events, e => e.Kind == AgentEventKind.ToolStarted && e.Title.Contains("gaming_logs"));
        string answer = events.Last(e => e.Kind == AgentEventKind.AssistantMessage).Title.ToLowerInvariant();
        Assert.Contains("better pets", answer.Replace("betterpets", "better pets"));
        Assert.Matches("bekræftet|sandsynlig|mulig|ikke fundet|ikke identificeret", answer);
    }
}

/// <summary>Fixtures with a known outcome, listed in Fixtures/known-outcomes.json. Add a file and a line to add a case.</summary>
public class KnownOutcomeFixtureTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in JsonDocument.Parse(Fixture.Text("known-outcomes.json")).RootElement.EnumerateArray())
            data.Add(c.GetProperty("file").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture_gives_its_known_outcome(string file)
    {
        var c = JsonDocument.Parse(Fixture.Text("known-outcomes.json")).RootElement.EnumerateArray()
            .Single(x => x.GetProperty("file").GetString() == file);
        string text = Fixture.Text(file.Split('/'));
        object parsed = c.GetProperty("parser").GetString() switch
        {
            "crash_report" => MinecraftCrashReport.Parse(text),
            "jvm_crash" => JvmCrashLog.Parse(text),
            "latest_log" => MinecraftLatestLog.Parse(text),
            "unreal" or "unity" or "roblox" => GameLog.Parse(text, c.GetProperty("parser").GetString()!),
            var p => throw new InvalidOperationException($"Unknown parser {p}"),
        };
        string json = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string outcome = c.GetProperty("outcome").GetString()!;

        foreach (var s in c.GetProperty("contains").EnumerateArray())
            Assert.True(json.Contains(s.GetString()!), $"{file}: expected \"{s}\" ({outcome})\n{json}");
        foreach (var s in c.GetProperty("absent").EnumerateArray())
            Assert.False(json.Contains(s.GetString()!), $"{file}: did not expect \"{s}\" ({outcome})");
    }

    [Fact]
    public void Every_log_fixture_has_a_known_outcome()
    {
        var listed = JsonDocument.Parse(Fixture.Text("known-outcomes.json")).RootElement.EnumerateArray()
            .Select(c => c.GetProperty("file").GetString()).ToHashSet();
        foreach (string folder in new[] { "minecraft", "games" })
            foreach (string f in Directory.GetFiles(Fixture.Path(folder)))
                Assert.Contains($"{folder}/{Path.GetFileName(f)}", listed);
    }
}

/// <summary>Runs only when a real API key is given in PCDOCTOR_LIVE_TEST_KEY.</summary>
sealed class LiveFactAttribute : FactAttribute
{
    public const string Variable = "PCDOCTOR_LIVE_TEST_KEY";

    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to run against the real API";
    }
}
