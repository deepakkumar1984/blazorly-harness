using System.Text.Json;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.TokenSaver;

/// <summary>
/// Post-execute policy that runs <see cref="ToolOutputCompressor"/> over rendered tool
/// results before they enter history (Settings → Token saver → Compress tool output).
/// Mounted unconditionally; <see cref="Enabled"/> gates it so the setting applies live
/// on save without a restart. Canonical tool values are untouched — only the presentation
/// content the model reads is replaced, exactly like the spill policy. Composes with
/// spill in either registration order: it hands its replacement down the waterfall and
/// respects a downstream replacement (a spill preview) over its own.
/// </summary>
public sealed class ToolOutputCompressionService
{
    public const string ServiceKey = "toolOutputCompression";

    private long _callsCompressed;
    private long _charsSaved;

    public bool Enabled { get; set; }

    /// <summary>Tool results compressed since boot.</summary>
    public long CallsCompressed => Interlocked.Read(ref _callsCompressed);
    /// <summary>Characters removed since boot (marker lines included in the compressed size).</summary>
    public long CharsSaved => Interlocked.Read(ref _charsSaved);
    /// <summary>Rough token saving at RTK's bytes/4 estimate — reliable as a ratio, approximate as a count.</summary>
    public long EstimatedTokensSaved => CharsSaved / 4;

    public static ToolOutputCompressionService Mount(HarnessContext ctx, bool enabled)
    {
        var service = new ToolOutputCompressionService { Enabled = enabled };
        ctx.Provide(ServiceKey, service);
        ctx.OnWaterfall<ToolPostExecute, PostToolDecision, PostToolDecision>("tools/post-execute",
            async (payload, value, next, _) =>
            {
                if (!service.Enabled
                    || value.Kind != PostToolDecision.AcceptKind
                    || payload.Execution.Input.Agent is null
                    || payload.Result.Content.OfType<ImageBlock>().Any())
                    return await next(value).ConfigureAwait(false);

                // An outer policy may already have replaced the content; compress what the
                // model would actually read.
                var source = value.Content ?? payload.Result.Content;
                var text = string.Concat(source.OfType<TextBlock>().Select(b => b.Text));
                var compressed = ToolOutputCompressor.Compress(
                    payload.Execution.Input.Name,
                    BashCommandOf(payload.Execution.Input.Arguments),
                    text,
                    payload.Result.IsError);
                if (compressed is null) return await next(value).ConfigureAwait(false);

                var downstream = await next(PostToolDecision.AcceptContent([new TextBlock(compressed)])).ConfigureAwait(false);
                if (downstream.Kind == PostToolDecision.AcceptKind
                    && downstream.Content is [TextBlock only] && only.Text == compressed)
                {
                    Interlocked.Increment(ref service._callsCompressed);
                    Interlocked.Add(ref service._charsSaved, text.Length - compressed.Length);
                }
                return downstream;
            });
        return service;
    }

    private static string? BashCommandOf(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("command", out var command)
            && command.ValueKind == JsonValueKind.String
            ? command.GetString()
            : null;
}
