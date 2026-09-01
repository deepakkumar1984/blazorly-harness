using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Sessions;

/// <summary>
/// Maintains the ordered list of surface seqs by replaying each surface event's SurfaceOp.
/// Model history is derived from this surface, not the raw log.
/// </summary>
public sealed class SurfaceManager
{
    private List<int> _surface = [];
    private readonly Dictionary<int, Message?> _derived = new();
    private int _replaceGeneration;

    public IReadOnlyList<int> Surface => _surface;
    public int ReplaceGeneration => _replaceGeneration;

    public void ValidateNext(SessionEvent upcoming, Func<int, SessionEvent> at)
    {
        var isSurfaceType = SessionEventTypes.SurfaceTypes.Contains(upcoming.Type);
        if (!isSurfaceType)
        {
            if (upcoming.SurfaceOp is not null || upcoming.SourceEventSeqs is not null)
                throw new SessionValidationException("SURFACE_NOT_ALLOWED", $"event '{upcoming.Type}' is not a surface type and may not carry surface metadata");
            return;
        }
        if (upcoming.SurfaceOp is null)
            throw new SessionValidationException("SURFACE_OP_REQUIRED", $"surface event '{upcoming.Type}' requires a surfaceOp");

        switch (upcoming.SurfaceOp)
        {
            case SurfaceOp.Append:
                // Only a tool/result must cite its tool/call; a user/message may lack a
                // derivation source (runtime-context snapshots), as may an empty assistant stream.
                break;
            case SurfaceOp.Replace replace:
            {
                if (replace.Start > replace.End || replace.Start < 0 || replace.End >= _surface.Count)
                    throw new SessionValidationException("REPLACE_RANGE_INVALID", "replace range does not exist on the surface");
                var shadowed = new HashSet<int>(_surface.GetRange(replace.Start, replace.End - replace.Start + 1));
                var cited = upcoming.SourceEventSeqs ?? [];
                if (cited.Length == 0 || !cited.All(seqs => shadowed.Contains(seqs) || seqs == upcoming.Seq - 1))
                    throw new SessionValidationException("REPLACE_SEQS_MISMATCH", "sourceEventSeqs must cite the shadowed surface nodes");
                if (upcoming.Type == SessionEventTypes.ToolResult)
                {
                    var within = _surface.GetRange(replace.Start, replace.End - replace.Start + 1);
                    if (within.Count != 1 || at(within[0]).Type != SessionEventTypes.ToolResult)
                        throw new SessionValidationException("TOOL_RESULT_REPLACE", "a tool/result replace must rewrite exactly one current tool/result");
                }
                break;
            }
        }

        if (upcoming.SourceEventSeqs is { Length: > 0 })
        {
            var seen = new HashSet<int>();
            foreach (var seq in upcoming.SourceEventSeqs)
            {
                if (seq >= upcoming.Seq) throw new SessionValidationException("SOURCE_SEQ_FUTURE", "sourceEventSeqs must be earlier than the event");
                if (!seen.Add(seq)) throw new SessionValidationException("SOURCE_SEQ_DUP", "sourceEventSeqs must be unique");
            }
        }
    }

    public void Apply(SessionEvent e)
    {
        if (e.SurfaceOp is null) return;
        switch (e.SurfaceOp)
        {
            case SurfaceOp.Append:
                _surface.Add(e.Seq);
                break;
            case SurfaceOp.Replace replace:
                _surface.RemoveRange(replace.Start, replace.End - replace.Start + 1);
                _surface.Insert(replace.Start, e.Seq);
                _replaceGeneration++;
                _derived.Clear();
                break;
        }
    }

    /// <summary>Projects model history from the ordered surface. Empty assistant content never enters the transcript.</summary>
    public IReadOnlyList<Message> DeriveMessages(Func<int, SessionEvent> at)
    {
        var result = new List<Message>(_surface.Count);
        foreach (var seq in _surface)
        {
            if (!_derived.TryGetValue(seq, out var message))
            {
                var e = at(seq);
                message = e.Type switch
                {
                    SessionEventTypes.UserMessage => SessionEventRead.MessageOf(e),
                    SessionEventTypes.AssistantMessage => SessionEventRead.AssistantMessageOf(e).Message.Content.Count == 0
                        ? null
                        : SessionEventRead.AssistantMessageOf(e).Message,
                    SessionEventTypes.ToolResult => SessionEventRead.ToolResultOf(e).Message,
                    _ => null,
                };
                _derived[seq] = message;
            }
            if (message is not null) result.Add(message);
        }
        return result;
    }

    public static SurfaceManager Replay(IEnumerable<SessionEvent> events)
    {
        var manager = new SurfaceManager();
        foreach (var e in events.Where(e => e.SurfaceOp is not null))
            manager.Apply(e);
        return manager;
    }
}
