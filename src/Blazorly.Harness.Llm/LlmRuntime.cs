using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Llm;

/// <summary>The record carried through the llm/stream waterfall.</summary>
public sealed record LlmStreamRequest(GenerateOptions Options, CancellationToken Ct);

/// <summary>Stream value that flows through the llm/stream waterfall middleware.</summary>
public sealed record LlmStream(IAsyncEnumerable<StreamChunk> Chunks);

/// <summary>
/// A provider adapter: one adapter instance per provider route. One adapter call is one
/// provider attempt — retries live in middleware or the loop's error policy, never here.
/// </summary>
public abstract class LlmAdapter
{
    public abstract string Provider { get; }

    public abstract IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken ct);

    public virtual IReadOnlyList<LlmModelInfo> ListModels() => [];
}

/// <summary>ctx.llm — the adapter registry plus the provider-neutral stream service.</summary>
public sealed class LlmRuntime
{
    public const string ServiceKey = "llm";

    private readonly HarnessContext _ctx;
    private readonly Dictionary<string, LlmAdapter> _adapters = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public LlmRuntime(HarnessContext ctx) => _ctx = ctx;

    public static LlmRuntime Mount(HarnessContext ctx)
    {
        var runtime = new LlmRuntime(ctx);
        ctx.Provide(ServiceKey, runtime);
        return runtime;
    }

    public IDisposable RegisterAdapter(LlmAdapter adapter)
    {
        lock (_gate)
        {
            if (_adapters.ContainsKey(adapter.Provider))
                throw new LlmException(LlmErrorCodes.DuplicateAdapter, $"adapter for provider '{adapter.Provider}' is already registered");
            _adapters[adapter.Provider] = adapter;
        }
        return _ctx.Effect(() =>
        {
            lock (_gate) _adapters.Remove(adapter.Provider);
        });
    }

    public IReadOnlyList<string> ListProviders()
    {
        lock (_gate) return [.. _adapters.Keys.OrderBy(p => p, StringComparer.Ordinal)];
    }

    public LlmAdapter? GetAdapter(string provider)
    {
        lock (_gate) return _adapters.GetValueOrDefault(provider);
    }

    public IReadOnlyList<LlmModelInfo> ListModels(string provider)
        => GetAdapter(provider)?.ListModels() ?? [];

    /// <summary>
    /// Streams a generation through the llm/stream waterfall. Adapter throws normalize into
    /// a terminal error/aborted finish chunk; consumer exceptions propagate.
    /// </summary>
    public async IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default, object? subjectKey = null)
    {
        var stream = await _ctx.Events.WaterfallAsync<LlmStreamRequest, LlmStream, LlmStream>(
            "llm/stream",
            new LlmStreamRequest(options, ct),
            new LlmStream(AdapterStream(options, ct)),
            static v => Task.FromResult(v),
            subjectKey,
            ct).ConfigureAwait(false);

        await foreach (var chunk in stream.Chunks.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<StreamChunk> AdapterStream(GenerateOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        LlmAdapter? adapter;
        lock (_gate) adapter = _adapters.GetValueOrDefault(options.Provider);
        if (adapter is null)
        {
            yield return new FinishChunk(FinishReason.Error, new LlmFailure($"no adapter registered for provider '{options.Provider}'", LlmErrorCodes.NoAdapter));
            yield break;
        }

        IAsyncEnumerator<StreamChunk> enumerator = adapter.Stream(options, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                StreamChunk? chunk = null;
                FinishChunk? failureFinish = null;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                    chunk = enumerator.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    failureFinish = new FinishChunk(FinishReason.Aborted, new LlmFailure("request cancelled", LlmErrorCodes.Aborted));
                }
                catch (LlmException ex)
                {
                    failureFinish = new FinishChunk(FinishReason.Error, ex.Failure);
                }
                catch (Exception ex)
                {
                    failureFinish = new FinishChunk(FinishReason.Error, new LlmFailure(ex.Message, LlmErrorCodes.Transport));
                }
                if (failureFinish is not null)
                {
                    yield return failureFinish;
                    yield break;
                }
                if (chunk is not null) yield return chunk;
                if (chunk is FinishChunk) yield break;
            }
            // Provider closed the stream without a terminal chunk.
            yield return new FinishChunk(FinishReason.Error, new LlmFailure("stream ended without finish", LlmErrorCodes.StreamClosed));
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
