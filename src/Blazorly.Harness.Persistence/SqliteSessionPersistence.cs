using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;
using Microsoft.Data.Sqlite;

namespace Blazorly.Harness.Persistence;

/// <summary>
/// SQLite persistence: one database file holding a sessions row per header and an events row
/// per event. A single connection is kept open (pooling disabled so disposal releases the file);
/// every command serializes behind a semaphore. Writes are durable at commit, so flushes no-op.
/// </summary>
public sealed class SqliteSessionPersistence : ISessionPersistence, IDisposable, IAsyncDisposable
{
    private const string Schema =
        """
        CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, version INTEGER NOT NULL, created_at INTEGER NOT NULL, cwd TEXT, parent_session TEXT, seed_length INTEGER NOT NULL DEFAULT 0, delegation_depth INTEGER NOT NULL DEFAULT 0, agent_preset TEXT, revision INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS events(session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE, seq INTEGER NOT NULL, type TEXT NOT NULL, time INTEGER NOT NULL, data TEXT NOT NULL, source_event_seqs TEXT, surface_op TEXT, ignorable INTEGER, PRIMARY KEY(session_id, seq));
        """;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _io = new(1, 1);

    public SqliteSessionPersistence(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            // FK enforcement is per-connection in SQLite; the cascade in the schema depends on it.
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            pragma.ExecuteNonQuery();
        }
        using (var schema = _connection.CreateCommand())
        {
            schema.CommandText = Schema;
            schema.ExecuteNonQuery();
        }
    }

    public Task CreateAsync(SessionHeader header, CancellationToken ct = default)
    {
        return WrapIo(async () =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions(id, version, created_at, cwd, parent_session, seed_length, delegation_depth, agent_preset)
                VALUES ($id, $version, $created_at, $cwd, $parent_session, $seed_length, $delegation_depth, $agent_preset)
                """;
            cmd.Parameters.AddWithValue("$id", header.Id);
            cmd.Parameters.AddWithValue("$version", header.Version);
            cmd.Parameters.AddWithValue("$created_at", header.CreatedAt);
            cmd.Parameters.AddWithValue("$cwd", Box(header.Cwd));
            cmd.Parameters.AddWithValue("$parent_session", Box(header.ParentSession));
            cmd.Parameters.AddWithValue("$seed_length", header.SeedLength);
            cmd.Parameters.AddWithValue("$delegation_depth", header.DelegationDepth);
            cmd.Parameters.AddWithValue("$agent_preset", Box(header.AgentPreset));
            try
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCodes.ConstraintViolation)
            {
                throw new HarnessException("SESSION_ALREADY_EXISTS", $"session '{header.Id}' already exists in the store");
            }
        }, ct);
    }

    public Task AppendAsync(string sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return Task.CompletedTask;
        return WrapIo(async () =>
        {
            var expectedSeq = await NextSeqAsync(sessionId, ct).ConfigureAwait(false);
            if (events[0].Seq != expectedSeq)
                throw new HarnessException("SESSION_SEQ_MISMATCH",
                    $"session '{sessionId}' expects next seq {expectedSeq} but the batch starts at {events[0].Seq}");

            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var e in events)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO events(session_id, seq, type, time, data, source_event_seqs, surface_op, ignorable)
                    VALUES ($session_id, $seq, $type, $time, $data, $source_event_seqs, $surface_op, $ignorable)
                    """;
                cmd.Parameters.AddWithValue("$session_id", sessionId);
                cmd.Parameters.AddWithValue("$seq", e.Seq);
                cmd.Parameters.AddWithValue("$type", e.Type);
                cmd.Parameters.AddWithValue("$time", e.Time);
                cmd.Parameters.AddWithValue("$data", e.Data.GetRawText());
                cmd.Parameters.AddWithValue("$source_event_seqs", Box(FormatSourceSeqs(e.SourceEventSeqs)));
                cmd.Parameters.AddWithValue("$surface_op", Box(FormatSurfaceOp(e.SurfaceOp)));
                cmd.Parameters.AddWithValue("$ignorable", Box(e.Ignorable));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            using (var bump = _connection.CreateCommand())
            {
                bump.Transaction = tx;
                bump.CommandText = "UPDATE sessions SET revision = revision + 1 WHERE id = $id";
                bump.Parameters.AddWithValue("$id", sessionId);
                await bump.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<(SessionHeader Header, IReadOnlyList<SessionEvent> Events)> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        return WrapIo<(SessionHeader, IReadOnlyList<SessionEvent>)>(async () =>
        {
            var header = await ReadHeaderAsync(sessionId, ct).ConfigureAwait(false);
            var events = new List<SessionEvent>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT seq, type, time, data, source_event_seqs, surface_op, ignorable FROM events WHERE session_id = $id ORDER BY seq";
            cmd.Parameters.AddWithValue("$id", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                events.Add(new SessionEvent
                {
                    Type = reader.GetString(1),
                    Seq = reader.GetInt32(0),
                    Time = reader.GetInt64(2),
                    Data = JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(),
                    SourceEventSeqs = ParseSourceSeqs(reader.IsDBNull(4) ? null : reader.GetString(4)),
                    SurfaceOp = ParseSurfaceOp(reader.IsDBNull(5) ? null : reader.GetString(5)),
                    Ignorable = reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                });
            }
            return (header, (IReadOnlyList<SessionEvent>)events);
        }, ct);
    }

    public Task<IReadOnlyList<SessionHeader>> ListAsync(CancellationToken ct = default)
    {
        return WrapIo<IReadOnlyList<SessionHeader>>(async () =>
        {
            var headers = new List<SessionHeader>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, version, created_at, cwd, parent_session, seed_length, delegation_depth, agent_preset FROM sessions ORDER BY created_at DESC";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                headers.Add(new SessionHeader
                {
                    Id = reader.GetString(0),
                    Version = reader.GetInt32(1),
                    CreatedAt = reader.GetInt64(2),
                    Cwd = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ParentSession = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SeedLength = reader.GetInt32(5),
                    DelegationDepth = reader.GetInt32(6),
                    AgentPreset = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }
            return (IReadOnlyList<SessionHeader>)headers;
        }, ct);
    }

    public Task FlushAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

    public Task FlushAllAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose()
    {
        _connection.Dispose();
        _io.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _io.Dispose();
    }

    private async Task<int> NextSeqAsync(string sessionId, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM sessions WHERE id = $id),
                   (SELECT COALESCE(MAX(seq), -1) + 1 FROM events WHERE session_id = $id)
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        if (reader.GetInt64(0) == 0)
            throw new HarnessException("SESSION_NOT_FOUND", $"no persisted session '{sessionId}'");
        return (int)reader.GetInt64(1);
    }

    private async Task<SessionHeader> ReadHeaderAsync(string sessionId, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT version, created_at, cwd, parent_session, seed_length, delegation_depth, agent_preset FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new HarnessException("SESSION_NOT_FOUND", $"no persisted session '{sessionId}'");
        var header = new SessionHeader
        {
            Id = sessionId,
            Version = reader.GetInt32(0),
            CreatedAt = reader.GetInt64(1),
            Cwd = reader.IsDBNull(2) ? null : reader.GetString(2),
            ParentSession = reader.IsDBNull(3) ? null : reader.GetString(3),
            SeedLength = reader.GetInt32(4),
            DelegationDepth = reader.GetInt32(5),
            AgentPreset = reader.IsDBNull(6) ? null : reader.GetString(6),
        };
        if (header.Version != SessionHeader.FormatVersion)
            throw new HarnessException("SESSION_FORMAT_UNSUPPORTED", $"session format version {header.Version} is not supported");
        return header;
    }

    private static string? FormatSourceSeqs(int[]? seqs) => seqs is null ? null : string.Join(',', seqs);

    private static string? FormatSurfaceOp(SurfaceOp? op) => op switch
    {
        null => null,
        SurfaceOp.Append => "append",
        SurfaceOp.Replace replace => $"replace:{replace.Start}:{replace.End}",
        _ => throw new InvalidOperationException("unknown surfaceOp"),
    };

    private static int[]? ParseSourceSeqs(string? text)
    {
        if (text is null) return null;
        if (text.Length == 0) return [];
        return text.Split(',').Select(int.Parse).ToArray();
    }

    private static SurfaceOp? ParseSurfaceOp(string? text)
    {
        if (text is null) return null;
        if (text == "append") return new SurfaceOp.Append();
        var parts = text.Split(':');
        if (parts.Length == 3 && parts[0] == "replace" && int.TryParse(parts[1], out var start) && int.TryParse(parts[2], out var end))
            return new SurfaceOp.Replace(start, end);
        throw new HarnessException("CORRUPT_SESSION", $"unreadable surfaceOp '{text}'");
    }

    private static object Box(object? value) => value ?? DBNull.Value;

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

    private static class SqliteCodes
    {
        public const int ConstraintViolation = 19; // SQLITE_CONSTRAINT
    }
}
