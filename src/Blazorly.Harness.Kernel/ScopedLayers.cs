namespace Blazorly.Harness.Kernel;

/// <summary>
/// Insertion-ordered named entries with idempotent undo; duplicate names throw.
/// </summary>
public sealed class NamedEntries<T>
{
    private readonly List<(string Name, T Value)> _items = new();

    public IDisposable Add(string name, T value)
    {
        if (_items.Any(i => i.Name == name))
            throw new HarnessException("DUPLICATE_ENTRY", $"'{name}' is already registered in this layer");
        _items.Add((name, value));
        return new ActionDisposable(() => _items.RemoveAll(i => i.Name == name));
    }

    public IReadOnlyList<(string Name, T Value)> Items => _items;
}

/// <summary>
/// Two-level registration store: an eager global layer plus lazy per-scope layers.
/// Resolution overlays the scope's ancestor chain (nearest shadows farther); reads never
/// create layers, and an emptied scope layer is reclaimed.
/// </summary>
public sealed class ScopedLayers<TLayer> where TLayer : class, new()
{
    private readonly object _gate = new();
    private readonly TLayer _global = new();
    private readonly Dictionary<object, TLayer> _scoped = new();

    public TLayer Global => _global;

    public TLayer ForCreate(object scopeKey)
    {
        lock (_gate)
        {
            return _scoped.TryGetValue(scopeKey, out var layer) ? layer : _scoped[scopeKey] = new TLayer();
        }
    }

    public TLayer? Peek(object scopeKey)
    {
        lock (_gate) return _scoped.GetValueOrDefault(scopeKey);
    }

    public IReadOnlyCollection<object> ScopedKeys
    {
        get { lock (_gate) return [.. _scoped.Keys]; }
    }

    /// <summary>Chains from the given scope to its farthest ancestor, nearest first. Scope keys resolve via the provided parent lookup.</summary>
    public IEnumerable<TLayer> ChainLayers(object scopeKey, Func<object, object?> parentOf)
    {
        var walk = scopeKey;
        var hops = 0;
        while (walk is not null)
        {
            var layer = Peek(walk);
            if (layer is not null) yield return layer;
            walk = parentOf(walk);
            if (++hops > 10_000) yield break;
        }
    }

    public void ReclaimIfEmpty(object scopeKey, Func<TLayer, bool> isEmpty)
    {
        lock (_gate)
        {
            if (_scoped.TryGetValue(scopeKey, out var layer) && isEmpty(layer))
                _scoped.Remove(scopeKey);
        }
    }
}
