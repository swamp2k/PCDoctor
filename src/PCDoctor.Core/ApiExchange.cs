namespace PCDoctor.Core;

/// <summary>
/// One call to the language model API: exactly what was sent and what came back
/// (or the error), for the technical detail view in the history. Never holds the API key,
/// which travels in a header, not in the body.
/// </summary>
public sealed record ApiExchange(DateTimeOffset Time, string Request, string? Response, double Seconds, string? Error = null);
