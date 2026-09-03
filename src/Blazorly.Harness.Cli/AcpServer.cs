using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Mcp;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.TokenMeter;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Cli;

public sealed record AcpServerOptions
{
    /// <summary>Workspace root for sessions; defaults to the invoking directory.</summary>
    public string? WorkspacePath { get; init; }

    /// <summary>"auto" (default) never asks; "ask" routes every tool call of ACP sessions through session/request_permission.</summary>
    public string Permission { get; init; } = "auto";
}

/// <summary>
/// Agent Client Protocol bridge over newline-delimited JSON-RPC 2.0 stdio (dsh
/// packages/acp): the editor/SDK is the client, the harness is the agent. Updates derive
/// from committed durable events only; `session/prompt` blocks for the whole turn and
/// settles with a standard stop reason; `session/cancel` settles the in-flight prompt as
/// `cancelled` out of band. Deviations from dsh are listed in docs/tier5-plan.md.
/// </summary>
public static class AcpServer
{
    private const long ProtocolVersion = 1;

    private static readonly JsonSerializerOptions FrameOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>One tracked ACP session: the owned agent plus its single prompt slot.</summary>
    private sealed class TrackedSession
    {
        public required Agent Agent { get; init; }
        public IDisposable? Subscription { get; set; }
        /// <summary>The one in-flight prompt, or null; reads/writes lock on the session.</summary>
        public InflightPrompt? Inflight;
        /// <summary>Client-provided stdio MCP mounts, alive for this session's lifetime.</summary>
        public List<McpClientService.ServerConnection> McpConnections { get; } = [];
    }

    private sealed class InflightPrompt
    {
        public string? MessageId { get; set; }
        public int StartSeq { get; set; } = -1;
        public int? MessageSeq { get; set; }
        public volatile bool CancelRequested;
        public TurnEndReason? EndReason { get; set; }
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AcpFault(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    public static async Task<int> RunAsync(AcpServerOptions options, TextReader input, TextWriter output, TextWriter log, CancellationToken ct)
    {
        var bootstrapper = new HarnessBootstrapper();
        bootstrapper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var root = options.WorkspacePath ?? Environment.CurrentDirectory;
            var workspace = bootstrapper.Workspaces.Ensure(root);

            var sessions = new ConcurrentDictionary<string, TrackedSession>(StringComparer.Ordinal);
            var initialized = false;
            var outstanding = new HashSet<Task>();
            var tools = bootstrapper.Context.Get<ToolRuntime>(ToolRuntime.ServiceKey);
            var policy = bootstrapper.Context.Get<ToolPolicyService>(ToolPolicyService.ServiceKey);
            var approval = bootstrapper.Context.TryGet<ApprovalService>(ApprovalService.ServiceKey);
            var askEveryTool = string.Equals(options.Permission, "ask", StringComparison.OrdinalIgnoreCase);

            // Server-ward requests (session/request_permission): string ids keep them
            // collision-free with the client's numeric request ids.
            var clientPending = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();
            var serverCallCounter = 0;

            async Task<JsonElement> RequestClientAsync(string method, object parameters, CancellationToken requestCt)
            {
                var id = "srv-" + Interlocked.Increment(ref serverCallCounter);
                var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                clientPending[id] = pending;
                try
                {
                    Write(output, JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, FrameOptions));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, requestCt);
                    return await pending.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    clientPending.TryRemove(id, out _);
                }
            }

            // Permission bridge: ACP sessions are answered with session/request_permission
            // (allow-once / reject-once); anything else falls through to the next answerer.
            IDisposable? answererHandle = approval?.PushAnswerer(async (request, requestCt) =>
            {
                if (request.Agent is null || !sessions.ContainsKey(request.Agent.Session.Id)) return ApprovalOutcome.Unavailable;
                JsonElement response;
                try
                {
                    response = await RequestClientAsync("session/request_permission", new
                    {
                        sessionId = request.Agent.Session.Id,
                        toolCall = new { toolCallId = request.CallId ?? string.Empty, title = request.ToolName },
                        options = new object[]
                        {
                            new { optionId = "allow-once", name = "Allow once", kind = "allow_once" },
                            new { optionId = "reject-once", name = "Reject", kind = "reject_once" },
                        },
                    }, requestCt).ConfigureAwait(false);
                }
                catch (AcpFault fault)
                {
                    log.WriteLine($"[acp] request_permission failed: {fault.Message}");
                    return ApprovalOutcome.Rejected;
                }
                var result = response.TryGetProperty("result", out var resultElement) ? resultElement : default;
                var outcome = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("outcome", out var outcomeElement)
                    ? outcomeElement
                    : default;
                if (outcome.ValueKind != JsonValueKind.Object) return ApprovalOutcome.Cancelled;
                var kind = outcome.TryGetProperty("outcome", out var kindElement) ? kindElement.GetString() : null;
                if (kind == "cancelled") return ApprovalOutcome.Cancelled;
                var optionId = outcome.TryGetProperty("optionId", out var optionElement) ? optionElement.GetString() : null;
                return optionId == "allow-once" ? ApprovalOutcome.AllowedOnce : ApprovalOutcome.Rejected;
            });

            TrackedSession Track(Agent agent)
            {
                var tracked = new TrackedSession { Agent = agent };
                tracked.Subscription = agent.Session.Subscribe(@event => OnEvent(tracked, @event));
                sessions[agent.Session.Id] = tracked;
                if (askEveryTool) policy.SetAskEveryTool(agent.Id, true);
                return tracked;
            }

            void MountClientMcpServers(TrackedSession tracked, JsonElement parameters, TextWriter logWriter)
            {
                foreach (var config in McpServersOf(parameters))
                {
                    var connection = new McpClientService.ServerConnection(config, tools, new McpOptions { ConfigPath = string.Empty }, new WriterLogger(logWriter));
                    tracked.McpConnections.Add(connection);
                    _ = connection.RunAsync();
                }
            }

            void OnEvent(TrackedSession tracked, SessionEvent @event)
            {
                try
                {
                    var inflight = tracked.Inflight;
                    if (@event.Type == SessionEventTypes.TurnEnd)
                    {
                        if (inflight is null) return;
                        var boundary = inflight.MessageSeq ?? inflight.StartSeq;
                        if (@event.Seq <= boundary) return;
                        inflight.EndReason = SessionEventRead.TurnEndReasonOf(@event);
                        inflight.Ended.TrySetResult();
                        return;
                    }
                    if (@event.Type == SessionEventTypes.UserMessage && inflight is { MessageSeq: null } pending
                        && MessageIdOf(@event) == pending.MessageId)
                    {
                        pending.MessageSeq = @event.Seq;
                        return;
                    }
                    foreach (var update in UpdatesOf(@event, includeUser: false, tracked.Agent, bootstrapper.Meter))
                        Write(output, Notification("session/update", new { sessionId = tracked.Agent.Session.Id, update }));
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[acp] update mapping failed: {ex.Message}");
                }
            }

            async Task<JsonElement?> HandleAsync(string method, JsonElement parameters, CancellationToken requestCt)
            {
                switch (method)
                {
                    case "initialize":
                        initialized = true;
                        return JsonSerializer.SerializeToElement(new
                        {
                            protocolVersion = ProtocolVersion,
                            agentInfo = new { name = "blazorly-harness-acp", version = "1.0.0" },
                            agentCapabilities = new
                            {
                                promptCapabilities = new { image = false, audio = false, embeddedContext = false },
                                sessionCapabilities = new { resume = new { } },
                            },
                            authMethods = Array.Empty<object>(),
                        }, FrameOptions);

                    case "authenticate" when initialized:
                        return JsonSerializer.SerializeToElement(new { }, FrameOptions);

                    case "session/new" when initialized:
                    {
                        var cwd = RequireAbsolutePath(parameters, "cwd");
                        RejectAdditionalDirectories(parameters);
                        var effectiveRoot = bootstrapper.Workspaces.Ensure(cwd).Root;
                        var agent = bootstrapper.Loop.Create(new SessionMeta(Cwd: effectiveRoot));
                        var newTracked = Track(agent);
                        MountClientMcpServers(newTracked, parameters, log);
                        _ = bootstrapper.Sessions.Persistence?.FlushAsync(agent.Session.Id, requestCt);
                        return JsonSerializer.SerializeToElement(new
                        {
                            sessionId = agent.Session.Id,
                            configOptions = BuildConfigOptions(agent.Options.Provider ?? "", agent.Options.Model ?? "default"),
                        }, FrameOptions);
                    }

                    case "session/set_config_option" when initialized:
                    {
                        var sessionId = RequireString(parameters, "sessionId");
                        var tracked = sessions.GetValueOrDefault(sessionId)
                            ?? throw new AcpFault(-32602, $"unknown session: {sessionId}");
                        var configId = parameters.TryGetProperty("configId", out var configIdElement) && configIdElement.ValueKind == JsonValueKind.String
                            ? configIdElement.GetString()
                            : null;
                        if (configId != "model") throw new AcpFault(-32602, $"unsupported config option: {configId ?? "(missing)"}");
                        var (provider, model) = RouteOf(parameters);
                        lock (tracked)
                        {
                            if (tracked.Inflight is not null)
                                throw new AcpFault(-32602, "cannot change the route while a prompt is in flight");
                        }
                        tracked.Agent.Options = new AgentOptions(provider, model, tracked.Agent.Options.MaxTokens);
                        if (HasContinuableDescriptor(tracked.Agent.Session))
                        {
                            // Refresh the log-only descriptor so a cold resume keeps this route.
                            tracked.Agent.Session.Append(SessionEventTypes.SubagentDescriptor,
                                new SessionPayloads.SubagentDescriptorPayload(Mode: SessionPayloads.SubagentModeContinuable, Provider: provider, Model: model),
                                new Session.AppendOptions(Ignorable: true));
                            _ = bootstrapper.Sessions.Persistence?.FlushAsync(sessionId, requestCt);
                        }
                        return JsonSerializer.SerializeToElement(new
                        {
                            configOptions = BuildConfigOptions(provider, model),
                        }, FrameOptions);
                    }

                    case "session/load" when initialized:
                    {
                        var sessionId = RequireString(parameters, "sessionId");
                        var cwd = RequireAbsolutePath(parameters, "cwd");
                        if (sessions.ContainsKey(sessionId))
                            throw new AcpFault(-32602, $"session is already active: {sessionId}");
                        var headers = await bootstrapper.Sessions.ListPersistedAsync().ConfigureAwait(false);
                        var header = headers.FirstOrDefault(h => h.Id == sessionId)
                            ?? throw new AcpFault(-32602, $"unknown session: {sessionId}");
                        var canonical = bootstrapper.Workspaces.Ensure(cwd).Root;
                        if (!string.Equals(header.Cwd, canonical, StringComparison.OrdinalIgnoreCase))
                            throw new AcpFault(-32602, $"session cwd does not match: {cwd}");
                        var agent = await bootstrapper.Loop.ResumeAsync(sessionId, ct: requestCt).ConfigureAwait(false);
                        // Snapshot the committed log BEFORE subscribing so replayed events
                        // cannot also arrive as live updates.
                        var history = agent.Session.Events;
                        Track(agent);
                        foreach (var @event in history)
                            foreach (var update in UpdatesOf(@event, includeUser: true, agent, bootstrapper.Meter))
                                Write(output, Notification("session/update", new { sessionId, update }));
                        _ = bootstrapper.Sessions.Persistence?.FlushAsync(sessionId, requestCt);
                        return null; // ACP load resolves with a null result
                    }

                    case "session/prompt" when initialized:
                    {
                        var sessionId = RequireString(parameters, "sessionId");
                        var tracked = sessions.GetValueOrDefault(sessionId)
                            ?? throw new AcpFault(-32602, $"unknown session: {sessionId}");
                        var prompt = parameters.TryGetProperty("prompt", out var promptElement) && promptElement.ValueKind == JsonValueKind.Array
                            ? promptElement
                            : throw new AcpFault(-32602, "missing array parameter 'prompt'");
                        var text = JoinPromptBlocks(prompt);
                        var inflight = new InflightPrompt { StartSeq = tracked.Agent.Session.Seq };
                        lock (tracked)
                        {
                            if (tracked.Inflight is not null)
                                throw new AcpFault(-32602, "a prompt is already in flight for this session");
                            tracked.Inflight = inflight;
                        }
                        try
                        {
                            var message = Message.CreateUserText(text);
                            inflight.MessageId = message.Id;
                            tracked.Agent.Followup(message);
                            _ = bootstrapper.Sessions.Persistence?.FlushAsync(sessionId, requestCt);

                            await inflight.Ended.Task.ConfigureAwait(false);
                            await tracked.Agent.WhenIdleAsync().ConfigureAwait(false);
                            _ = bootstrapper.Sessions.Persistence?.FlushAsync(sessionId, requestCt);

                            if (inflight.CancelRequested)
                                return JsonSerializer.SerializeToElement(new { stopReason = "cancelled" }, FrameOptions);
                            return JsonSerializer.SerializeToElement(new { stopReason = StopReasonOf(inflight.EndReason) }, FrameOptions);
                        }
                        finally
                        {
                            lock (tracked)
                            {
                                if (tracked.Inflight == inflight) tracked.Inflight = null;
                            }
                        }
                    }

                    default:
                        if (!initialized && method is "session/new" or "session/load" or "session/prompt" or "session/cancel" or "authenticate")
                            throw new AcpFault(-32602, "not initialized: call initialize first");
                        throw new AcpFault(-32601, $"method not found: {method}");
                }
            }

            async Task HandleRequestAsync(long id, string method, JsonElement parameters, CancellationToken requestCt)
            {
                try
                {
                    var result = await HandleAsync(method, parameters, requestCt).ConfigureAwait(false);
                    Write(output, Result(id, result));
                }
                catch (AcpFault fault)
                {
                    log.WriteLine($"[acp] {method}: {fault.Message}");
                    Write(output, Fault(id, fault.Code, fault.Message));
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[acp] {method} failed: {ex.Message}");
                    Write(output, Fault(id, -32603, ex.Message));
                }
            }

            async Task HandleNotificationAsync(string method, JsonElement parameters)
            {
                if (method != "session/cancel")
                {
                    // Unknown notifications cannot fault; note and move on.
                    if (method is not ("initialize" or "session/new" or "session/load" or "session/prompt")) return;
                    throw new AcpFault(-32601, $"method not found: {method}");
                }
                if (!initialized) return;
                var sessionId = RequireString(parameters, "sessionId");
                var tracked = sessions.GetValueOrDefault(sessionId);
                if (tracked is null) return; // notifications cannot fault; dsh ignores unknown ids
                var inflight = tracked.Inflight;
                if (inflight is not null) inflight.CancelRequested = true;
                tracked.Agent.Cancel(AgentCancelCause.User());
            }

            while (!ct.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // stdin EOF: quiesce and exit
                if (line.Length == 0 || line[0] != '{') continue;

                JsonElement frame;
                try
                {
                    frame = JsonDocument.Parse(line).RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    log.WriteLine($"[acp] ignoring malformed line: {ex.Message}");
                    continue;
                }

                var id = frame.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetInt64()
                    : (long?)null;
                var method = frame.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
                    ? methodElement.GetString()!
                    : null;
                if (method is null)
                {
                    // A response to one of OUR client-ward requests (string id).
                    if (idElement.ValueKind == JsonValueKind.String
                        && clientPending.TryRemove(idElement.GetString()!, out var pendingResponse))
                    {
                        pendingResponse.TrySetResult(frame);
                    }
                    continue;
                }
                var parameters = frame.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object
                    ? paramsElement.Clone()
                    : JsonSerializer.SerializeToElement(new { }, FrameOptions);

                if (id is not null)
                {
                    // Requests run concurrently: a prompt blocks for a whole turn, and the
                    // read loop must keep serving cancel/other requests meanwhile.
                    var task = Task.Run(() => HandleRequestAsync(id.Value, method, parameters, ct), CancellationToken.None);
                    lock (outstanding) outstanding.Add(task);
                    _ = task.ContinueWith(_ => { lock (outstanding) outstanding.Remove(task); }, TaskScheduler.Default);
                }
                else
                {
                    try
                    {
                        await HandleNotificationAsync(method, parameters).ConfigureAwait(false);
                    }
                    catch (AcpFault fault)
                    {
                        log.WriteLine($"[acp] {method}: {fault.Message}");
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine($"[acp] {method} failed: {ex.Message}");
                    }
                }
            }

            // Quiesce: cancel every owned agent, drain bounded, flush, dispose.
            foreach (var tracked in sessions.Values)
            {
                if (tracked.Inflight is { } inflight) inflight.CancelRequested = true;
                tracked.Agent.Cancel(AgentCancelCause.User());
            }
            using (var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    await Task.WhenAll(sessions.Values.Select(t => t.Agent.WhenIdleAsync())).WaitAsync(drainCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    log.WriteLine("[acp] drain deadline reached; exiting");
                }
            }
            _ = bootstrapper.Sessions.Persistence?.FlushAllAsync();
            foreach (var tracked in sessions.Values)
            {
                tracked.Subscription?.Dispose();
                foreach (var connection in tracked.McpConnections) await connection.DisposeAsync().ConfigureAwait(false);
            }
            answererHandle?.Dispose();
            return 0;
        }
        finally
        {
            await bootstrapper.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Committed event → zero or more standard session/update payloads.</summary>
    private static IEnumerable<object> UpdatesOf(SessionEvent @event, bool includeUser, Agent agent, TokenMeterService? meter)
    {
        switch (@event.Type)
        {
            case SessionEventTypes.UserMessage when includeUser:
            {
                var message = SessionEventRead.MessageOf(@event);
                var text = JoinText(message.Content.OfType<TextBlock>().Select(b => b.Text));
                yield return new { sessionUpdate = "user_message_chunk", messageId = message.Id, content = TextContent(text) };
                break;
            }
            case SessionEventTypes.AssistantMessage:
            {
                var payload = SessionEventRead.AssistantMessageOf(@event);
                foreach (var block in payload.Message.Content)
                {
                    if (block is ReasoningBlock reasoning && reasoning.Text.Length > 0)
                        yield return new { sessionUpdate = "agent_thought_chunk", messageId = payload.Message.Id, content = TextContent(reasoning.Text) };
                    else if (block is TextBlock text)
                        yield return new { sessionUpdate = "agent_message_chunk", messageId = payload.Message.Id, content = TextContent(text.Text) };
                }
                var usage = UsageUpdate(agent, meter);
                if (usage is not null) yield return usage;
                break;
            }
            case SessionEventTypes.ToolCall:
            {
                var call = SessionEventRead.ToolCallOf(@event);
                object rawInput;
                try
                {
                    rawInput = JsonDocument.Parse(call.Arguments).RootElement.Clone();
                }
                catch (JsonException)
                {
                    rawInput = call.Arguments; // preserve malformed model output as opaque input
                }
                yield return new { sessionUpdate = "tool_call", toolCallId = call.CallId, title = call.Name, kind = "other", status = "in_progress", rawInput };
                break;
            }
            case SessionEventTypes.ToolResult:
            {
                var payload = SessionEventRead.ToolResultOf(@event);
                var result = payload.Message.Content.OfType<ToolResultBlock>().FirstOrDefault();
                if (result is null) yield break;
                var text = JoinText(result.Content.OfType<TextBlock>().Select(b => b.Text));
                var failed = result.IsError == true || payload.Error is not null;
                yield return new
                {
                    sessionUpdate = "tool_call_update",
                    toolCallId = result.ToolCallId,
                    status = failed ? "failed" : "completed",
                    content = new object[] { new { type = "content", content = TextContent(text) } },
                };
                break;
            }
            case SessionEventTypes.TodoWrite:
            {
                var todos = SessionEventRead.TodosOf(@event);
                yield return new
                {
                    sessionUpdate = "plan",
                    entries = todos.Select(t => new { content = t.Content, priority = "medium", status = t.Status }).ToArray(),
                };
                break;
            }
        }
    }

    /// <summary>
    /// dsh usage_update extension, deviation: emitted after every committed assistant
    /// message whenever meter and window are known (dsh also requires per-message usage,
    /// which scripted providers never produce); `used` is the cumulative usage total.
    /// </summary>
    private static object? UsageUpdate(Agent agent, TokenMeterService? meter)
    {
        if (meter is null) return null;
        var reading = meter.Measure(agent);
        if (reading.ContextWindowTokens is not { } window || window <= 0) return null;
        var used = reading.TotalInputTokens + reading.TotalOutputTokens
            + reading.TotalCacheReadTokens + reading.TotalCacheWriteTokens;
        return new { sessionUpdate = "usage_update", used, size = window };
    }

    private static string StopReasonOf(TurnEndReason? reason) => reason switch
    {
        TurnEndReason.MaxTokens => "max_tokens",
        TurnEndReason.Interrupted => "cancelled",
        TurnEndReason.Error error => throw new AcpFault(-32603, $"turn failed: {error.Message}"),
        _ => "end_turn",
    };

    private static string? MessageIdOf(SessionEvent @event)
    {
        try
        {
            return SessionEventRead.MessageOf(@event).Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Client-provided stdio MCP mounts: [{name, command, args?, env?: [{name, value}]}].</summary>
    private static IEnumerable<McpServerConfig> McpServersOf(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var server in servers.EnumerateArray())
        {
            var name = server.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
            var command = server.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String ? commandElement.GetString() : null;
            if (name is null || command is null) continue;
            var args = server.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
                ? argsElement.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList()
                : [];
            Dictionary<string, string>? env = null;
            if (server.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Array)
            {
                env = [];
                foreach (var entry in envElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var key = entry.TryGetProperty("name", out var keyElement) && keyElement.ValueKind == JsonValueKind.String ? keyElement.GetString() : null;
                    var value = entry.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString() : null;
                    if (key is { Length: > 0 } && value is not null) env[key] = value;
                }
            }
            yield return new McpServerConfig(name, command, args, env);
        }
    }

    /// <summary>The one standard config option: a provider-grouped model select, dsh shape.</summary>
    private static JsonElement BuildConfigOptions(string provider, string model)
    {
        var select = new
        {
            id = "model",
            name = "Model",
            category = "model",
            type = "select",
            currentValue = RouteValue(provider, model),
            options = ProviderCatalog.Providers.Select(p => new
            {
                @group = p,
                name = p,
                options = new[] { new { value = RouteValue(p, ProviderCatalog.DefaultModel(p)), name = ProviderCatalog.DefaultModel(p) } },
            }).ToArray(),
        };
        return JsonSerializer.SerializeToElement(new[] { select }, FrameOptions);
    }

    private static string RouteValue(string provider, string model) => JsonSerializer.Serialize(new[] { provider, model });

    /// <summary>Accepts [provider, model] arrays or {provider, model} objects for the route value.</summary>
    private static (string Provider, string Model) RouteOf(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("value", out var value))
            throw new AcpFault(-32602, "missing parameter 'value'");
        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
            if (parts.Count == 2 && parts.All(p => p.Length > 0)) return (parts[0], parts[1]);
        }
        else if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("provider", out var providerElement) && providerElement.ValueKind == JsonValueKind.String
            && value.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
        {
            return (providerElement.GetString()!, modelElement.GetString()!);
        }
        throw new AcpFault(-32602, "value must be [provider, model]");
    }

    private static bool HasContinuableDescriptor(Session session)
    {
        var events = session.Events;
        for (var i = events.Count - 1; i >= session.Header.SeedLength; i--)
        {
            if (events[i].Type != SessionEventTypes.SubagentDescriptor) continue;
            return SessionEventRead.SubagentDescriptorOf(events[i]).Mode == SessionPayloads.SubagentModeContinuable;
        }
        return false;
    }

    private sealed class WriterLogger(TextWriter writer) : McpClientService.ILogger
    {
        public void Write(string message) => writer.WriteLine("[acp-mcp] " + message);
    }

    /// <summary>Admit text content blocks only; everything else faults (capabilities advertise image/audio false).</summary>
    private static string JoinPromptBlocks(JsonElement prompt)
    {
        if (prompt.GetArrayLength() == 0)
            throw new AcpFault(-32602, "prompt must contain at least one content block");
        var joined = new StringBuilder();
        foreach (var block in prompt.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            if (type == "text" && block.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                joined.Append(textElement.GetString());
                continue;
            }
            throw new AcpFault(-32602, $"unsupported prompt content block: {type ?? "(missing type)"}");
        }
        return joined.ToString();
    }

    private static object TextContent(string text) => new { type = "text", text };

    private static string JoinText(IEnumerable<string> parts) => string.Join("", parts);

    private static string RequireString(JsonElement parameters, string name)
        => parameters.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value
            ? value
            : throw new AcpFault(-32602, $"missing string parameter '{name}'");

    private static string RequireAbsolutePath(JsonElement parameters, string name)
    {
        var value = RequireString(parameters, name);
        if (!Path.IsPathRooted(value)) throw new AcpFault(-32602, $"{name} must be an absolute path: {value}");
        return value;
    }

    private static void RejectAdditionalDirectories(JsonElement parameters)
    {
        if (parameters.TryGetProperty("additionalDirectories", out var extra)
            && extra.ValueKind == JsonValueKind.Array && extra.GetArrayLength() > 0)
        {
            throw new AcpFault(-32602, "additionalDirectories is not supported");
        }
    }

    private static void Write(TextWriter output, string frame)
    {
        lock (output) output.WriteLine(frame);
    }

    private static string Result(long id, JsonElement? result)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }, FrameOptions);

    private static string Fault(long id, int code, string message)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        }, FrameOptions);

    private static string Notification(string method, object parameters)
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, FrameOptions);
}
