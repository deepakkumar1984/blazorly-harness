using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Retry;

/// <summary>One provider route's retry policy (dsh: the adapter's nested retryPolicy).</summary>
public sealed record RetryPolicyConfig
{
    /// <summary>"normal" retries retryable codes up to MaxRetries; "always" retries every failure without an attempt ceiling.</summary>
    public string Mode { get; init; } = "normal";
    public int MaxRetries { get; init; } = 5;
    public long InitialDelayMs { get; init; } = 500;
    public long MaxDelayMs { get; init; } = 10_000;
    public double JitterRatio { get; init; } = 0.1;

    /// <summary>Overrides the default retryable set when non-null (normal mode only).</summary>
    public IReadOnlyList<string>? RetryableCodes { get; init; }
}

public sealed record RetryOptions
{
    public RetryPolicyConfig Default { get; init; } = new();
    public IReadOnlyDictionary<string, RetryPolicyConfig> Providers { get; init; }
        = new Dictionary<string, RetryPolicyConfig>(StringComparer.Ordinal);
}

/// <summary>
/// Exact-provider retry policy executed at the agent/request-error waterfall. A retrying
/// decision appends a durable llm/retry event, sleeps the backoff (bounded exponential with
/// symmetric jitter; a provider Retry-After at or below MaxDelayMs replaces local backoff
/// without jitter), appends llm/retry-started, and returns a BackoffHandled retry so the
/// driver does not delay again. Everything else falls through to the next policy.
/// </summary>
public sealed class RetryService
{
    public const string ServiceKey = "llmRetry";

    private readonly HarnessContext _ctx;
    private RetryOptions _options;
    private int _counter;

    private RetryService(HarnessContext ctx, RetryOptions options)
    {
        _ctx = ctx;
        _options = options;
    }

    public static RetryService Mount(HarnessContext ctx, RetryOptions? options = null)
    {
        var service = new RetryService(ctx, options ?? new RetryOptions());
        ctx.Provide(ServiceKey, service);
        ctx.OnWaterfall<RequestErrorEvent, RequestErrorAction?, RequestErrorAction?>("agent/request-error",
            async (payload, value, next, ct) =>
            {
                var decision = await service.DecideAsync(payload, ct).ConfigureAwait(false);
                return decision ?? await next(value).ConfigureAwait(false);
            });
        return service;
    }

    public RetryOptions Options
    {
        get => _options;
        set => _options = value;
    }

    public RetryPolicyConfig PolicyFor(string? provider)
        => provider is not null && _options.Providers.TryGetValue(provider, out var policy) ? policy : _options.Default;

    private async Task<RequestErrorAction?> DecideAsync(RequestErrorEvent payload, CancellationToken ct)
    {
        var policy = PolicyFor(payload.Agent.Options.Provider);
        if (policy.Mode != "always")
        {
            if (!LlmErrorCodes.IsRetryable(payload.Failure.Code, policy.RetryableCodes)) return null;
            if (payload.Attempts >= policy.MaxRetries) return null;
        }

        // Context-overflow recovery belongs to compaction; an over-cap provider delay
        // delegates to downstream recovery in normal mode (always mode uses local backoff).
        var providerDelay = payload.Failure.ProviderRetryAfterMs;
        if (payload.Failure.Code == LlmErrorCodes.ContextWindowExceeded) return null;
        if (providerDelay is { } cap && cap > policy.MaxDelayMs && policy.Mode != "always") return null;

        var delay = ScheduleDelay(policy, providerDelay, payload.Attempts);
        var retryId = $"retry_{++_counter}";
        var session = payload.Agent.Session;
        session.Append(SessionEventTypes.LlmRetry, new
        {
            retryId,
            provider = payload.Agent.Options.Provider,
            mode = policy.Mode,
            code = payload.Failure.Code,
            message = payload.Failure.Message,
            attempt = payload.Attempts + 1,
            delayMs = delay,
            maxRetries = policy.Mode == "always" ? (int?)null : policy.MaxRetries,
        });

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation during backoff writes no started event (dsh contract).
            throw;
        }

        session.Append(SessionEventTypes.LlmRetryStarted, new
        {
            retryId,
            turn = payload.Turn,
            step = payload.Step,
            attempt = payload.Attempts + 1,
        });
        return RequestErrorAction.Retry(backoffHandled: true);
    }

    /// <summary>Scheduled wait in milliseconds: provider Retry-After when usable, else bounded exponential with jitter.</summary>
    public static long ScheduleDelay(RetryPolicyConfig policy, long? providerRetryAfterMs, int attempts = 0)
    {
        if (providerRetryAfterMs is { } retryAfter && retryAfter <= policy.MaxDelayMs)
        {
            return Math.Max(0, retryAfter); // replaces local backoff without jitter
        }
        var baseDelay = Math.Min(policy.InitialDelayMs * Math.Pow(2, attempts), policy.MaxDelayMs);
        var jitter = baseDelay * policy.JitterRatio * 2 * (Random.Shared.NextDouble() - 0.5);
        return Math.Max(0, (long)(baseDelay + jitter));
    }
}
