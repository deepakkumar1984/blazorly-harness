using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Subagents;
using Blazorly.Harness.Core.Telemetry;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Tier 7: the generic delegation tools (foreground/background/fork/structured output), the
/// E2B remote-sandbox client against a fake transport, local telemetry aggregation, and the
/// ACP extras over the wire (request_permission round-trip, per-session config, client MCP
/// mounts).
/// </summary>
public class SubagentToolPipelineTests
{
    private static TestHarness Harness(string childReply)
        => TestHarness.Create(options =>
        {
            var sessionId = options.SessionId ?? "";
            var hasToolResults = options.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Any();
            if (sessionId.Contains("sub")) return Scripted.Text(childReply);
            if (!hasToolResults)
            {
                return Scripted.ToolCalls(("subagent_start", new { prompt = "FRESH-CHILD-TASK", description = "delegate the work" }));
            }
            return Scripted.Text("parent done");
        });

    private static async Task<Agent> RunToIdleAsync(TestHarness harness, string prompt)
    {
        var parent = harness.Loop.Create(new SessionMeta(Directory.GetCurrentDirectory()));
        var startSeq = parent.Session.Seq;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = parent.Session.Subscribe(e =>
        {
            if (e.Type == SessionEventTypes.TurnEnd && e.Seq > startSeq) ended.TrySetResult();
        });
        parent.Followup(Message.CreateUserText(prompt));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await parent.WhenIdleAsync();
        return parent;
    }

    private static string LastToolResultText(Agent agent)
        => (from e in agent.Session.Events
            where e.Type == SessionEventTypes.ToolResult
            from resultBlock in SessionEventRead.ToolResultOf(e).Message.Content.OfType<ToolResultBlock>()
            from textBlock in resultBlock.Content.OfType<TextBlock>()
            select textBlock.Text).LastOrDefault() ?? "";

    [Fact]
    public async Task SubagentStart_Foreground_DelegatesAndReturnsSummary()
    {
        await using var harness = Harness("fresh child done");
        var subagents = SubagentService.Mount(harness.Ctx);
        new SubagentToolsPlugin().Apply(harness.Ctx);

        var parent = await RunToIdleAsync(harness, "delegate some work");

        var children = subagents.ChildrenOf(parent.Id);
        Assert.Single(children);
        Assert.Equal(1, children[0].DelegationDepth);
        Assert.Contains("fresh child done", LastToolResultText(parent));
    }

    [Fact]
    public async Task SubagentStart_Background_RunsAutonomouslyAndReportsLater()
    {
        var harness = TestHarness.Create(options =>
        {
            var sessionId = options.SessionId ?? "";
            var hasToolResults = options.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Any();
            if (sessionId.Contains("sub")) return Scripted.Text("background child finished its work");
            if (!hasToolResults)
            {
                return Scripted.ToolCalls(("subagent_start", new { prompt = "BACKGROUND-TASK", mode = "background" }));
            }
            return Scripted.Text("parent done");
        });
        await using var _ = harness;
        var subagents = SubagentService.Mount(harness.Ctx);
        new SubagentToolsPlugin().Apply(harness.Ctx);

        var parent = await RunToIdleAsync(harness, "start background work");

        Assert.Contains("running", LastToolResultText(parent));
        var child = subagents.GetChild(childrenId(subagents, parent))!;
        await child.WhenIdleAsync();
        Assert.Contains("background child finished", string.Join("\n", child.Session.DeriveMessages()
            .SelectMany(m => m.Content.OfType<TextBlock>())
            .Select(b => b.Text)));
    }

    private static string childrenId(SubagentService subagents, Agent parent)
        => subagents.ChildrenOf(parent.Id).Single().Id;

    [Fact]
    public async Task SubagentStart_StructuredOutput_ValidatesOrDiagnoses()
    {
        // Valid: the child ends with a bare integer matching the schema.
        var validHarness = TestHarness.Create(options =>
        {
            var sessionId = options.SessionId ?? "";
            var hasToolResults = options.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Any();
            if (sessionId.Contains("sub")) return Scripted.Text("42");
            if (!hasToolResults)
            {
                return Scripted.ToolCalls(("subagent_start", new
                {
                    prompt = "STRUCTURED-TASK",
                    output_schema = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"integer\"}"),
                }));
            }
            return Scripted.Text("parent done");
        });
        await using (var harness = validHarness)
        {
            SubagentService.Mount(harness.Ctx);
            new SubagentToolsPlugin().Apply(harness.Ctx);
            var parent = await RunToIdleAsync(harness, "run structured");
            Assert.Equal("42", LastToolResultText(parent).Trim());
        }

        // Invalid: prose instead of JSON produces the safe diagnostic with the raw text kept.
        var invalidHarness = TestHarness.Create(options =>
        {
            var sessionId = options.SessionId ?? "";
            var hasToolResults = options.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Any();
            if (sessionId.Contains("sub")) return Scripted.Text("I refuse to emit JSON");
            if (!hasToolResults)
            {
                return Scripted.ToolCalls(("subagent_start", new
                {
                    prompt = "STRUCTURED-TASK",
                    output_schema = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"integer\"}"),
                }));
            }
            return Scripted.Text("parent done");
        });
        await using (var harness = invalidHarness)
        {
            SubagentService.Mount(harness.Ctx);
            new SubagentToolsPlugin().Apply(harness.Ctx);
            var parent = await RunToIdleAsync(harness, "run structured");
            var text = LastToolResultText(parent);
            Assert.Contains("structured output", text);
            Assert.Contains("I refuse to emit JSON", text);
        }
    }

    [Fact]
    public async Task SubagentStart_Fork_SeedsChildWithParentPrefix()
    {
        var harness = TestHarness.Create(options =>
        {
            var allText = string.Join("\n", options.Messages.SelectMany(m => m.Content).OfType<TextBlock>().Select(b => b.Text));
            if (allText.Contains("Task: FORK-CHILD-TASK")) return Scripted.Text("fork child done");
            if (options.Messages.SelectMany(m => m.Content).OfType<ToolCallBlock>().Any(c => c.Name == "subagent_start"))
                return Scripted.Text("parent done");
            if (allText.Contains("FORK-NOW"))
            {
                return Scripted.ToolCalls(("subagent_start", new { prompt = "FORK-CHILD-TASK", fork = true }));
            }
            if (options.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Any()) return Scripted.Text("context seeded");
            return Scripted.ToolCalls(("bash", new { command = "echo seed", description = "seed context" }));
        });
        await using var _ = harness;
        var subagents = SubagentService.Mount(harness.Ctx);
        new SubagentToolsPlugin().Apply(harness.Ctx);

        var parent = await RunToIdleAsync(harness, "SEED-CONTEXT: keep this context in the fork");
        var startSeq = parent.Session.Seq;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = parent.Session.Subscribe(e =>
        {
            if (e.Type == SessionEventTypes.TurnEnd && e.Seq > startSeq) ended.TrySetResult();
        });
        parent.Followup(Message.CreateUserText("FORK-NOW"));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await parent.WhenIdleAsync();

        var child = subagents.GetChild(subagents.ChildrenOf(parent.Id).Single().Id)!;
        Assert.True(child.Session.Header.SeedLength > 0, "forked child carries the parent's log prefix");
        var childText = string.Join("\n", child.Session.DeriveMessages()
            .SelectMany(m => m.Content.OfType<TextBlock>())
            .Select(b => b.Text));
        Assert.Contains("SEED-CONTEXT", childText);
        Assert.Contains("fork child done", childText);
}
}

public class E2bClientTests
{
    private sealed class FakeE2bHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Body, string? ApiKey)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            request.Headers.TryGetValues("X-API-Key", out var apiKeys);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body, apiKeys?.FirstOrDefault()));
            var path = request.RequestUri.AbsolutePath;
            HttpResponseMessage response;
            if (request.Method == HttpMethod.Post && path.EndsWith("/sandboxes"))
            {
                response = new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"sandboxID\":\"sb-123\"}"),
                };
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/process.Process/Start"))
            {
                response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"event\":{\"stdout\":\"hello out\\n\",\"exitCode\":0}}"),
                };
            }
            else if (request.Method == HttpMethod.Delete)
            {
                response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            else
            {
                response = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Lifecycle_UsesE2bManagementApiAndParsesExecOutput()
    {
        var handler = new FakeE2bHandler();
        using var client = new Blazorly.Harness.Core.RemoteSandbox.E2bSandboxClient(
            new HttpClient(handler),
            new Blazorly.Harness.Core.RemoteSandbox.E2bOptions { ApiKey = "secret-key", EnvdBaseUrl = "http://fake.test/envd" });

        var sandboxId = await client.CreateAsync();
        Assert.Equal("sb-123", sandboxId);
        Assert.Equal("secret-key", handler.Requests[0].ApiKey);

        var exec = await client.ExecAsync(sandboxId, "echo hello");
        Assert.Equal(0, exec.ExitCode);
        Assert.Equal("hello out\n", exec.Stdout);

        await client.KillAsync(sandboxId);

        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.EndsWith("/sandboxes", handler.Requests[0].Url);
        Assert.Contains("templateID", handler.Requests[0].Body);
        Assert.StartsWith("http://fake.test/envd", handler.Requests[1].Url);
        Assert.EndsWith("/process.Process/Start", handler.Requests[1].Url);
        Assert.Contains("echo hello", handler.Requests[1].Body);
        Assert.Equal("DELETE", handler.Requests[2].Method);
        Assert.EndsWith("/sandboxes/sb-123", handler.Requests[2].Url);
    }

    [Fact]
    public void ParseExecResponse_DegradesDefensivelyOnGarbage()
    {
        var garbage = Blazorly.Harness.Core.RemoteSandbox.E2bSandboxClient.ParseExecResponse("not json at all"u8.ToArray());
        Assert.Equal(1, garbage.ExitCode);
        Assert.Contains("not json", garbage.Stdout + garbage.Stderr);

        var blank = Blazorly.Harness.Core.RemoteSandbox.E2bSandboxClient.ParseExecResponse(" "u8.ToArray());
        Assert.Equal(1, blank.ExitCode);
    }
}

public class TelemetryTests
{
    private static async Task<Agent> RunTurnAsync(TestHarness harness, string prompt)
    {
        var agent = harness.CreateAgent();
        var startSeq = agent.Session.Seq;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = agent.Session.Subscribe(e =>
        {
            if (e.Type == SessionEventTypes.TurnEnd && e.Seq > startSeq) ended.TrySetResult();
        });
        agent.Followup(Message.CreateUserText(prompt));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await agent.WhenIdleAsync();
        return agent;
    }

    [Fact]
    public async Task AggregatesTurnsMessagesAndToolCalls_PersistsAcrossReload()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "blazorly-tel-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var harness = TestHarness.Create(options =>
            {
                var hasToolResults = options.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Any();
                return hasToolResults
                    ? Scripted.Text("done")
                    : Scripted.ToolCalls(("bash", new { command = "echo telemetry" }));
            });
            await using (var _ = harness)
            {
                var telemetry = UsageTelemetryService.Mount(harness.Ctx, storePath, enabled: true);
                await RunTurnAsync(harness, "make a tool call");

                var snapshot = telemetry.Snapshot();
                Assert.True(snapshot.Enabled);
                var day = Assert.Single(snapshot.Days);
                Assert.True(day.Turns >= 1);
                Assert.True(day.AssistantMessages >= 1);
                Assert.True(day.ToolCalls.TryGetValue("bash", out var calls) && calls == 1);
            }

            // Reload from the store in a fresh context: the aggregates survive.
            await using var kernel2 = Blazorly.Harness.Kernel.HarnessContext.CreateRoot();
            var reloaded = UsageTelemetryService.Mount(kernel2, storePath, enabled: true);
            var reloadedSnapshot = reloaded.Snapshot();
            var reloadedDay = Assert.Single(reloadedSnapshot.Days);
            Assert.True(reloadedDay.ToolCalls.TryGetValue("bash", out var reloadedCalls) && reloadedCalls == 1);
        }
        finally
        {
            try { File.Delete(storePath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Disabled_CollectsNothing()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "blazorly-tel-off-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var harness = TestHarness.Create(_ => Scripted.Text("done"));
            await using var _ = harness;
            var telemetry = UsageTelemetryService.Mount(harness.Ctx, storePath, enabled: false);
            await RunTurnAsync(harness, "no collection");
            Assert.False(telemetry.Snapshot().Enabled);
            Assert.Empty(telemetry.Snapshot().Days);
            Assert.False(File.Exists(storePath));
        }
        finally
        {
            try { File.Delete(storePath); } catch (IOException) { }
        }
    }
}

/// <summary>
/// ACP extras over the real stdio process: the request_permission round-trip in ask mode
/// (allow and reject), per-session model config, and client-provided MCP mounts.
/// </summary>
[Collection("BlazorlyHome")]
public class AcpTier7WireTests : BootstrapperTestBase
{
    private string Workspace() => Path.Combine(Path.GetTempPath(), "blazorly-acp7-ws-" + Guid.NewGuid().ToString("N")[..8]);

    private static object[] TextPrompt(string text) => [new { type = "text", text }];

    [Fact]
    public async Task PermissionAsk_RoundTripsRequestPermission_ToAllowAndReject()
    {
        var workspace = Workspace();
        await using var client = AcpTestClient.Spawn(Home, workspace, chunkDelayMs: 100, permission: "ask");
        await client.RequestAsync("initialize");
        var sessionId = (await client.RequestAsync("session/new", new { cwd = workspace })).GetProperty("sessionId").GetString()!;

        client.AutoPermissionOptionId = "allow-once";
        Assert.Equal("end_turn", (await client.RequestAsync("session/prompt", new { sessionId, prompt = TextPrompt("run the demo") }))
            .GetProperty("stopReason").GetString());
        var permissionRequests = client.ServerRequests.Count(r => r.Method == "session/request_permission");
        Assert.True(permissionRequests >= 2, "bash and todo_write both ask in ask mode");
        var updates = client.Updates(sessionId);
        Assert.Contains(updates, u => u.GetProperty("sessionUpdate").GetString() == "tool_call_update"
            && u.GetProperty("status").GetString() == "completed"
            && u.GetProperty("toolCallId").GetString() == BashCallId(updates));
        Assert.Contains("hello from blazorly harness", BashOutput(updates));

        client.AutoPermissionOptionId = "reject-once";
        var rejectSession = (await client.RequestAsync("session/new", new { cwd = workspace })).GetProperty("sessionId").GetString()!;
        Assert.Equal("end_turn", (await client.RequestAsync("session/prompt", new { sessionId = rejectSession, prompt = TextPrompt("run it again") }))
            .GetProperty("stopReason").GetString());
        var rejectUpdates = client.Updates(rejectSession);
        Assert.Contains(rejectUpdates, u => u.GetProperty("sessionUpdate").GetString() == "tool_call_update"
            && u.GetProperty("status").GetString() == "failed"
            && u.GetProperty("toolCallId").GetString() == BashCallId(rejectUpdates));
    }

    private static string? BashCallId(List<JsonElement> updates, int skip = 0)
        => updates.Where(u => u.GetProperty("sessionUpdate").GetString() == "tool_call"
                && u.GetProperty("title").GetString() == "bash")
            .Skip(skip)
            .Select(u => u.GetProperty("toolCallId").GetString())
            .FirstOrDefault();

    private static string BashOutput(List<JsonElement> updates)
    {
        var bashId = BashCallId(updates);
        return updates
            .Where(u => u.GetProperty("sessionUpdate").GetString() == "tool_call_update"
                && u.GetProperty("toolCallId").GetString() == bashId)
            .Select(u => u.GetProperty("content")[0].GetProperty("content").GetProperty("text").GetString())
            .FirstOrDefault("") ?? "";
    }

    [Fact]
    public async Task SetConfigOption_UpdatesRoute_AndRefusesWhileBusy()
    {
        var workspace = Workspace();
        await using var client = AcpTestClient.Spawn(Home, workspace, chunkDelayMs: 120);
        await client.RequestAsync("initialize");
        var created = await client.RequestAsync("session/new", new { cwd = workspace });
        var sessionId = created.GetProperty("sessionId").GetString()!;
        var configOptions = created.GetProperty("configOptions");
        Assert.Equal("model", configOptions[0].GetProperty("id").GetString());

        var updated = await client.RequestAsync("session/set_config_option", new
        {
            sessionId,
            configId = "model",
            value = new[] { "scripted", "test" },
        });
        Assert.Equal(JsonSerializer.Serialize(new[] { "scripted", "test" }),
            updated.GetProperty("configOptions")[0].GetProperty("currentValue").GetString());

        var promptTask = client.RequestAsync("session/prompt", new { sessionId, prompt = TextPrompt("start the demo") });
        await client.WaitForUpdateAsync(sessionId, "tool_call");
        var busy = await Assert.ThrowsAsync<AcpServerFaultException>(() => client.RequestAsync("session/set_config_option", new
        {
            sessionId,
            configId = "model",
            value = new[] { "scripted", "test" },
        }));
        Assert.Equal(-32602, busy.Code);
        await client.NotifyAsync("session/cancel", new { sessionId });
        Assert.Equal("cancelled", (await promptTask).GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task ClientMcpServers_AreMountedHandshakenPerSession()
    {
        var workspace = Workspace();
        var marker = Path.Combine(Path.GetTempPath(), "blazorly-mcp-marker-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var script = Path.Combine(Path.GetTempPath(), "blazorly-mcp-fake-" + Guid.NewGuid().ToString("N")[..8] + ".py");
        var scriptBody = """
    import sys, json

    def send(obj):
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()

    for line in sys.stdin:
        if not line.strip():
            continue
        msg = json.loads(line)
        method = msg.get("method")
        if method == "initialize":
            send({"jsonrpc": "2.0", "id": msg["id"], "result": {
                "protocolVersion": "2024-11-05", "capabilities": {"tools": {}},
                "serverInfo": {"name": "fake", "version": "0.0.1"}}})
        elif method == "notifications/initialized":
            pass
        elif method == "tools/list":
            open("MARKER", "a").write("listed\n")
            send({"jsonrpc": "2.0", "id": msg["id"], "result": {"tools": []}})
    """;
await File.WriteAllTextAsync(script, scriptBody.Replace("MARKER", marker));
        try
        {
            await using var client = AcpTestClient.Spawn(Home, workspace);
            await client.RequestAsync("initialize");
            var created = await client.RequestAsync("session/new", new
            {
                cwd = workspace,
                mcpServers = new[] { new { name = "testsrv", command = "python3", args = new[] { script } } },
            });
            Assert.Contains("session-", created.GetProperty("sessionId").GetString());

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline && !File.Exists(marker)) await Task.Delay(100);
            Assert.True(File.Exists(marker), "the mounted MCP server was never handed a tools/list");
        }
        finally
        {
            try { File.Delete(script); } catch (IOException) { }
            try { File.Delete(marker); } catch (IOException) { }
        }
    }
}
