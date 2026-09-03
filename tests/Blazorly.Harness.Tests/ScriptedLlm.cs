using System.Text.Json;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;

namespace Blazorly.Harness.Tests;

/// <summary>Test-only scripted LLM route: the script function returns the chunk sequence per request.</summary>
public sealed class ScriptedLlmAdapter : LlmAdapter
{
    public const string ProviderName = "scripted";
    public const string ModelName = "test";

    private readonly Func<GenerateOptions, IReadOnlyList<StreamChunk>> _script;
    public int Calls { get; private set; }

    /// <summary>Delay before each chunk; use to simulate slow streams and mid-stream aborts.</summary>
    public int ChunkDelayMs { get; set; }

    public ScriptedLlmAdapter(Func<GenerateOptions, IReadOnlyList<StreamChunk>> script) => _script = script;

    public override string Provider => ProviderName;

    public override IReadOnlyList<LlmModelInfo> ListModels()
        => [new LlmModelInfo(ProviderName, ModelName, "scripted test model")];

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

/// <summary>Helpers for authoring scripted responses; arguments serialize camelCase like session payloads.</summary>
public static class Scripted
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

/// <summary>The canonical two-step scripted flow: tools first, then a summary once results return.</summary>
public static class ScriptedDemoFlow
{
    private static readonly Dictionary<string, int> Step = new(StringComparer.Ordinal);

    public static IReadOnlyList<StreamChunk> Respond(GenerateOptions options)
    {
        if (options.Purpose == "session-title")
        {
            return Scripted.Text("Scripted run");
        }
        var hasToolResults = options.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Any();
        var step = Step.TryGetValue(options.SessionId ?? "anon", out var current) ? current + 1 : 1;
        Step[options.SessionId ?? "anon"] = step;
        if (!hasToolResults && step == 1)
        {
            return Scripted.ToolCalls(
                ("bash", new { command = "sleep 2.5 && echo \"hello from blazorly harness\" && date", description = "Greet and print the date" }),
                ("todo_write", new { todos = new object[]
                    {
                        new { content = "Run the scripted greeting", status = "completed" },
                        new { content = "Summarize the result", status = "in_progress" },
                    } }));
        }
        var bashOutput = options.Messages
            .SelectMany(m => m.Content).OfType<ToolResultBlock>()
            .SelectMany(b => b.Content).OfType<TextBlock>()
            .Select(t => t.Text).FirstOrDefault() ?? "";
        var summary = bashOutput.Contains("hello from blazorly harness")
            ? "The scripted run completed: I executed `bash` (expand the card above to inspect it) and updated the todo list."
            : "The scripted run completed.";
        return Scripted.Text(summary);
    }
}
