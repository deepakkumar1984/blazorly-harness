using System.Collections.Concurrent;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Sessions;

namespace Blazorly.Harness.Web.Services;

/// <summary>
/// Fans durable session events out to UI subscribers (Blazor circuits and WebSocket
/// clients). Subscribers filter by session id themselves.
/// </summary>
public sealed class UiEventBroker
{
    public sealed record Frame(string SessionId, SessionEvent Event);

    private readonly object _gate = new();
    private readonly List<Subscriber> _subscribers = [];

    private sealed class Subscriber(Func<Frame, Task> deliver)
    {
        public readonly Func<Frame, Task> Deliver = deliver;
        public bool Dead;
    }

    public IDisposable Subscribe(Func<Frame, Task> deliver)
    {
        var subscriber = new Subscriber(deliver);
        lock (_gate) _subscribers.Add(subscriber);
        return new ActionDisposable(() =>
        {
            lock (_gate) _subscribers.Remove(subscriber);
        });
    }

    /// <summary>Delivers to every live subscriber; failures mark the subscriber dead and are contained.</summary>
    public Task PublishAsync(Frame frame)
    {
        List<Subscriber> snapshot;
        lock (_gate)
        {
            snapshot = [.. _subscribers.Where(s => !s.Dead)];
        }
        if (snapshot.Count == 0) return Task.CompletedTask;
        var tasks = snapshot.Select(subscriber => Task.Run(async () =>
        {
            try { await subscriber.Deliver(frame).ConfigureAwait(false); }
            catch { subscriber.Dead = true; }
        })).ToList();
        return Task.WhenAll(tasks);
    }
}

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

/// <summary>A pending human interaction (approval or question) surfaced to the UI.</summary>
public sealed record PendingInteraction(
    string Id,
    string SessionId,
    string Kind, // "approval" | "question"
    string? ToolName,
    string? Reason,
    IReadOnlyList<AskQuestion>? Questions,
    TaskCompletionSource<string> Completion);

/// <summary>
/// Bridges the approval and user-questions seams to the UI: asks park here with a
/// TaskCompletionSource until a front end answers or the request cancels.
/// </summary>
public sealed class UiInteractions
{
    private readonly ConcurrentDictionary<string, PendingInteraction> _pending = new(StringComparer.Ordinal);
    private readonly UiEventBroker _broker;
    private int _counter;

    public UiInteractions(UiEventBroker broker) => _broker = broker;

    public IReadOnlyList<PendingInteraction> Pending => [.. _pending.Values];

    public void Mount(HarnessBootstrapper harness)
    {
        harness.Approval.SetAnswerer(async (request, ct) =>
        {
            var interaction = Park(request.Agent.Id, "approval", request.ToolName, request.Reason, null);
            using var registration = ct.Register(() => interaction.Completion.TrySetResult("cancelled"));
            var answer = await interaction.Completion.Task.ConfigureAwait(false);
            return answer switch
            {
                "allowed" => ApprovalOutcome.AllowedOnce,
                "rejected" => ApprovalOutcome.Rejected,
                _ => ApprovalOutcome.Cancelled,
            };
        });

        harness.UserQuestions.SetProvider(async (questions, ct) =>
        {
            var interaction = Park("(global)", "question", null, null, questions);
            using var registration = ct.Register(() => interaction.Completion.TrySetResult("cancelled"));
            var answers = await interaction.Completion.Task.ConfigureAwait(false);
            if (answers is null or "cancelled")
            {
                throw new OperationCanceledException("the question was cancelled");
            }
            // answers arrive as one concatenated payload "id=text\u0001id=text…"
            return [.. answers.Split('\u0001', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .Select(kv => new AskAnswer(kv[0], kv[1]))];
        });
    }

    private PendingInteraction Park(string sessionId, string kind, string? toolName, string? reason, IReadOnlyList<AskQuestion>? questions)
    {
        var id = $"ask_{Interlocked.Increment(ref _counter)}";
        var interaction = new PendingInteraction(
            id, sessionId, kind, toolName, reason, questions,
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[id] = interaction;
        _ = _broker.PublishAsync(new UiEventBroker.Frame(sessionId,
            new SessionEvent { Type = $"ui/{kind}-requested", Seq = -1, Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Data = System.Text.Json.JsonSerializer.SerializeToElement(new { id }) }));
        return interaction;
    }

    public bool TryAnswer(string id, string answer)
    {
        if (!_pending.TryRemove(id, out var interaction)) return false;
        var resolved = interaction.Completion.TrySetResult(answer);
        _ = _broker.PublishAsync(new UiEventBroker.Frame(interaction.SessionId,
            new SessionEvent { Type = $"ui/{interaction.Kind}-resolved", Seq = -1, Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Data = System.Text.Json.JsonSerializer.SerializeToElement(new { id }) }));
        return resolved;
    }
}
