using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Persistence;

/// <summary>
/// JSONL persistence: one directory per session under root/&lt;projectKey(cwd)&gt;/&lt;id&gt;/session.jsonl.
/// Line 1 is the header record; every following line is one session event. A torn final line
/// (no trailing newline) is discarded; append-only, never rewritten.
/// </summary>
public sealed class JsonlSessionPersistence : ISessionPersistence
{
    private static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SurfaceOpJsonConverter() },
    };

    private readonly string _root;
    private readonly SemaphoreSlim _io = new(1, 1);

    public JsonlSessionPersistence(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    private static string ProjectKey(SessionHeader header)
        => header.Cwd is { Length: > 0 } ? EncodeSegment(Path.GetFileName(header.Cwd.TrimEnd('/'))) : "_no-cwd";

    private static string EncodeSegment(string value) => Uri.EscapeDataString(value);

    private string DirectoryFor(SessionHeader header) => Path.Combine(_root, ProjectKey(header), EncodeSegment(header.Id));

    private string FileFor(SessionHeader header) => Path.Combine(DirectoryFor(header), "session.jsonl");

    public Task CreateAsync(SessionHeader header, CancellationToken ct = default)
    {
        return WrapIo(async () =>
        {
            var dir = DirectoryFor(header);
            var file = FileFor(header);
            if (File.Exists(file)) throw new HarnessException("SESSION_ALREADY_EXISTS", $"session file already exists: {file}");
            Directory.CreateDirectory(dir);
            var headerRecord = JsonSerializer.Serialize(new StorageHeader(header), EventJson);
            await File.WriteAllTextAsync(file, headerRecord + "\n", Encoding.UTF8, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task AppendAsync(string sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return Task.CompletedTask;
        return WrapIo(async () =>
        {
            var path = FindFile(sessionId) ?? throw new HarnessException("SESSION_NOT_FOUND", $"no persisted session '{sessionId}'");
            var builder = new StringBuilder();
            foreach (var e in events)
            {
                builder.AppendLine(JsonSerializer.Serialize(new StorageEvent(e), EventJson));
            }
            await File.AppendAllTextAsync(path, builder.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<(SessionHeader Header, IReadOnlyList<SessionEvent> Events)> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        return WrapIo<(SessionHeader, IReadOnlyList<SessionEvent>)>(async () =>
        {
            var path = FindFile(sessionId) ?? throw new HarnessException("SESSION_NOT_FOUND", $"no persisted session '{sessionId}'");
            var committed = await ReadCommittedLines(path, ct).ConfigureAwait(false);
            if (committed.Count == 0) throw new HarnessException("CORRUPT_SESSION", "session file has no header record");
            var header = JsonSerializer.Deserialize<StorageHeader>(committed[0], EventJson)?.ToHeader()
                ?? throw new HarnessException("CORRUPT_SESSION", "unreadable header record");
            if (header.Version != SessionHeader.FormatVersion)
                throw new HarnessException("SESSION_FORMAT_UNSUPPORTED", $"session format version {header.Version} is not supported");
            var events = new List<SessionEvent>(committed.Count - 1);
            for (var i = 1; i < committed.Count; i++)
            {
                var storageEvent = JsonSerializer.Deserialize<StorageEvent>(committed[i], EventJson)
                    ?? throw new HarnessException("CORRUPT_SESSION", $"unreadable event record at line {i + 1}");
                events.Add(storageEvent.ToEvent());
            }
            return (header, events);
        }, ct);
    }

    /// <summary>All committed lines; a torn final line (file not ending in newline) is discarded.</summary>
    private static async Task<List<string>> ReadCommittedLines(string path, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
        }
        var endsWithNewline = await FileEndsWithNewlineAsync(path).ConfigureAwait(false);
        if (!endsWithNewline && lines.Count > 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    private static async Task<bool> FileEndsWithNewlineAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer.AsMemory(0, 1)).ConfigureAwait(false);
        return read == 1 && buffer[0] == '\n';
    }

    public Task<IReadOnlyList<SessionHeader>> ListAsync(CancellationToken ct = default)
    {
        return WrapIo<IReadOnlyList<SessionHeader>>(async () =>
        {
            var headers = new List<SessionHeader>();
            if (!Directory.Exists(_root)) return headers;
            foreach (var projectDir in Directory.EnumerateDirectories(_root))
            {
                foreach (var sessionDir in Directory.EnumerateDirectories(projectDir))
                {
                    var file = Path.Combine(sessionDir, "session.jsonl");
                    if (!File.Exists(file)) continue;
                    try
                    {
                        await using var stream = File.OpenRead(file);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var first = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (first is null) continue;
                        if (JsonSerializer.Deserialize<StorageHeader>(first, EventJson) is { } record)
                        {
                            headers.Add(record.ToHeader());
                        }
                    }
                    catch (Exception ex) when (ex is IOException or JsonException)
                    {
                        // unreadable sessions are skipped in listings
                    }
                }
            }
            headers.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            return (IReadOnlyList<SessionHeader>)headers;
        }, ct);
    }

    public async Task FlushAsync(string sessionId, CancellationToken ct = default)
    {
        // Appends are synchronous file writes; nothing to flush beyond the OS.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task FlushAllAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        return WrapIo(() =>
        {
            var path = FindFile(sessionId);
            if (path is not null)
            {
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            return Task.CompletedTask;
        }, ct);
    }

    private string? FindFile(string sessionId)
    {
        foreach (var projectDir in Directory.EnumerateDirectories(_root))
        {
            var candidate = Path.Combine(projectDir, EncodeSegment(sessionId), "session.jsonl");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
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

    private sealed record StorageHeader
    {
        public string Type { get; init; } = "session";
        public int Version { get; init; }
        public string Id { get; init; } = "";
        public long CreatedAt { get; init; }
        public string? Cwd { get; init; }
        public string? ParentSession { get; init; }
        public int SeedLength { get; init; }
        public int DelegationDepth { get; init; }
        public string? AgentPreset { get; init; }

        public StorageHeader() { }

        public StorageHeader(SessionHeader header)
        {
            Version = header.Version;
            Id = header.Id;
            CreatedAt = header.CreatedAt;
            Cwd = header.Cwd;
            ParentSession = header.ParentSession;
            SeedLength = header.SeedLength;
            DelegationDepth = header.DelegationDepth;
            AgentPreset = header.AgentPreset;
        }

        public SessionHeader ToHeader() => new()
        {
            Version = Version,
            Id = Id,
            CreatedAt = CreatedAt,
            Cwd = Cwd,
            ParentSession = ParentSession,
            SeedLength = SeedLength,
            DelegationDepth = DelegationDepth,
            AgentPreset = AgentPreset,
        };
    }

    private sealed record StorageEvent
    {
        public string Type { get; init; } = "";
        public int Seq { get; init; }
        public long Time { get; init; }
        public JsonElement Data { get; init; }
        public bool? Ignorable { get; init; }
        public int[]? SourceEventSeqs { get; init; }
        public SurfaceOp? SurfaceOp { get; init; }

        public StorageEvent() { }

        public StorageEvent(SessionEvent e)
        {
            Type = e.Type;
            Seq = e.Seq;
            Time = e.Time;
            Data = e.Data;
            Ignorable = e.Ignorable;
            SourceEventSeqs = e.SourceEventSeqs;
            SurfaceOp = e.SurfaceOp;
        }

        public SessionEvent ToEvent() => new()
        {
            Type = Type,
            Seq = Seq,
            Time = Time,
            Data = Data,
            Ignorable = Ignorable,
            SourceEventSeqs = SourceEventSeqs,
            SurfaceOp = SurfaceOp,
        };
    }
}
