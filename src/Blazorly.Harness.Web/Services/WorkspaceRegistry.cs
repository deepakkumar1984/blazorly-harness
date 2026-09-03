using System.Collections.Concurrent;
using System.Text.Json;

namespace Blazorly.Harness.Web.Services;

public sealed record Workspace(string Id, string Name, string Root, int Order)
{
    public string Key => Id;
}

/// <summary>
/// The host workspace registry: named folders the user works in, plus archive state.
/// Persisted under ~/.blazorly/workspaces.json; sessions belong to the workspace whose
/// Root matches the session header Cwd.
/// </summary>
public sealed class WorkspaceRegistry
{
    public sealed class Store
    {
        public List<Workspace> Workspaces { get; set; } = [];
        public HashSet<string> ArchivedSessions { get; set; } = [];
        public string? DefaultWorkspaceId { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private Store _store = new();

    public WorkspaceRegistry(string home)
    {
        _path = Path.Combine(home, "workspaces.json");
        Directory.CreateDirectory(home);
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_path), Json)
                         ?? new Store();
            }
        }
        catch
        {
            _store = new Store();
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_store, Json));
    }

    /// <summary>Ensures a default workspace exists for the given root; returns the registry.</summary>
    public WorkspaceRegistry EnsureDefault(string root)
    {
        lock (_gate)
        {
            if (_store.Workspaces.Count == 0)
            {
                var workspace = new Workspace("ws-default", "default", Path.GetFullPath(root), 0);
                _store.Workspaces.Add(workspace);
                _store.DefaultWorkspaceId = workspace.Id;
                Save();
            }
            return this;
        }
    }

    /// <summary>
    /// Ensures a workspace exists for the given root (canonical path is the uniqueness key),
    /// returning the existing record when present. Used by the CLI, whose invoking directory
    /// becomes the workspace on first use (dsh launcher behavior).
    /// </summary>
    public Workspace Ensure(string root, string? name = null)
    {
        var full = Path.GetFullPath(root);
        lock (_gate)
        {
            var existing = _store.Workspaces.FirstOrDefault(w => string.Equals(Path.GetFullPath(w.Root), full, StringComparison.Ordinal));
            if (existing is not null) return existing;
            var workspace = new Workspace($"ws-{Guid.NewGuid().ToString("N")[..8]}", string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name, full, _store.Workspaces.Count);
            _store.Workspaces.Add(workspace);
            Save();
            return workspace;
        }
    }

    public IReadOnlyList<Workspace> List()
    {
        lock (_gate) return [.. _store.Workspaces.OrderBy(w => w.Order).ThenBy(w => w.Name, StringComparer.Ordinal)];
    }

    public Workspace? Get(string id)
    {
        lock (_gate) return _store.Workspaces.FirstOrDefault(w => w.Id == id);
    }

    public Workspace? ForRoot(string root)
    {
        var full = Path.GetFullPath(root);
        lock (_gate) return _store.Workspaces.FirstOrDefault(w => string.Equals(Path.GetFullPath(w.Root), full, StringComparison.Ordinal));
    }

    public Workspace Add(string name, string root)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new InvalidOperationException($"folder does not exist: {full}");
        lock (_gate)
        {
            if (_store.Workspaces.Any(w => string.Equals(Path.GetFullPath(w.Root), full, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"workspace already exists for {full}");
            }
            var id = $"ws-{Guid.NewGuid().ToString("N")[..8]}";
            var workspace = new Workspace(id, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name, full, _store.Workspaces.Count);
            _store.Workspaces.Add(workspace);
            Save();
            return workspace;
        }
    }

    public void Rename(string id, string name)
    {
        lock (_gate)
        {
            var workspace = _store.Workspaces.FirstOrDefault(w => w.Id == id) ?? throw new InvalidOperationException("unknown workspace");
            _store.Workspaces.Remove(workspace);
            _store.Workspaces.Add(workspace with { Name = name });
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _store.Workspaces.RemoveAll(w => w.Id == id);
            if (_store.DefaultWorkspaceId == id) _store.DefaultWorkspaceId = _store.Workspaces.OrderBy(w => w.Order).FirstOrDefault()?.Id;
            Save();
        }
    }

    public void SetDefault(string id)
    {
        lock (_gate)
        {
            if (_store.Workspaces.All(w => w.Id != id)) throw new InvalidOperationException("unknown workspace");
            _store.DefaultWorkspaceId = id;
            Save();
        }
    }

    public Workspace Default()
    {
        lock (_gate)
        {
            return _store.Workspaces.FirstOrDefault(w => w.Id == _store.DefaultWorkspaceId)
                   ?? _store.Workspaces.OrderBy(w => w.Order).FirstOrDefault()
                   ?? throw new InvalidOperationException("no workspaces registered");
        }
    }

    public bool IsArchived(string sessionId)
    {
        lock (_gate) return _store.ArchivedSessions.Contains(sessionId);
    }

    public void Archive(string sessionId, bool archived)
    {
        lock (_gate)
        {
            if (archived) _store.ArchivedSessions.Add(sessionId);
            else _store.ArchivedSessions.Remove(sessionId);
            Save();
        }
    }
}

public sealed record DirectoryEntry(string Name, string FullPath, bool IsDirectory, bool Empty = false);

/// <summary>Server-side directory browsing for the add-workspace flow (dsh's browse picker).</summary>
public static class DirectoryBrowser
{
    private static readonly string[] Ignore = [".git", "node_modules", "bin", "obj", ".venv", "__pycache__", "dist", ".next", "target", ".cache"];

    public static IReadOnlyList<DirectoryEntry> List(string path)
    {
        var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "/" : path);
        if (!Directory.Exists(full)) throw new InvalidOperationException($"not a folder: {full}");
        var entries = new List<DirectoryEntry>();
        foreach (var dir in Directory.EnumerateDirectories(full))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || Ignore.Contains(name)) continue;
            entries.Add(new DirectoryEntry(name, dir, true, IsEmptyFolder(dir)));
        }
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return [.. entries.Take(300)];
    }

    /// <summary>True when the folder holds nothing a user would work with (hidden-only counts as empty).</summary>
    public static bool IsEmptyFolder(string path)
    {
        try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }
}
