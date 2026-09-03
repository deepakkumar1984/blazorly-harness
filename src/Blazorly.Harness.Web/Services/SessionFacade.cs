using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using PlanModeService = Blazorly.Harness.Tools.PlanModeService;

namespace Blazorly.Harness.Web.Services;

/// <summary>The UI/REST facade over the harness: workspaces, sessions, prompts, commands, events.</summary>
public sealed class SessionFacade(HarnessBootstrapper harness, UiEventBroker broker)
{
    public HarnessBootstrapper Harness => harness;

    // ---- workspaces ----

    public IReadOnlyList<Workspace> Workspaces() => harness.Workspaces.List();

    public Workspace AddWorkspace(string name, string root) => harness.Workspaces.Add(name, root);

    public void RemoveWorkspace(string id) => harness.Workspaces.Remove(id);

    public void RenameWorkspace(string id, string name) => harness.Workspaces.Rename(id, name);

    public Workspace? WorkspaceOf(Core.Sessions.Session session)
        => harness.Workspaces.ForRoot(session.Header.Cwd ?? "");

    /// <summary>Server user profile folder — the "home" quick link in the folder browser.</summary>
    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public sealed record FolderListing(string Path, string? Parent, IReadOnlyList<DirectoryEntry> Entries);

    /// <summary>In-process directory listing for the add-workspace browser (no HTTP round-trip).</summary>
    public FolderListing BrowseFolders(string? path)
    {
        var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "/" : path);
        return new(full, Directory.GetParent(full)?.FullName, DirectoryBrowser.List(path ?? "/"));
    }

    // ---- sessions ----

    public Core.Sessions.Session CreateSession(string? workspaceId = null)
    {
        var workspace = workspaceId is null ? harness.Workspaces.Default() : harness.Workspaces.Get(workspaceId)
            ?? throw new InvalidOperationException($"unknown workspace '{workspaceId}'");
        var session = harness.Sessions.Create(meta: new SessionMeta(Cwd: workspace.Root));
        AttachAgent(session, workspace);
        return session;
    }

    public async Task<Core.Sessions.Session> OpenSessionAsync(string id)
    {
        var existing = harness.Sessions.Get(id);
        if (existing is not null) return existing;
        var session = await harness.Sessions.OpenAsync(id);
        AttachAgent(session, WorkspaceOf(session));
        return session;
    }

    public Agent EnsureAgent(Core.Sessions.Session session)
    {
        var agent = harness.Agents.Get(session.Id);
        if (agent is null) AttachAgent(session, WorkspaceOf(session));
        return harness.Agents.Get(session.Id)!;
    }

    private void AttachAgent(Core.Sessions.Session session, Workspace? workspace)
    {
        // The agent's model selection: deployment default, with a per-session override stamped
        // at creation when the session carries a durable header mismatch.
        var agent = new Agent(
            harness.Context,
            harness.Llm,
            harness.Tools,
            harness.Context.Get<Core.SystemPrompt.SystemPromptService>("systemPrompt"),
            session,
            new AgentOptions(harness.Settings.Provider, harness.Settings.Model));
        agent.RetryLimit = harness.Loop.RetryLimit;
        agent.Driver.MaxParallelToolCalls = harness.Loop.MaxParallelToolCalls;
        var header = session.LatestRequestHeader();
        if (header is not null)
        {
            agent.Options = new AgentOptions(header.Header.Provider, header.Header.Model, header.Header.MaxTokens);
        }
        harness.Agents.Publish(agent);
        _ = harness.Context.Events.EmitAsync("agent/session-start", new SessionStartEvent(agent, "startup"), agent);
        session.Subscribe(e => _ = broker.PublishAsync(new UiEventBroker.Frame(session.Id, e)));
        _ = workspace;
    }

    public void Prompt(string sessionId, string text, string mode)
    {
        var agent = harness.Agents.Get(sessionId) ?? throw new InvalidOperationException("unknown session");
        var message = Message.CreateUserText(text);
        if (mode == "steer") agent.Steer(message);
        else agent.Followup(message);
    }

    public void Cancel(string sessionId)
    {
        harness.Agents.Get(sessionId)?.Cancel(AgentCancelCause.User());
    }

    public Core.Sessions.Session Fork(string sessionId, int? atSeq)
        => harness.Sessions.Fork(sessionId, atSeq);

    public IReadOnlyList<Core.Sessions.Session> LiveSessions() => harness.Sessions.LiveSessions();

    public async Task<IReadOnlyList<SessionHeader>> ListPersistedAsync()
        => await harness.Sessions.ListPersistedAsync();

    public async Task FlushAsync(string sessionId)
    {
        if (harness.Sessions.Persistence is not null) await harness.Sessions.Persistence.FlushAsync(sessionId);
    }

    // ---- per-session controls (durable) ----

    public void RenameSession(string sessionId, string title)
    {
        var session = GetSession(sessionId);
        session.Append(SessionEventTypes.SessionTitle, new SessionPayloads.SessionTitlePayload(title.Trim(), [], "user"));
    }

    public void SetSessionSandboxMode(string sessionId, string mode)
    {
        if (mode is not (SandboxPolicy.ReadOnly or SandboxPolicy.WorkspaceWrite or SandboxPolicy.DangerFullAccess))
            throw new InvalidOperationException($"unknown permission preset '{mode}'");
        var session = GetSession(sessionId);
        session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload(mode));
    }

    public void SetSessionModel(string sessionId, string provider, string model)
    {
        var agent = harness.Agents.Get(sessionId) ?? throw new InvalidOperationException("unknown session");
        agent.Options = new AgentOptions(provider, model, agent.Options.MaxTokens);
    }

    public bool IsArchived(string sessionId) => harness.Workspaces.IsArchived(sessionId);

    public void Archive(string sessionId, bool archived) => harness.Workspaces.Archive(sessionId, archived);

    /// <summary>Permanently deletes a chat. Returns an error message, or null on success.</summary>
    public async Task<string?> DeleteSession(string sessionId)
    {
        var agent = harness.Agents.Get(sessionId);
        if (agent is { Status: Core.Agent.AgentStatus.Running })
            return "this chat is still running — stop it before deleting";
        await harness.Sessions.Delete(sessionId);
        return null;
    }

    // ---- the human command plane ----

    public sealed record CommandOutcome(string Name, bool Ok, string Text);

    /// <summary>Adjudicates slash commands locally; null means the input is not a command.</summary>
    public CommandOutcome? TryCommand(string sessionId, string input)
    {
        if (!input.StartsWith('/')) return null;
        var trimmed = input.Trim();
        var space = trimmed.IndexOf(' ');
        var name = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var args = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        var session = GetSession(sessionId);

        session.Append(SessionEventTypes.CommandRun, new SessionPayloads.CommandRunPayload(name, args.Length > 0 ? args : null));
        try
        {
            var result = name switch
            {
                "/permission" => CommandPermission(sessionId, args),
                "/model" => CommandModel(sessionId, args),
                "/effort" => CommandEffort(sessionId, args),
                "/title" => CommandTitle(sessionId, args),
                "/help" => ("/help — show commands\n/permission <read-only|workspace-write|danger-full-access> — switch this session's sandbox preset\n/model <provider>/<model> — switch the model route\n/effort <off|low|high|max|…> — set reasoning effort for this session's model\n/title <text> — rename this session\n/plan — toggle plan mode (restricts mutations until a plan is approved)\n/goal <objective> — set a persistent goal that auto-continues across turns\n/compact — prune + summarize older context now (frees window space)", true),
                "/plan" => CommandPlan(sessionId),
                "/goal" => CommandGoal(sessionId, args),
                "/compact" => CommandCompact(sessionId),
                _ => ($"unknown command '{name}' — try /help", false),
            };
            var outcome = new CommandOutcome(name, result.Item2, result.Item1);
            session.Append(SessionEventTypes.CommandDone, new SessionPayloads.CommandDonePayload(outcome.Ok ? "success" : "error", outcome.Text));
            return outcome;
        }
        catch (Exception ex)
        {
            session.Append(SessionEventTypes.CommandDone, new SessionPayloads.CommandDonePayload("error", ex.Message));
            return new CommandOutcome(name, false, ex.Message);
        }
    }

    /// <summary>Reasoning effort for this session (dsh agentOptions.reasoningEffort): catalog-validated.</summary>
    private (string, bool) CommandEffort(string sessionId, string args)
    {
        var agent = harness.Agents.Get(sessionId);
        if (agent is null) return ("no active agent for this session", false);
        var model = harness.RuntimeModels(agent.Options.Provider ?? "")
            .FirstOrDefault(m => m.Id == agent.Options.Model);
        if (model is null || model.ReasoningEfforts is not { Length: > 0 } efforts)
            return ($"model '{agent.Options.Model}' offers no reasoning effort levels", false);

        if (args.Length == 0)
        {
            var current = agent.Options.ReasoningEffort ?? model.DefaultEffort ?? "model default";
            return ($"reasoning effort: {current} — choose one of: {string.Join(", ", efforts)} (or 'default')", true);
        }
        var requested = args.Trim().ToLowerInvariant();
        if (requested is "default" or "reset")
        {
            agent.Options = agent.Options with { ReasoningEffort = null };
            return ($"reasoning effort reset to the model default ({model.DefaultEffort ?? "provider default"})", true);
        }
        if (!efforts.Contains(requested, StringComparer.Ordinal))
            return ($"unknown effort '{requested}' for {agent.Options.Model} — choose one of: {string.Join(", ", efforts)}", false);
        agent.Options = agent.Options with { ReasoningEffort = requested };
        return ($"reasoning effort set to {requested}", true);
    }

    /// <summary>Manual compaction (dsh command-compact): typed failures, runs in the background.</summary>
    private (string, bool) CommandCompact(string sessionId)
    {
        var agent = harness.Agents.Get(sessionId);
        if (agent is null) return ("nothing to compact: no active agent for this session", false);
        if (agent.Status != AgentStatus.Idle) return ("busy: the agent is running; /compact again once it is idle", false);
        if (agent.Session.SurfaceSeqs.Count < 3) return ("nothing to compact: the context is already small", false);
        var compaction = harness.Compaction;
        if (compaction is null) return ("compaction is not mounted", false);
        _ = Task.Run(async () =>
        {
            try
            {
                // keepTokens: 0 = forced: shadow everything but the most recent node.
                var shadowed = await compaction.CompactAsync(agent, keepTokens: 0).ConfigureAwait(false);
                await FlushAsync(sessionId).ConfigureAwait(false);
                Console.Error.WriteLine($"[compaction] /compact shadowed {shadowed} nodes for session {sessionId}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[compaction] /compact failed for session {sessionId}: {ex.Message}");
            }
        });
        return ("compaction started — older context will be pruned and summarized in the background", true);
    }

    private (string, bool) CommandPermission(string sessionId, string args)    {
        if (args.Length == 0)
        {
            var current = GetSession(sessionId).LatestSandboxMode() ?? Harness.Settings.SandboxMode;
            return ($"current permission preset: {current}\nusage: /permission <read-only|workspace-write|danger-full-access>", true);
        }
        SetSessionSandboxMode(sessionId, args);
        return ($"permission preset switched to {args}", true);
    }

    private (string, bool) CommandModel(string sessionId, string args)
    {
        if (args.Length == 0 || !args.Contains('/'))
        {
            var providers = string.Join(", ", harness.Llm.ListProviders());
            var agent = harness.Agents.Get(sessionId);
            return ($"current model: {agent?.Options.Provider}/{agent?.Options.Model}\navailable providers: {providers}\nusage: /model <provider>/<model>", true);
        }
        var split = args.Split('/', 2);
        SetSessionModel(sessionId, split[0].Trim(), split[1].Trim());
        return ($"model route switched to {split[0].Trim()}/{split[1].Trim()}", true);
    }

    private (string, bool) CommandPlan(string sessionId)
    {
        var session = GetSession(sessionId);
        var service = harness.Context.TryGet<PlanModeService>("planMode");
        if (service is null) return ("plan mode is not enabled in settings", false);
        var active = !service.IsActive(session);
        service.SetActive(session, active);
        return (active
            ? "plan mode ON: the session is restricted to read-only work until a plan is approved via exit_plan_mode"
            : "plan mode OFF", true);
    }

    private (string, bool) CommandGoal(string sessionId, string args)
    {
        var session = GetSession(sessionId);
        if (harness.Context.TryGet<GoalService>("goals") is null) return ("goals are not enabled in settings", false);
        if (args.Length == 0)
        {
            var goal = Tools.GoalService.Active(session);
            return goal is null
                ? ("no active goal — usage: /goal <objective>", true)
                : ($"active goal (round {goal.RoundsStarted}/{goal.MaxRounds}): {goal.Objective}", true);
        }
        Tools.GoalService.Create(session, args, maxRounds: 8);
        return ($"goal set: \"{args}\" — the session will continue across turns until it is completed or blocked", true);
    }

    private (string, bool) CommandTitle(string sessionId, string args)
    {
        if (args.Length == 0) return ("usage: /title <text>", false);
        RenameSession(sessionId, args);
        return ($"session renamed to \"{args}\"", true);
    }

    // ---- search ----

    public sealed record SearchHit(string SessionId, string Title, string Kind, string Snippet);

    /// <summary>Searches live sessions' titles and message text; bounded results.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var hits = new List<SearchHit>();
        var needle = query.Trim();
        foreach (var session in harness.Sessions.LiveSessions())
        {
            var title = session.LatestTitle();
            if (title is not null && title.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new SearchHit(session.Id, title, "title", title));
                continue;
            }
            foreach (var e in session.Events)
            {
                string? text = e.Type switch
                {
                    SessionEventTypes.UserMessage => SessionEventRead.MessageOf(e).FlattenText(),
                    SessionEventTypes.AssistantMessage => SessionEventRead.AssistantMessageOf(e).Message.FlattenText(),
                    _ => null,
                };
                if (text is not null && text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    var index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                    var start = Math.Max(0, index - 40);
                    var snippet = text[start..Math.Min(text.Length, index + needle.Length + 60)];
                    hits.Add(new SearchHit(session.Id, DeriveTitle(session), e.Type == SessionEventTypes.UserMessage ? "user" : "assistant",
                        (start > 0 ? "…" : "") + snippet.Replace("\n", " ")));
                    break;
                }
            }
            if (hits.Count >= limit) break;
        }
        return [.. hits.Take(limit)];
    }

    public static string DeriveTitle(Core.Sessions.Session session)
    {
        var title = session.LatestTitle();
        if (title is { Length: > 0 }) return title.Length > 44 ? title[..44] + "…" : title;
        var events = session.Events;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Type == SessionEventTypes.UserMessage)
            {
                var text = SessionEventRead.MessageOf(events[i]).FlattenText().Replace("\n", " ");
                if (text.Length > 44) text = text[..44] + "…";
                return text;
            }
        }
        return $"Session {session.Id[^8..]}";
    }

    private Core.Sessions.Session GetSession(string sessionId)
        => harness.Sessions.Get(sessionId) ?? throw new InvalidOperationException($"unknown session '{sessionId}'");
}
