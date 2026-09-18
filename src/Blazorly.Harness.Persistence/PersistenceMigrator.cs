using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Persistence;

public readonly record struct MigrationReport(int Imported, int Skipped, int Failed);

/// <summary>
/// One-time JSONL → SQLite migration. Idempotent: sessions already present in the target
/// (matched by id) are skipped, so it is safe to run on every boot until the source is gone.
/// </summary>
public static class PersistenceMigrator
{
    /// <summary>Creates (or opens) the SQLite store; when the JSONL root still holds sessions
    /// the target has not seen, imports them. Returns the store plus what happened.</summary>
    public static async Task<(SqliteSessionPersistence Store, MigrationReport Report)> EnsureSqliteAsync(
        string sqlitePath, string jsonlRoot, Action<string>? log = null, CancellationToken ct = default)
    {
        var store = new SqliteSessionPersistence(sqlitePath);
        if (!Directory.Exists(jsonlRoot))
            return (store, new MigrationReport(0, 0, 0));
        try
        {
            var source = new JsonlSessionPersistence(jsonlRoot);
            var report = await ImportAsync(source, store, log, ct).ConfigureAwait(false);
            return (store, report);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[migrate] jsonl import failed ({ex.Message}); sqlite store used as-is");
            return (store, new MigrationReport(0, 0, 0));
        }
    }

    public static async Task<MigrationReport> ImportAsync(
        JsonlSessionPersistence source, SqliteSessionPersistence target, Action<string>? log = null, CancellationToken ct = default)
    {
        var imported = 0;
        var skipped = 0;
        var failed = 0;
        var headers = await source.ListAsync(ct).ConfigureAwait(false);
        var existing = (await target.ListAsync(ct).ConfigureAwait(false)).Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (existing.Contains(header.Id))
            {
                skipped++;
                continue;
            }
            try
            {
                var (parsed, events) = await source.LoadAsync(header.Id, ct).ConfigureAwait(false);
                await target.CreateAsync(parsed, ct).ConfigureAwait(false);
                if (events.Count > 0) await target.AppendAsync(parsed.Id, events, ct).ConfigureAwait(false);
                imported++;
            }
            catch (HarnessException ex)
            {
                failed++;
                log?.Invoke($"[migrate] session '{header.Id}' skipped: {ex.Message}");
            }
        }
        var report = new MigrationReport(imported, skipped, failed);
        if (imported > 0 || failed > 0)
            log?.Invoke($"[migrate] jsonl→sqlite: {imported} imported, {skipped} already present, {failed} failed");
        return report;
    }
}
