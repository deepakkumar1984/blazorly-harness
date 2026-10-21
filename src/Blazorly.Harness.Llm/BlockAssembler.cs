using System.Text;

namespace Blazorly.Harness.Llm;

/// <summary>
/// Folds a stream chunk sequence into the final assistant message. Tolerates delta-only
/// streams; block-end blocks are authoritative when present; deltas arriving for an index
/// that already closed are ignored.
/// </summary>
public sealed class BlockAssembler
{
    private sealed class BlockState(int index, string type)
    {
        public readonly int Index = index;
        public readonly string Type = type;
        public readonly StringBuilder Text = new();
        public string? ToolCallId;
        public string? ToolName;
        public readonly StringBuilder Arguments = new();
        public ContentBlock? Final;
    }

    private readonly Dictionary<int, BlockState> _blocks = new();
    private List<BlockState>? _ordered;
    public TokenUsage? Usage { get; private set; }
    public FinishChunk? Finish { get; private set; }

    public void Push(StreamChunk chunk)
    {
        switch (chunk)
        {
            case BlockStartChunk start:
                GetOrCreate(start.Index, start.BlockType);
                break;
            case TextDeltaChunk delta:
                GetOrCreate(delta.Index, "text").Text.Append(delta.Text);
                break;
            case ReasoningDeltaChunk delta:
                GetOrCreate(delta.Index, "reasoning").Text.Append(delta.Text);
                break;
            case ToolCallDeltaChunk delta:
            {
                var state = GetOrCreate(delta.Index, "tool-call");
                if (!string.IsNullOrEmpty(delta.Id)) state.ToolCallId = delta.Id;
                if (!string.IsNullOrEmpty(delta.Name)) state.ToolName = delta.Name;
                state.Arguments.Append(delta.ArgumentsDelta);
                break;
            }
            case BlockEndChunk end:
                GetOrCreate(end.Index, BlockTypeName(end.Block)).Final = end.Block;
                break;
            case UsageChunk usage:
                Usage = usage.Usage;
                break;
            case FinishChunk finish:
                Finish ??= finish;
                break;
        }
    }

    private BlockState GetOrCreate(int index, string type)
    {
        if (!_blocks.TryGetValue(index, out var state))
        {
            state = new BlockState(index, type);
            _blocks[index] = state;
        }
        _ordered = null;
        return state;
    }

    private List<BlockState> Ordered() => _ordered ??= [.. _blocks.Values.OrderBy(b => b.Index)];

    /// <summary>Assembled blocks: block-end blocks when present, else folded deltas. Max-tokens drops tool-call blocks.</summary>
    public IReadOnlyList<ContentBlock> Blocks()
    {
        var dropToolCalls = Finish?.Reason == FinishReason.MaxTokens;
        return [.. Ordered().Where(b => !(dropToolCalls && b.Type == "tool-call")).Select(Materialize)];
    }

    /// <summary>Non-whitespace text/reasoning blocks only — used for a partial message on abort.</summary>
    public IReadOnlyList<ContentBlock> InterruptedBlocks()
        => [.. Ordered()
            .Where(b => b.Type is "text" or "reasoning")
            .Select(Materialize)
            .Where(b => b switch
            {
                TextBlock t => !string.IsNullOrWhiteSpace(t.Text),
                ReasoningBlock r => !string.IsNullOrWhiteSpace(r.Text),
                _ => false,
            })];

    private static string BlockTypeName(ContentBlock block) => block switch
    {
        TextBlock => "text",
        ReasoningBlock => "reasoning",
        ToolCallBlock => "tool-call",
        ToolResultBlock => "tool-result",
        _ => "text",
    };

    private ContentBlock Materialize(BlockState state)
    {
        if (state.Final is not null) return state.Final;
        return state.Type switch
        {
            "text" => new TextBlock(state.Text.ToString()),
            "reasoning" => new ReasoningBlock(state.Text.ToString()),
            "tool-call" => new ToolCallBlock(state.ToolCallId ?? Ids.NewCallId(), state.ToolName ?? "", state.Arguments.ToString()),
            _ => new TextBlock(state.Text.ToString()),
        };
    }

    public Message BuildMessage(string provider, string model) => Message.CreateAssistant(provider, model, Blocks());
}
