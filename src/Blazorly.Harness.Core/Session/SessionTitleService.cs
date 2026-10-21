using System.Collections.Concurrent;
using System.Text;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Agent = Blazorly.Harness.Core.Agent.Agent;

namespace Blazorly.Harness.Core.Sessions;

/// <summary>
/// Generates a session title after the first completed turn: an LLM call over the first user
/// prompt (purpose "session-title"), falling back to a trimmed copy of that prompt. Runs only
/// while no session/title event exists, so manual renames always win.
/// </summary>
public sealed class SessionTitleService
{
    public const string ServiceKey = "sessionTitle";

    private const int MaxTitleChars = 60;
    private const int FallbackChars = 48;

    private readonly HarnessContext _ctx;
    private readonly LlmRuntime _llm;
    private readonly AgentRuntime _agents;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);

    private SessionTitleService(HarnessContext ctx, LlmRuntime llm, AgentRuntime agents)
    {
        _ctx = ctx;
        _llm = llm;
        _agents = agents;
    }

    public static SessionTitleService Mount(HarnessContext ctx)
    {
        var service = new SessionTitleService(
            ctx,
            ctx.Get<LlmRuntime>(LlmRuntime.ServiceKey),
            ctx.Get<AgentRuntime>(AgentRuntime.ServiceKey));
        ctx.Provide(ServiceKey, service);
        _ = ctx.Events.On<Blazorly.Harness.Core.Agent.Agent>("agent/created", (agent, _) =>
        {
            service.Watch(agent.Session);
            return Task.CompletedTask;
        });
        foreach (var agent in service._agents.LiveAgents())
        {
            service.Watch(agent.Session);
        }
        return service;
    }

    private void Watch(Session session)
    {
        if (HasTitle(session)) return;
        var subscription = _subscriptions.GetOrAdd(session.Id, key => session.Subscribe(@event =>
        {
            try
            {
                if (@event.Type == SessionEventTypes.SessionTitle)
                {
                    // Any title (manual rename or ours) ends the watch.
                    if (_subscriptions.TryRemove(session.Id, out var sub)) sub.Dispose();
                    return;
                }
                if (@event.Type != SessionEventTypes.TurnEnd) return;
                if (SessionEventRead.TurnOf(@event) != 1) return;
                if (HasTitle(session)) return;
                _ = Task.Run(() => GenerateAsync(session));
            }
            catch
            {
                // title generation must never break the session
            }
        }));
    }

    private static bool HasTitle(Session session)
        => session.Events.Any(e => e.Type == SessionEventTypes.SessionTitle);

    private async Task GenerateAsync(Session session)
    {
        if (!HasTitle(session) && _inFlight.TryAdd(session.Id, 0))
        {
            try
            {
                var fallback = FirstUserText(session);
                var title = await GenerateTitleAsync(session).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(title)) title = fallback;
                title = Sanitize(title);
                if (title.Length == 0) title = Sanitize(fallback);
                if (title.Length == 0) return;
                if (HasTitle(session)) return;
                session.Append(SessionEventTypes.SessionTitle,
                    new SessionPayloads.SessionTitlePayload(title, [], title == Sanitize(fallback) ? "fallback" : "generated"));
            }
            catch
            {
                // a failed title generation is silently retried never; the fallback fold still applies
            }
            finally
            {
                _inFlight.TryRemove(session.Id, out _);
                if (HasTitle(session) && _subscriptions.TryRemove(session.Id, out var sub)) sub.Dispose();
            }
        }
    }

    private async Task<string?> GenerateTitleAsync(Session session)
    {
        var agent = _agents.Get(session.Id);
        var provider = agent?.Options.Provider;
        var model = agent?.Options.Model;
        if (provider is null || model is null) return null;
        var firstPrompt = FirstUserText(session);
        if (firstPrompt.Length == 0) return null;

        var options = new GenerateOptions
        {
            Provider = provider,
            Model = model,
            Purpose = "session-title",
            MaxTokens = 64,
            Messages = [Llm.Message.CreateUserText(
                "Write a concise title (3-6 words, no quotes, no trailing period) for a coding session that starts with this request. Reply with the title only.\n\n" + firstPrompt)],
        };
        var assembler = new BlockAssembler();
        await foreach (var chunk in _llm.Stream(options).ConfigureAwait(false))
        {
            assembler.Push(chunk);
        }
        return string.Join(" ", assembler.Blocks().OfType<TextBlock>().Select(b => b.Text)).Trim();
    }

    internal static string FirstUserText(Session session)
    {
        var message = session.Events
            .Where(e => e.Type == SessionEventTypes.UserMessage)
            .Select(SessionEventRead.MessageOf)
            .FirstOrDefault(m => m.Content.OfType<TextBlock>().Any(t => t.Text.Trim().Length > 0));
        if (message is null) return string.Empty;
        return string.Join(" ", message.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
    }

    internal static string Sanitize(string raw)
    {
        var collapsed = string.Join(' ', raw.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        collapsed = collapsed.Trim().Trim('"', '\'', '`').Trim();
        if (collapsed.Length <= MaxTitleChars) return collapsed;
        return collapsed[..(MaxTitleChars - 1)].TrimEnd() + "…";
    }
}
