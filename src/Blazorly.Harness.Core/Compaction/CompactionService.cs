using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Agent = Blazorly.Harness.Core.Agent.Agent;

namespace Blazorly.Harness.Core.Compaction;

/// <summary>Compaction policy knobs; resolved from user settings by the host.</summary>
public sealed record CompactionOptions
{
    /// <summary>Context window used for pressure math (fallback when the model catalog has none).</summary>
    public long ContextWindowTokens { get; init; } = 65_536;

    /// <summary>Fraction of the window that triggers compaction.</summary>
    public double Threshold { get; init; } = 0.72;

    /// <summary>Fraction of the window kept unshadowed (recent messages stay verbatim).</summary>
    public double KeepRatio { get; init; } = 0.25;

    public int SummaryMaxTokens { get; init; } = 1024;

    /// <summary>Old tool results rendering more than this many chars are pruned to a placeholder.</summary>
    public int PrunerChars { get; init; } = 4_000;

    public long TriggerTokens => (long)(ContextWindowTokens * Threshold);
    public long KeepTokens => (long)(ContextWindowTokens * KeepRatio);
}

/// <summary>
/// Context management: measures replay-consistent pressure, and when the projected request
/// exceeds the threshold, shadows the old surface behind a durable summary (surface-replacing
/// user message) via a compaction model call. Also recovers from CONTEXT_WINDOW_EXCEEDED
/// request errors by compacting harder and retrying.
/// </summary>
public sealed class CompactionService
{
    private readonly HarnessContext _ctx;
    private readonly LlmRuntime _llm;
    private readonly SystemPromptService _systemPrompt;
    private CompactionOptions _options = new();
    private int _counter;

    public CompactionService(HarnessContext ctx)
    {
        _ctx = ctx;
        _llm = ctx.Get<LlmRuntime>("llm");
        _systemPrompt = ctx.Get<SystemPromptService>("systemPrompt");
    }

    public static CompactionService Mount(HarnessContext ctx, CompactionOptions? options = null)
    {
        var service = new CompactionService(ctx);
        if (options is not null) service._options = options;
        ctx.Provide("compaction", service);

        // Pressure check before request derivation (dsh: agent/pre-step). The model-free
        // pruner runs first; summarization only fires when pruning alone is not enough.
        ctx.OnWaterfall<PreStepEvent, List<Llm.Message>, PreStepDecision>("agent/pre-step",
            async (payload, value, next, ct) =>
            {
                var agent = payload.Agent;
                if (service.ShouldCompact(agent))
                {
                    await service.PruneAsync(agent, ct).ConfigureAwait(false);
                    if (service.ShouldCompact(agent))
                    {
                        await service.CompactAsync(agent, keepTokens: null, ct).ConfigureAwait(false);
                    }
                }
                return await next(value).ConfigureAwait(false);
            });

        // Canonical context-overflow recovery (dsh: agent/request-error).
        ctx.OnWaterfall<RequestErrorEvent, RequestErrorAction?, RequestErrorAction?>("agent/request-error",
            async (payload, value, next, ct) =>
            {
                if (payload.Failure.Code == LlmErrorCodes.ContextWindowExceeded)
                {
                    await service.PruneAsync(payload.Agent, ct).ConfigureAwait(false);
                    await service.CompactAsync(payload.Agent, keepTokens: service._options.KeepTokens / 2, ct).ConfigureAwait(false);
                    return RequestErrorAction.Retry();
                }
                return await next(value).ConfigureAwait(false);
            });

        return service;
    }

    public CompactionOptions Options
    {
        get => _options;
        set => _options = value;
    }

    /// <summary>Window for this session: the model catalog's context size when known, else the global option.</summary>
    public long ResolveWindow(Agent.Agent agent)
    {
        if (agent.Options.Provider is { } provider && agent.Options.Model is { } model)
        {
            var info = _llm.ListModels(provider).FirstOrDefault(m => m.Id == model);
            if (info?.ContextWindowTokens is { } window && window > 0) return window;
        }
        return _options.ContextWindowTokens;
    }

    /// <summary>Trigger threshold against this session's resolved window.</summary>
    public long TriggerFor(Agent.Agent agent) => (long)(ResolveWindow(agent) * _options.Threshold);

    /// <summary>Estimated tokens of the next request: header (system + tools) + surface.</summary>
    public long Measure(Agent.Agent agent)
    {
        var assembly = _systemPrompt.Assemble(agent, agent.Session.Header.Cwd);
        var header = TokenEstimator.EstimateHeader(
            string.Join("\n\n", assembly.Sections.Select(s => s.Text)),
            [.. assembly.ToolSchemas]);
        var surface = TokenEstimator.EstimateMessages(agent.Session.DeriveMessages());
        return header + surface;
    }

    public bool ShouldCompact(Agent.Agent agent) => Measure(agent) > TriggerFor(agent);

    /// <summary>
    /// Model-free pruning (dsh compaction-tool-result-pruner): old oversized tool results on
    /// the surface are replaced in place by one-line placeholders, each a 1:1 surface
    /// replacement that preserves tool-call pairing and narrative context. Returns the
    /// estimated tokens removed; a durable compaction/prune event records the accounting.
    /// </summary>
    public async Task<long> PruneAsync(Agent.Agent agent, CancellationToken ct = default)
    {
        var session = agent.Session;
        var surface = session.SurfaceSeqs.ToList();
        var keepRecent = Math.Max(4, surface.Count / 4); // the newest quarter stays verbatim
        var pruned = new List<int>();
        long prunedChars = 0;

        foreach (var seq in surface.Take(Math.Max(0, surface.Count - keepRecent)))
        {
            ct.ThrowIfCancellationRequested();
            var @event = session.Events.FirstOrDefault(e => e.Seq == seq);
            if (@event?.Type != SessionEventTypes.ToolResult) continue;
            var message = SessionEventRead.ToolResultOf(@event).Message;
            var block = message.Content.OfType<ToolResultBlock>().FirstOrDefault();
            if (block is null) continue;
            var text = string.Concat(block.Content.OfType<TextBlock>().Select(b => b.Text));
            if (text.Length <= _options.PrunerChars) continue;

            var callName = "tool";
            var callSeq = @event.SourceEventSeqs is { Length: > 0 } sources ? sources[0] : -1;
            var callEvent = session.Events.FirstOrDefault(e => e.Seq == callSeq && e.Type == SessionEventTypes.ToolCall);
            if (callEvent is not null) callName = SessionEventRead.ToolCallOf(callEvent).Name;

            var placeholder = $"[tool output pruned: {text.Length} chars from '{callName}' to reduce context; re-run the tool if you need it]";
            var replacement = Llm.Message.CreateToolResult(block.ToolCallId, [new TextBlock(placeholder)], block.IsError == true);
            var position = surface.IndexOf(seq);
            if (position < 0) continue;
            session.Append(SessionEventTypes.UserMessage, replacement, new Session.AppendOptions(
                SourceEventSeqs: [seq],
                SurfaceOp: new SurfaceOp.Replace(position, position)));
            pruned.Add(seq);
            prunedChars += text.Length;
        }

        if (pruned.Count == 0) return 0;
        var compactionId = $"prune_{++_counter}";
        var prunedTokens = (prunedChars + 3) / 4; // chars/4 heuristic for the removed bulk
        session.Append(SessionEventTypes.CompactionPrune, new
        {
            compactionId,
            prunedChars,
            prunedTokens,
            prunedSeqs = pruned,
        });
        agent.RetainedContextSnapshot = null; // snapshot may reference pruned output
        return prunedTokens;
    }

    /// <summary>
    /// Shadows the old surface behind a durable summary. The summary replaces surface
    /// positions [0..k); the tail stays verbatim. Returns the number of shadowed nodes.
    /// </summary>
    public async Task<int> CompactAsync(Agent.Agent agent, long? keepTokens = null, CancellationToken ct = default)
    {
        var session = agent.Session;
        if (session.SurfaceSeqs.Count < 3) return 0;

        // Prune oversized tool results first: it is free, shrinks the summary input, and the
        // 1:1 replacements keep the surface length (positions are re-read below).
        await PruneAsync(agent, ct).ConfigureAwait(false);

        var window = ResolveWindow(agent);
        var keep = keepTokens ?? (long)(window * _options.KeepRatio);
        var surface = session.SurfaceSeqs;
        if (surface.Count < 3) return 0;

        var assembly = _systemPrompt.Assemble(agent, session.Header.Cwd);

        // Walk the surface tail-first to find the keep boundary.
        var boundary = surface.Count;
        long kept = 0;
        var crossed = false;
        for (var i = surface.Count - 1; i >= 0; i--)
        {
            var message = ProjectNode(session, surface[i]);
            if (message is null) continue;
            kept += TokenEstimator.EstimateMessage(message);
            if (kept > keep && boundary > i + 1)
            {
                boundary = i + 1;
                crossed = true;
                break;
            }
        }
        if (!crossed && keepTokens.HasValue && surface.Count > 2)
        {
            // Forced compaction (manual /compact, overflow recovery): shadow everything
            // except the most recent node even when the surface is under budget.
            boundary = surface.Count - 1;
        }
        if (boundary <= 1) boundary = 2; // always keep at least one recent node
        if (boundary >= surface.Count) return 0;

        var shadowSeqs = surface.Take(boundary).ToList();
        var shadowedMessages = shadowSeqs.Select(seq => ProjectNode(session, seq)).Where(m => m is not null).ToList()!;

        var compactionId = $"compact_{++_counter}";
        var turn = CurrentTurn(session);
        session.Append(SessionEventTypes.CompactionStart, new { compactionId, turn });

        var summary = await SummarizeAsync(agent, assembly, shadowedMessages!, ct).ConfigureAwait(false);

        session.Append(SessionEventTypes.CompactionSummary, new
        {
            compactionId,
            summary,
            shadowedRange = new { start = 0, end = boundary - 1 },
            shadowSeqs,
            shadowedTokenCount = shadowedMessages!.Sum(m => TokenEstimator.EstimateMessage(m!)),
            provider = agent.Options.Provider,
            model = agent.Options.Model,
        });

        // The surface-replacing summary message: enters model history at the shadowed position.
        var summaryMessage = new Llm.Message(
            Ids.NewMessageId(),
            "user",
            [new TextBlock($"[Context compacted] Summary of the earlier conversation:\n\n{summary}")],
            MessageSource.FromPlugin("compaction", "summary"));
        session.Append(SessionEventTypes.UserMessage, summaryMessage, new Session.AppendOptions(
            SourceEventSeqs: [.. shadowSeqs],
            SurfaceOp: new SurfaceOp.Replace(0, boundary - 1)));

        session.Append(SessionEventTypes.CompactionEnd, new { compactionId, turn });
        // Re-arm: the summary may have shadowed the runtime-context snapshot (project
        // instructions); clearing the retained snapshot re-appends a fresh one next pre-step.
        agent.RetainedContextSnapshot = null;
        return shadowSeqs.Count;
    }

    private static int CurrentTurn(Sessions.Session session)
    {
        var events = session.Events;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Type == SessionEventTypes.TurnStart) return SessionEventRead.TurnOf(events[i]);
        }
        return 0;
    }

    private static Llm.Message? ProjectNode(Sessions.Session session, int seq)
    {
        var e = session.Events.FirstOrDefault(ev => ev.Seq == seq);
        if (e is null) return null;
        return e.Type switch
        {
            SessionEventTypes.UserMessage => SessionEventRead.MessageOf(e),
            SessionEventTypes.AssistantMessage => SessionEventRead.AssistantMessageOf(e).Message.Content.Count == 0
                ? null
                : SessionEventRead.AssistantMessageOf(e).Message,
            SessionEventTypes.ToolResult => SessionEventRead.ToolResultOf(e).Message,
            _ => null,
        };
    }

    /// <summary>
    /// Summarizes the pruned shadowed messages by replaying the conversation's own system
    /// prompt, tool schemas, and message order verbatim (dsh: reuse the provider's warm KV
    /// cache), with the summarization instruction appended as the final user message.
    /// </summary>
    private async Task<string> SummarizeAsync(Agent.Agent agent, PromptAssembly assembly, IReadOnlyList<Llm.Message> shadowed, CancellationToken ct)
    {
        var request = new GenerateOptions
        {
            Provider = agent.Options.Provider ?? throw new LlmException(LlmErrorCodes.NoAdapter, "no provider configured for compaction"),
            Model = agent.Options.Model ?? throw new LlmException(LlmErrorCodes.NoAdapter, "no model configured for compaction"),
            Purpose = "compaction",
            MaxTokens = _options.SummaryMaxTokens,
            System = SystemPromptService.RenderPrompt(assembly),
            Tools = assembly.ToolSchemas,
            Messages = [.. shadowed, Llm.Message.CreateUserText("""
                Summarize the conversation above so work can continue without it: the task and
                its current state, decisions made, files touched, commands run and their
                outcomes, open problems, and the immediate next steps. Be concrete and terse;
                keep names, paths, and ids. Plain text only.
                """)],
        };
        var assembler = new BlockAssembler();
        await foreach (var chunk in _llm.Stream(request, ct).ConfigureAwait(false))
        {
            assembler.Push(chunk);
        }
        var text = string.Join("\n", assembler.Blocks().OfType<TextBlock>().Select(b => b.Text)).Trim();
        if (text.Length == 0) text = "(compaction produced no summary; earlier context was dropped)";
        return text;
    }
}
