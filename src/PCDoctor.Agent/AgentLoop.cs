using Anthropic.Exceptions;
using PCDoctor.Core;

namespace PCDoctor.Agent;

/// <summary>
/// The agent loop: send the question, run the tools the model asks for, send results back,
/// and repeat until the model answers or a limit is hit.
/// </summary>
public sealed class AgentLoop
{
    readonly IChatModel _model;
    readonly ToolInvoker _invoker;
    readonly AgentSettings _settings;

    public AgentLoop(IChatModel model, ToolInvoker invoker, AgentSettings settings)
    {
        _model = model;
        _invoker = invoker;
        _settings = settings;
    }

    public async Task RunAsync(string question, Action<AgentEvent> report, CancellationToken ct)
    {
        _model.AddUserText(question);
        try
        {
            for (int i = 0; i < _settings.MaxIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                ModelTurn turn = await _model.NextTurnAsync(_invoker.Tools, ct);

                if (!string.IsNullOrWhiteSpace(turn.Text))
                    report(new AgentEvent(AgentEventKind.AssistantMessage, turn.Text));

                if (turn.StopReason == "refusal")
                {
                    report(new AgentEvent(AgentEventKind.Error, "Modellen afviste at svare på denne forespørgsel."));
                    return;
                }
                if (turn.StopReason == "max_tokens")
                {
                    report(new AgentEvent(AgentEventKind.Error, "Svaret blev for langt og blev afbrudt. Prøv at stille et mere afgrænset spørgsmål."));
                    return;
                }
                if (turn.ToolCalls.Count == 0)
                    return;

                var results = new List<ToolCallResult>();
                foreach (ToolCall call in turn.ToolCalls)
                {
                    string input = ToolInvoker.Describe(call.Input);
                    report(new AgentEvent(AgentEventKind.ToolStarted, $"Kører {call.Name}", input));
                    var started = DateTimeOffset.Now;
                    ToolResult result = await _invoker.InvokeAsync(call, report, ct);
                    double secs = (DateTimeOffset.Now - started).TotalSeconds;
                    report(new AgentEvent(
                        AgentEventKind.ToolFinished,
                        $"{call.Name} {(result.IsError ? "fejlede" : "færdig")} ({secs:0.0} s)",
                        $"Input:\n{input}\n\nResultat:\n{result.Content}"));
                    results.Add(new ToolCallResult(call.Id, result));
                }
                _model.AddToolResults(results);
            }

            report(new AgentEvent(AgentEventKind.Error,
                $"Stoppede efter {_settings.MaxIterations} runder uden et endeligt svar. Grænsen kan ændres under Indstillinger."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report(new AgentEvent(AgentEventKind.Info, "Stoppet af brugeren."));
        }
        catch (AnthropicUnauthorizedException)
        {
            report(new AgentEvent(AgentEventKind.Error, "API-nøglen blev afvist. Tjek nøglen under Indstillinger."));
        }
        catch (AnthropicRateLimitException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, "For mange forespørgsler lige nu (rate limit). Vent lidt og prøv igen.", ex.Message));
        }
        catch (Anthropic5xxException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, "Anthropic's tjeneste svarede med en fejl. Prøv igen om lidt.", ex.Message));
        }
        catch (AnthropicIOException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, "Kunne ikke forbinde til Anthropic. Tjek internetforbindelsen.", ex.Message));
        }
        catch (AnthropicException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, "Fejl fra Anthropic API.", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, "Kunne ikke forbinde til Anthropic. Tjek internetforbindelsen.", ex.Message));
        }
    }
}
