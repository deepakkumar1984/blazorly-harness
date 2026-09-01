using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Subagents;

public sealed record SubagentRequest(
    string Prompt,
    string? Description = null,
    string? Provider = null,
    string? Model = null,
    string? Persona = null,
    Action<Agent.Agent>? Setup = null,
    bool Continuable = false,
    bool Fork = false,
    JsonSchema.Schema? OutputSchema = null);

public sealed record SubagentResult(
    string SessionId,
    string Summary,
    string FinishKind,
    JsonElement? Structured = null,
    string? Diagnostic = null);

/// <summary>
/// ctx.subagents — the delegation seam. The in-process provider spawns a child agent with its
/// own session (lineage stamped, depth-capped), drains it to idle, and returns its final
/// assistant text. Child sessions are ordinary sessions: they persist, compact, and render.
/// Continuable children additionally carry a log-only descriptor and accept later deliveries:
/// a message for a child that is no longer live cold-resumes it from the persisted session
/// (dsh packages/subagent continuation), authorized for the resuming parent only. A child can
/// also be forked from the parent's log prefix (the fork backend) and asked for schema-validated
/// structured output (safe diagnostic on failure).
/// </summary>
public sealed class SubagentService
{
    public const string ServiceKey = "subagents";
    public const int MaxDelegationDepth = 3;

    private readonly HarnessContext _ctx;
    private readonly List<(string Parent, Agent.Agent Child)> _children = new();
    private readonly List<Action<Agent.Agent>> _continuableSetups = [];
    private readonly object _gate = new();
    private int _counter;

    public SubagentService(HarnessContext ctx) => _ctx = ctx;

    public static SubagentService Mount(HarnessContext ctx)
    {
        var service = new SubagentService(ctx);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    /// <summary>
    /// Process-local setups re-run when a continuable child is cold-resumed (compositions
    /// re-register at boot, so scoped tools like the team report channel survive the resume).
    /// </summary>
    public void RegisterContinuableSetup(Action<Agent.Agent> setup)
    {
        lock (_gate) _continuableSetups.Add(setup);
    }

    public IReadOnlyList<SessionHeader> ChildrenOf(string parentSessionId)
    {
        lock (_gate) return [.. _children.Where(c => c.Parent == parentSessionId).Select(c => c.Child.Session.Header)];
    }

    /// <summary>Live children merged with persisted ones, so lineage survives restarts.</summary>
    public async Task<IReadOnlyList<SessionHeader>> ChildrenOfAsync(string parentSessionId, CancellationToken ct = default)
    {
        var merged = new Dictionary<string, SessionHeader>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var (parent, child) in _children)
                if (parent == parentSessionId)
                    merged.TryAdd(child.Session.Header.Id, child.Session.Header);
        }
        foreach (var header in await _ctx.Get<SessionStore>("sessions").ListPersistedAsync(ct).ConfigureAwait(false))
            if (string.Equals(header.ParentSession, parentSessionId, StringComparison.Ordinal))
                merged.TryAdd(header.Id, header);
        return [.. merged.Values];
    }

    public Agent.Agent? GetChild(string sessionId)
    {
        lock (_gate) return _children.FirstOrDefault(c => c.Child.Id == sessionId).Child;
    }

    public async Task<SubagentResult> SpawnAsync(Agent.Agent parent, SubagentRequest request, CancellationToken ct)
    {
        var child = StartChild(parent, request, out var childSession);
        _ = _ctx.Events.EmitAsync("subagent/started", new { parentSessionId = parent.Session.Id, childSessionId = childSession.Id }, parent);

        var prompt = $"{SystemPromptOf(request)}\n\nTask: {request.Prompt}" + SchemaInstruction(request.OutputSchema);
        await DeliverAsync(child, Message.CreateUserText(prompt), ct).ConfigureAwait(false);
        await FlushChildAsync(childSession.Id, ct).ConfigureAwait(false);

        var (summary, finishKind) = LastAssistantOutput(childSession);
        var (structured, diagnostic) = EvaluateStructured(request.OutputSchema, summary);
        _ = _ctx.Events.EmitAsync("subagent/finished", new { childSessionId = childSession.Id, finishKind }, parent);
        return new SubagentResult(childSession.Id, summary, finishKind, structured, diagnostic);
    }

    /// <summary>Background start: the child runs its first turn autonomously; read progress via list/status.</summary>
    public SubagentResult SpawnBackgroundAsync(Agent.Agent parent, SubagentRequest request)
    {
        var child = StartChild(parent, request, out var childSession);
        _ = _ctx.Events.EmitAsync("subagent/started", new { parentSessionId = parent.Session.Id, childSessionId = childSession.Id }, parent);

        var prompt = $"{SystemPromptOf(request)}\n\nTask: {request.Prompt}" + SchemaInstruction(request.OutputSchema);
        child.Followup(Message.CreateUserText(prompt));
        _ = Task.Run(async () =>
        {
            await child.WhenIdleAsync().ConfigureAwait(false);
            await FlushChildAsync(childSession.Id, CancellationToken.None).ConfigureAwait(false);
        });

        return new SubagentResult(childSession.Id, "", "running");
    }

    /// <summary>
    /// Continuation: delivers another instruction to a child as its next turn. A live child
    /// queues it; a settled continuable child is cold-resumed from its persisted session
    /// (authorized for the exact resuming parent); one-shot children refuse.
    /// </summary>
    public async Task<SubagentResult> ContinueAsync(Agent.Agent parent, string childSessionId, string prompt, CancellationToken ct)
    {
        var live = GetChild(childSessionId);
        if (live is not null)
        {
            await DeliverAsync(live, Message.CreateUserText(prompt), ct).ConfigureAwait(false);
            await FlushChildAsync(childSessionId, ct).ConfigureAwait(false);
            var (summary, finishKind) = LastAssistantOutput(live.Session);
            return new SubagentResult(childSessionId, summary, finishKind);
        }
        return await ColdResumeAsync(parent, childSessionId, prompt, ct).ConfigureAwait(false);
    }

    private async Task<SubagentResult> ColdResumeAsync(Agent.Agent parent, string childSessionId, string prompt, CancellationToken ct)
    {
        var sessions = _ctx.Get<SessionStore>("sessions");
        Session childSession;
        try
        {
            childSession = await sessions.OpenAsync(childSessionId, ct).ConfigureAwait(false);
        }
        catch (Kernel.HarnessException ex)
        {
            throw new Kernel.HarnessException("SUBAGENT_NOT_FOUND", $"no live or persisted subagent '{childSessionId}'", ex);
        }

        if (!string.Equals(childSession.Header.ParentSession, parent.Id, StringComparison.Ordinal))
            throw new Kernel.HarnessException("SUBAGENT_NOT_RESUMABLE", $"subagent '{childSessionId}' belongs to a different parent");
        if (childSession.Header.DelegationDepth > MaxDelegationDepth)
            throw new Kernel.HarnessException("SUBAGENT_NOT_RESUMABLE", $"subagent '{childSessionId}' exceeds the delegation depth cap");

        var descriptor = FoldDescriptor(childSession);
        if (descriptor is null || descriptor.Mode != SessionPayloads.SubagentModeContinuable)
            throw new Kernel.HarnessException(
                "SUBAGENT_NOT_RESUMABLE",
                $"subagent '{childSessionId}' has no continuable continuation state and cannot be resumed; do not retry this delivery");

        var child = ConstructChild(parent, childSession, descriptor, runSetups: true);
        _ctx.Get<AgentRuntime>("agents").Publish(child);
        lock (_gate) _children.Add((childSession.Header.ParentSession!, child));
        _ = _ctx.Events.EmitAsync("subagent/resumed", new { parentSessionId = parent.Id, childSessionId }, parent);

        await DeliverAsync(child, Message.CreateUserText(prompt), ct).ConfigureAwait(false);
        await FlushChildAsync(childSessionId, ct).ConfigureAwait(false);

        var (summary, finishKind) = LastAssistantOutput(childSession);
        return new SubagentResult(childSessionId, summary, finishKind);
    }

    /// <summary>
    /// Forks the parent at its last settled turn boundary (dsh fork-in-process): the child
    /// seed is the parent's log through the newest turn/end, never a partially open turn.
    /// </summary>
    private static Session ForkParentSession(Session parentSession, SessionStore sessions)
    {
        var lastTurnEnd = -1;
        foreach (var @event in parentSession.Events)
        {
            if (@event.Type == SessionEventTypes.TurnEnd) lastTurnEnd = @event.Seq;
        }
        if (lastTurnEnd < 0)
            throw new Kernel.HarnessException("INVALID_FORK", "fork requires a parent with at least one settled turn");
        return sessions.Fork(parentSession.Id, lastTurnEnd);
    }

    private Agent.Agent StartChild(Agent.Agent parent, SubagentRequest request, out Session childSession)
    {
        var parentSession = parent.Session;
        if (parentSession.Header.DelegationDepth >= MaxDelegationDepth)
        {
            throw new Kernel.HarnessException("DELEGATION_DEPTH", $"delegation depth cap of {MaxDelegationDepth} reached");
        }

        var sessions = _ctx.Get<SessionStore>("sessions");
        childSession = request.Fork
            ? ForkParentSession(parentSession, sessions)
            : sessions.Create(
                id: $"session-sub-{++_counter:x8}",
                meta: new SessionMeta(
                    Cwd: parentSession.Header.Cwd,
                    ParentSession: parentSession.Id,
                    DelegationDepth: parentSession.Header.DelegationDepth + 1));

        var descriptor = new SessionPayloads.SubagentDescriptorPayload(
            Mode: SessionPayloads.SubagentModeContinuable,
            Provider: request.Provider ?? parent.Options.Provider,
            Model: request.Model ?? parent.Options.Model,
            Persona: request.Persona);
        var child = ConstructChild(parent, childSession, request.Continuable ? descriptor : null, runSetups: false);

        request.Setup?.Invoke(child);
        _ctx.Get<AgentRuntime>("agents").Publish(child);
        lock (_gate) _children.Add((childSession.Header.ParentSession!, child));
        if (request.Continuable)
        {
            // Log-only continuation state: never enters model history, survives compaction.
            childSession.Append(SessionEventTypes.SubagentDescriptor, descriptor, new Session.AppendOptions(Ignorable: true));
        }
        return child;
    }

    private string SystemPromptOf(SubagentRequest request)
        => request.Persona is { Length: > 0 }
            ? $"You are a delegated subagent. {request.Persona}"
            : "You are a delegated subagent: complete the task you are given autonomously, then stop.";

    private static string SchemaInstruction(JsonSchema.Schema? schema)
        => schema is null
            ? ""
            : "\n\nEnd your final message with a single JSON value — no prose around it — matching this JSON schema exactly:\n"
                + schema.ToJson();

    /// <summary>Safe structured-output evaluation: never throws, reports a diagnostic instead.</summary>
    private static (JsonElement? Structured, string? Diagnostic) EvaluateStructured(JsonSchema.Schema? schema, string summary)
    {
        if (schema is null) return (null, null);
        var text = summary.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            if (firstBreak > 0) text = text[(firstBreak + 1)..];
            if (text.TrimEnd().EndsWith("```", StringComparison.Ordinal)) text = text.TrimEnd()[..^3];
            text = text.Trim();
        }
        JsonElement parsed;
        try
        {
            parsed = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return (null, $"structured output did not parse as JSON: {ex.Message}");
        }
        var error = JsonSchema.Validate(parsed, schema);
        return error is null ? (parsed, null) : (null, $"structured output did not match the schema: {error}");
    }

    /// <summary>Folds the child's own descriptor (after the fork seed; the newest wins).</summary>
    private static SessionPayloads.SubagentDescriptorPayload? FoldDescriptor(Session childSession)
    {
        var events = childSession.Events;
        for (var i = events.Count - 1; i >= childSession.Header.SeedLength; i--)
        {
            if (events[i].Type != SessionEventTypes.SubagentDescriptor) continue;
            return SessionEventRead.SubagentDescriptorOf(events[i]);
        }
        return null;
    }

    private Agent.Agent ConstructChild(Agent.Agent parent, Session childSession, SessionPayloads.SubagentDescriptorPayload? descriptor, bool runSetups)
    {
        var child = new Agent.Agent(
            _ctx,
            _ctx.Get<LlmRuntime>("llm"),
            _ctx.Get<ToolRuntime>("tools"),
            _ctx.Get<Core.SystemPrompt.SystemPromptService>("systemPrompt"),
            childSession,
            new AgentOptions(
                descriptor?.Provider ?? parent.Options.Provider,
                descriptor?.Model ?? parent.Options.Model,
                parent.Options.MaxTokens));
        child.RetryLimit = parent.RetryLimit;
        child.Driver.MaxParallelToolCalls = parent.Driver.MaxParallelToolCalls;
        if (runSetups)
        {
            Action<Agent.Agent>[] setups;
            lock (_gate) setups = [.. _continuableSetups];
            foreach (var setup in setups) setup(child);
        }
        return child;
    }

    /// <summary>
    /// One FIFO next-turn delivery with durable settle gating: the child's own
    /// <c>user/message</c> claim, then the <c>turn/end</c> after it, then quiescence. Gating
    /// on the durable claim is what makes queued deliveries settle even when the child was
    /// busy with an earlier turn.
    /// </summary>
    private static async Task DeliverAsync(Agent.Agent child, Message message, CancellationToken ct)
    {
        var claimed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageSeq = (int?)null;
        using var subscription = child.Session.Subscribe(@event =>
        {
            if (@event.Type == SessionEventTypes.UserMessage && messageSeq is null && MessageIdOf(@event) == message.Id)
            {
                messageSeq = @event.Seq;
                claimed.TrySetResult(@event.Seq);
            }
            else if (@event.Type == SessionEventTypes.TurnEnd && messageSeq is { } seq && @event.Seq > seq)
            {
                ended.TrySetResult();
            }
        });
        child.Followup(message);
        var claimSeq = await claimed.Task.WaitAsync(ct).ConfigureAwait(false);
        await ended.Task.WaitAsync(ct).ConfigureAwait(false);
        await child.WhenIdleAsync().ConfigureAwait(false);
    }

    private static string? MessageIdOf(SessionEvent @event)
    {
        try
        {
            return SessionEventRead.MessageOf(@event).Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task FlushChildAsync(string sessionId, CancellationToken ct)
    {
        var persistence = _ctx.Get<SessionStore>("sessions").Persistence;
        if (persistence is not null) await persistence.FlushAsync(sessionId, ct).ConfigureAwait(false);
    }

    private static (string Summary, string FinishKind) LastAssistantOutput(Session session)
    {
        string finishKind = "completed";
        foreach (var e in session.Events)
        {
            if (e.Type != SessionEventTypes.TurnEnd) continue;
            var reason = SessionEventRead.TurnEndReasonOf(e);
            finishKind = reason switch
            {
                TurnEndReason.Aborted => "aborted",
                TurnEndReason.Error => "error",
                TurnEndReason.MaxTokens => "max-tokens",
                _ => "completed",
            };
        }
        for (var i = session.Events.Count - 1; i >= 0; i--)
        {
            var e = session.Events[i];
            if (e.Type != SessionEventTypes.AssistantMessage) continue;
            var payload = SessionEventRead.AssistantMessageOf(e);
            var text = string.Join("\n", payload.Message.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
            if (text.Length > 0) return (text, finishKind);
        }
        return ("(the subagent produced no final message)", finishKind);
    }
}
