using System.Text.Json;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Attachments;

public sealed record AttachmentMeta(string Id, string SessionId, string MimeType, long CreatedAt, int Size);

public sealed record AttachmentContent(byte[] Data, string MimeType, string SessionId);

/// <summary>
/// ctx.attachments — a binary store keyed by opaque id: bytes land under root/&lt;sessionId&gt;/&lt;id&gt;.bin
/// with a &lt;id&gt;.json meta beside them. Reads resolve through an in-memory index built lazily from disk.
/// </summary>
public sealed class AttachmentService
{
    public const string ServiceKey = "attachments";

    private static readonly JsonSerializerOptions MetaJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _indexGate = new();
    private Dictionary<string, AttachmentMeta>? _index;

    public AttachmentService(string? rootDir = null)
    {
        _root = rootDir ?? DefaultRootDir();
    }

    public string RootDir => _root;

    public static string DefaultRootDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly", "attachments");

    public static AttachmentService Mount(HarnessContext ctx, string? rootDir = null)
    {
        var service = new AttachmentService(rootDir);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    public Task<string> SaveAsync(string sessionId, byte[] data, string mimeType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        return WrapIo(async () =>
        {
            var id = "att_" + Guid.NewGuid().ToString("N")[..12];
            var meta = new AttachmentMeta(id, sessionId, mimeType, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data.Length);
            var dir = Path.Combine(_root, sessionId);
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, id + ".bin"), data, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(meta, MetaJson), ct).ConfigureAwait(false);
            lock (_indexGate) Index()[id] = meta;
            return id;
        }, ct);
    }

    public Task<AttachmentContent?> ReadAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return WrapIo<AttachmentContent?>(async () =>
        {
            AttachmentMeta meta;
            lock (_indexGate) meta = Index().GetValueOrDefault(id)!;
            if (meta is null) return null;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, meta.SessionId, meta.Id + ".bin"), ct).ConfigureAwait(false);
            return new AttachmentContent(bytes, meta.MimeType, meta.SessionId);
        }, ct);
    }

    public IReadOnlyList<AttachmentMeta> List(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_indexGate)
        {
            return [.. Index().Values.Where(m => m.SessionId == sessionId).OrderBy(m => m.CreatedAt)];
        }
    }

    /// <summary>id → meta, built lazily by scanning meta files; ids are unique so later writes only add.</summary>
    private Dictionary<string, AttachmentMeta> Index()
    {
        lock (_indexGate)
        {
            if (_index is not null) return _index;
            _index = new Dictionary<string, AttachmentMeta>(StringComparer.Ordinal);
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var meta = JsonSerializer.Deserialize<AttachmentMeta>(File.ReadAllText(file), MetaJson);
                        if (meta is not null && !string.IsNullOrEmpty(meta.Id)) _index[meta.Id] = meta;
                    }
                    catch (Exception ex) when (ex is IOException or JsonException)
                    {
                        // unreadable meta files are skipped; their ids read as unknown
                    }
                }
            }
            return _index;
        }
    }

    private async Task WrapIo(Func<Task> action, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _io.Release(); }
    }

    private async Task<T> WrapIo<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { _io.Release(); }
    }
}
