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

        var matches = new List<SessionSearchMatch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in store.LiveSessions())
        {
            seen.Add(session.Id);
            if (!WithinWorkspace(session.Header.Cwd, root)) continue;
            ScanEvents(session.Id, session.Events, query, matches);
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
                ScanEvents(header.Id, events, query, matches);
            }
        }
        return Capped(matches);
    }

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
        var events = store.Get(args.SessionId)?.Events;
        if (events is null)
        {
            if (store.Persistence is not { } persistence)
                throw new ToolException("SESSION_NOT_FOUND", $"session '{args.SessionId}' is not live and no persistence backend is mounted");
            try
            {
                (_, events) = await persistence.LoadAsync(args.SessionId, exec.Signal).ConfigureAwait(false);
            }
            catch (Kernel.HarnessException)
            {
                throw;
            }
            catch
            {
                throw new ToolException("SESSION_NOT_FOUND", $"session '{args.SessionId}' could not be loaded");
            }
        }

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
        ctx.Effect(tools.Register(new SessionEventReadTool(store)).Dispose);
        return Task.CompletedTask;
    }
}
