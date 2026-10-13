using System.Text.RegularExpressions;

namespace PCDoctor.Core;

/// <summary>
/// Hides secrets before text is saved or shown in the history: the user's own API key,
/// anything shaped like an Anthropic key, and common "key=..." / "Bearer ..." patterns.
/// </summary>
public static partial class SecretMasker
{
    public const string Mask = "[skjult]";

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"(?i)(\bbearer\s+)[A-Za-z0-9._~+/\-]{8,}=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)(""?\b(?:x-api-key|api[_-]?key|password|secret|token)""?\s*[:=]\s*""?)([^""\s,;}]{4,})")]
    private static partial Regex KeyValue();

    public static string Apply(string? text, string? knownSecret = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!string.IsNullOrWhiteSpace(knownSecret) && knownSecret.Length >= 8)
            text = text.Replace(knownSecret, Mask, StringComparison.Ordinal);
        text = AnthropicKey().Replace(text, Mask);
        text = Bearer().Replace(text, m => m.Groups[1].Value + Mask);
        text = KeyValue().Replace(text, m => m.Groups[1].Value + Mask);
        return text;
    }
}
