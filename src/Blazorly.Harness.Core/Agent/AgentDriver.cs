using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Agent;

/// <summary>
/// The concrete loop driver: turns drain admitted input; steps are one model request plus
/// its tool calls. Tool bodies overlap in a bounded rolling pool while results commit in
/// model order. Cancellation rides the running phase's token; the first cause wins.
/// </summary>
public sealed class AgentDriver
{
    private readonly Agent _agent;
    private readonly HarnessContext _ctx;
    private readonly LlmRuntime _llm;
    private readonly ToolRuntime _tools;
    private readonly SystemPromptService _systemPrompt;

    public AgentDriver(Agent agent, HarnessContext ctx, LlmRuntime llm, ToolRuntime tools, SystemPromptService systemPrompt)
    {
        _agent = agent;
        _ctx = ctx;
        _llm = llm;
        _tools = tools;
        _systemPrompt = systemPrompt;
    }

    public int MaxParallelToolCalls { get; set; } = 10;

    /// <summary>Each true result means queued work remains, so another turn runs.</summary>
    public async Task KickAsync()
    {
        while (await TurnAsync().ConfigureAwait(false)) { }
    }

    private async Task<bool> TurnAsync()
    {
        var ct = _agent.DriverToken;
        var session = _agent.Session;
        var turn = _agent.AdvanceTurn(LastTurn() + 1).Turn;
        session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(turn));
        TurnEndReason? turnEnds = null;
        var target = InboxTarget.NextTurn;
        var step = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                step++;
                var (decision, assembly) = await PreStepAsync(target, turn, step, ct).ConfigureAwait(false);
                if (decision.Kind == PreStepDecision.RejectKind)
                {
                    turnEnds = new TurnEndReason.Blocked();
                    return _agent.Inbox.HasPending;
                }
                if (turnEnds is not null && decision.Messages.Count == 0)
                {
                    return _agent.Inbox.HasPending; // continuation found no new input
                }
                if (step == 1 && decision.Messages.Count == 0)
                {
                    turnEnds = new TurnEndReason.Completed(); // the boundary is logged but spends no step
                    return _agent.Inbox.HasPending;
                }

                session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(turn, step));
                _agent.SetStep(step);
                try
                {
                    var stepEnd = await RunStepAsync(turn, step, assembly, decision.Messages, ct).ConfigureAwait(false);
                    // max-tokens is sticky
                    if (stepEnd is not null && turnEnds is not TurnEndReason.MaxTokens) turnEnds = stepEnd;
                }
                finally
                {
                    session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(turn, step));
                }

                if (turnEnds is not null)
                {
                    if (_agent.Inbox.NextStep.Count == 0)
                    {
                        await _ctx.Events.SerialAsync("agent/turn-stopping", new TurnStoppingEvent(_agent, turn, ct), _agent, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (_agent.Inbox.NextStep.Count == 0) return _agent.Inbox.HasPending; // data decides
                    }
                }
                target = InboxTarget.NextStep;
            }
        }
        catch (OperationCanceledException)
        {
            turnEnds = new TurnEndReason.Aborted(_agent.CancelCause?.Kind ?? TurnEndAbortedCauses.User);
            throw;
        }
        catch (LlmException ex)
        {
            turnEnds = new TurnEndReason.Error(ex.Message, ex.Code);
            throw;
        }
        catch (Exception ex)
        {
            turnEnds = new TurnEndReason.Error(ex.Message, "UNKNOWN");
            throw;
        }
        finally
        {
            session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(turn, turnEnds ?? new TurnEndReason.Completed()));
        }
    }

    private int LastTurn()
    {
        var events = _agent.Session.Events;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Type == SessionEventTypes.TurnStart) return events[i].Data.GetProperty("turn").GetInt32();
        }
        return 0;
    }

    // ---- pre-step ----

    private async Task<(PreStepDecision Decision, PromptAssembly Assembly)> PreStepAsync(string target, int turn, int step, CancellationToken ct)
    {
        var claimed = _agent.Inbox.Claim(target);
        foreach (var message in claimed)
        {
            _ = _ctx.Events.EmitAsync("agent/inbox/claimed", new InboxMessageEvent(_agent, message, turn), _agent);
        }

        var assembly = _systemPrompt.Assemble(_agent, _agent.Session.Header.Cwd);

        var messages = new List<Message>(claimed);
        if (assembly.ContextSections.Count > 0)
        {
            var contextText = SystemPromptService.RenderContextSections(assembly);
            if (!string.Equals(contextText, _agent.RetainedContextSnapshot, StringComparison.Ordinal))
            {
                var body = contextText.Length > 0
                    ? "Current runtime context. This snapshot supersedes earlier runtime-context snapshots.\n\n" + contextText
                    : "Current runtime context: none. This clears any earlier runtime-context snapshot.";
                var snapshot = new Message(Ids.NewMessageId(), "user", [new TextBlock(body)], MessageSource.FromPlugin("system-prompt", "snapshot"));
                _agent.RetainedContextSnapshot = contextText;
                messages.Add(snapshot);
            }
        }

        var decision = await _ctx.Events.WaterfallAsync<PreStepEvent, List<Message>, PreStepDecision>(
            "agent/pre-step",
            new PreStepEvent(_agent, turn, step, messages, ct),
            messages,
            static ms => Task.FromResult(PreStepDecision.Enter(ms)),
            _agent,
            ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (decision, assembly);
    }

    // ---- step ----

    private async Task<TurnEndReason?> RunStepAsync(int turn, int step, PromptAssembly assembly, IReadOnlyList<Message> entered, CancellationToken ct)
    {
        var session = _agent.Session;
        foreach (var message in entered)
        {
            session.Append(SessionEventTypes.UserMessage, message, new Session.AppendOptions(
                SurfaceOp: new SurfaceOp.Append()));
        }

        var system = SystemPromptService.RenderPrompt(assembly);
        var attempts = 0;
        while (true)
        {
            var config = await BuildRequestAsync(turn, step, ct).ConfigureAwait(false);
            var options = new GenerateOptions
            {
                Provider = config.Provider,
                Model = config.Model,
                System = system.Length > 0 ? system : null,
                Messages = session.DeriveMessages(),
                Tools = assembly.ToolSchemas,
                Temperature = config.Temperature,
                MaxTokens = config.MaxTokens,
                Stop = config.Stop,
                ReasoningEffort = config.ReasoningEffort,
                SessionId = session.Id,
            };

            var assembler = new BlockAssembler();
            var chunkSeqs = new List<int>();
            await foreach (var chunk in _llm.Stream(options, ct, _agent).ConfigureAwait(false))
            {
                var appended = session.Append(SessionEventTypes.AssistantChunk, new SessionPayloads.AssistantChunk(turn, step, chunk));
                chunkSeqs.Add(appended.Seq);
                assembler.Push(chunk);
            }

            var finish = assembler.Finish ?? new FinishChunk(FinishReason.Error, new LlmFailure("stream ended without finish", LlmErrorCodes.EmptyResponse));
            if (finish.Reason is FinishReason.Error or FinishReason.Aborted)
            {
                if (finish.Reason == FinishReason.Aborted)
                {
                    if (assembler.InterruptedBlocks().Count > 0)
                    {
                        session.Append(SessionEventTypes.AssistantMessage,
                            new SessionPayloads.AssistantMessage(turn, step, Message.CreateAssistant(config.Provider, config.Model, assembler.InterruptedBlocks()), Interrupted: true),
                            new Session.AppendOptions(SourceEventSeqs: [.. chunkSeqs], SurfaceOp: new SurfaceOp.Append()));
                    }
                    ct.ThrowIfCancellationRequested();
                }
                var failure = finish.Failure ?? new LlmFailure("request failed", "UNKNOWN");
                var retry = await RequestErrorDecisionAsync(turn, step, failure, attempts, ct).ConfigureAwait(false);
                if (retry is not null)
                {
                    attempts++;
                    if (!retry.BackoffHandled) await BackoffAsync(attempts, ct).ConfigureAwait(false);
                    continue;
                }
                throw new LlmException(failure);
            }

            var message = assembler.BuildMessage(config.Provider, config.Model);
            session.Append(SessionEventTypes.AssistantMessage,
                new SessionPayloads.AssistantMessage(turn, step, message, assembler.Usage),
                new Session.AppendOptions(SourceEventSeqs: [.. chunkSeqs], SurfaceOp: new SurfaceOp.Append()));

            if (finish.Reason == FinishReason.MaxTokens)
            {
                return new TurnEndReason.MaxTokens();
            }
            var toolCalls = message.Content.OfType<ToolCallBlock>().ToList();
            if (toolCalls.Count == 0)
            {
                return new TurnEndReason.Completed();
            }
            var concluded = await ExecuteToolCallsAsync(toolCalls, turn, step, ct).ConfigureAwait(false);
            return concluded ? new TurnEndReason.Completed() : null;
        }
    }

    private async Task<LlmCallConfig> BuildRequestAsync(int turn, int step, CancellationToken ct)
    {
        var session = _agent.Session;
        var proposal = new LlmCallConfig
        {
            Provider = _agent.Options.Provider ?? throw new LlmException(LlmErrorCodes.NoAdapter, "no provider configured for this agent"),
            Model = _agent.Options.Model ?? throw new LlmException(LlmErrorCodes.NoAdapter, "no model configured for this agent"),
            MaxTokens = _agent.Options.MaxTokens,
            ReasoningEffort = _agent.Options.ReasoningEffort,
        };
        var config = await _ctx.Events.WaterfallAsync<RequestEvent, LlmCallConfig, LlmCallConfig>(
            "agent/request",
            new RequestEvent(_agent, turn, step, proposal, ct),
            proposal,
            static v => Task.FromResult(v),
            _agent,
            ct).ConfigureAwait(false);

        var latest = session.LatestRequestHeader();
        if (latest is null)
        {
            session.Append(SessionEventTypes.RequestHeader, new SessionPayloads.RequestHeaderPayload(config, "initial"));
        }
        else if (!latest.Header.ValueEquals(config))
        {
            session.Append(SessionEventTypes.RequestHeader, new SessionPayloads.RequestHeaderPayload(config, "change"));
        }
        if (latest is null || latest.Header.Provider != config.Provider || latest.Header.Model != config.Model)
        {
            session.Append(SessionEventTypes.RequestContext, new SessionPayloads.RequestContextPayload(config.Provider, config.Model, null));
        }
        return config;
    }

    private async Task<RequestErrorAction?> RequestErrorDecisionAsync(int turn, int step, LlmFailure failure, int attempts, CancellationToken ct)
    {
        return await _ctx.Events.WaterfallAsync<RequestErrorEvent, RequestErrorAction?, RequestErrorAction?>(
            "agent/request-error",
            new RequestErrorEvent(_agent, turn, step, failure, attempts, ct),
            null,
            _ => Task.FromResult(DefaultRetryPolicy(failure, attempts)),
            _agent,
            ct).ConfigureAwait(false);
    }

    private RequestErrorAction? DefaultRetryPolicy(LlmFailure failure, int attempts)
    {
        if (attempts >= _agent.RetryLimit) return null;
        return LlmErrorCodes.IsRetryable(failure.Code) ? RequestErrorAction.Retry() : null;
    }

    private static async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var delay = Math.Min(500 * Math.Pow(2, attempt - 1), 10_000);
        var jitter = delay * 0.1 * Random.Shared.NextDouble();
        await Task.Delay(TimeSpan.FromMilliseconds(delay + jitter), ct).ConfigureAwait(false);
    }

    // ---- tool scheduling ----

    private static JsonElement ParseArgs(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments.Length == 0 ? "{}" : arguments);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    /// <summary>Groups calls into barriers: a parallel head opens a maximal parallel run; exclusive calls run alone.</summary>
    private async Task<bool> ExecuteToolCallsAsync(IReadOnlyList<ToolCallBlock> calls, int turn, int step, CancellationToken ct)
    {
        var concluded = false;
        var index = 0;
        while (index < calls.Count)
        {
            var headMode = _tools.ExecutionMode(calls[index].Name, ParseArgs(calls[index].Arguments), _agent.ScopeKey);
            var groupEnd = index + 1;
            if (headMode == ToolRuntime.Mode.Parallel)
            {
                while (groupEnd < calls.Count
                       && _tools.ExecutionMode(calls[groupEnd].Name, ParseArgs(calls[groupEnd].Arguments), _agent.ScopeKey) == ToolRuntime.Mode.Parallel)
                {
                    groupEnd++;
                }
            }
            concluded |= await RunGroupAsync(calls, index, groupEnd, turn, step, ct).ConfigureAwait(false);
            index = groupEnd;
        }
        return concluded;
    }

    private sealed class Slot(ToolCallBlock call)
    {
        public readonly ToolCallBlock Call = call;
        public int CallSeq = -1;
        public ToolRuntime.PreparedCall? Prepared;
        public Task<ToolExecutionResult>? Dispatch;
        public ToolExecutionResult? Result;
        public bool ConcludeRequested;
        public bool Committed;
        public bool Started => Prepared is not null;

        public bool Settled => Prepared is { FinalResult: not null } || Result is not null || (Dispatch is { IsCompleted: true });
    }

    private async Task<bool> RunGroupAsync(IReadOnlyList<ToolCallBlock> calls, int start, int end, int turn, int step, CancellationToken ct)
    {
        var session = _agent.Session;
        var slots = new List<Slot>(end - start);
        for (var i = start; i < end; i++) slots.Add(new Slot(calls[i]));

        var nextToStart = 0;
        var committed = 0;
        var concluded = false;
        while (committed < slots.Count)
        {
            // Fill the pool: prepare (policy) awaited sequentially, dispatch (body) overlapping.
            while (nextToStart < slots.Count && slots.Count(s => s.Dispatch is not null && !s.Dispatch.IsCompleted) < MaxParallelToolCalls)
            {
                var slot = slots[nextToStart];
                var mode = _tools.ExecutionMode(slot.Call.Name, ParseArgs(slot.Call.Arguments), _agent.ScopeKey);
                if (nextToStart > start && mode == ToolRuntime.Mode.Exclusive)
                {
                    break; // reclassification barrier: drain, the caller re-groups the remainder
                }

                var callEvent = session.Append(SessionEventTypes.ToolCall, new SessionPayloads.ToolCall(turn, step, slot.Call.Id, slot.Call.Name, slot.Call.Arguments));
                slot.CallSeq = callEvent.Seq;

                var input = new ToolExecutionInput
                {
                    Name = slot.Call.Name,
                    Arguments = ParseArgs(slot.Call.Arguments),
                    CallId = slot.Call.Id,
                    Signal = ct,
                    Agent = _agent,
                    DeferContextAsync = async message =>
                    {
                        _agent.Inbox.Insert(message, InboxTarget.NextStep);
                        await Task.CompletedTask.ConfigureAwait(false);
                    },
                    ConcludeTurn = () => slot.ConcludeRequested = true,
                };
                slot.Prepared = await _tools.Prepare(input).ConfigureAwait(false);
                if (slot.Prepared.FinalResult is not null)
                {
                    slot.Result = slot.Prepared.FinalResult;
                }
                else
                {
                    slot.Dispatch = _tools.Dispatch(slot.Prepared, ct);
                }
                nextToStart++;
            }

            // Wait for any in-flight settle (or immediate commit when pool work is final-result-only).
            var inFlight = slots.Where(s => s.Dispatch is { IsCompleted: false }).ToList();
            if (inFlight.Count > 0)
            {
                await Task.WhenAny(inFlight.Select(s => s.Dispatch!)).ConfigureAwait(false);
            }

            // Commit strictly in model order across contiguous settled slots.
            while (committed < slots.Count && slots[committed].Settled)
            {
                var slot = slots[committed];
                var result = slot.Result ?? await slot.Dispatch!.ConfigureAwait(false);
                if (slot.ConcludeRequested && !result.IsError)
                {
                    result = result with { ConcludesTurn = true };
                }
                result = await _tools.Finalize(slot.Prepared!, result).ConfigureAwait(false);

                var resultMessage = Message.CreateToolResult(slot.Call.Id, result.Content, result.IsError);
                session.Append(SessionEventTypes.ToolResult,
                    new SessionPayloads.ToolResult(turn, step, resultMessage,
                        result.IsError ? new SessionPayloads.ToolErrorInfo(result.Error?.Info?.Name ?? "ToolError", result.Error?.Info?.Code ?? "TOOL_FAILED") : null,
                        result.Meta),
                    new Session.AppendOptions(SourceEventSeqs: [slot.CallSeq], SurfaceOp: new SurfaceOp.Append()));

                foreach (var context in result.AdditionalContexts)
                {
                    _agent.Inbox.Insert(context, InboxTarget.NextStep);
                }
                concluded |= result.ConcludesTurn;
                slot.Committed = true;
                committed++;
            }

            // No forward progress possible (pool idle but head unsettled shouldn't happen); guard against deadlock.
            if (committed < slots.Count && slots.Count(s => s.Dispatch is { IsCompleted: false }) == 0 && nextToStart >= slots.Count && !slots[committed].Settled)
            {
                throw new Kernel.HarnessException("SCHEDULER_STALL", "tool scheduler stalled");
            }
        }
        return concluded;
    }
}
