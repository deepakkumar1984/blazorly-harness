using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Harnessing;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Persistence;

namespace Blazorly.Harness.Cli;

/// <summary>Full captured event data, independently of the clipped human transcript.</summary>
public static class TrialEvidence
{
    private static readonly JsonSerializerOptions Json = new(HarnessArtifacts.Json) { WriteIndented = false };

    internal static async Task<HeadlessUsage> WritePersistedAsync(string directory, string sessionsRoot,
        string sessionId, string response, CancellationToken ct = default)
    {
        // The persistence reader drops a torn final line left by a killed process.
        var (header, events) = await new JsonlSessionPersistence(sessionsRoot).LoadAsync(sessionId, ct).ConfigureAwait(false);
        Write(directory, header, events, response);
        var extraChildren = ChildrenByHeader(sessionsRoot, sessionId);
        return await AccountAsync(events, (id, token) => LoadChildAsync(sessionsRoot, id, token), extraChildren, ct).ConfigureAwait(false);
    }

    public static void Write(string directory, SessionHeader header, IReadOnlyList<SessionEvent> events, string response)
    {
        Directory.CreateDirectory(directory);
        var log = new StringBuilder().AppendLine(JsonSerializer.Serialize(header, Json));
        foreach (var item in events)
            log.AppendLine(JsonSerializer.Serialize(item, Json));
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), log.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "answer.md"), response, new UTF8Encoding(false));
    }

    /// <summary>
    /// Sums parent assistant usage, compaction calls, and referenced child sessions.
    /// A metered call with no usage, or a child log that cannot be loaded, is incomplete:
    /// the returned totals are a lower bound, not a bill.
    /// </summary>
    public static async Task<HeadlessUsage> AccountAsync(IReadOnlyList<SessionEvent> events,
        Func<string, CancellationToken, Task<IReadOnlyList<SessionEvent>?>>? loadChild,
        IEnumerable<string>? extraChildIds = null, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return await AccountAsync(events, loadChild, extraChildIds, seen, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Each run gets an equal share of <paramref name="budget"/>. Lines are kept whole, from the
    /// most recent event backward, so one long baseline cannot erase the other runs.
    /// </summary>
    public static string Excerpt(IReadOnlyDictionary<string, string> trajectories, int budget = 100_000)
    {
        if (trajectories.Count == 0 || budget < 1) return "";
        var share = Math.Max(1, budget / trajectories.Count);
        var text = new StringBuilder();
        foreach (var (id, path) in trajectories)
        {
            text.Append("Run ").Append(id).Append(" (").Append(path).AppendLine("):");
            text.AppendLine(ExcerptFile(path, share));
        }
        return text.ToString();
    }

    public static string ReadEvent(string path, int seq, int maxChars = 20_000)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("seq", out var value) || value.ValueKind != JsonValueKind.Number || value.GetInt32() != seq)
                continue;
            if (line.Length > maxChars)
                return $"event {seq} is {line.Length} characters and does not fit in one read; full log: {path}";
            return line;
        }
        throw new ArgumentException($"event {seq} is absent from {path}");
    }

    private static async Task<HeadlessUsage> AccountAsync(IReadOnlyList<SessionEvent> events,
        Func<string, CancellationToken, Task<IReadOnlyList<SessionEvent>?>>? loadChild,
        IEnumerable<string>? extraChildIds, HashSet<string> seen, CancellationToken ct)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        var compactions = 0;
        var retries = 0;
        var complete = true;
        var children = new HashSet<string>(extraChildIds ?? [], StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (item.Type == SessionEventTypes.AssistantMessage)
            {
                var usage = SessionEventRead.AssistantMessageOf(item).Usage;
                if (usage is null) complete = false;
                else Add(usage, ref input, ref output, ref cacheRead, ref cacheWrite);
            }
            else if (item.Type == SessionEventTypes.CompactionSummary)
            {
                compactions++;
                if (!AddCompaction(item.Data, ref input, ref output, ref cacheRead, ref cacheWrite)) complete = false;
            }
            else if (item.Type == SessionEventTypes.LlmRetry)
            {
                retries++;
            }
            CollectChildIds(item.Data, children);
        }
        foreach (var id in children)
        {
            if (!seen.Add(id)) continue;
            if (loadChild is null)
            {
                complete = false;
                continue;
            }
            var child = await loadChild(id, ct).ConfigureAwait(false);
            if (child is null)
            {
                complete = false;
                continue;
            }
            var nested = await AccountAsync(child, loadChild, null, seen, ct).ConfigureAwait(false);
            input += nested.Input;
            output += nested.Output;
            cacheRead += nested.CacheRead;
            cacheWrite += nested.CacheWrite;
            compactions += nested.Compactions;
            retries += nested.Retries;
            complete &= nested.Complete;
        }
        return new HeadlessUsage(input, output, cacheRead, cacheWrite, compactions, retries, complete);
    }

    private static async Task<IReadOnlyList<SessionEvent>?> LoadChildAsync(string sessionsRoot, string sessionId, CancellationToken ct)
    {
        try
        {
            var loaded = await new JsonlSessionPersistence(sessionsRoot).LoadAsync(sessionId, ct).ConfigureAwait(false);
            return loaded.Events;
        }
        catch (Exception ex) when (ex is IOException or HarnessException or JsonException)
        {
            return null;
        }
    }

    internal static List<string> ChildrenByHeader(string sessionsRoot, string parentId)
    {
        var ids = new List<string>();
        if (!Directory.Exists(sessionsRoot)) return ids;
        foreach (var log in Directory.EnumerateFiles(sessionsRoot, "session.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                using var reader = new StreamReader(log);
                var first = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(first)) continue;
                using var document = JsonDocument.Parse(first);
                var root = document.RootElement;
                var parent = root.TryGetProperty("parentSession", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var id = root.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
                if (parent == parentId && !string.IsNullOrEmpty(id)) ids.Add(id!);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { /* a torn sibling log is an incomplete child, found via the parent event */ }
        }
        return ids;
    }

    private static void Add(TokenUsage usage, ref long input, ref long output, ref long cacheRead, ref long cacheWrite)
    {
        input += usage.InputTokens;
        output += usage.OutputTokens;
        cacheRead += usage.CacheReadTokens ?? 0;
        cacheWrite += usage.CacheWriteTokens ?? 0;
    }

    private static bool AddCompaction(JsonElement data, ref long input, ref long output, ref long cacheRead, ref long cacheWrite)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;
        if (!usage.TryGetProperty("inputTokens", out var inputValue) || !usage.TryGetProperty("outputTokens", out var outputValue))
            return false;
        input += inputValue.GetInt64();
        output += outputValue.GetInt64();
        if (usage.TryGetProperty("cacheReadTokens", out var read) && read.ValueKind == JsonValueKind.Number) cacheRead += read.GetInt64();
        if (usage.TryGetProperty("cacheWriteTokens", out var write) && write.ValueKind == JsonValueKind.Number) cacheWrite += write.GetInt64();
        return true;
    }

    private static void CollectChildIds(JsonElement data, HashSet<string> ids)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        if (data.TryGetProperty("childSessionId", out var child) && child.ValueKind == JsonValueKind.String && child.GetString() is { Length: > 0 } childId)
            ids.Add(childId);
        if (data.TryGetProperty("member", out var member) && member.ValueKind == JsonValueKind.Object
            && member.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String && sid.GetString() is { Length: > 0 } memberId)
            ids.Add(memberId);
    }

    private static string ExcerptFile(string path, int share)
    {
        var lines = File.ReadAllLines(path);
        var taken = new List<string>();
        var used = 0;
        var omitted = 0;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var cost = lines[i].Length + 1;
            if (cost > share && taken.Count == 0)
            {
                omitted++;
                continue;
            }
            if (used + cost > share)
            {
                omitted += i + 1;
                break;
            }
            taken.Add(lines[i]);
            used += cost;
        }
        taken.Reverse();
        var body = string.Join('\n', taken);
        return omitted == 0 ? body : $"[omitted {omitted} earlier events; full log remains at this path]\n{body}";
    }
}
