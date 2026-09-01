using System.Text.Json;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Tools;

public sealed record ToolExecutionInput
{
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
    public required string CallId { get; init; }
    public CancellationToken Signal { get; init; }
    public Agent.Agent? Agent { get; init; }
    public string? RootCallId { get; init; }
    public Func<Message, Task>? DeferContextAsync { get; init; }
    public Action? ConcludeTurn { get; init; }
}

public sealed record PreToolDecision
{
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string Ask = "ask";

    public required string Kind { get; init; }
    public string? Reason { get; init; }

    public static PreToolDecision Allowed() => new() { Kind = Allow };
    public static PreToolDecision Denied(string reason) => new() { Kind = Deny, Reason = reason };
    public static PreToolDecision Asked(string? reason) => new() { Kind = Ask, Reason = reason };
}

public sealed record PostToolDecision
{
    public const string AcceptKind = "accept";
    public const string BlockKind = "block";

    public required string Kind { get; init; }
    /// <summary>Accept: replace presentation content only.</summary>
    public IReadOnlyList<ContentBlock>? Content { get; init; }
    /// <summary>Accept: replace the canonical value (success results only); re-renders content.</summary>
    public JsonElement? Value { get; init; }
    /// <summary>Block: corrective feedback as content.</summary>
    public IReadOnlyList<ContentBlock>? Feedback { get; init; }
    public IReadOnlyList<Message>? AdditionalContexts { get; init; }

    public static PostToolDecision Accept() => new() { Kind = AcceptKind };
    public static PostToolDecision AcceptContent(IReadOnlyList<ContentBlock> content) => new() { Kind = AcceptKind, Content = content };
    public static PostToolDecision AcceptValue(JsonElement value) => new() { Kind = AcceptKind, Value = value };
    public static PostToolDecision Block(IReadOnlyList<ContentBlock> feedback) => new() { Kind = BlockKind, Feedback = feedback };
}

/// <summary>A deny-only monotonic guard: a returned string denies; ordering can never re-allow.</summary>
public delegate string? ToolGuard(ToolExecutionInput exec);

public sealed record ToolExecution(ToolExecutionInput Input, ToolDefinition Definition, object? ScopeKey);

public sealed record ToolDispatchExecution(ToolExecution Execution, CancellationToken Signal);

public sealed record ToolPostExecute(ToolExecution Execution, ToolExecutionResult Result);

/// <summary>ctx.tools — scoped registry plus the guarded execution pipeline.</summary>
public sealed class ToolRuntime
{
    public const string ServiceKey = "tools";

    public enum Mode { Parallel, Exclusive }

    private sealed class ToolLayer
    {
        public NamedEntries<ToolDefinition> Tools { get; } = new();
        public List<CompiledRestriction> Restrictions { get; } = [];
    }

    private sealed record CompiledRestriction(IReadOnlySet<string>? Allow, IReadOnlySet<string>? Deny)
    {
        public bool Admits(string name) => (Allow is null || Allow.Contains(name)) && (Deny is null || !Deny.Contains(name));
    }

    private readonly HarnessContext _ctx;
    private readonly ScopedLayers<ToolLayer> _layers = new();
    private readonly List<ToolGuard> _guards = [];
    private readonly SystemPrompt.SystemPromptService _systemPrompt;

    public ToolRuntime(HarnessContext ctx, SystemPrompt.SystemPromptService systemPrompt)
    {
        _ctx = ctx;
        _systemPrompt = systemPrompt;
    }

    public static ToolRuntime Mount(HarnessContext ctx, SystemPrompt.SystemPromptService systemPrompt)
    {
        var runtime = new ToolRuntime(ctx, systemPrompt);
        ctx.Provide(ServiceKey, runtime);
        systemPrompt.RegisterToolProvider(scope => runtime.Schemas(scope));
        return runtime;
    }

    /// <summary>
    /// Deployment timeout policy: per-call deadline for tools that do not declare their own
    /// TimeoutMs (dsh timeout-policy guard). Null disables the default.
    /// </summary>
    public long? DefaultToolTimeoutMs { get; set; }

    // ---- registry ----

    public IDisposable Register(ToolDefinition tool) => RegisterScoped(null, tool);

    /// <summary>Registers into one scope's layer (the agent's own ctx); scoped shadows global by name.</summary>
    public IDisposable RegisterScoped(object? scopeKey, ToolDefinition tool)
    {
        var layer = scopeKey is null ? _layers.Global : _layers.ForCreate(scopeKey);
        var undo = layer.Tools.Add(tool.Name, tool);
        _ = _ctx.Events.EmitAsync("tools/change", tool.Name);
        return Disposable.Of(() =>
        {
            undo.Dispose();
            if (scopeKey is not null) _layers.ReclaimIfEmpty(scopeKey, l => l.Tools.Items.Count == 0 && l.Restrictions.Count == 0);
            _ = _ctx.Events.EmitAsync("tools/change", tool.Name);
        });
    }

    public IDisposable Restrict(object scopeKey, IReadOnlySet<string>? allow = null, IReadOnlySet<string>? deny = null)
    {
        var layer = _layers.ForCreate(scopeKey);
        var restriction = new CompiledRestriction(allow, deny);
        layer.Restrictions.Add(restriction);
        return Disposable.Of(() => layer.Restrictions.Remove(restriction));
    }

    public IDisposable AddGuard(ToolGuard guard)
    {
        _guards.Add(guard);
        return Disposable.Of(() => _guards.Remove(guard));
    }

    public ToolDefinition? Get(string name, object? scopeKey = null) => View(scopeKey).GetValueOrDefault(name);

    /// <summary>Debug/diagnostic access to registered scope keys.</summary>
    public IReadOnlyCollection<object> ScopedKeys => _layers.ScopedKeys;

    public IReadOnlyList<ToolSchema> Schemas(object? scopeKey = null)
    {
        var view = View(scopeKey);
        return [.. view.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ToolSchema(kv.Value.Name, kv.Value.Description, kv.Value.Parameters.ToJson()))];
    }

    /// <summary>Fails closed: only an exact concurrency-safe classification runs parallel.</summary>
    public Mode ExecutionMode(string name, JsonElement args, object? scopeKey)
    {
        try
        {
            var tool = Get(name, scopeKey);
            return tool is not null && tool.IsConcurrencySafe(args) ? Mode.Parallel : Mode.Exclusive;
        }
        catch
        {
            return Mode.Exclusive;
        }
    }

    private Dictionary<string, ToolDefinition> View(object? scopeKey)
    {
        var result = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
        foreach (var (name, tool) in _layers.Global.Tools.Items) result[name] = tool;

        var chain = new List<ToolLayer>();
        if (scopeKey is not null)
        {
            chain.AddRange(_layers.ChainLayers(scopeKey, key => _ctx.ScopeParentOf(key)));
            // farthest ancestor overlays first, nearest last (nearest shadows)
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                foreach (var (name, tool) in chain[i].Tools.Items) result[name] = tool;
            }
        }

        // Restrictions: every layer on the chain must admit the name.
        var restricted = new List<string>();
        foreach (var (name, _) in result)
        {
            var admitted = _layers.Global.Restrictions.All(r => r.Admits(name))
                && chain.All(layer => layer.Restrictions.All(r => r.Admits(name)));
            if (!admitted) restricted.Add(name);
        }
        foreach (var name in restricted) result.Remove(name);

        // The scope's own registrations ride on top, exempt from restrictions.
        if (scopeKey is not null)
        {
            var own = _layers.Peek(scopeKey);
            if (own is not null)
            {
                foreach (var (name, tool) in own.Tools.Items) result[name] = tool;
            }
        }
        return result;
    }

    // ---- pipeline ----

    /// <summary>A prepared call: policy ran (tool/call logged by the scheduler); FinalResult set when the body is skipped.</summary>
    public sealed record PreparedCall(ToolExecution Execution, ToolExecutionResult? FinalResult)
    {
        public bool Ready => FinalResult is null;
    }

    /// <summary>
    /// Single-call convenience over the staged pipeline: prepare → dispatch → finalize.
    /// The agent loop uses the stages directly so bodies overlap while policy stays ordered.
    /// </summary>
    public async Task<ToolExecutionResult> Execute(ToolExecutionInput input)
    {
        var prepared = await Prepare(input).ConfigureAwait(false);
        var dispatched = await Dispatch(prepared, input.Signal).ConfigureAwait(false);
        return await Finalize(prepared, dispatched).ConfigureAwait(false);
    }

    /// <summary>Stage 1–3: pre-execute waterfall → approval seam → monotonic guards. Fails closed.</summary>
    public async Task<PreparedCall> Prepare(ToolExecutionInput input)
    {
        var definition = Get(input.Name, input.Agent?.ScopeKey);
        if (definition is null)
        {
            return new PreparedCall(
                new ToolExecution(input, new UnresolvedToolDefinition(input.Name), input.Agent?.ScopeKey),
                Error(input, new ToolFailure($"Error: tool '{input.Name}' is not available", new ToolErrorInfo("ToolNotFoundError", ToolErrorCodes.UnknownTool))));
        }
        var execution = new ToolExecution(input, definition, input.Agent?.ScopeKey);

        // Stage 1: pre-execute waterfall.
        var decision = await _ctx.Events.WaterfallAsync<ToolExecution, PreToolDecision, PreToolDecision>(
            "tools/pre-execute", execution, PreToolDecision.Allowed(), static v => Task.FromResult(v), input.Agent?.ScopeKey, input.Signal).ConfigureAwait(false);

        // Stage 2: approval seam on ask; everything fails closed.
        if (decision.Kind == PreToolDecision.Ask)
        {
            var approval = _ctx.TryGet<ApprovalService>(ApprovalService.ServiceKey);
            if (approval is null)
            {
                decision = PreToolDecision.Denied(decision.Reason is null
                    ? $"tool '{input.Name}' requires approval but no approval service is mounted"
                    : $"approval required: {decision.Reason}");
            }
            else if (input.Agent is null)
            {
                decision = PreToolDecision.Denied("no agent to route the approval through");
            }
            else
            {
                var outcome = await approval.RequestAsync(new ApprovalRequest(input.Agent, input.Name, input.CallId, decision.Reason), input.Signal).ConfigureAwait(false);
                decision = outcome switch
                {
                    ApprovalOutcome.AllowedOnce => PreToolDecision.Allowed(),
                    ApprovalOutcome.Cancelled => PreToolDecision.Denied("the approval request was cancelled"),
                    ApprovalOutcome.Unavailable => PreToolDecision.Denied("nobody could answer the approval request"),
                    _ => PreToolDecision.Denied("the user rejected this tool call"),
                };
            }
        }

        if (decision.Kind != PreToolDecision.Allow)
        {
            return new PreparedCall(execution, Error(input, new ToolFailure($"Error: {decision.Reason ?? "denied"}", new ToolErrorInfo("ToolDeniedError", ToolErrorCodes.Denied))));
        }

        // Stage 3: monotonic guards (deny-only).
        foreach (var guard in _guards)
        {
            var denial = guard(input);
            if (denial is not null)
            {
                return new PreparedCall(execution, Error(input, new ToolFailure($"Error: {denial}", new ToolErrorInfo("ToolGuardError", ToolErrorCodes.Denied))));
            }
        }

        if (input.Signal.IsCancellationRequested)
        {
            return new PreparedCall(execution, Error(input, new ToolFailure("Error: cancelled before dispatch", new ToolErrorInfo("ToolAbortedError", ToolErrorCodes.AbortedBeforeDispatch))));
        }

        return new PreparedCall(execution, null);
    }

    /// <summary>Stage 4: the execute waterfall around dispatch (timeout + body live in the terminal).</summary>
    public Task<ToolExecutionResult> Dispatch(PreparedCall prepared, CancellationToken signal)
    {
        if (!prepared.Ready) return Task.FromResult(prepared.FinalResult!);
        var input = prepared.Execution.Input;
        return _ctx.Events.WaterfallAsync<ToolDispatchExecution, ToolExecutionResult, ToolExecutionResult>(
            "tools/execute",
            new ToolDispatchExecution(prepared.Execution, signal),
            null!,
            _ => DispatchAsync(prepared.Execution, signal),
            input.Agent?.ScopeKey,
            signal);
    }

    /// <summary>Stage 5–6: post-execute waterfall, then the tools/result observation. Denials run post; aborts skip it.</summary>
    public async Task<ToolExecutionResult> Finalize(PreparedCall prepared, ToolExecutionResult dispatched)
    {
        if (!prepared.Ready)
        {
            var bypass = dispatched.Error?.Info?.Code == ToolErrorCodes.AbortedBeforeDispatch;
            if (bypass)
            {
                await _ctx.Events.EmitAsync("tools/result", new ToolPostExecute(prepared.Execution, dispatched), prepared.Execution.Input.Agent?.ScopeKey).ConfigureAwait(false);
                return dispatched;
            }
        }
        return await PostExecuteAsync(prepared.Execution, dispatched).ConfigureAwait(false);
    }

    private async Task<ToolExecutionResult> DispatchAsync(ToolExecution execution, CancellationToken signal)
    {
        var input = execution.Input;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(signal);
        if ((execution.Definition.TimeoutMs ?? DefaultToolTimeoutMs) is { } timeoutMs)
        {
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        }
        var exec = new ToolRunContext
        {
            Args = input.Arguments,
            Signal = timeoutCts.Token,
            Agent = input.Agent,
            CallId = input.CallId,
            DeferContextAsync = input.DeferContextAsync ?? (_ => Task.CompletedTask),
            ConcludeTurn = input.ConcludeTurn ?? (() => { }),
        };

        JsonElement value;
        try
        {
            value = await execution.Definition.Execute(input.Arguments, exec).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !signal.IsCancellationRequested)
        {
            return Error(input, new ToolFailure("Error: the tool timed out", new ToolErrorInfo("ToolTimeoutError", ToolErrorCodes.ToolTimeout)));
        }
        catch (OperationCanceledException)
        {
            return Error(input, new ToolFailure("Error: cancelled", new ToolErrorInfo("ToolAbortedError", ToolErrorCodes.Aborted)));
        }
        catch (ToolException ex)
        {
            return Error(input, new ToolFailure($"Error: {ex.Message}", new ToolErrorInfo("ToolException", ex.Code)));
        }
        catch (Exception ex)
        {
            return Error(input, new ToolFailure($"Error: {ex.Message}", new ToolErrorInfo("ToolError", "TOOL_FAILED")));
        }

        // Output contract: value validated against the schema, content derived through render.
        var violation = JsonSchema.Validate(value, execution.Definition.Output);
        if (violation is not null)
        {
            return Error(input, new ToolFailure($"Error: tool output violated its contract: {violation}",
                new ToolErrorInfo("ToolOutputError", ToolErrorCodes.InvalidToolOutput)));
        }
        IReadOnlyList<ContentBlock> content;
        try
        {
            content = SafeRender(execution.Definition, input.Arguments, value);
        }
        catch (Exception ex)
        {
            return Error(input, new ToolFailure($"Error: tool output rendering failed: {ex.Message}",
                new ToolErrorInfo("ToolOutputError", ToolErrorCodes.InvalidToolOutput)));
        }
        return new ToolExecutionResult
        {
            IsError = false,
            Value = value,
            Content = content,
            Meta = SafeMeta(execution.Definition, input.Arguments, value),
        };
    }

    private async Task<ToolExecutionResult> PostExecuteAsync(ToolExecution execution, ToolExecutionResult result)
    {
        var decision = await _ctx.Events.WaterfallAsync<ToolPostExecute, PostToolDecision, PostToolDecision>(
            "tools/post-execute",
            new ToolPostExecute(execution, result),
            PostToolDecision.Accept(),
            static v => Task.FromResult(v),
            execution.Input.Agent?.ScopeKey,
            execution.Input.Signal).ConfigureAwait(false);

        var final = result;
        var extraContexts = new List<Message>(result.AdditionalContexts);
        if (decision.AdditionalContexts is not null) extraContexts.AddRange(decision.AdditionalContexts);

        if (decision.Kind == PostToolDecision.BlockKind)
        {
            var feedback = decision.Feedback ?? [new TextBlock("Error: blocked by policy")];
            final = new ToolExecutionResult
            {
                IsError = true,
                Content = feedback,
                Error = new ToolFailure(FeedbackMessage(feedback), new ToolErrorInfo("ToolBlockedError", "TOOL_BLOCKED")),
                AdditionalContexts = extraContexts,
            };
        }
        else if (decision.Value is { } replacementValue && !result.IsError)
        {
            var violation = JsonSchema.Validate(replacementValue, execution.Definition.Output);
            if (violation is not null)
            {
                final = Error(execution.Input, new ToolFailure($"Error: post-execute replacement violated the output contract: {violation}",
                    new ToolErrorInfo("ToolOutputError", ToolErrorCodes.InvalidToolOutput)));
            }
            else
            {
                final = new ToolExecutionResult
                {
                    IsError = false,
                    Value = replacementValue,
                    Content = SafeRender(execution.Definition, execution.Input.Arguments, replacementValue),
                    Meta = SafeMeta(execution.Definition, execution.Input.Arguments, replacementValue),
                    AdditionalContexts = extraContexts,
                };
            }
        }
        else if (decision.Content is { } replacementContent)
        {
            final = result with { Content = replacementContent, AdditionalContexts = extraContexts };
        }
        else if (extraContexts.Count != result.AdditionalContexts.Count)
        {
            final = result with { AdditionalContexts = extraContexts };
        }

        await _ctx.Events.EmitAsync("tools/result", new ToolPostExecute(execution, final), execution.Input.Agent?.ScopeKey).ConfigureAwait(false);
        return final;
    }

    private static string FeedbackMessage(IReadOnlyList<ContentBlock> feedback)
        => string.Join(" ", feedback.OfType<TextBlock>().Select(b => b.Text)) is { Length: > 0 } text ? text : "blocked";

    private static IReadOnlyList<ContentBlock> SafeRender(ToolDefinition definition, JsonElement args, JsonElement value)
    {
        var content = definition.Render(args, value);
        return content.Count > 0 ? content : [new TextBlock("(no output)")];
    }

    private static JsonElement? SafeMeta(ToolDefinition definition, JsonElement args, JsonElement value)
    {
        try { return definition.PresentationMeta(args, value); }
        catch { return null; }
    }

    private static ToolExecutionResult Error(ToolExecutionInput input, ToolFailure failure) => new()
    {
        IsError = true,
        Content = [new TextBlock(failure.Message)],
        Error = failure,
    };

    /// <summary>Placeholder definition for unresolved calls; its body never runs.</summary>
    private sealed class UnresolvedToolDefinition(string name) : ToolDefinition
    {
        public override string Name { get; } = name;
        public override string Description => "(not available)";
        public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();
        public override JsonSchema.Schema Output { get; } = JsonSchema.Object();
        public override Task<JsonElement> Execute(JsonElement args, ToolRunContext exec)
            => throw new ToolException(ToolErrorCodes.UnknownTool, $"tool '{Name}' is not available");
        public override IReadOnlyList<ContentBlock> Render(JsonElement args, JsonElement value) => [];
    }
}
