using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Llm;

/// <summary>
/// Replay-consistent token estimation (dsh token-meter density): ceil(chars/4) + per-structure
/// overhead. Used for compaction pressure before real usage anchors exist.
/// </summary>
public static class TokenEstimator
{
    private const int CharsPerToken = 4;
    private const int BlockOverhead = 4;
    private const int RoleOverhead = 4;

    public static long Estimate(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;

    public static long EstimateContent(IReadOnlyList<ContentBlock> blocks)
    {
        long total = 0;
        foreach (var block in blocks)
        {
            total += block switch
            {
                TextBlock text => Estimate(text.Text) + BlockOverhead,
                ReasoningBlock reasoning => Estimate(reasoning.Text) + BlockOverhead,
                ToolCallBlock call => Estimate(call.Name) + Estimate(call.Arguments) + BlockOverhead,
                ToolResultBlock result => EstimateContent(result.Content) + BlockOverhead,
                ImageBlock => 1024, // coarse image placeholder
                _ => BlockOverhead,
            };
        }
        return total;
    }

    public static long EstimateMessage(Message message) => EstimateContent(message.Content) + RoleOverhead;

    public static long EstimateMessages(IEnumerable<Message> messages)
        => messages.Sum(EstimateMessage);

    /// <summary>Prices the request header the way the provider sees it: system + tool schemas.</summary>
    public static long EstimateHeader(string? system, IReadOnlyList<ToolSchema>? tools)
    {
        long total = 0;
        if (!string.IsNullOrEmpty(system)) total += Estimate(system) + BlockOverhead;
        total += EstimateToolSchemas(tools);
        return total;
    }

    /// <summary>Prices just the tool-schema block of the header.</summary>
    public static long EstimateToolSchemas(IReadOnlyList<ToolSchema>? tools)
    {
        long total = 0;
        if (tools is not null)
        {
            foreach (var tool in tools)
            {
                total += Estimate(tool.Name) + Estimate(tool.Description)
                    + Estimate(tool.Parameters.GetRawText()) + BlockOverhead;
            }
        }
        return total;
    }
}
