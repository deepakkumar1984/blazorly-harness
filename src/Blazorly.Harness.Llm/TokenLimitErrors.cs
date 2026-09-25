using System.Text.RegularExpressions;

namespace Blazorly.Harness.Llm;

/// <summary>Distinguishes an unsupported token-limit field from a rejected token count.</summary>
public static partial class TokenLimitErrors
{
    public static string? NamedParameter(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("max_completion_tokens")) return "max_completion_tokens";
        if (lower.Contains("max_output") || lower.Contains("max output")) return "max_output_tokens";
        return lower.Contains("max_tokens") ? "max_tokens" : null;
    }

    public static string? UnsupportedParameter(string message)
    {
        var match = UnsupportedParameterPattern().Match(message);
        return match.Success ? match.Groups["parameter"].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex("""(?:(?:unsupported|unknown|unrecognized)\s+(?:parameter|field|request\s+argument)(?:\s+supplied)?\s*:?\s*['"`]?(?<parameter>max_tokens|max_completion_tokens|max_output_tokens)\b|\b(?<parameter>max_tokens|max_completion_tokens|max_output_tokens)['"`]?\s+(?:is\s+)?(?:not\s+supported|unsupported|not\s+allowed))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsupportedParameterPattern();
}
