using Blazorly.Harness.Llm;
using Blazorly.Harness.Core.Sessions;

namespace Blazorly.Harness.Core.Agent;

public static class InboxTarget
{
    public const string NextTurn = "next-turn";
    public const string NextStep = "next-step";
}

/// <summary>
/// The agent inbox: two ordered pending lists (next-turn queued prompts, next-step
/// steering/context). Every mutation first appends a durable agent/inbox/spliced event,
/// then mutates in-memory state; the class itself replays those events on construction.
/// </summary>
public sealed class Inbox
{
    private readonly Session _session;
    private readonly Action<SessionEvent> _publish;
    private readonly List<Message> _nextTurn = [];
    private readonly List<Message> _nextStep = [];

    public Inbox(Session session, Action<SessionEvent> publish, IEnumerable<SessionEvent>? seed = null, int seedLength = 0)
    {
        _session = session;
        _publish = publish;
        if (seed is not null)
        {
            foreach (var e in seed)
            {
                if (e.Seq < seedLength) continue;
                if (e.Type != SessionEventTypes.AgentInboxSpliced) continue;
                Apply(SessionEventRead.InboxSplicedOf(e));
            }
        }
    }

    public IReadOnlyList<Message> NextTurn => _nextTurn;
    public IReadOnlyList<Message> NextStep => _nextStep;
    public bool HasPending => _nextTurn.Count > 0 || _nextStep.Count > 0;

    private void Apply(SessionPayloads.InboxSpliced splice)
    {
        var list = ListOf(splice.Target);
        var removeCount = Math.Min(splice.RemovedCount ?? 0, list.Count);
        list.RemoveRange(splice.Start, removeCount);
        list.InsertRange(splice.Start, splice.Inserted);
    }

    private List<Message> ListOf(string target) => target == InboxTarget.NextTurn ? _nextTurn : _nextStep;

    private SessionEvent Record(string target, int start, int? removedCount, IReadOnlyList<Message> inserted, string? outcome = null)
    {
        var payload = new SessionPayloads.InboxSpliced(target, start, removedCount, inserted, outcome);
        var e = _session.Append(SessionEventTypes.AgentInboxSpliced, payload);
        _publish(e);
        return e;
    }

    /// <summary>Appends at the tail; a duplicate pending id across both lists throws.</summary>
    public void Insert(Message message, string target)
    {
        AssertNotPending(message.Id);
        var list = ListOf(target);
        Record(target, list.Count, 0, [message]);
        list.Add(message);
    }

    public void Remove(Message message)
    {
        var list = _nextTurn.Contains(message) ? _nextTurn : _nextStep.Contains(message) ? _nextStep : null;
        if (list is null) return;
        var index = list.IndexOf(message);
        var target = ReferenceEquals(list, _nextTurn) ? InboxTarget.NextTurn : InboxTarget.NextStep;
        Record(target, index, 1, [], "canceled");
        list.RemoveAt(index);
    }

    /// <summary>Replaces a queued message's text in place (old discarded, new inserted at its slot).</summary>
    public void Replace(Message message, string newText)
    {
        var isNextTurn = _nextTurn.Contains(message);
        var list = isNextTurn ? _nextTurn : _nextStep.Contains(message) ? _nextStep : null;
        if (list is null) return;
        var target = isNextTurn ? InboxTarget.NextTurn : InboxTarget.NextStep;
        var index = list.IndexOf(message);
        var replacement = new Message(Ids.NewMessageId(), message.Role, [new TextBlock(newText)], message.Source);
        Record(target, index, 1, [replacement], "canceled");
        list[index] = replacement;
    }

    /// <summary>Promotes a queued followup to steering (consumed at the next step boundary).</summary>
    public void PromoteToNextStep(Message message)
    {
        var index = _nextTurn.IndexOf(message);
        if (index < 0) return;
        Record(InboxTarget.NextTurn, index, 1, [], "canceled");
        _nextTurn.RemoveAt(index);
        Record(InboxTarget.NextStep, _nextStep.Count, 0, [message]);
        _nextStep.Add(message);
    }

    public void Clear()
    {
        if (_nextStep.Count > 0)
        {
            Record(InboxTarget.NextStep, 0, _nextStep.Count, [], "canceled");
            _nextStep.Clear();
        }
        if (_nextTurn.Count > 0)
        {
            Record(InboxTarget.NextTurn, 0, _nextTurn.Count, [], "canceled");
            _nextTurn.Clear();
        }
    }

    /// <summary>
    /// The step-boundary operation: claims all next-step items plus, at a turn boundary,
    /// exactly one next-turn head. Recorded as pure deletions.
    /// </summary>
    public List<Message> Claim(string target)
    {
        var claimed = new List<Message>();
        if (_nextStep.Count > 0)
        {
            Record(InboxTarget.NextStep, 0, _nextStep.Count, []);
            claimed.AddRange(_nextStep);
            _nextStep.Clear();
        }
        if (target == InboxTarget.NextTurn && _nextTurn.Count > 0)
        {
            Record(InboxTarget.NextTurn, 0, 1, []);
            claimed.Add(_nextTurn[0]);
            _nextTurn.RemoveAt(0);
        }
        return claimed;
    }

    private void AssertNotPending(string messageId)
    {
        if (_nextTurn.Any(m => m.Id == messageId) || _nextStep.Any(m => m.Id == messageId))
            throw new SessionValidationException("INBOX_DUP", $"message '{messageId}' is already pending");
    }
}
