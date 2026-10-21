using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Guards;

public sealed record RepeatGuardOptions
{
    /// <summary>Consecutive identical calls after which the advisory reminder is injected.</summary>
    public int Threshold { get; set; } = 3;
    public int MaxReminderChars { get; set; } = 200;
    /// <summary>Consecutive validation failures after which the turn ends instead of looping:
    /// same input fails schema validation identically every time, so further steps cannot
    /// make progress. One grace step past the advisory; successes reset the count.</summary>
    public int StopAfterValidationFailures { get; set; } = 4;
}

/// <summary>
/// Loop hygiene guard: when one agent repeats the exact same tool call (name + raw arguments)
/// Threshold times in a row, an advisory reminder is inserted into the next-step inbox — once
/// per extension of the streak. The call itself is never denied (dsh repeat-tool-reminder).
/// The exception is deterministic failure: consecutive schema-validation failures (same or
/// merely similar arguments — validation is input-pure) end the turn at post-step, or the
/// agent burns a full model step per identical error forever.
/// </summary>
public sealed class RepeatCallGuard
{
    private readonly ConcurrentDictionary<object, (string Last, int Streak, int Reminded)> _streaks = new();
    private readonly ConcurrentDictionary<object, (int Turn, int Streak, int Pending)> _validation = new();

    public RepeatCallGuard(RepeatGuardOptions? options = null) => Options = options ?? new RepeatGuardOptions();

    public RepeatGuardOptions Options { get; set; }

    public static RepeatCallGuard Mount(HarnessContext ctx, RepeatGuardOptions? options = null)
    {
        var guard = new RepeatCallGuard(options);
        _ = ctx.Events.On<ToolPostExecute>("tools/result", (payload, _) =>
        {
            guard.Observe(payload.Execution.Input, payload.Result);
            return Task.CompletedTask;
        });
        _ = ctx.OnWaterfall<PostStepEvent, PostStepDecision, PostStepDecision>("agent/post-step",
            (payload, value, next, _) =>
            {
                if (guard.ShouldStop(payload.Agent, payload.Turn)) return Task.FromResult(PostStepDecision.Stop());
                return next(value);
            });
        return guard;
    }

    public void Observe(ToolExecutionInput input, ToolExecutionResult? result = null)
    {
        if (input.Agent is null || input.Signal.IsCancellationRequested) return;
        var key = input.Agent.ScopeKey ?? (object)"__global__";
        var signature = $"{input.Name}\n{RawArgs(input.Arguments)}";
        var (_, _, reminded) = _streaks.AddOrUpdate(key,
            _ => (signature, 1, 0),
            (_, current) => current.Last == signature
                ? (signature, current.Streak + 1, current.Reminded)
                : (signature, 1, 0));

        // Validation outcomes are deterministic per input: fold them into this step's pending
        // count first (post-step folds per turn). Anything else resets the count.
        if (result?.Error?.Info?.Code is ToolErrorCodes.InvalidArgs or ToolErrorCodes.UnknownTool)
            _validation.AddOrUpdate(key, _ => (0, 0, 1),
                (_, current) => (current.Turn, current.Streak, current.Pending + 1));
        else
            _validation.TryRemove(key, out _);

        var (_, streak, _) = _streaks[key];
        // No advisory on the step that trips the stop: the reminder would sit unread in the
        // inbox, read as pending work, and resurrect the turn the stop just ended.
        var stopping = _validation.TryGetValue(key, out var pending)
            && pending.Streak + pending.Pending >= Options.StopAfterValidationFailures;
        if (streak >= Options.Threshold && reminded < streak && !stopping)
        {
            _streaks[key] = (signature, streak, streak); // remind once per streak extension
            var argsPreview = signature[(signature.IndexOf('\n') + 1)..];
            if (argsPreview.Length > Options.MaxReminderChars) argsPreview = argsPreview[..Options.MaxReminderChars] + "…";
            input.Agent.Inbox.Insert(Message.CreateUserText(
                $"[reminder] You have called '{input.Name}' with identical arguments {streak} times in a row "
                + $"({argsPreview}). The result will not change: adjust the arguments, pick a different tool, or move on."),
                InboxTarget.NextStep);
        }
    }

    /// <summary>Folds this step's pending validation failures into the turn's streak and ends
    /// the turn once it reaches the stop threshold. Streaks never cross turns: a fresh turn
    /// gets a fresh budget, and stopping clears the entry.</summary>
    public bool ShouldStop(Agent.Agent? agent, int turn)
    {
        if (agent is null) return false;
        var key = agent.ScopeKey ?? (object)"__global__";
        if (!_validation.TryGetValue(key, out var current)) return false;
        var streak = current.Turn == turn ? current.Streak + current.Pending : current.Pending;
        if (streak < Options.StopAfterValidationFailures)
        {
            _validation[key] = (turn, streak, 0);
            return false;
        }
        _validation.TryRemove(key, out _);
        return true;
    }

    private static string RawArgs(JsonElement arguments)
    {
        try { return arguments.GetRawText(); }
        catch { return "?"; }
    }
}
