using System.Text;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record SessionSearchArgs(string Query);

public sealed record SessionSearchMatch(string SessionId, string? Title, string Kind, string Snippet);

public sealed record SessionSearchOutput(IReadOnlyList<SessionSearchMatch> Matches, bool Capped);

/// <summary>session_search: case-insensitive phrase search over user/assistant text in live, then persisted, sessions.</summary>
public sealed class SessionSearchTool(SessionStore store) : ToolDefinition<SessionSearchArgs, SessionSearchOutput>
{
    public const int MaxHits = 20;
    public const int MaxPersistedSessions = 50;
    private const int SnippetWidth = 40;

    public override string Name => "session_search";

    public override string Description =>
        "Search session transcripts for a case-insensitive phrase in user and assistant messages — live sessions "
        + $"first, then the newest persisted ones. Returns up to {MaxHits} hits as 'session-id | kind | snippet' lines.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["query"] = JsonSchema.String("Phrase to find in session transcripts."),
        },
        required: ["query"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["matches"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["sessionId"] = JsonSchema.String(),
                    ["title"] = JsonSchema.String(),
                    ["kind"] = JsonSchema.String(),
                    ["snippet"] = JsonSchema.String(),
                },
                Required = ["sessionId", "kind", "snippet"],
                AdditionalProperties = false,
            }),
            ["capped"] = JsonSchema.Boolean(),
        },
        required: ["matches", "capped"]);

    public override int? TimeoutMs => 30_000;

    protected override bool IsConcurrencySafeTyped(SessionSearchArgs args) => true;

    protected override async Task<SessionSearchOutput> ExecuteTyped(SessionSearchArgs args, ToolRunContext exec)
    {
        if (exec.Agent is null)
            throw new ToolException("NO_AGENT", "session search requires an owning agent for workspace authority");
        var query = args.Query.Trim();
        if (query.Length == 0) throw new ToolException("INVALID_ARGS", "query must be non-empty");
        var root = Path.GetFullPath(exec.Agent.Session.Header.Cwd ?? Directory.GetCurrentDirectory());
        var index = exec.Agent.Ctx.TryGet<SessionSearchIndex>(SessionSearchIndex.ServiceKey);

        var matches = new List<SessionSearchMatch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in store.LiveSessions())
        {
            seen.Add(session.Id);
            if (!WithinWorkspace(session.Header.Cwd, root)) continue;
            if (index is not null)
            {
                var title = await index.SyncSessionAsync(session.Id, session.Events, exec.Signal).ConfigureAwait(false);
                if (!await AddIndexHits(index, session.Id, title, query, matches, MaxHits - matches.Count, exec.Signal).ConfigureAwait(false)) return Capped(matches);
            }
            else
            {
                ScanEvents(session.Id, session.Events, query, matches);
            }
            if (matches.Count >= MaxHits) return Capped(matches);
        }

        if (store.Persistence is { } persistence)
        {
            var headers = await persistence.ListAsync(exec.Signal).ConfigureAwait(false);
            foreach (var header in headers.OrderByDescending(h => h.CreatedAt).Take(MaxPersistedSessions))
            {
                if (matches.Count >= MaxHits) break;
                if (seen.Contains(header.Id) || !WithinWorkspace(header.Cwd, root)) continue;
                IReadOnlyList<SessionEvent> events;
                try
                {
                    (_, events) = await persistence.LoadAsync(header.Id, exec.Signal).ConfigureAwait(false);
                }
                catch
                {
                    continue; // unreadable logs are skipped, not fatal to the scan
                }
                if (index is not null)
                {
                    var title = await index.SyncSessionAsync(header.Id, events, exec.Signal).ConfigureAwait(false);
                    if (!await AddIndexHits(index, header.Id, title, query, matches, MaxHits - matches.Count, exec.Signal).ConfigureAwait(false)) return Capped(matches);
                }
                else
                {
                    ScanEvents(header.Id, events, query, matches);
                }
            }
        }
        return Capped(matches);
    }

    /// <summary>Appends up to budget FTS hits; returns false when the overall cap is reached.</summary>
    private static async Task<bool> AddIndexHits(
        SessionSearchIndex index, string sessionId, string? title, string query,
        List<SessionSearchMatch> matches, int budget, CancellationToken ct)
    {
        foreach (var hit in await index.SearchAsync(query, sessionId, Math.Max(1, budget), ct).ConfigureAwait(false))
        {
            matches.Add(new SessionSearchMatch(sessionId, title, KindOf(hit.Type), SnippetOf(hit.Text, query)));
            if (matches.Count >= MaxHits) return false;
        }
        return true;
    }

    private static string KindOf(string type) => type switch
    {
        SessionEventTypes.UserMessage => "user",
        SessionEventTypes.AssistantMessage => "assistant",
        SessionEventTypes.ToolCall => "tool",
        _ => type,
    };

    private static SessionSearchOutput Capped(List<SessionSearchMatch> matches)
        => new(matches, matches.Count >= MaxHits);

    private static void ScanEvents(string sessionId, IReadOnlyList<SessionEvent> events, string query, List<SessionSearchMatch> matches)
    {
        foreach (var e in events)
        {
            if (e.Type != SessionEventTypes.UserMessage && e.Type != SessionEventTypes.AssistantMessage) continue;
            var message = e.Type == SessionEventTypes.UserMessage
                ? SessionEventRead.MessageOf(e)
                : SessionEventRead.AssistantMessageOf(e).Message;
            var text = message.FlattenText();
            if (!text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(new SessionSearchMatch(sessionId, TitleOf(events), RoleOf(e), SnippetOf(text, query)));
            if (matches.Count >= MaxHits) return;
        }
    }

    /// <summary>Session title: the latest session/title event, else the first user message text.</summary>
    internal static string? TitleOf(IReadOnlyList<SessionEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Type == SessionEventTypes.SessionTitle) return SessionEventRead.TitleOf(events[i]);
        }
        foreach (var e in events)
        {
            if (e.Type != SessionEventTypes.UserMessage) continue;
            var text = SessionEventRead.MessageOf(e).FlattenText();
            if (text.Length > 0) return text.Length > 80 ? text[..80] : text;
        }
        return null;
    }

    private static string RoleOf(SessionEvent e)
        => e.Type == SessionEventTypes.UserMessage ? "user" : "assistant";

    /// <summary>A ~40-character context window centered on the first match.</summary>
    internal static string SnippetOf(string text, string query)
    {
        if (text.Length <= SnippetWidth) return text;
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var center = index < 0 ? SnippetWidth / 2 : Math.Clamp(index + query.Length / 2, 0, text.Length - 1);
        var start = Math.Clamp(center - SnippetWidth / 2, 0, text.Length - SnippetWidth);
        var snippet = text.Substring(start, SnippetWidth).Trim();
        return (start > 0 ? "…" : "") + snippet + (start + SnippetWidth < text.Length ? "…" : "");
    }

    private static bool WithinWorkspace(string? cwd, string root)
    {
        if (cwd is null) return true;
        var full = Path.GetFullPath(cwd);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return string.Equals(full, root, StringComparison.Ordinal)
            || full.StartsWith(prefix, StringComparison.Ordinal);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SessionSearchArgs args, SessionSearchOutput output)
    {
        if (output.Matches.Count == 0) return [new TextBlock("No matching sessions found.")];
        var builder = new StringBuilder();
        foreach (var match in output.Matches)
            builder.Append(match.SessionId).Append(" | ").Append(match.Kind).Append(" | ").AppendLine(match.Snippet);
        if (output.Capped) builder.AppendLine($"(capped at {MaxHits} hits)");
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(SessionSearchArgs args) => new()
    {
        Card = "generic",
        Kind = "search",
        Title = args.Query,
        Description = "session search",
    };
}

public sealed record SessionEventReadArgs(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
    [property: System.Text.Json.Serialization.JsonPropertyName("from_seq")] int? FromSeq = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("to_seq")] int? ToSeq = null);

public sealed record SessionEventView(int Seq, string Type);

public sealed record SessionEventReadOutput(string SessionId, IReadOnlyList<SessionEventView> Events, bool Capped, int Total);

/// <summary>session_event_read: seq/type lines over a session's durable log, live or persisted.</summary>
public sealed class SessionEventReadTool(SessionStore store) : ToolDefinition<SessionEventReadArgs, SessionEventReadOutput>
{
    public const int MaxEvents = 200;

    public override string Name => "session_event_read";

    public override string Description =>
        $"Read a session's durable event log as 'seq type' lines (e.g. '12 {SessionEventTypes.TurnStart}'), live or persisted, "
        + $"optionally bounded by from_seq/to_seq (inclusive). Capped at {MaxEvents} events.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("Id of the session to read."),
            ["from_seq"] = JsonSchema.Integer("First seq to return, inclusive. Defaults to 0."),
            ["to_seq"] = JsonSchema.Integer("Last seq to return, inclusive. Defaults to the end of the log."),
        },
        required: ["session_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["events"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["seq"] = JsonSchema.Integer(),
                    ["type"] = JsonSchema.String(),
                },
                Required = ["seq", "type"],
                AdditionalProperties = false,
            }),
            ["capped"] = JsonSchema.Boolean(),
            ["total"] = JsonSchema.Integer(),
        },
        required: ["sessionId", "events", "capped", "total"]);

    public override int? TimeoutMs => 30_000;

    protected override bool IsConcurrencySafeTyped(SessionEventReadArgs args) => true;

    protected override async Task<SessionEventReadOutput> ExecuteTyped(SessionEventReadArgs args, ToolRunContext exec)
    {
        var events = await LoadEventsAsync(store, args.SessionId, exec.Signal).ConfigureAwait(false);

        var from = Math.Max(0, args.FromSeq ?? 0);
        var to = Math.Min(args.ToSeq ?? int.MaxValue, events.Count - 1);
        var range = to < from
            ? []
            : events.SkipWhile(e => e.Seq < from).TakeWhile(e => e.Seq <= to).ToList();
        var total = range.Count;
        var capped = total > MaxEvents;
        return new SessionEventReadOutput(args.SessionId,
            [.. range.Take(MaxEvents).Select(e => new SessionEventView(e.Seq, e.Type))], capped, total);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SessionEventReadArgs args, SessionEventReadOutput output)
    {
        if (output.Events.Count == 0) return [new TextBlock($"No events in the requested range for session '{output.SessionId}'.")];
        var builder = new StringBuilder();
        foreach (var e in output.Events) builder.Append(e.Seq).Append(' ').AppendLine(e.Type);
        if (output.Capped) builder.AppendLine($"(capped at {MaxEvents} of {output.Total} events in range)");
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(SessionEventReadArgs args) => new()
    {
        Card = "generic",
        Kind = "read",
        Title = args.SessionId,
        Description = "read session events",
    };

    /// <summary>Live first, persisted fallback; shared by the read/search/trace tools.</summary>
    internal static async Task<IReadOnlyList<SessionEvent>> LoadEventsAsync(
        SessionStore store, string sessionId, CancellationToken ct)
    {
        var live = store.Get(sessionId)?.Events;
        if (live is not null) return live;
        if (store.Persistence is not { } persistence)
            throw new ToolException("SESSION_NOT_FOUND", $"session '{sessionId}' is not live and no persistence backend is mounted");
        try
        {
            (_, var events) = await persistence.LoadAsync(sessionId, ct).ConfigureAwait(false);
            return events;
        }
        catch (Kernel.HarnessException)
        {
            throw;
        }
        catch
        {
            throw new ToolException("SESSION_NOT_FOUND", $"session '{sessionId}' could not be loaded");
        }
    }
}

/// <summary>Mounts the session-query tools over the session store.</summary>
public sealed class SessionQueryPlugin : HarnessPlugin
{
    public override string Name => "session-query";
    public override string[] Inject { get; } = ["tools", "sessions"];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var store = ctx.Get<SessionStore>(SessionStore.ServiceKey);
        var tools = ctx.Get<ToolRuntime>("tools");
        ctx.Effect(tools.Register(new SessionSearchTool(store)).Dispose);
        ctx.Effect(tools.Register(new SessionEventSearchTool(store)).Dispose);
        ctx.Effect(tools.Register(new SessionTraceTool(store)).Dispose);
        ctx.Effect(tools.Register(new SessionEventTraceTool(store)).Dispose);
        ctx.Effect(tools.Register(new SessionEventReadTool(store)).Dispose);
        return Task.CompletedTask;
    }
}

public sealed record SessionEventSearchArgs(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
    string Query);

public sealed record SessionEventHit(int Seq, string Type, string Snippet);

public sealed record SessionEventSearchOutput(string SessionId, IReadOnlyList<SessionEventHit> Hits, bool Capped);

/// <summary>session_event_search: phrase search inside one session's events (live or persisted).</summary>
public sealed class SessionEventSearchTool(SessionStore store) : ToolDefinition<SessionEventSearchArgs, SessionEventSearchOutput>
{
    public const int MaxHits = 20;

    public override string Name => "session_event_search";

    public override string Description =>
        "Search one session's events for a case-insensitive phrase in user/assistant text and tool call names — "
        + $"live or persisted. Returns up to {MaxHits} 'seq type snippet' hits. Use session_search to find the session first.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("Id of the session to search."),
            ["query"] = JsonSchema.String("Phrase to find."),
        },
        required: ["session_id", "query"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["hits"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["seq"] = JsonSchema.Integer(),
                    ["type"] = JsonSchema.String(),
                    ["snippet"] = JsonSchema.String(),
                },
                Required = ["seq", "type", "snippet"],
                AdditionalProperties = false,
            }),
            ["capped"] = JsonSchema.Boolean(),
        },
        required: ["sessionId", "hits", "capped"]);

    public override int? TimeoutMs => 30_000;

    protected override bool IsConcurrencySafeTyped(SessionEventSearchArgs args) => true;

    protected override async Task<SessionEventSearchOutput> ExecuteTyped(SessionEventSearchArgs args, ToolRunContext exec)
    {
        if (args.Query.Trim().Length == 0) throw new ToolException("INVALID_ARGS", "query must be non-empty");
        var events = await SessionEventReadTool.LoadEventsAsync(store, args.SessionId, exec.Signal).ConfigureAwait(false);
        var query = args.Query.Trim();
        var hits = new List<SessionEventHit>();
        foreach (var e in events)
        {
            var text = EventText(e);
            if (text is null || !text.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            hits.Add(new SessionEventHit(e.Seq, e.Type, SessionSearchTool.SnippetOf(text, query)));
            if (hits.Count >= MaxHits) break;
        }
        return new SessionEventSearchOutput(args.SessionId, hits, hits.Count >= MaxHits);
    }

    private static string? EventText(SessionEvent e)
    {
        if (e.Type == SessionEventTypes.UserMessage) return SessionEventRead.MessageOf(e).FlattenText();
        if (e.Type == SessionEventTypes.AssistantMessage) return SessionEventRead.AssistantMessageOf(e).Message.FlattenText();
        if (e.Type == SessionEventTypes.ToolCall)
        {
            try { return SessionEventRead.ToolCallOf(e).Name; }
            catch { return null; }
        }
        return null;
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SessionEventSearchArgs args, SessionEventSearchOutput output)
    {
        if (output.Hits.Count == 0) return [new TextBlock($"No matches for '{args.Query}' in session '{output.SessionId}'.")];
        var builder = new StringBuilder();
        foreach (var hit in output.Hits) builder.Append(hit.Seq).Append(' ').Append(hit.Type).Append(' ').AppendLine(hit.Snippet);
        if (output.Capped) builder.AppendLine($"(capped at {MaxHits} hits)");
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(SessionEventSearchArgs args) => new()
    {
        Card = "generic",
        Kind = "search",
        Title = $"{args.SessionId}: {args.Query}",
        Description = "search session events",
    };
}

public sealed record SessionTraceArgs(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId);

public sealed record SessionTraceEntry(string SessionId, string Relation, string? Title);

public sealed record SessionTraceOutput(string SessionId, IReadOnlyList<SessionTraceEntry> Lineage);

/// <summary>session_trace: ancestor/descendant lineage of one session via fork parent links.</summary>
public sealed class SessionTraceTool(SessionStore store) : ToolDefinition<SessionTraceArgs, SessionTraceOutput>
{
    public override string Name => "session_trace";

    public override string Description =>
        "Read the fork lineage around one session: ancestors (following parent links) and descendants, "
        + "live or persisted, each with its title. Use it to understand where a resumed or forked session came from.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("Id of the session to trace."),
        },
        required: ["session_id"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["lineage"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["sessionId"] = JsonSchema.String(),
                    ["relation"] = JsonSchema.String(),
                    ["title"] = JsonSchema.String(),
                },
                Required = ["sessionId", "relation"],
                AdditionalProperties = false,
            }),
        },
        required: ["sessionId", "lineage"]);

    public override int? TimeoutMs => 30_000;

    protected override bool IsConcurrencySafeTyped(SessionTraceArgs args) => true;

    protected override async Task<SessionTraceOutput> ExecuteTyped(SessionTraceArgs args, ToolRunContext exec)
    {
        var live = store.LiveSessions().ToDictionary(s => s.Id, StringComparer.Ordinal);
        Dictionary<string, SessionHeader> persisted = new(StringComparer.Ordinal);
        if (store.Persistence is { } persistence)
        {
            foreach (var header in await persistence.ListAsync(exec.Signal).ConfigureAwait(false))
                persisted.TryAdd(header.Id, header);
        }
        if (!live.ContainsKey(args.SessionId) && !persisted.ContainsKey(args.SessionId))
            throw new ToolException("SESSION_NOT_FOUND", $"session '{args.SessionId}' is neither live nor persisted");

        string? ParentOf(string id)
            => live.TryGetValue(id, out var s) ? s.Header.ParentSession
                : persisted.TryGetValue(id, out var h) ? h.ParentSession : null;

        string? TitleOfId(string id)
            => live.TryGetValue(id, out var s) ? SessionSearchTool.TitleOf(s.Events)
                : persisted.TryGetValue(id, out _) ? null : null;

        var lineage = new List<SessionTraceEntry>();
        var ancestors = new Stack<string>();
        for (var parent = ParentOf(args.SessionId); parent is not null; parent = ParentOf(parent))
        {
            if (parent == args.SessionId || ancestors.Contains(parent)) break; // corrupt cycle guard
            ancestors.Push(parent);
        }
        foreach (var ancestor in ancestors)
            lineage.Add(new SessionTraceEntry(ancestor, "ancestor", TitleOfId(ancestor)));
        lineage.Add(new SessionTraceEntry(args.SessionId, "self", TitleOfId(args.SessionId)));

        var seen = new HashSet<string>(lineage.Select(e => e.SessionId), StringComparer.Ordinal);
        var frontier = new Queue<string>();
        frontier.Enqueue(args.SessionId);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var candidate in live.Keys.Concat(persisted.Keys))
            {
                if (!seen.Add(candidate)) continue;
                if (!string.Equals(ParentOf(candidate), current, StringComparison.Ordinal)) { seen.Remove(candidate); continue; }
                lineage.Add(new SessionTraceEntry(candidate, "descendant", TitleOfId(candidate)));
                frontier.Enqueue(candidate);
            }
        }
        return new SessionTraceOutput(args.SessionId, lineage);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SessionTraceArgs args, SessionTraceOutput output)
    {
        var builder = new StringBuilder();
        foreach (var entry in output.Lineage)
        {
            builder.Append(entry.Relation).Append(' ').Append(entry.SessionId);
            if (entry.Title is { Length: > 0 }) builder.Append(" — ").Append(entry.Title);
            builder.AppendLine();
        }
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(SessionTraceArgs args) => new()
    {
        Card = "generic",
        Kind = "read",
        Title = args.SessionId,
        Description = "trace session lineage",
    };
}

public sealed record SessionEventTraceArgs(
    [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string SessionId,
    int Seq);

public sealed record SessionEventLink(int Seq, string Type, string Relation);

public sealed record SessionEventTraceOutput(string SessionId, int Seq, IReadOnlyList<SessionEventLink> Related);

/// <summary>session_event_trace: derivation relationships of one event (sources, derived, same-turn).</summary>
public sealed class SessionEventTraceTool(SessionStore store) : ToolDefinition<SessionEventTraceArgs, SessionEventTraceOutput>
{
    public override string Name => "session_event_trace";

    public override string Description =>
        "Read the relationships of one session event: the events it was derived from (sources), the events "
        + "derived from it, and same-turn siblings. Use it to audit how a summary, compaction, or result came to be.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["session_id"] = JsonSchema.String("Id of the session."),
            ["seq"] = JsonSchema.Integer("Target event sequence number."),
        },
        required: ["session_id", "seq"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["sessionId"] = JsonSchema.String(),
            ["seq"] = JsonSchema.Integer(),
            ["related"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["seq"] = JsonSchema.Integer(),
                    ["type"] = JsonSchema.String(),
                    ["relation"] = JsonSchema.String(),
                },
                Required = ["seq", "type", "relation"],
                AdditionalProperties = false,
            }),
        },
        required: ["sessionId", "seq", "related"]);

    public override int? TimeoutMs => 30_000;

    protected override bool IsConcurrencySafeTyped(SessionEventTraceArgs args) => true;

    protected override async Task<SessionEventTraceOutput> ExecuteTyped(SessionEventTraceArgs args, ToolRunContext exec)
    {
        var events = await SessionEventReadTool.LoadEventsAsync(store, args.SessionId, exec.Signal).ConfigureAwait(false);
        var target = events.FirstOrDefault(e => e.Seq == args.Seq)
            ?? throw new ToolException("SESSION_EVENT_NOT_FOUND", $"session '{args.SessionId}' has no event seq {args.Seq}");
        var related = new List<SessionEventLink>();
        if (target.SourceEventSeqs is { } sources)
        {
            foreach (var seq in sources)
            {
                var source = events.FirstOrDefault(e => e.Seq == seq);
                related.Add(new SessionEventLink(seq, source?.Type ?? "unknown", "source"));
            }
        }
        foreach (var e in events)
        {
            if (e.Seq != args.Seq && e.SourceEventSeqs?.Contains(args.Seq) == true)
                related.Add(new SessionEventLink(e.Seq, e.Type, "derived"));
        }
        if (TryTurnOf(target, out var turn))
        {
            foreach (var e in events)
            {
                if (e.Seq != args.Seq && TryTurnOf(e, out var other) && other == turn)
                    related.Add(new SessionEventLink(e.Seq, e.Type, "same-turn"));
            }
        }
        return new SessionEventTraceOutput(args.SessionId, args.Seq, related);
    }

    private static bool TryTurnOf(SessionEvent e, out int turn)
    {
        turn = 0;
        if (e.Data.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        return e.Data.TryGetProperty("turn", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Number && t.TryGetInt32(out turn);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SessionEventTraceArgs args, SessionEventTraceOutput output)
    {
        if (output.Related.Count == 0) return [new TextBlock($"Event {output.Seq} has no recorded relationships.")];
        var builder = new StringBuilder();
        foreach (var link in output.Related) builder.Append(link.Relation).Append(' ').Append(link.Seq).Append(' ').AppendLine(link.Type);
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(SessionEventTraceArgs args) => new()
    {
        Card = "generic",
        Kind = "read",
        Title = $"{args.SessionId} #{args.Seq}",
        Description = "trace event relationships",
    };
}
