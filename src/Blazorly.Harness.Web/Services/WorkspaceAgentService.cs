using System.Collections.Concurrent;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Web.Services;

/// <summary>
/// Live application point for per-workspace agent configuration (custom system instructions
/// plus a tool allow-list, both stored on the <see cref="Workspace"/> record). Instructions
/// resolve per prompt assembly through <see cref="AgentLoopService.IdentityOverride"/>; the
/// allow-list is enforced by restricting each agent's tool scope as it is published — the
/// agent/created hook covers every creation path (chat sessions, CLI runs, and subagents,
/// whose scopes are not children of their parent's and so need their own restriction).
/// </summary>
public sealed class WorkspaceAgentService
{
    private readonly HarnessBootstrapper _harness;
    private readonly ConcurrentDictionary<string, Entry> _restrictions = new(StringComparer.Ordinal);
    /// <summary>Serializes apply/release so a save-triggered refresh can never interleave with
    /// an agent creation or disposal for the same session (which would orphan a restriction).</summary>
    private readonly object _gate = new();

    private sealed record Entry(Agent Agent, IDisposable Restriction);

    public WorkspaceAgentService(HarnessBootstrapper harness) => _harness = harness;

    /// <summary>Subscribes to the agent lifecycle; the returned handle unsubscribes. Listener
    /// bodies stay synchronous so the restriction is in force before Publish returns and the
    /// agent can be given work.</summary>
    public IDisposable Attach()
    {
        var events = _harness.Context.Events;
        var created = events.On<Agent>("agent/created", (agent, _) =>
        {
            Apply(agent);
            return Task.CompletedTask;
        });
        var disposed = events.On<Agent>("agent/disposed", (agent, _) =>
        {
            // Only release this instance's entry: a re-attached agent for the same session id
            // may already own a fresh restriction that must survive the old agent's teardown.
            lock (_gate)
            {
                if (_restrictions.TryGetValue(agent.Id, out var entry) && ReferenceEquals(entry.Agent, agent))
                    Remove(agent.Id);
            }
            return Task.CompletedTask;
        });
        return Disposable.Of(() =>
        {
            created.Dispose();
            disposed.Dispose();
        });
    }

    /// <summary>Custom instructions for a session rooted at <paramref name="cwd"/>, or null
    /// for the harness default. Subagent sessions inherit the parent's cwd, so delegated
    /// work picks up the same workspace instructions.</summary>
    public string? CustomInstructionsFor(string? cwd)
    {
        var prompt = WorkspaceFor(cwd)?.SystemPrompt;
        return string.IsNullOrWhiteSpace(prompt) ? null : prompt;
    }

    /// <summary>(Re)applies the workspace allow-list to one agent, replacing any previous restriction.</summary>
    public void Apply(Agent agent)
    {
        lock (_gate)
        {
            // Replace semantics: whatever was tracked for this session id is stale (this agent
            // or a dead predecessor's), so it goes regardless of instance identity.
            Remove(agent.Id);
            // A workspace's own allow-list wins; otherwise the global default from
            // Settings → Capabilities applies, so one selection covers every workspace.
            var allow = WorkspaceFor(agent.Session.Header.Cwd)?.EnabledTools
                ?? _harness.Settings.DefaultEnabledTools;
            if (allow is null) return;
            var restriction = _harness.Tools.Restrict(agent.ScopeKey, new HashSet<string>(allow, StringComparer.Ordinal));
            _restrictions[agent.Id] = new Entry(agent, restriction);
        }
    }

    /// <summary>Re-applies configuration to every live agent; called after a workspace save so
    /// running chats pick up the new instruction and tool selection on their next request.</summary>
    public void RefreshAll()
    {
        foreach (var agent in _harness.Agents.LiveAgents())
        {
            try { Apply(agent); }
            catch (ObjectDisposedException) { /* agent torn down mid-refresh */ }
        }
    }

    private void Remove(string agentId)
    {
        if (_restrictions.TryRemove(agentId, out var entry)) entry.Restriction.Dispose();
    }

    private Workspace? WorkspaceFor(string? cwd)
        => string.IsNullOrWhiteSpace(cwd) || _harness.Workspaces is not { } registry
            ? null
            : registry.ForRoot(cwd);
}
