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
}

/// <summary>
/// Loop hygiene guard: when one agent repeats the exact same tool call (name + raw arguments)
/// Threshold times in a row, an advisory reminder is inserted into the next-step inbox — once
/// per extension of the streak. The call itself is never denied (dsh repeat-tool-reminder).
/// </summary>
public sealed class RepeatCallGuard
{
    private readonly ConcurrentDictionary<object, (string Last, int Streak, int Reminded)> _streaks = new();

    public RepeatCallGuard(RepeatGuardOptions? options = null) => Options = options ?? new RepeatGuardOptions();

    public RepeatGuardOptions Options { get; set; }

    public static RepeatCallGuard Mount(HarnessContext ctx, RepeatGuardOptions? options = null)
    {
        var guard = new RepeatCallGuard(options);
        _ = ctx.Events.On<ToolPostExecute>("tools/result", (payload, _) =>
        {
            guard.Observe(payload.Execution.Input);
            return Task.CompletedTask;
        });
        return guard;
    }

    public void Observe(ToolExecutionInput input)
    {
        if (input.Agent is null || input.Signal.IsCancellationRequested) return;
        var key = input.Agent.ScopeKey ?? (object)"__global__";
        var signature = $"{input.Name}\n{RawArgs(input.Arguments)}";
        var (_, _, reminded) = _streaks.AddOrUpdate(key,
            _ => (signature, 1, 0),
            (_, current) => current.Last == signature
                ? (signature, current.Streak + 1, current.Reminded)
                : (signature, 1, 0));

        var (_, streak, _) = _streaks[key];
        if (streak >= Options.Threshold && reminded < streak)
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

    private static string RawArgs(JsonElement arguments)
    {
        try { return arguments.GetRawText(); }
        catch { return "?"; }
    }
}
