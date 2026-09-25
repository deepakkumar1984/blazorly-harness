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

    /// <summary>
    /// Longest provider Retry-After that is waited out verbatim. Rate-limit windows are routinely
    /// longer than MaxDelayMs (30–60s is common), and failing the turn instead of honoring the ask
    /// loses work the provider already told us how to recover. An ask beyond this cap is
    /// unschedulable: normal mode declines and falls through, always mode uses local backoff.
    /// </summary>
    public long MaxRetryAfterMs { get; init; } = 120_000;

    /// <summary>
    /// Local-backoff window for RATE_LIMIT failures with no usable provider Retry-After. Most
    /// providers reset quotas on a rolling minute, so a sub-second first retry just burns attempts
    /// against the same window; the floor spaces attempts out and the ceiling keeps any single wait
    /// bounded (default 5s → 30s, so MaxRetries 5 waits ~95s in total). Both apply only to
    /// RATE_LIMIT; other codes keep InitialDelayMs/MaxDelayMs.
    /// </summary>
    public long RateLimitMinDelayMs { get; init; } = 5_000;
    public long RateLimitMaxDelayMs { get; init; } = 30_000;

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
/// symmetric jitter; a provider Retry-After at or below MaxRetryAfterMs replaces local backoff
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
        var adaptable = TryReduceMaxTokens(payload.Failure, payload.Agent.Options.MaxTokens, out var reduced);
        if (policy.Mode != "always")
        {
            if (payload.Attempts >= policy.MaxRetries) return null;
            // Adaptation is its own retryable case: an invalid-request failure with a
            // reducible cap retries below; anything else still needs a retryable code.
            if (!adaptable && !LlmErrorCodes.IsRetryable(payload.Failure.Code, policy.RetryableCodes)) return null;
        }

        // Context-overflow recovery belongs to compaction; a provider ask longer than we are
        // willing to sleep delegates to downstream recovery in normal mode (always mode uses
        // local backoff instead of stalling on an unbounded wait).
        var providerDelay = payload.Failure.ProviderRetryAfterMs;
        if (payload.Failure.Code == LlmErrorCodes.ContextWindowExceeded) return null;
        if (providerDelay is { } ask && ask > RetryAfterCap(policy) && policy.Mode != "always") return null;

        // A 400 that names no other culprit is usually an oversized max_tokens — the model's
        // true output ceiling is unknown or lower than configured (undiscovered routes have no
        // metadata to clamp against). Halve the agent's cap and retry immediately instead of
        // failing the turn; the reduced value sticks as a proven-accepted ceiling.
        if (adaptable)
        {
            var from = payload.Agent.Options.MaxTokens!.Value;
            payload.Agent.Options = payload.Agent.Options with { MaxTokens = reduced };
            var adaptId = $"retry_{++_counter}";
            AppendRetryScheduled(payload, policy, adaptId, delayMs: 0, retryAfterMs: null, maxTokensFrom: from, maxTokensTo: reduced);
            AppendRetryStarted(payload, adaptId);
            return RequestErrorAction.Retry(backoffHandled: true);
        }

        var delay = ScheduleDelay(policy, providerDelay, payload.Attempts, payload.Failure.Code);
        var retryId = $"retry_{++_counter}";
        AppendRetryScheduled(payload, policy, retryId, delay, delay == providerDelay ? providerDelay : null);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation during backoff writes no started event (dsh contract).
            throw;
        }

        AppendRetryStarted(payload, retryId);
        return RequestErrorAction.Retry(backoffHandled: true);
    }

    private static void AppendRetryScheduled(RequestErrorEvent payload, RetryPolicyConfig policy, string retryId,
        long delayMs, long? retryAfterMs, int? maxTokensFrom = null, int? maxTokensTo = null)
    {
        payload.Agent.Session.Append(SessionEventTypes.LlmRetry, new
        {
            retryId,
            provider = payload.Agent.Options.Provider,
            mode = policy.Mode,
            code = payload.Failure.Code,
            message = payload.Failure.Message,
            attempt = payload.Attempts + 1,
            delayMs,
            retryAfterMs,
            maxRetries = policy.Mode == "always" ? (int?)null : policy.MaxRetries,
            maxTokensFrom,
            maxTokensTo,
        });
    }

    private static void AppendRetryStarted(RequestErrorEvent payload, string retryId)
    {
        payload.Agent.Session.Append(SessionEventTypes.LlmRetryStarted, new
        {
            retryId,
            turn = payload.Turn,
            step = payload.Step,
            attempt = payload.Attempts + 1,
        });
    }

    /// <summary>Floor for adaptive max_tokens reduction: a route rejecting this little has a different problem.</summary>
    private const int AdaptiveMaxTokensFloor = 1024;

    /// <summary>
    /// Whether a failed request is worth retrying with a smaller max_tokens: an invalid-request
    /// failure with a reducible cap, unless the provider's message clearly blames another knob
    /// (reasoning effort) or an unknown model — those fail identically at any cap and must
    /// surface with their terminal hint instead of burning the attempt budget.
    /// </summary>
    public static bool TryReduceMaxTokens(LlmFailure failure, int? maxTokens, out int reduced)
    {
        reduced = 0;
        if (failure.Code != LlmErrorCodes.InvalidRequest) return false;
        if (maxTokens is null or <= AdaptiveMaxTokensFloor) return false;
        if (OpenAiProtocol.RequiresResponsesApi(failure.Message)) return false;
        // An unsupported field fails at every value; the adapter handles wire-name negotiation.
        if (TokenLimitErrors.UnsupportedParameter(failure.Message) is not null) return false;
        var lower = failure.Message.ToLowerInvariant();
        var namesMaxTokens = TokenLimitErrors.NamedParameter(failure.Message) is not null;
        if (!namesMaxTokens)
        {
            if (lower.Contains("reasoning") || lower.Contains("thinking") || lower.Contains("effort")) return false;
            // A rejected payload fails identically at any cap: bad tool-call arguments, an
            // unknown tool name, or (below) an unknown model. Stored arguments are already
            // coerced to {} on the wire, so a repeat needs eyes, not retries.
            if (lower.Contains("argument") || lower.Contains("tool_call") || lower.Contains("tool call")) return false;
            if (lower.Contains("unknown tool") || lower.Contains("invalid tool") || lower.Contains("no such tool")) return false;
            if (lower.Contains("model")
                && (lower.Contains("not found") || lower.Contains("unknown") || lower.Contains("does not exist")
                    || lower.Contains("no such") || lower.Contains("invalid model") || lower.Contains("not available")
                    || lower.Contains("does not support") || lower.Contains("not supported"))) return false;
        }
        reduced = Math.Max(AdaptiveMaxTokensFloor, maxTokens.Value / 2);
        return reduced < maxTokens.Value;
    }

    /// <summary>Longest provider Retry-After waited out verbatim; never below MaxDelayMs.</summary>
    public static long RetryAfterCap(RetryPolicyConfig policy) => Math.Max(policy.MaxDelayMs, policy.MaxRetryAfterMs);

    /// <summary>
    /// Scheduled wait in milliseconds: the provider's Retry-After when schedulable, else bounded
    /// exponential with symmetric jitter — inside the rate-limit window when <paramref name="code"/>
    /// is RATE_LIMIT, so attempts are spaced to outlast a rolling quota reset.
    /// </summary>
    public static long ScheduleDelay(RetryPolicyConfig policy, long? providerRetryAfterMs, int attempts = 0, string? code = null)
    {
        if (providerRetryAfterMs is { } retryAfter && retryAfter <= RetryAfterCap(policy))
        {
            return Math.Max(0, retryAfter); // replaces local backoff without jitter
        }
        var rateLimited = string.Equals(code, LlmErrorCodes.RateLimit, StringComparison.Ordinal);
        var floor = rateLimited ? Math.Max(0, policy.RateLimitMinDelayMs) : 0;
        var ceiling = Math.Max(rateLimited ? Math.Max(policy.MaxDelayMs, policy.RateLimitMaxDelayMs) : policy.MaxDelayMs, floor);
        var initial = Math.Max(policy.InitialDelayMs, floor);
        var baseDelay = Math.Clamp(initial * Math.Pow(2, attempts), floor, ceiling);
        var jitter = baseDelay * policy.JitterRatio * 2 * (Random.Shared.NextDouble() - 0.5);
        return Math.Max(0, (long)(baseDelay + jitter));
    }
}
