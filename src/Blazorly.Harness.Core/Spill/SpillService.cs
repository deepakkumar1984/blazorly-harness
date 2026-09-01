using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Spill;

public sealed record SpillOptions
{
    /// <summary>Total rendered text length above which output is spilled.</summary>
    public int ThresholdChars { get; set; } = 20_000;
    public int HeadChars { get; set; } = 1_600;
    public int TailChars { get; set; } = 400;
    public int ReadWindowChars { get; set; } = 4_000;
}

public sealed record SpillRef(string Id, string Path, int Chars, string Source);

/// <summary>
/// Persists oversized tool output to disk and hands back a locator (dsh's spill store).
/// Files live under <c>&lt;root&gt;/&lt;sessionId&gt;/&lt;id&gt;.txt</c>; the in-memory index
/// maps ids to metadata for the spill_read tool.
/// </summary>
public sealed class SpillService
{
    public const string ServiceKey = "spill";

    private readonly string _root;
    private readonly ConcurrentDictionary<string, SpillRef> _spills = new(StringComparer.Ordinal);
    private int _counter;

    public SpillService(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    /// <summary>Mounts the store, the post-execute spill policy, and the spill_read tool.</summary>
    public static SpillService Mount(HarnessContext ctx, string root, SpillOptions? options = null)
    {
        var service = new SpillService(root)
        {
            Options = options ?? new SpillOptions(),
        };
        ctx.Provide(ServiceKey, service);
        SpillPolicy.Mount(ctx, service);
        ctx.Get<ToolRuntime>(ToolRuntime.ServiceKey).Register(new SpillReadTool(service));
        return service;
    }

    public SpillOptions Options { get; set; } = new();

    public SpillRef Save(string? sessionId, string source, string text)
    {
        var id = $"spill_{++_counter}";
        var dir = Path.Combine(_root, string.IsNullOrWhiteSpace(sessionId) ? "_global" : sessionId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id}.txt");
        File.WriteAllText(path, text, Encoding.UTF8);
        var reference = new SpillRef(id, path, text.Length, source);
        _spills[id] = reference;
        return reference;
    }

    public SpillRef? Describe(string id) => _spills.GetValueOrDefault(id);

    /// <summary>Reads a window of the spilled text; returns the window plus how many chars remain after it.</summary>
    public (string Text, int RemainingChars) Read(string id, int offset = 0, int? maxChars = null)
    {
        var spill = _spills.GetValueOrDefault(id)
            ?? throw new ToolException("SPILL_NOT_FOUND", $"no spill named '{id}'");
        var window = Math.Clamp(maxChars ?? Options.ReadWindowChars, 1, 100_000);
        var text = File.ReadAllText(spill.Path, Encoding.UTF8);
        var start = Math.Clamp(offset, 0, text.Length);
        var end = Math.Min(text.Length, start + window);
        return (text[start..end], text.Length - end);
    }

    public string BuildPreview(SpillRef spill, string text)
    {
        var head = text[..Math.Min(Options.HeadChars, text.Length)];
        var tailStart = Math.Max(Options.HeadChars, text.Length - Options.TailChars);
        var tail = text.Length > tailStart ? text[tailStart..] : string.Empty;
        return $"{head}\n[…{spill.Chars} chars spilled as '{spill.Id}'; call spill_read with spill_id=\"{spill.Id}\" to read it in windows]\n{tail}";
    }
}

/// <summary>
/// Post-execute policy: successful results whose rendered text exceeds the threshold get
/// their presentation content replaced by the preview + locator. Canonical values are
/// untouched; error results and image-bearing content are never spilled.
/// </summary>
public static class SpillPolicy
{
    public static IDisposable Mount(HarnessContext ctx, SpillService spills)
    {
        return ctx.OnWaterfall<ToolPostExecute, PostToolDecision, PostToolDecision>("tools/post-execute",
            (payload, value, next, _) =>
            {
                var options = spills.Options;
                var result = payload.Result;
                if (value.Kind == PostToolDecision.AcceptKind
                    && !result.IsError
                    && payload.Execution.Input.Agent is not null
                    && !result.Content.OfType<ImageBlock>().Any())
                {
                    var text = string.Concat(result.Content.OfType<TextBlock>().Select(b => b.Text));
                    if (text.Length > options.ThresholdChars)
                    {
                        var spill = spills.Save(payload.Execution.Input.Agent.Session.Id, payload.Execution.Input.Name, text);
                        return Task.FromResult(PostToolDecision.AcceptContent([new TextBlock(spills.BuildPreview(spill, text))]));
                    }
                }
                return next(value);
            });
    }
}

/// <summary>Retrieves windows of spilled output by locator id.</summary>
public sealed class SpillReadTool(SpillService spills) : ToolDefinition<SpillReadTool.Args, string>
{
    public sealed record Args(
        [property: JsonPropertyName("spill_id")] string SpillId,
        int? Offset = null,
        int? MaxChars = null);

    public override string Name => "spill_read";

    public override string Description =>
        "Read a window of output that was spilled to disk because it exceeded the inline size limit. "
        + "Use the spill id from the '[output spilled …]' note; page with offset until remaining_chars is 0.";

    public override int? TimeoutMs => 30_000;

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["spill_id"] = JsonSchema.String("The spill id from the output note, e.g. spill_1."),
            ["offset"] = JsonSchema.Integer("Character offset to start reading from (default 0)."),
            ["max_chars"] = JsonSchema.Integer("Window size in characters (default 4000)."),
        },
        required: ["spill_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.String("The requested window of the spilled output plus a remaining_chars note.");

    protected override Task<string> ExecuteTyped(Args args, ToolRunContext exec)
    {
        var (text, remaining) = spills.Read(args.SpillId, args.Offset ?? 0, args.MaxChars);
        return Task.FromResult(text + $"\n[remaining_chars: {remaining}]");
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(Args args, string value) => [new TextBlock(value)];
}
