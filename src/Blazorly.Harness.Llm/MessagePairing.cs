namespace Blazorly.Harness.Llm;

/// <summary>
/// Providers reject a history whose tool calls and tool results are not paired, and they reject it
/// as a hard 400 rather than skipping the stray message:
/// <list type="bullet">
/// <item>OpenAI-compatible — "Messages with role 'tool' must be a response to a preceding message
/// with 'tool_calls'", and the mirror error for a tool_call no tool message answers.</item>
/// <item>Anthropic — a <c>tool_result</c> block with no preceding <c>tool_use</c>.</item>
/// <item>Responses API — a <c>function_call_output</c> whose <c>call_id</c> has no <c>function_call</c>.</item>
/// </list>
/// Compaction boundaries, pruning, and log repair after a kill can all produce such a history, so
/// the derivation repairs it before any adapter sees it: one place, every provider.
/// </summary>
public static class MessagePairing
{
    /// <summary>
    /// Drops what cannot be sent: tool results with no surviving call, and tool calls no result
    /// answers (the rest of the assistant message — text and reasoning — is kept). Messages left
    /// with no content are removed. A history that is already paired is returned unchanged.
    /// </summary>
    public static IReadOnlyList<Message> Repair(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return messages;

        // A call is answerable only if some result for it appears later in the history.
        var resultPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            foreach (var block in messages[i].Content.OfType<ToolResultBlock>())
                resultPositions.TryAdd(block.ToolCallId, i);
        }

        var repaired = new List<Message>(messages.Count);
        var emittedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var calls = message.Content.OfType<ToolCallBlock>().ToList();
            var results = message.Content.OfType<ToolResultBlock>().ToList();

            if (calls.Count > 0)
            {
                var kept = calls.Where(call => resultPositions.TryGetValue(call.Id, out var at) && at > i).ToList();
                if (kept.Count != calls.Count)
                {
                    changed = true;
                    message = WithContent(message, ReplaceCalls(message.Content, kept));
                }
                foreach (var call in kept) emittedCallIds.Add(call.Id);
            }

            if (results.Count > 0)
            {
                var kept = results.Where(result => emittedCallIds.Contains(result.ToolCallId)).ToList();
                if (kept.Count != results.Count)
                {
                    changed = true;
                    message = WithContent(message, ReplaceResults(message.Content, kept));
                }
            }

            if (message.Content.Count == 0)
            {
                changed = true;
                continue; // nothing left to send
            }
            repaired.Add(message);
        }

        return changed ? repaired : messages;
    }

    /// <summary>True when repair would drop or rewrite anything — i.e. the history is not sendable as-is.</summary>
    public static bool HasOrphans(IReadOnlyList<Message> messages)
    {
        var repaired = Repair(messages);
        if (repaired.Count != messages.Count) return true;
        for (var i = 0; i < repaired.Count; i++)
        {
            if (!ReferenceEquals(repaired[i], messages[i])) return true;
        }
        return false;
    }

    private static IReadOnlyList<ContentBlock> ReplaceCalls(IReadOnlyList<ContentBlock> blocks, IReadOnlyList<ToolCallBlock> kept)
    {
        var ids = kept.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
        return blocks.Where(b => b is not ToolCallBlock call || ids.Contains(call.Id)).ToList();
    }

    private static IReadOnlyList<ContentBlock> ReplaceResults(IReadOnlyList<ContentBlock> blocks, IReadOnlyList<ToolResultBlock> kept)
    {
        var ids = kept.Select(k => k.ToolCallId).ToHashSet(StringComparer.Ordinal);
        return blocks.Where(b => b is not ToolResultBlock result || ids.Contains(result.ToolCallId)).ToList();
    }

    private static Message WithContent(Message message, IReadOnlyList<ContentBlock> content)
        => message with { Content = content };
}
