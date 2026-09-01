namespace Blazorly.Harness.Kernel;

/// <summary>
/// Typed event bus with dsh's four dispatch modes. Listeners may carry a scope key;
/// a dispatch with a subject key admits untagged listeners plus tagged listeners whose
/// key is the subject or one of its ancestors (events flow up the scope chain, never down).
/// </summary>
public sealed class EventBus
{
    private readonly object _gate = new();
    private readonly List<Registration> _listeners = [];
    private readonly Dictionary<object, object?> _scopeParents;

    private sealed class Registration(string name, Delegate handler, object? scopeKey)
    {
        public readonly string Name = name;
        public readonly Delegate Handler = handler;
        public readonly object? ScopeKey = scopeKey;
        public bool Removed;
    }

    internal EventBus(Dictionary<object, object?> scopeParents) => _scopeParents = scopeParents;

    /// <summary>Handler invoked for emit/serial/parallel dispatches. Exceptions in emit mode are contained.</summary>
    public delegate Task Listener<in TPayload>(TPayload payload, CancellationToken ct);

    /// <summary>
    /// Waterfall middleware: receives the incoming value and a <c>next</c> continuation.
    /// <c>next(value)</c> carries the (possibly rewritten) value to downstream listeners and
    /// returns their result; returning without calling <c>next</c> short-circuits the chain.
    /// </summary>
    public delegate Task<TResult> Waterfall<TPayload, TValue, TResult>(TPayload payload, TValue value, Func<TValue, Task<TResult>> next, CancellationToken ct);

    public Action<string, Exception>? OnListenerError { get; set; }

    public IDisposable On<TPayload>(string name, Listener<TPayload> listener, object? scopeKey = null, bool prepend = false)
    {
        var reg = new Registration(name, listener, scopeKey);
        Add(reg, prepend);
        return Disposer(reg);
    }

    public IDisposable OnWaterfall<TPayload, TValue, TResult>(string name, Waterfall<TPayload, TValue, TResult> middleware, object? scopeKey = null, bool prepend = false)
    {
        var reg = new Registration(name, middleware, scopeKey);
        Add(reg, prepend);
        return Disposer(reg);
    }

    private void Add(Registration reg, bool prepend)
    {
        lock (_gate)
        {
            if (prepend) _listeners.Insert(0, reg); else _listeners.Add(reg);
        }
    }

    private IDisposable Disposer(Registration reg) => new ActionDisposable(() =>
    {
        lock (_gate) reg.Removed = true;
    });

    private List<Registration> Snapshot(string name, object? subjectKey)
    {
        List<Registration> result;
        lock (_gate)
        {
            result = [];
            foreach (var reg in _listeners)
            {
                if (reg.Removed || reg.Name != name) continue;
                if (reg.ScopeKey is not null && !Admits(subjectKey, reg.ScopeKey)) continue;
                result.Add(reg);
            }
        }
        return result;
    }

    private bool Admits(object? subjectKey, object listenerKey)
    {
        var walk = subjectKey;
        while (walk is not null)
        {
            if (ReferenceEquals(walk, listenerKey)) return true;
            walk = _scopeParents.TryGetValue(walk, out var parent) ? parent : null;
        }
        return false;
    }

    /// <summary>Fire-and-forget notification; listener failures are contained and reported to <see cref="OnListenerError"/>.</summary>
    public async Task EmitAsync<TPayload>(string name, TPayload payload, object? subjectKey = null, CancellationToken ct = default)
    {
        foreach (var reg in Snapshot(name, subjectKey))
        {
            try
            {
                var handler = (Listener<TPayload>)reg.Handler;
                await handler(payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnListenerError?.Invoke(name, ex);
            }
        }
    }

    /// <summary>Awaited in registration order; a listener failure propagates.</summary>
    public async Task SerialAsync<TPayload>(string name, TPayload payload, object? subjectKey = null, CancellationToken ct = default)
    {
        foreach (var reg in Snapshot(name, subjectKey))
        {
            var handler = (Listener<TPayload>)reg.Handler;
            await handler(payload, ct).ConfigureAwait(false);
        }
    }

    /// <summary>All listeners run concurrently; failures are collected and the first propagates after all settle.</summary>
    public async Task ParallelAsync<TPayload>(string name, TPayload payload, object? subjectKey = null, CancellationToken ct = default)
    {
        var listeners = Snapshot(name, subjectKey);
        var errors = new List<Exception>();
        await Task.WhenAll(listeners.Select(async reg =>
        {
            try
            {
                var handler = (Listener<TPayload>)reg.Handler;
                await handler(payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        })).ConfigureAwait(false);
        if (errors.Count > 0) throw errors[0];
    }

    /// <summary>
    /// Middleware chain: first-registered listener is outermost. The initial value flows in,
    /// each listener may rewrite it for downstream via <c>next(value)</c>, the terminal computes
    /// the core result, and results flow back out through each listener.
    /// </summary>
    public Task<TResult> WaterfallAsync<TPayload, TValue, TResult>(string name, TPayload payload, TValue value, Func<TValue, Task<TResult>> terminal, object? subjectKey = null, CancellationToken ct = default)
    {
        var listeners = Snapshot(name, subjectKey);
        Func<TValue, Task<TResult>> next = terminal;
        for (var i = listeners.Count - 1; i >= 0; i--)
        {
            var middleware = (Waterfall<TPayload, TValue, TResult>)listeners[i].Handler;
            var captured = next;
            next = v => middleware(payload, v, captured, ct);
        }
        return next(value);
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
