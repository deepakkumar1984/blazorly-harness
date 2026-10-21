using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Decisions;

public sealed record ToolGateOptions
{
    /// <summary>Master switch. Off means every request carries the full tool list, exactly as before.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Filtering only engages above this many tools. Small tool sets select fine on their own —
    /// the accuracy curve falls off with scale, and a 20-tool harness has no ambiguous tail.
    /// </summary>
    public int MaxTools { get; init; } = 28;

    /// <summary>Shortlist target size: core + recently used + lexically relevant + model picks, capped here.</summary>
    public int Keep { get; init; } = 18;

    /// <summary>
    /// When the lexical pass scores fewer than this many tools, the brief is ambiguous for code
    /// alone and the decision model gets the tail (if the seam is on). Otherwise rules decide
    /// everything and no model call is made at all.
    /// </summary>
    public int MinLexical { get; init; } = 6;

    /// <summary>Tools that are always relevant regardless of the brief. Settings may override.</summary>
    public IReadOnlyList<string> CoreTools { get; init; } =
        ["read", "write", "edit", "bash", "grep", "glob", "todo_write", "ask_user_question"];
}

public sealed record ToolGateStats
{
    public long Requests { get; init; }
    public long PassedThrough { get; init; }
    public long Engaged { get; init; }
    public long ModelTailCalls { get; init; }
    public long ToolsIn { get; init; }
    public long ToolsOut { get; init; }
    public long SchemaCharsIn { get; init; }
    public long SchemaCharsOut { get; init; }

    public double MeanToolsOut => Engaged == 0 ? 0 : (double)ToolsOut / Engaged;
    /// <summary>Rough share of tool-schema payload that stopped being sent per engaged request.</summary>
    public double SchemaCharsSaved => SchemaCharsIn == 0 ? 0 : 1 - (double)SchemaCharsOut / SchemaCharsIn;
}

/// <summary>
/// ctx.toolGate — the hybrid tool selector. At many tools (built-ins plus MCP servers) raw
/// selection accuracy falls and every request pays the full schema payload. The gate is the
/// consensus hybrid: a deterministic first pass (core tools + recently used + lexical relevance
/// to the current brief) carries the easy cases with zero model calls, and a System One
/// <see cref="Choice"/> handles the ambiguous tail — one small parallel decision, not a
/// generation, only when the lexical signal is weak.
///
/// <b>Degrade-to-full is the contract.</b> Disabled, under threshold, or any failure returns the
/// assembly untouched: the model then sees every tool, which is exactly the pre-gate behaviour.
/// A wrong shortlist is also recoverable — tools used once stay kept for the session, and the
/// shortlist recomputes as the brief changes.
/// </summary>
public sealed class ToolGateService
{
    public const string ServiceKey = "toolGate";
    public const string ChoiceKey = "tools";

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        { "the", "a", "an", "and", "or", "to", "of", "in", "on", "for", "with", "me", "my", "our", "please",
          "this", "that", "these", "those", "is", "are", "be", "it", "its", "can", "you", "i", "we", "now",
          "then", "there", "here", "into", "from", "at", "by", "up", "out", "about", "what", "which", "how" };

    private readonly DecisionService? _decisions;
    private readonly ToolGateOptions _options;
    private long _requests, _passedThrough, _engaged, _modelTailCalls, _toolsIn, _toolsOut, _charsIn, _charsOut;

    public ToolGateService(DecisionService? decisions, ToolGateOptions options)
    {
        _decisions = decisions;
        _options = options;
    }

    public ToolGateOptions Options => _options;

    public ToolGateStats Stats() => new()
    {
        Requests = Interlocked.Read(ref _requests),
        PassedThrough = Interlocked.Read(ref _passedThrough),
        Engaged = Interlocked.Read(ref _engaged),
        ModelTailCalls = Interlocked.Read(ref _modelTailCalls),
        ToolsIn = Interlocked.Read(ref _toolsIn),
        ToolsOut = Interlocked.Read(ref _toolsOut),
        SchemaCharsIn = Interlocked.Read(ref _charsIn),
        SchemaCharsOut = Interlocked.Read(ref _charsOut),
    };

    /// <summary>
    /// Filters the assembly's tool schemas for this agent. Returns the assembly unchanged whenever
    /// the gate is off, the tool count is under the threshold, or anything fails — never a smaller
    /// list by accident.
    /// </summary>
    public async Task<PromptAssembly> FilterAsync(Agent.Agent agent, PromptAssembly assembly, CancellationToken ct = default)
    {
        if (!_options.Enabled) return assembly;
        Interlocked.Increment(ref _requests);
        var schemas = assembly.ToolSchemas;
        if (schemas.Count <= _options.MaxTools)
        {
            Interlocked.Increment(ref _passedThrough);
            return assembly;
        }

        try
        {
            var keep = await SelectAsync(agent, schemas, ct).ConfigureAwait(false);
            if (keep.Count == 0)
            {
                // An empty shortlist is a broken request, never a small one: nothing scored, so
                // the rules had no reading of the brief at all. Degrade to the full list.
                Interlocked.Increment(ref _passedThrough);
                return assembly;
            }
            var filtered = schemas.Where(s => keep.Contains(s.Name)).ToList();
            if (filtered.Count == schemas.Count)
            {
                Interlocked.Increment(ref _passedThrough);
                return assembly; // nothing worth dropping; do not churn the request
            }
            Interlocked.Increment(ref _engaged);
            Interlocked.Add(ref _toolsIn, schemas.Count);
            Interlocked.Add(ref _toolsOut, filtered.Count);
            Interlocked.Add(ref _charsIn, schemas.Sum(SchemaChars));
            Interlocked.Add(ref _charsOut, filtered.Sum(SchemaChars));
            return assembly with { ToolSchemas = filtered };
        }
        catch (OperationCanceledException)
        {
            throw; // a user cancel must never be swallowed into a filtered request
        }
        catch (Exception ex) when (ex is HarnessException or InvalidOperationException or JsonException)
        {
            Interlocked.Increment(ref _passedThrough);
            return assembly;
        }
    }

    private async Task<HashSet<string>> SelectAsync(Agent.Agent agent, IReadOnlyList<ToolSchema> schemas, CancellationToken ct)
    {
        var present = schemas.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);

        // 1. core tools (whichever of them actually exist here)
        foreach (var name in _options.CoreTools)
            if (present.Contains(name)) keep.Add(name);

        // 2. recently used tools: a tool the agent reached for is load-bearing for this task
        foreach (var name in RecentTools(agent.Session, 40))
            if (present.Contains(name)) keep.Add(name);

        // 3. lexical relevance to the current brief (the newest human message; stable within a
        //    turn, so the decision cache below hits for every step after the first)
        var brief = LatestUserText(agent.Session);
        var tokens = Tokenize(brief);
        var lexicallyScored = schemas
            .Where(s => !keep.Contains(s.Name))
            .Select(s => (s.Name, Score: Score(s, tokens)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();
        foreach (var (name, _) in lexicallyScored.Take(Math.Max(0, _options.Keep - keep.Count)))
            keep.Add(name);

        // 4. the ambiguous tail: few lexical hits means code could not read the intent — let the
        //    decision model pick from the not-yet-kept names. One Choice, options-capped.
        if (lexicallyScored.Count < _options.MinLexical
            && _decisions is { } decisions
            && decisions.IsEnabled(DecisionSeams.ToolGate)
            && keep.Count < _options.Keep)
        {
            var candidates = schemas.Select(s => s.Name).Where(n => !keep.Contains(n)).Take(Choice.MaxOptions).ToList();
            if (candidates.Count > 0)
            {
                var state = new DecisionState()
                    .Add("brief", brief)
                    .Add("recentTools", string.Join(", ", RecentTools(agent.Session, 12)));
                var question = new Choice(ChoiceKey,
                    "Which of these tools does the described work need? Consider the whole brief, not just its last line.",
                    candidates);
                var result = await decisions.DecideAsync(DecisionSeams.ToolGate, state, [question], agent.Session, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _modelTailCalls);
                foreach (var name in PicksOf(result, candidates))
                {
                    if (keep.Count >= _options.Keep) break;
                    if (present.Contains(name)) keep.Add(name);
                }
            }
        }

        return keep;
    }

    /// <summary>Top picks from a choice answer: the reported distribution when present, else the chosen value.</summary>
    private static IEnumerable<string> PicksOf(DecisionResult? result, IReadOnlyList<string> candidates)
    {
        if (result is null || !result.TryGet(ChoiceKey, out var answer)) return [];
        var order = new HashSet<string>(candidates, StringComparer.Ordinal);
        if (answer.Options is { Count: > 0 })
        {
            return answer.Options
                .Where(kv => order.Contains(kv.Key))
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key);
        }
        return answer.Value is not null && order.Contains(answer.Value) ? [answer.Value] : [];
    }

    /// <summary>Newest human message text; plugin/tool traffic is maintenance, not intent.</summary>
    private static string LatestUserText(Session session)
    {
        var messages = session.DeriveMessages();
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Role != "user" || message.Source.Kind is "plugin" or "tool") continue;
            var text = message.FlattenText().Trim();
            return text.Length > 4_000 ? text[..4_000] : text;
        }
        return "";
    }

    /// <summary>Tool names from the log tail, newest first, deduplicated.</summary>
    internal static IReadOnlyList<string> RecentTools(Session session, int maxEvents)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var events = session.Events;
        for (var i = events.Count - 1; i >= 0 && names.Count < maxEvents; i--)
        {
            if (events[i].Type != SessionEventTypes.ToolCall) continue;
            try
            {
                var name = SessionEventRead.ToolCallOf(events[i]).Name;
                if (seen.Add(name)) names.Add(name);
            }
            catch (JsonException)
            {
                // unreadable payload: skip the event, never the gate
            }
        }
        return names;
    }

    internal static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) builder.Append(ch);
            else if (builder.Length > 0) Flush();
        }
        if (builder.Length > 0) Flush();
        return tokens;

        void Flush()
        {
            if (builder.Length >= 3 && !StopWords.Contains(builder.ToString())) tokens.Add(builder.ToString());
            builder.Clear();
        }
    }

    /// <summary>Lexical relevance: brief tokens matched against the tool name (heavy) and description (light).</summary>
    internal static int Score(ToolSchema schema, HashSet<string> briefTokens)
    {
        if (briefTokens.Count == 0) return 0;
        var nameTokens = Tokenize(schema.Name.Replace('_', ' '));
        var descriptionTokens = Tokenize(schema.Description ?? "");
        var score = 0;
        foreach (var token in briefTokens)
        {
            if (nameTokens.Contains(token)) score += 3;
            else if (descriptionTokens.Contains(token)) score += 1;
        }
        return score;
    }

    private static int SchemaChars(ToolSchema schema)
        => schema.Name.Length + (schema.Description?.Length ?? 0) + schema.Parameters.GetRawText().Length;
}

/// <summary>
/// Mounts the tool gate. Independent of System One: with the seam off this is the pure-code
/// filter (zero AI calls); with <c>tool-gate</c> in <c>systemOneSeams</c> the ambiguous tail goes
/// to the decision model. Plugin name maps to <c>EnableToolGate</c> for patches.json.
/// </summary>
public sealed class ToolGatePlugin(ToolGateOptions options, DecisionService? decisions = null) : HarnessPlugin
{
    public const string PluginName = "tool-gate";

    public override string Name => PluginName;

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        ctx.Provide(ToolGateService.ServiceKey, new ToolGateService(decisions, options));
        return Task.CompletedTask;
    }
}
