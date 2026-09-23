using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Attachments;
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
        harness.Workspaces.ThrowIfDeleting(workspace.Root);
        var session = harness.Sessions.Create(meta: new SessionMeta(Cwd: workspace.Root));
        AttachAgent(session, workspace);
        return session;
    }

    public async Task<Core.Sessions.Session> OpenSessionAsync(string id)
    {
        var existing = harness.Sessions.Get(id);
        if (existing is not null)
        {
            await ReconcileDelegationsAsync(existing.Id).ConfigureAwait(false);
            return existing;
        }
        var session = await harness.Sessions.OpenAsync(id);
        AttachAgent(session, WorkspaceOf(session));
        await ReconcileDelegationsAsync(session.Id).ConfigureAwait(false);
        return session;
    }

    /// <summary>
    /// Best-effort healing of orphaned "running" delegation rows whenever a session is
    /// opened for viewing: a child that settled while nobody was watching (restart,
    /// abandoned await, dead monitor) flips to its real outcome instead of claiming to
    /// run forever. Never breaks the open itself.
    /// </summary>
    private async Task ReconcileDelegationsAsync(string sessionId)
    {
        try
        {
            var subagents = harness.Subagents;
            if (subagents is not null) await subagents.ReconcileAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Healing is advisory; the session view must open regardless.
        }
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
            new AgentOptions(harness.Settings.Provider, harness.Settings.Model,
                HarnessBootstrapper.ResolveMaxOutputTokens(harness.Settings,
                    harness.RuntimeModels(harness.Settings.Provider), harness.Settings.Model)));
        agent.RetryLimit = harness.Loop.RetryLimit;
        agent.Driver.MaxParallelToolCalls = harness.Loop.MaxParallelToolCalls;
        var header = session.LatestRequestHeader();
        if (header is not null)
        {
            agent.Options = new AgentOptions(header.Header.Provider, header.Header.Model,
                header.Header.MaxTokens ?? HarnessBootstrapper.ResolveMaxOutputTokens(harness.Settings,
                    harness.RuntimeModels(header.Header.Provider), header.Header.Model),
                header.Header.ReasoningEffort);
        }
        harness.Agents.Publish(agent);
        _ = harness.Context.Events.EmitAsync("agent/session-start", new SessionStartEvent(agent, "startup"), agent);
        session.Subscribe(e => _ = broker.PublishAsync(new UiEventBroker.Frame(session.Id, e)));
        _ = workspace;
    }

    /// <summary>Uploads any file to the attachment store and returns its id, classified kind,
    /// and text content for text files (capped at 256 KB, the same limit as @file references).
    /// The promise that this store is local-only and never leaves the harness home.</summary>
    public sealed record FileUploadResult(string Id, string FileName, string MimeType, string Kind, string? TextContent);

    private static string Classify(string mime, string fileName)
    {
        if (mime.StartsWith("image/", StringComparison.Ordinal)) return "image";
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".md" or ".json" or ".xml" or ".csv" or ".log" or ".yaml" or ".yml"
                or ".html" or ".css" or ".js" or ".ts" or ".py" or ".cs" or ".java" or ".go"
                or ".rs" or ".toml" or ".ini" or ".cfg" or ".sh" or ".bash" or ".zsh"
                or ".sql" or ".rb" or ".php" or ".swift" or ".kt" => "text",
            ".pdf" or ".docx" or ".doc" or ".xlsx" or ".xls" or ".pptx" or ".ppt" => "document",
            _ => "binary",
        };
    }

    private static bool ContainsNullBytes(string text, int snip)
    {
        for (var i = 0; i < Math.Min(text.Length, snip); i++)
            if (text[i] == '\0') return true;
        return false;
    }

    /// <summary>Decodes UTF-8 text for fallback attachment classification (REST callers
    /// that reference ids this process never uploaded).</summary>
    private static string? DecodeText(byte[] data)
    {
        if (data.Length == 0 || data.Length > Core.Context.FileReferences.MaxTextBytes) return null;
        var text = System.Text.Encoding.UTF8.GetString(data);
        return ContainsNullBytes(text, Math.Min(data.Length, 8192)) ? null : text;
    }

    /// <summary>Drops a copy of a binary attachment under the session workspace, keeping
    /// the original extension so shell tools recognize the format.</summary>
    private static async Task<string> SaveToWorkspaceAsync(string? cwd, AttachmentContent content, string fileName)
    {
        var root = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;
        var dir = Path.Combine(root, ".blazorly-uploads");
        Directory.CreateDirectory(dir);
        var ext = Path.GetExtension(fileName);
        var safe = Path.GetFileName(fileName.Trim());
        if (safe.Length == 0 || safe == ".") safe = "attached" + ext;
        else if (!safe.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && ext.Length > 0) safe += ext;
        // De-dupe collisions from repeated pastes of the same name.
        var path = Path.Combine(dir, safe);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(safe)}-{i}{ext}");
        await File.WriteAllBytesAsync(path, content.Data);
        return path;
    }

    public async Task PromptAsync(string sessionId, string text, string mode, string[]? attachmentIds = null)
    {
        var agent = harness.Agents.Get(sessionId) ?? throw new InvalidOperationException("unknown session");
        harness.Workspaces.ThrowIfDeleting(agent.Session.Header.Cwd);
        var message = await BuildUserMessageAsync(sessionId, agent, text, attachmentIds);
        if (mode == "steer") agent.Steer(message);
        else agent.Followup(message);
    }

    /// <summary>Upload classification remembered per attachment id: browsers paste text
    /// files with an empty or octet-stream MIME, so the filename-based kind decided at
    /// upload time is the one that matters at send time.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileUploadResult> _uploads = new();

    public async Task<FileUploadResult> UploadFileAsync(string sessionId, byte[] data, string mimeType, string fileName)
    {
        if (harness.Attachments is null)
            throw new InvalidOperationException("attachments are not mounted");
        var id = await harness.Attachments.SaveAsync(sessionId, data, mimeType);
        var kind = Classify(mimeType, fileName);
        string? textContent = null;
        if (kind == "text" && data.Length <= Core.Context.FileReferences.MaxTextBytes)
        {
            textContent = System.Text.Encoding.UTF8.GetString(data);
            if (ContainsNullBytes(textContent, Math.Min(data.Length, 8192)))
            {
                textContent = null;
                kind = "binary";
            }
        }
        var result = new FileUploadResult(id, fileName, mimeType, kind, textContent);
        _uploads[id] = result;
        return result;
    }

    /// <summary>Builds the user message, expanding "@path" references into content blocks
    /// (file bodies, images via the attachment store, or notices). Expansion failures fall
    /// back to the plain text — a bad reference must never block sending.</summary>
    private async Task<Llm.Message> BuildUserMessageAsync(string sessionId, Agent agent, string text, string[]? attachmentIds = null)
    {
        try
        {
            var cwd = agent.Session.Header.Cwd ?? Directory.GetCurrentDirectory();
            var expanded = await Core.Context.FileReferences.ExpandAsync(text, cwd, sessionId, harness.Attachments);
            if (attachmentIds is { Length: > 0 })
            {
                var blocks = new List<Llm.ContentBlock>(expanded.Blocks.Count + attachmentIds.Length);
                blocks.AddRange(expanded.Blocks);
                foreach (var id in attachmentIds)
                {
                    // One bad attachment degrades to a notice; it must never take the
                    // other attachments or the whole message down with it.
                    try
                    {
                        if (harness.Attachments is null) continue;
                        var uploaded = _uploads.GetValueOrDefault(id);
                        var content = await harness.Attachments.ReadAsync(id);
                        if (content is null)
                        {
                            blocks.Add(new Llm.TextBlock($"\n[Attachment {id} is no longer readable — it was not included.]\n"));
                            continue;
                        }
                        var name = uploaded?.FileName is { Length: > 0 } n ? n : "attached file";
                        var sizeLabel = $"{content.Data.Length / 1024.0:0.#} KB";
                        // Prefer the upload-time classification (filename-aware); REST
                        // callers that never uploaded fall back to the stored MIME type.
                        var kind = uploaded?.Kind
                            ?? (content.MimeType.StartsWith("image/", StringComparison.Ordinal) ? "image"
                                : content.MimeType.StartsWith("text/", StringComparison.Ordinal) ? "text" : "binary");
                        if (kind == "image")
                        {
                            blocks.Add(new Llm.ImageBlock(id, content.MimeType));
                        }
                        else if (kind == "text" && (uploaded?.TextContent ?? DecodeText(content.Data)) is { Length: > 0 } body)
                        {
                            blocks.Add(new Llm.TextBlock($"\n[Attached file: {name} ({sizeLabel})]\n{body}\n[End of {name}]\n"));
                        }
                        else
                        {
                            // Binary/document (PDF, DOCX, XLSX…) — drop a copy into the
                            // workspace so the agent's tools can open it.
                            var savedPath = await SaveToWorkspaceAsync(agent.Session.Header.Cwd, content, name);
                            blocks.Add(new Llm.TextBlock(
                                $"\n[Attached file: {name} ({sizeLabel}) — binary format]\n"
                                + $"Saved to: {savedPath}\n"
                                + $"Use bash tools to inspect it (e.g. pdftotext, python, unzip -p, libreoffice --headless).\n"));
                        }
                    }
                    catch (Exception ex)
                    {
                        blocks.Add(new Llm.TextBlock($"\n[Attachment {id} failed to attach: {ex.Message}]\n"));
                    }
                }
                return Llm.Message.CreateUser(blocks);
            }
            return expanded.Attached.Count == 0
                ? Llm.Message.CreateUserText(text)
                : Llm.Message.CreateUser(expanded.Blocks);
        }
        catch
        {
            return Llm.Message.CreateUserText(text);
        }
    }

    /// <summary>@-mention autocomplete candidates under the session cwd (bounded, best-first).</summary>
    public IReadOnlyList<Core.Context.FileCandidate> FileCandidates(string sessionId, string? query)
    {
        var session = harness.Sessions.Get(sessionId);
        if (session is null) return [];
        return Core.Context.FileReferences.ListCandidates(session.Header.Cwd ?? "", query ?? "");
    }

    public void Cancel(string sessionId)
    {
        harness.Agents.Get(sessionId)?.Cancel(AgentCancelCause.User());
    }

    // ---- ui terminal (a persistent shell the user drives; scoped to the session's
    // agent so the model can also inspect it with terminal_read/terminal_list) ----

    private const string UiTerminalName = "ui";

    public Tools.TerminalService? Terminals
        => harness.Context.TryGet<Tools.TerminalService>(Tools.TerminalService.ServiceKey);

    private Agent TerminalAgent(string sessionId)
        => harness.Agents.Get(sessionId) ?? throw new InvalidOperationException("unknown session");

    /// <summary>The session's ui shell id, or null when none was opened yet.</summary>
    public string? UiTerminalId(string sessionId)
        => Terminals?.List(TerminalAgent(sessionId)).FirstOrDefault(t => t.Name == UiTerminalName)?.SessionId;

    /// <summary>Opens (or revives) the session's ui shell; returns the terminal id.</summary>
    public string UiTerminalOpen(string sessionId)
    {
        var service = Terminals ?? throw new InvalidOperationException("terminals are not enabled in settings");
        var agent = TerminalAgent(sessionId);
        var existing = service.List(agent).FirstOrDefault(t => t.Name == UiTerminalName)?.SessionId;
        if (existing is not null)
        {
            if (service.Info(agent, existing).Running) return existing;
            service.Close(agent, existing); // dead shell: replace it
        }
        return service.Open(agent, UiTerminalName, GetSession(sessionId).Header.Cwd);
    }

    /// <summary>Runs one command line; returns the output it produced (sentinel-waited).</summary>
    public async Task<string> UiTerminalSendAsync(string sessionId, string terminalId, string text)
        => await Terminals!.SendAsync(TerminalAgent(sessionId), terminalId, text, waitMs: 400);

    /// <summary>Full buffer snapshot (stdout+stderr since the shell opened), with the
    /// command-completion sentinel lines stripped — the UI displays this raw.</summary>
    public string UiTerminalRead(string sessionId, string terminalId)
        => FilterSentinels(Terminals!.Read(TerminalAgent(sessionId), terminalId));

    /// <summary>Clears the server-side buffer too, so the 400ms poll doesn't refill the view.</summary>
    public void UiTerminalClear(string sessionId, string terminalId)
        => Terminals!.Clear(TerminalAgent(sessionId), terminalId);

    internal static string FilterSentinels(string buffer)
    {
        if (!buffer.Contains(Tools.TerminalService.SentinelPrefix, StringComparison.Ordinal)) return buffer;
        var kept = buffer.Split('\n')
            .Where(line => !line.Contains(Tools.TerminalService.SentinelPrefix, StringComparison.Ordinal));
        return string.Join('\n', kept);
    }

    public bool UiTerminalRunning(string sessionId, string terminalId)
        => Terminals!.Info(TerminalAgent(sessionId), terminalId).Running;

    public void UiTerminalSignal(string sessionId, string terminalId, string signal)
        => Terminals!.Signal(TerminalAgent(sessionId), terminalId, signal);

    /// <summary>Kills the shell (the drawer itself just closes and can reopen a fresh one).</summary>
    public void UiTerminalClose(string sessionId, string terminalId)
        => Terminals!.Close(TerminalAgent(sessionId), terminalId);

    public Core.Sessions.Session Fork(string sessionId, int? atSeq)
        => harness.Sessions.Fork(sessionId, atSeq);

    // ---- workspace docs (blazorly init) ----

    public Core.Instructions.DocsInitService.InitPreview PreviewSessionDocs(string sessionId, int depth)
        => Core.Instructions.DocsInitService.Preview(SessionRoot(sessionId), depth);

    public Core.Instructions.DocsInitService.InitApplyResult ApplySessionDocs(string sessionId, int depth, bool force)
        => Core.Instructions.DocsInitService.Apply(SessionRoot(sessionId), depth, force);

    /// <summary>AI-drafted docs with the session's route: draft, verify, correct, write.</summary>
    public async Task<Core.Instructions.AiDocsDrafter.AiApplyResult> GenerateSessionDocsAsync(
        string sessionId, int depth, string provider, string model, string? reasoningEffort,
        CancellationToken ct = default)
    {
        var complete = Core.Instructions.AiDocsDrafter.CompleteWith(harness.Llm, provider, model, reasoningEffort);
        return await Core.Instructions.AiDocsDrafter.DraftAndApplyAsync(
            SessionRoot(sessionId), depth, $"{provider}/{model}", complete, force: false, ct).ConfigureAwait(false);
    }

    private string SessionRoot(string sessionId)
    {
        var cwd = harness.Sessions.Get(sessionId)?.Header.Cwd;
        if (string.IsNullOrWhiteSpace(cwd))
            throw new Kernel.HarnessException("NO_WORKSPACE", $"session '{sessionId}' has no workspace directory");
        return cwd;
    }

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
        var canonical = SandboxPolicy.Normalize(mode)
            ?? throw new InvalidOperationException($"unknown permission preset '{mode}'");
        if (canonical is not (SandboxPolicy.ReadOnly or SandboxPolicy.WorkspaceWrite or SandboxPolicy.FullAccess))
            throw new InvalidOperationException($"unknown permission preset '{mode}'");
        var session = GetSession(sessionId);
        session.Append(SessionEventTypes.SandboxMode, new SessionPayloads.SandboxModePayload(canonical));
    }

    public void SetSessionModel(string sessionId, string provider, string model)
    {
        var agent = harness.Agents.Get(sessionId) ?? throw new InvalidOperationException("unknown session");
        agent.Options = new AgentOptions(provider, model,
            HarnessBootstrapper.ResolveMaxOutputTokens(harness.Settings, harness.RuntimeModels(provider), model));
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
        await harness.SearchIndex.PruneSessionAsync(sessionId);
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
                "/help" => ("/help — show commands\n/permission <read-only|workspace-write|full-access> — switch this session's sandbox preset\n/model <provider>/<model> — switch the model route\n/effort <off|low|high|max|…> — set reasoning effort for this session's model\n/title <text> — rename this session\n/plan — toggle plan mode (restricts mutations until a plan is approved)\n/goal <objective> — set a persistent goal that auto-continues across turns\n/compact — prune + summarize older context now (frees window space)", true),
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
        if (model is null)
            return ($"model '{agent.Options.Model}' is unknown on route '{agent.Options.Provider}'", false);
        var efforts = model.EffectiveReasoningEfforts;

        if (args.Length == 0)
        {
            var current = agent.Options.ReasoningEffort ?? model.EffectiveDefaultEffort ?? "model default";
            return ($"reasoning effort: {current} — choose one of: {string.Join(", ", efforts)} (or 'default')", true);
        }
        var requested = args.Trim().ToLowerInvariant();
        if (requested is "default" or "reset")
        {
            agent.Options = agent.Options with { ReasoningEffort = null };
            return ($"reasoning effort reset to the model default ({model.EffectiveDefaultEffort ?? "provider default"})", true);
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
            return ($"current permission preset: {current}\nusage: /permission <read-only|workspace-write|full-access>", true);
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
