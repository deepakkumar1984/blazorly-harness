namespace Blazorly.Harness.Kernel;

/// <summary>
/// A Cordis-like context: a tree of service registries plus an event bus and reversible
/// effects. Child contexts (scopes) see services provided on ancestors; effects registered
/// on a context unwind (LIFO) when that context is disposed.
/// </summary>
public sealed class HarnessContext : IAsyncDisposable
{
    private readonly HarnessContext? _parent;
    private readonly Dictionary<string, object> _services = new();
    private readonly List<(string Key, object Service)> _ownedServices = new();
    private readonly List<EffectEntry> _effects = new();
    private readonly Dictionary<object, object?> _scopeParents;
    private readonly List<HarnessContext> _children = new();
    private readonly object _gate = new();
    private bool _disposed;

    private sealed class EffectEntry(Func<ValueTask> dispose)
    {
        public readonly Func<ValueTask> Dispose = dispose;
        public bool Removed;
    }

    private HarnessContext(HarnessContext? parent, object? scopeKey, Dictionary<object, object?> scopeParents, EventBus events)
    {
        _parent = parent;
        _scopeParents = scopeParents;
        ScopeKey = scopeKey;
        Events = events;
    }

    /// <summary>The root context. Disposing it unwinds every effect and child scope in the tree.</summary>
    public static HarnessContext CreateRoot()
    {
        var scopeParents = new Dictionary<object, object?>();
        return new HarnessContext(null, null, scopeParents, new EventBus(scopeParents));
    }

    public HarnessContext? Parent => _parent;

    /// <summary>Scope tag of this context (null for the root and plain extensions).</summary>
    public object? ScopeKey { get; }

    /// <summary>The shared event bus for the whole tree.</summary>
    public EventBus Events { get; }

    public bool IsDisposed => _disposed;

    /// <summary>Registers a service at a stable key on this context; removed on dispose.</summary>
    public void Provide<T>(string key, T service) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_services.ContainsKey(key))
                throw new HarnessException("DUPLICATE_SERVICE", $"service '{key}' is already provided on this context");
            _services[key] = service;
            _ownedServices.Add((key, service));
        }
    }

    public T Get<T>(string key) where T : class
    {
        return TryGet<T>(key) ?? throw new HarnessException("NO_SERVICE", $"service '{key}' is not available");
    }

    public T? TryGet<T>(string key) where T : class
    {
        var ctx = this;
        while (ctx is not null)
        {
            T? found;
            lock (ctx._gate)
            {
                found = ctx._services.TryGetValue(key, out var service)
                    ? service as T ?? throw new HarnessException("SERVICE_TYPE_MISMATCH", $"service '{key}' is not a {typeof(T).Name}")
                    : null;
            }
            if (found is not null) return found;
            ctx = ctx._parent;
        }
        return null;
    }

    /// <summary>Registers a reversible effect on this context; it unwinds on dispose or when its disposer runs.</summary>
    public IDisposable Effect(Action dispose) => Effect(() => { dispose(); return ValueTask.CompletedTask; });

    public IDisposable Effect(Func<ValueTask> dispose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = new EffectEntry(dispose);
        lock (_gate) _effects.Add(entry);
        return new ActionDisposable(() =>
        {
            lock (_gate) entry.Removed = true;
            dispose().GetAwaiter().GetResult();
        });
    }

    /// <summary>Registers a listener that unwinds with this context. Scope-tagged when this context carries a scope key.</summary>
    public IDisposable On<TPayload>(string name, EventBus.Listener<TPayload> listener, bool prepend = false)
    {
        var disposer = Events.On(name, listener, ScopeKey, prepend);
        return Own(disposer);
    }

    public IDisposable OnWaterfall<TPayload, TValue, TResult>(string name, EventBus.Waterfall<TPayload, TValue, TResult> middleware, bool prepend = false)
    {
        var disposer = Events.OnWaterfall(name, middleware, ScopeKey, prepend);
        return Own(disposer);
    }

    /// <summary>Parent of a scope key in the tree (null at the root of a chain).</summary>
    public object? ScopeParentOf(object key)
    {
        return _scopeParents.TryGetValue(key, out var parent) ? parent : null;
    }

    private IDisposable Own(IDisposable disposer)
    {
        var effect = Effect(disposer.Dispose);
        // When the disposer itself runs first, the owning effect becomes inert.
        return new ActionDisposable(() =>
        {
            effect.Dispose();
            disposer.Dispose();
        });
    }

    /// <summary>Creates a plain child context (no new scope tag) sharing this context's services.</summary>
    public HarnessContext Extend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var child = new HarnessContext(this, ScopeKey, _scopeParents, Events);
        lock (_gate) _children.Add(child);
        return child;
    }

    /// <summary>
    /// Creates a scoped child context: registrations through it are scope-visible and
    /// scope-lifetime. The scope key is identity-compared; the parent key links event
    /// admission up the chain.
    /// </summary>
    public Scope CreateScope(object key, object? parentKey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (parentKey is not null && !ReferenceEquals(key, parentKey))
        {
            CheckCycle(key, parentKey);
            _scopeParents[key] = parentKey;
        }
        var child = new HarnessContext(this, key, _scopeParents, Events);
        lock (_gate) _children.Add(child);
        return new Scope(key, child, async () =>
        {
            await child.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _children.Remove(child);
                _scopeParents.Remove(key);
            }
        });
    }

    private void CheckCycle(object key, object? parentKey)
    {
        var walk = parentKey;
        var hops = 0;
        while (walk is not null)
        {
            if (ReferenceEquals(walk, key))
                throw new HarnessException("SCOPE_CYCLE", "scope parent binding would create a cycle");
            walk = _scopeParents.TryGetValue(walk, out var next) ? next : null;
            if (++hops > 10_000) throw new HarnessException("SCOPE_CYCLE", "scope chain too deep");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        List<EffectEntry> unwind;
        lock (_gate)
        {
            unwind = _effects.Where(e => !e.Removed).ToList();
            _effects.Clear();
        }
        foreach (var effect in unwind.AsEnumerable().Reverse())
        {
            try { await effect.Dispose().ConfigureAwait(false); }
            catch { /* disposal continues */ }
        }
        lock (_gate)
        {
            foreach (var (key, _) in _ownedServices.AsEnumerable().Reverse()) _services.Remove(key);
            _ownedServices.Clear();
        }
    }
}

/// <summary>A scope: an identity key plus the child context it owns. Disposing unwinds its registrations.</summary>
public sealed class Scope(object key, HarnessContext ctx, Func<ValueTask> dispose) : IDisposable
{
    public object Key { get; } = key;
    public HarnessContext Ctx { get; } = ctx;
    private Func<ValueTask>? _dispose = dispose;

    public async ValueTask DisposeAsync()
    {
        var d = Interlocked.Exchange(ref _dispose, null);
        if (d is not null) await d().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
}

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
