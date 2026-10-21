using System.Text.Json;

namespace Blazorly.Harness.Llm.Adapters;

/// <summary>
/// Outbound tool-call arguments guard. Gateways validate <c>function.arguments</c> as JSON on
/// every request — including history replays — while local execution already coerces. A model
/// emission of <c>""</c> or truncated/non-JSON arguments must be repaired on the wire, or one
/// bad emission wedges the whole session: every later step re-sends it and 400s.
/// Repair matches execution semantics (invalid runs as <c>{}</c>), so the wire never claims
/// arguments the tool did not run with.
/// </summary>
public static class ToolCallWireFormat
{
    public static string CoerceArgumentsJson(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return arguments;
        }
        catch (JsonException)
        {
            return "{}";
        }
    }
}
