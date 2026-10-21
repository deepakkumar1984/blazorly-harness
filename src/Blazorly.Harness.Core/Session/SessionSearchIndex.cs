using Microsoft.Data.Sqlite;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Sessions;

/// <summary>One FTS hit: where the phrase matched.</summary>
public sealed record IndexHit(string SessionId, int Seq, string Type, string Text);

/// <summary>
/// ctx.search-index — a SQLite FTS5 index over session event text (user/assistant messages
/// plus tool call names), shared by session_search so heavy sessions are never full-scanned.
/// The index is a projection of the log, not truth: rows are keyed (sessionId, seq), synced
/// incrementally from live lists (real-time bus inserts cover the tail), and self-heal by
/// re-sync when a session's log outgrows the indexed prefix. Absent in unit-test compositions,
/// where callers fall back to direct scans.
/// </summary>
public sealed class SessionSearchIndex : IDisposable
{
    public const string ServiceKey = "search-index";

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SessionSearchIndex(string path)
    {
        SQLitePCL.Batteries.Init();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var schema = _connection.CreateCommand();
        schema.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS events_fts USING fts5(sessionId UNINDEXED, seq UNINDEXED, type UNINDEXED, text);
            CREATE TABLE IF NOT EXISTS sync(sessionId TEXT PRIMARY KEY, maxSeq INTEGER NOT NULL DEFAULT -1, title TEXT);
            """;
        schema.ExecuteNonQuery();
    }

    public static SessionSearchIndex Mount(HarnessContext ctx, SessionStore store, string path)
    {
        var index = new SessionSearchIndex(path);
        ctx.Provide(ServiceKey, index);
        ctx.Effect(index.Dispose);
        ctx.Events.On<SessionEventNotification>("session/event", async (payload, ct) =>
        {
            try { await index.InsertEventAsync(payload.Session.Id, payload.Event, ct).ConfigureAwait(false); }
            catch
            {
                // the index must never break the event bus
            }
        });
        return index;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        _connection.Dispose();
    }

    /// <summary>Index any rows in events beyond the stored prefix; returns the session title.</summary>
    public async Task<string?> SyncSessionAsync(string sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var maxSeq = MaxSeqLocked(sessionId);
            using var tx = _connection.BeginTransaction();
            string? title = TitleLocked(sessionId);
            string? firstUser = null;
            foreach (var e in events)
            {
                if (e.Seq <= maxSeq) continue;
                var text = EventText(e);
                if (text is not null)
                {
                    using var insert = _connection.CreateCommand();
                    insert.Transaction = (SqliteTransaction)tx;
                    insert.CommandText = "INSERT INTO events_fts(sessionId, seq, type, text) VALUES (@s, @q, @t, @x)";
                    insert.Parameters.AddWithValue("@s", sessionId);
                    insert.Parameters.AddWithValue("@q", e.Seq);
                    insert.Parameters.AddWithValue("@t", e.Type);
                    insert.Parameters.AddWithValue("@x", text);
                    insert.ExecuteNonQuery();
                }
                if (e.Seq > maxSeq) maxSeq = e.Seq;
                if (title is null && e.Type == SessionEventTypes.UserMessage && firstUser is null)
                {
                    var prose = SessionEventRead.MessageOf(e).FlattenText();
                    if (prose.Length > 0) firstUser = prose.Length > 80 ? prose[..80] : prose;
                    title ??= firstUser;
                }
                if (e.Type == SessionEventTypes.SessionTitle)
                    title = TitleFrom(e) ?? title;
            }
            using var upsert = _connection.CreateCommand();
            upsert.Transaction = (SqliteTransaction)tx;
            upsert.CommandText = "INSERT INTO sync(sessionId, maxSeq, title) VALUES (@s, @m, @t) "
                + "ON CONFLICT(sessionId) DO UPDATE SET maxSeq = excluded.maxSeq, title = COALESCE(excluded.title, sync.title)";
            upsert.Parameters.AddWithValue("@s", sessionId);
            upsert.Parameters.AddWithValue("@m", maxSeq);
            upsert.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
            upsert.ExecuteNonQuery();
            tx.Commit();
            return title;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Phrase search (case-insensitive); sessionId null searches every indexed session.</summary>
    public async Task<IReadOnlyList<IndexHit>> SearchAsync(
        string phrase, string? sessionId = null, int limit = 20, CancellationToken ct = default)
    {
        var query = phrase.Trim();
        if (query.Length == 0) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sessionId is null
                ? "SELECT sessionId, seq, type, text FROM events_fts WHERE text MATCH @q LIMIT @n"
                : "SELECT sessionId, seq, type, text FROM events_fts WHERE text MATCH @q AND sessionId = @s LIMIT @n";
            cmd.Parameters.AddWithValue("@q", "\"" + query.Replace("\"", "\"\"") + "\"");
            cmd.Parameters.AddWithValue("@n", Math.Max(1, limit));
            if (sessionId is not null) cmd.Parameters.AddWithValue("@s", sessionId);
            var hits = new List<IndexHit>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                hits.Add(new IndexHit(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
            return hits;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drop a deleted session's rows.</summary>
    public async Task PruneSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            using var del = _connection.CreateCommand();
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM events_fts WHERE sessionId = @s";
            del.Parameters.AddWithValue("@s", sessionId);
            del.ExecuteNonQuery();
            using var sync = _connection.CreateCommand();
            sync.Transaction = (SqliteTransaction)tx;
            sync.CommandText = "DELETE FROM sync WHERE sessionId = @s";
            sync.Parameters.AddWithValue("@s", sessionId);
            sync.ExecuteNonQuery();
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InsertEventAsync(string sessionId, SessionEvent e, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var maxSeq = MaxSeqLocked(sessionId);
            if (e.Seq <= maxSeq) return;
            var text = EventText(e);
            if (text is not null)
            {
                using var insert = _connection.CreateCommand();
                insert.CommandText = "INSERT INTO events_fts(sessionId, seq, type, text) VALUES (@s, @q, @t, @x)";
                insert.Parameters.AddWithValue("@s", sessionId);
                insert.Parameters.AddWithValue("@q", e.Seq);
                insert.Parameters.AddWithValue("@t", e.Type);
                insert.Parameters.AddWithValue("@x", text);
                insert.ExecuteNonQuery();
            }
            using var upsert = _connection.CreateCommand();
            upsert.CommandText = "INSERT INTO sync(sessionId, maxSeq) VALUES (@s, @m) "
                + "ON CONFLICT(sessionId) DO UPDATE SET maxSeq = MAX(sync.maxSeq, excluded.maxSeq)";
            upsert.Parameters.AddWithValue("@s", sessionId);
            upsert.Parameters.AddWithValue("@m", e.Seq);
            upsert.ExecuteNonQuery();
            if (e.Type == SessionEventTypes.SessionTitle)
            {
                using var title = _connection.CreateCommand();
                title.CommandText = "UPDATE sync SET title = @t WHERE sessionId = @s";
                title.Parameters.AddWithValue("@t", SessionEventRead.TitleOf(e));
                title.Parameters.AddWithValue("@s", sessionId);
                title.ExecuteNonQuery();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private int MaxSeqLocked(string sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT maxSeq FROM sync WHERE sessionId = @s";
        cmd.Parameters.AddWithValue("@s", sessionId);
        return cmd.ExecuteScalar() is long max ? (int)max : -1;
    }

    private string? TitleLocked(string sessionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT title FROM sync WHERE sessionId = @s";
        cmd.Parameters.AddWithValue("@s", sessionId);
        return cmd.ExecuteScalar() is string title ? title : null;
    }

    private static string? TitleFrom(SessionEvent e)
    {
        if (e.Type == SessionEventTypes.SessionTitle)
        {
            try { return SessionEventRead.TitleOf(e); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Indexed text per event: user/assistant prose plus tool call names.</summary>
    internal static string? EventText(SessionEvent e)
    {
        try
        {
            if (e.Type == SessionEventTypes.UserMessage) return SessionEventRead.MessageOf(e).FlattenText();
            if (e.Type == SessionEventTypes.AssistantMessage) return SessionEventRead.AssistantMessageOf(e).Message.FlattenText();
            if (e.Type == SessionEventTypes.ToolCall) return SessionEventRead.ToolCallOf(e).Name;
        }
        catch
        {
            return null;
        }
        return null;
    }
}
