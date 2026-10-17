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

    /// <summary>The "stopped by the user" message in each language (Danish, English, Swedish, Norwegian, German).</summary>
    static readonly string[] StopMessages =
        ["Stoppet af brugeren.", "Stopped by the user.", "Stoppad av användaren.", "Stoppet av brukeren.", "Vom Benutzer gestoppt."];

    /// <summary>True for the "stopped by the user" message in any language, so the chat can show it.</summary>
    public static bool IsStopMessage(string text) => StopMessages.Contains(text) || text.StartsWith("Stoppet");

    static string NoConnection => L.T("Kunne ikke forbinde til Anthropic. Tjek internetforbindelsen.", "Could not connect to Anthropic. Check the internet connection.",
        "Kunde inte ansluta till Anthropic. Kontrollera internetanslutningen.", "Kunne ikke koble til Anthropic. Sjekk internettforbindelsen.",
        "Keine Verbindung zu Anthropic. Prüfe die Internetverbindung.");

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
                    report(new AgentEvent(AgentEventKind.Error, L.T("Modellen afviste at svare på denne forespørgsel.", "The model refused to answer this request.",
                        "Modellen vägrade att svara på den här förfrågan.", "Modellen nektet å svare på denne forespørselen.", "Das Modell hat die Antwort auf diese Anfrage verweigert.")));
                    return;
                }
                if (turn.StopReason == "max_tokens")
                {
                    report(new AgentEvent(AgentEventKind.Error, L.T("Svaret blev for langt og blev afbrudt. Prøv at stille et mere afgrænset spørgsmål.",
                        "The answer got too long and was cut off. Try asking a narrower question.",
                        "Svaret blev för långt och avbröts. Försök ställa en mer avgränsad fråga.",
                        "Svaret ble for langt og ble avbrutt. Prøv å stille et mer avgrenset spørsmål.",
                        "Die Antwort wurde zu lang und abgebrochen. Stelle eine enger gefasste Frage.")));
                    return;
                }
                if (turn.ToolCalls.Count == 0)
                    return;

                var results = new List<ToolCallResult>();
                foreach (ToolCall call in turn.ToolCalls)
                {
                    string input = ToolInvoker.Describe(call.Input);
                    report(new AgentEvent(AgentEventKind.ToolStarted, L.T($"Kører {call.Name}", $"Running {call.Name}", $"Kör {call.Name}", $"Kjører {call.Name}", $"Führe {call.Name} aus"), input));
                    var started = DateTimeOffset.Now;
                    ToolResult result = await _invoker.InvokeAsync(call, report, ct);
                    double secs = (DateTimeOffset.Now - started).TotalSeconds;
                    report(new AgentEvent(
                        AgentEventKind.ToolFinished,
                        $"{call.Name} {(result.IsError ? L.T("fejlede", "failed", "misslyckades", "feilet", "fehlgeschlagen") : L.T("færdig", "done", "klar", "ferdig", "fertig"))} ({secs:0.0} s)",
                        $"Input:\n{input}\n\nResultat:\n{result.Content}"));
                    results.Add(new ToolCallResult(call.Id, result));
                }
                _model.AddToolResults(results);
            }

            report(new AgentEvent(AgentEventKind.Error,
                L.T(
                $"Stoppede efter {_settings.MaxIterations} runder uden et endeligt svar. Grænsen kan ændres under Indstillinger.",
                $"Stopped after {_settings.MaxIterations} rounds without a final answer. The limit can be changed under Settings.",
                $"Stoppade efter {_settings.MaxIterations} omgångar utan ett slutligt svar. Gränsen kan ändras under Inställningar.",
                $"Stoppet etter {_settings.MaxIterations} runder uten et endelig svar. Grensen kan endres under Innstillinger.",
                $"Nach {_settings.MaxIterations} Runden ohne endgültige Antwort gestoppt. Das Limit kann unter Einstellungen geändert werden.")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report(new AgentEvent(AgentEventKind.Info, L.T(StopMessages[0], StopMessages[1], StopMessages[2], StopMessages[3], StopMessages[4])));
        }
        catch (AnthropicUnauthorizedException)
        {
            report(new AgentEvent(AgentEventKind.Error, L.T("API-nøglen blev afvist. Tjek nøglen under Indstillinger.", "The API key was rejected. Check the key under Settings.",
                "API-nyckeln avvisades. Kontrollera nyckeln under Inställningar.", "API-nøkkelen ble avvist. Sjekk nøkkelen under Innstillinger.",
                "Der API-Schlüssel wurde abgelehnt. Prüfe den Schlüssel unter Einstellungen.")));
        }
        catch (AnthropicRateLimitException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, L.T("For mange forespørgsler lige nu (rate limit). Vent lidt og prøv igen.",
                "Too many requests right now (rate limit). Wait a moment and try again.",
                "För många förfrågningar just nu (rate limit). Vänta lite och försök igen.",
                "For mange forespørsler akkurat nå (rate limit). Vent litt og prøv igjen.",
                "Gerade zu viele Anfragen (Rate Limit). Warte kurz und versuche es erneut."), ex.Message));
        }
        catch (Anthropic5xxException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, L.T("Anthropic's tjeneste svarede med en fejl. Prøv igen om lidt.", "Anthropic's service answered with an error. Try again in a moment.",
                "Anthropics tjänst svarade med ett fel. Försök igen om en stund.", "Anthropics tjeneste svarte med en feil. Prøv igjen om litt.",
                "Der Dienst von Anthropic hat mit einem Fehler geantwortet. Versuche es gleich noch einmal."), ex.Message));
        }
        catch (AnthropicIOException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, NoConnection, ex.Message));
        }
        catch (AnthropicException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, L.T("Fejl fra Anthropic API.", "Error from the Anthropic API.", "Fel från Anthropic API.", "Feil fra Anthropic API.", "Fehler von der Anthropic-API."), ex.Message));
        }
        catch (HttpRequestException ex)
        {
            report(new AgentEvent(AgentEventKind.Error, NoConnection, ex.Message));
        }
    }
}
