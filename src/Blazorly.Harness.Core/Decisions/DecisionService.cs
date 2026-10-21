using System.Collections.Concurrent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Decisions;

/// <summary>Durable payload of a <c>decision/result</c> event (camelCase via SessionJson).</summary>
public sealed record DecisionEventPayload(
    string Seam,
    string Impl,
    long LatencyMs,
    int StateChars,
    string StateHash,
    bool Degraded,
    string? Error,
    string? ModelVersion,
    IReadOnlyDictionary<string, DecisionAnswer> Answers);

public sealed record DecisionOptions
{
    /// <summary>Master switch. Off means every seam falls through to its deterministic logic.</summary>
    public bool Enabled { get; init; }

    /// <summary>Seams allowed to consult the model; empty means none, <c>*</c> means all.</summary>
    public IReadOnlyCollection<string> Seams { get; init; } = [];

    /// <summary>Reuse an identical decision for this long instead of calling again.</summary>
    public int CacheSeconds { get; init; } = 120;

    /// <summary>Cap on cached entries per seam before the oldest are dropped.</summary>
    public int CacheEntries { get; init; } = 256;
}

public sealed record DecisionStats
{
    public long Calls { get; init; }
    public long Answered { get; init; }
    public long Degraded { get; init; }
    public long CacheHits { get; init; }
    public long TotalLatencyMs { get; init; }
    public long MaxLatencyMs { get; init; }
    public long StateChars { get; init; }
    public IReadOnlyDictionary<string, long> PerSeam { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);

    public double MeanLatencyMs => Calls == 0 ? 0 : (double)TotalLatencyMs / Calls;
}

/// <summary>
/// ctx.decisions — the seam between the agent loop and a System One style decision model.
///
/// Three rules make this safe to leave on:
/// <list type="number">
/// <item><b>No opinion is a first-class result.</b> Disabled, unconfigured, timed out, errored or
/// low-confidence all return <c>null</c>, and every caller falls back to the deterministic logic
/// it already had. Turning System One off must be indistinguishable from it never having existed.</item>
/// <item><b>Never in the cancel path unbounded.</b> The client owns a hard timeout; a user cancel
/// propagates through the same token, so a decision can never delay an interruption.</item>
/// <item><b>Every decision is durable.</b> An ignorable <c>decision/result</c> event records the
/// seam, the state hash, the probabilities and the latency, so a behaviour change is attributable
/// and an eval can pin <c>impl: none</c> to stay comparable with older runs.</item>
/// </list>
/// </summary>
public sealed class DecisionService
{
    public const string ServiceKey = "decisions";

    /// <summary>Plugin event type; deliberately not in SessionEventTypes.KnownTypes so readers skip it.</summary>
    public const string EventType = "decision/result";

    private readonly IDecisionModel _model;
    private readonly DecisionOptions _options;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seams;

    private long _calls, _answered, _degraded, _cacheHits, _totalLatency, _maxLatency, _stateChars;
    private readonly ConcurrentDictionary<string, long> _perSeam = new(StringComparer.Ordinal);

    private sealed record CacheEntry(DecisionResult Result, long ExpiresAt);

    public DecisionService(IDecisionModel model, DecisionOptions options)
    {
        _model = model;
        _options = options;
        _seams = new HashSet<string>(options.Seams, StringComparer.OrdinalIgnoreCase);
    }

    public static DecisionService Mount(HarnessContext ctx, IDecisionModel model, DecisionOptions options)
    {
        var service = new DecisionService(model, options);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    public string Impl => _model.Impl;

    /// <summary>True when this seam may consult the model right now.</summary>
    public bool IsEnabled(string seam)
        => _options.Enabled && _model.Available
           && (_seams.Contains("*") || _seams.Contains(seam));

    /// <summary>
    /// Asks the model about <paramref name="seam"/>. Returns null — meaning "use your
    /// deterministic fallback" — when the seam is off, the model is unavailable, the call fails,
    /// or the reply answers none of the questions. Never throws.
    /// </summary>
    public async Task<DecisionResult?> DecideAsync(
        string seam,
        DecisionState state,
        IReadOnlyList<DecisionQuestion> questions,
        Session? session = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled(seam) || questions.Count == 0 || ct.IsCancellationRequested) return null;

        var cacheKey = CacheKey(seam, state, questions);
        if (_options.CacheSeconds > 0 && _cache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                Interlocked.Increment(ref _cacheHits);
                return cached.Result.Answers.Count > 0 ? cached.Result : null;
            }
            _cache.TryRemove(cacheKey, out _);
        }

        DecisionResult? result;
        try
        {
            result = await _model.DecideAsync(state, questions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new DecisionResult(_model.Impl, new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal),
                0, state.Chars, Degraded: true, Error: ex.Message);
        }

        if (result is null) return null;

        Record(seam, result, state);
        if (session is not null) Append(session, seam, result, state);

        if (_options.CacheSeconds > 0)
        {
            TrimCache(seam);
            _cache[cacheKey] = new CacheEntry(result,
                DateTimeOffset.UtcNow.AddSeconds(_options.CacheSeconds).ToUnixTimeMilliseconds());
        }

        // A reply that answered nothing is no opinion at all.
        return result.Answers.Count > 0 ? result : null;
    }

    /// <summary>Convenience: one yes/no question, returning null when unanswered.</summary>
    public Task<DecisionResult?> AskAsync(
        string seam, DecisionState state, DecisionQuestion question,
        Session? session = null, CancellationToken ct = default)
        => DecideAsync(seam, state, [question], session, ct);

    public DecisionStats Stats() => new()
    {
        Calls = Interlocked.Read(ref _calls),
        Answered = Interlocked.Read(ref _answered),
        Degraded = Interlocked.Read(ref _degraded),
        CacheHits = Interlocked.Read(ref _cacheHits),
        TotalLatencyMs = Interlocked.Read(ref _totalLatency),
        MaxLatencyMs = Interlocked.Read(ref _maxLatency),
        StateChars = Interlocked.Read(ref _stateChars),
        PerSeam = new Dictionary<string, long>(_perSeam, StringComparer.Ordinal),
    };

    public void ClearCache() => _cache.Clear();

    private void Record(string seam, DecisionResult result, DecisionState state)
    {
        Interlocked.Increment(ref _calls);
        if (result.Degraded || result.Answers.Count == 0) Interlocked.Increment(ref _degraded);
        else Interlocked.Increment(ref _answered);
        Interlocked.Add(ref _totalLatency, result.LatencyMs);
        Interlocked.Add(ref _stateChars, state.Chars);
        long max;
        do
        {
            max = Interlocked.Read(ref _maxLatency);
            if (result.LatencyMs <= max) break;
        }
        while (Interlocked.CompareExchange(ref _maxLatency, result.LatencyMs, max) != max);
        _perSeam.AddOrUpdate(seam, 1, (_, count) => count + 1);
    }

    private void Append(Session session, string seam, DecisionResult result, DecisionState state)
    {
        try
        {
            session.Append(EventType, new DecisionEventPayload(
                seam, result.Impl, result.LatencyMs, state.Chars, state.Hash(),
                result.Degraded, result.Error, result.ModelVersion, result.Answers),
                new Session.AppendOptions(Ignorable: true));
        }
        catch (Exception ex) when (ex is HarnessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // An observability event must never break the turn it is observing.
        }
    }

    private static string CacheKey(string seam, DecisionState state, IReadOnlyList<DecisionQuestion> questions)
        => seam + "|" + state.Hash() + "|" + string.Join(",", questions.Select(q => q.Key));

    private void TrimCache(string seam)
    {
        if (_cache.Count < _options.CacheEntries) return;
        var prefix = seam + "|";
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Take(_options.CacheEntries / 4))
            _cache.TryRemove(key, out _);
    }
}

/// <summary>
/// Mounts the decision seam. With no client (System One off or keyless) this mounts
/// <see cref="NoOpDecisionModel"/>, so every consumer sees "no opinion" and the harness behaves
/// exactly as it did before the seam existed.
///
/// The service is built eagerly, not during <see cref="ApplyAsync"/>, so a composition root can
/// hand the same instance to the plugins that consume it (auto-plan, risk-gate) without depending
/// on boot order to wire them.
///
/// Named <c>system-one</c>, not <c>decisions</c>, to follow the patches.json convention that a
/// plugin name maps to its <c>Enable*</c> flag — <c>{"disable":["system-one"]}</c> resolves to
/// <c>EnableSystemOne</c>. The service key stays <c>decisions</c>: the seam is vendor-neutral even
/// though this plugin is the System One integration.
/// </summary>
public sealed class DecisionPlugin(IDecisionModel? model, DecisionOptions options) : HarnessPlugin
{
    /// <summary>Plugin name; also the token used in <c>disabledPlugins</c> and patches.json.</summary>
    public const string PluginName = "system-one";

    public override string Name => PluginName;
    public override string[] Inject { get; } = ["sessions"];

    public IDecisionModel Model { get; } = model ?? NoOpDecisionModel.Instance;

    public DecisionService Service { get; } = new(model ?? NoOpDecisionModel.Instance, options);

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        ctx.Provide(DecisionService.ServiceKey, Service);
        return Task.CompletedTask;
    }
}
