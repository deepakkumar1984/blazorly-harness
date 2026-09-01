using System.Text.Json;

namespace Blazorly.Harness.Llm.Adapters;
/// <summary>A scripted adapter: the script function is invoked once per request and returns the chunk sequence.</summary>
public sealed class ReplayAdapter : LlmAdapter
{
    public const string ProviderName = "replay";

    private readonly Func<GenerateOptions, IReadOnlyList<StreamChunk>> _script;
    public int Calls { get; private set; }

    /// <summary>Delay before each chunk; use to simulate slow streams and mid-stream aborts.</summary>
    public int ChunkDelayMs { get; set; }

    public ReplayAdapter(Func<GenerateOptions, IReadOnlyList<StreamChunk>> script) => _script = script;

    public override string Provider => ProviderName;

    public override IReadOnlyList<LlmModelInfo> ListModels()
        => [new LlmModelInfo(ProviderName, "demo", "Replay demo (scripted, no API key)")];

    public override async IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Calls++;
        foreach (var chunk in _script(options))
        {
            ct.ThrowIfCancellationRequested();
            if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs, ct).ConfigureAwait(false);
            else await Task.Yield();
            yield return chunk;
        }
    }
}

/// <summary>Helpers for authoring replay scripts; arguments serialize camelCase like session payloads.</summary>
public static class ReplayScript
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string SerializeArguments(object arguments) => JsonSerializer.Serialize(arguments, CamelCase);
    public static IReadOnlyList<StreamChunk> Text(string text, string finish = FinishReason.Stop)
    {
        var chunks = new List<StreamChunk>();
        if (text.Length > 0)
        {
            chunks.Add(new BlockStartChunk(0, "text"));
            chunks.Add(new TextDeltaChunk(0, text));
            chunks.Add(new BlockEndChunk(0, new TextBlock(text)));
        }
        chunks.Add(new FinishChunk(finish));
        return chunks;
    }

    public static IReadOnlyList<StreamChunk> ToolCall(string name, object arguments, string finish = FinishReason.ToolCalls)
    {
        var callId = Ids.NewCallId();
        var args = SerializeArguments(arguments);
        var textBlock = new TextBlock($"Using {name}.");
        return
        [
            new BlockStartChunk(0, "text"),
            new TextDeltaChunk(0, textBlock.Text),
            new BlockEndChunk(0, textBlock),
            new BlockStartChunk(1, "tool-call"),
            new ToolCallDeltaChunk(1, callId, name, args),
            new BlockEndChunk(1, new ToolCallBlock(callId, name, args)),
            new FinishChunk(finish),
        ];
    }

    public static IReadOnlyList<StreamChunk> ToolCalls(params (string Name, object Arguments)[] calls)
    {
        var chunks = new List<StreamChunk>();
        var callBlocks = new List<ToolCallBlock>();
        foreach (var (name, arguments) in calls)
        {
            var callId = Ids.NewCallId();
            var args = SerializeArguments(arguments);
            var index = callBlocks.Count;
            chunks.Add(new BlockStartChunk(index, "tool-call"));
            chunks.Add(new ToolCallDeltaChunk(index, callId, name, args));
            var block = new ToolCallBlock(callId, name, args);
            chunks.Add(new BlockEndChunk(index, block));
            callBlocks.Add(block);
        }
        chunks.Add(new FinishChunk(FinishReason.ToolCalls));
        return chunks;
    }

    public static IReadOnlyList<StreamChunk> Error(string code, string message)
        => [new FinishChunk(FinishReason.Error, new LlmFailure(message, code))];
}
