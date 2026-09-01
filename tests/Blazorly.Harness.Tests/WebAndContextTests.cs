using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

internal static class WebAndContextKit
{
    public static ToolExecutionInput Input(string name, object args, Agent? agent = null) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(args),
        CallId = "call_" + name,
        Signal = CancellationToken.None,
        Agent = agent,
    };

    public static string Text(ToolExecutionResult result)
        => string.Join("\n", result.Content.OfType<TextBlock>().Select(b => b.Text));

    public static JsonElement Json(ToolExecutionResult result) => result.Value!.Value;
}

/// <summary>An in-process HTTP server with canned GET responses for the web tools.</summary>
internal sealed class CannedWebServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public CannedWebServer()
    {
        var port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{port}";
        _loop = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public Uri SearchUrl => new(BaseUrl + "/search");

    public Uri EmptySearchUrl => new(BaseUrl + "/search-empty");

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            try
            {
                Respond(context);
            }
            catch
            {
                // a failing response must not kill the loop
            }
        }
    }

    private static void Respond(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        var (status, contentType, body) = path switch
        {
            "/page" => (200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PageHtml)),
            "/text" => (200, "text/plain", Encoding.UTF8.GetBytes("plain text body")),
            "/binary" => (200, "application/octet-stream", new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            "/search" => (200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(SearchHtml)),
            "/search-empty" => (200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes("<html><body><div class=\"links\">nothing here</div></body></html>")),
            _ => (404, "text/plain", Encoding.UTF8.GetBytes("not found")),
        };
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Close();
    }

    private static readonly string PageHtml =
        "<html><head><title>Docs</title><style>body { color: red; }</style>"
        + "<script>alert('should be stripped');</script></head><body><h1>Hello Harness</h1><p>"
        + string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 1200))
        + "</p></body></html>";

    private const string SearchHtml =
        """
        <html><body>
        <div class="result">
        <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fdocs&rut=xyz">Example <b>Docs</b></a>
        <a class="result__snippet">Everything about <em>harness</em> docs.</a>
        </div>
        <div class="result">
        <a class="result__a" href="https://example.org/second">Second Result</a>
        <a class="result__snippet">Second snippet text</a>
        </div>
        </body></html>
        """;

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* loop teardown is best-effort */ }
    }
}

public class WebToolsTests
{
    private static async Task<TestHarness> HarnessWithProvider(CannedWebServer server, Uri searchBase)
    {
        var harness = await Task.FromResult(TestHarness.Create());
        new WebPlugin(new HttpWebProvider(searchBase)).Apply(harness.Ctx);
        return harness;
    }

    [Fact]
    public async Task Fetch_StripsHtmlAndCapsAtLimit()
    {
        using var server = new CannedWebServer();
        await using var harness = await HarnessWithProvider(server, server.SearchUrl);

        var result = await harness.Tools.Execute(WebAndContextKit.Input("web_fetch", new { url = server.BaseUrl + "/page" }));
        Assert.False(result.IsError);
        Assert.Equal(200, WebAndContextKit.Json(result).GetProperty("status").GetInt32());
        var text = WebAndContextKit.Json(result).GetProperty("text").GetString()!;
        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color", text);
        Assert.Contains("Hello Harness", text);
        Assert.Equal(WebLimits.MaxFetchChars, text.Length);
        Assert.True(WebAndContextKit.Json(result).GetProperty("truncated").GetBoolean());
        Assert.Contains("truncated", WebAndContextKit.Text(result));
    }

    [Fact]
    public async Task Fetch_ReturnsRawTextAndNotesBinaryContent()
    {
        using var server = new CannedWebServer();
        await using var harness = await HarnessWithProvider(server, server.SearchUrl);

        var text = await harness.Tools.Execute(WebAndContextKit.Input("web_fetch", new { url = server.BaseUrl + "/text" }));
        Assert.False(text.IsError);
        Assert.Equal("plain text body", WebAndContextKit.Json(text).GetProperty("text").GetString());

        var binary = await harness.Tools.Execute(WebAndContextKit.Input("web_fetch", new { url = server.BaseUrl + "/binary" }));
        Assert.False(binary.IsError);
        Assert.Equal("(binary content)", WebAndContextKit.Json(binary).GetProperty("text").GetString());
        Assert.False(WebAndContextKit.Json(binary).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Fetch_Non2xx_FailsWithHttpStatus()
    {
        using var server = new CannedWebServer();
        await using var harness = await HarnessWithProvider(server, server.SearchUrl);

        var result = await harness.Tools.Execute(WebAndContextKit.Input("web_fetch", new { url = server.BaseUrl + "/missing" }));
        Assert.True(result.IsError);
        Assert.Equal("WEB_FETCH_FAILED", result.Error!.Info!.Code);
        Assert.Contains("HTTP 404", result.Error.Message);
    }

    [Fact]
    public async Task Search_ParsesResultsAndDecodesUddgRedirect()
    {
        using var server = new CannedWebServer();
        await using var harness = await HarnessWithProvider(server, server.SearchUrl);

        Assert.Equal(ToolRuntime.Mode.Parallel,
            harness.Tools.ExecutionMode("web_search", JsonSerializer.SerializeToElement(new { query = "x" }), null));

        var result = await harness.Tools.Execute(WebAndContextKit.Input("web_search", new { query = "harness docs" }));
        Assert.False(result.IsError);
        var results = WebAndContextKit.Json(result).GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());

        var first = results[0];
        Assert.Equal("Example Docs", first.GetProperty("title").GetString());
        Assert.Equal("https://example.com/docs", first.GetProperty("url").GetString());
        Assert.Contains("harness", first.GetProperty("snippet").GetString());

        Assert.Equal("Second Result", results[1].GetProperty("title").GetString());
        Assert.Equal("https://example.org/second", results[1].GetProperty("url").GetString());
        Assert.Contains("Second snippet text", WebAndContextKit.Text(result));
    }

    [Fact]
    public async Task Search_NoResults_FailsClosed()
    {
        using var server = new CannedWebServer();
        await using var harness = await HarnessWithProvider(server, server.EmptySearchUrl);

        var result = await harness.Tools.Execute(WebAndContextKit.Input("web_search", new { query = "zzz nothing" }));
        Assert.True(result.IsError);
        Assert.Equal("WEB_SEARCH_EMPTY", result.Error!.Info!.Code);
    }
}

public class SkillToolsTests
{
    private static void WriteSkill(string root, string dir, string name, string description, string body)
    {
        var path = Path.Combine(root, dir);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");
    }

    private static string TempSkillsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "blazorly-skills-" + Guid.NewGuid().ToString("N"));
        WriteSkill(root, "beta", "beta", "Beta skill", "Beta body: check the diff line by line.");
        WriteSkill(root, "alpha", "alpha", "Alpha skill", "Alpha body: run the setup script first.");
        return root;
    }

    [Fact]
    public void List_ReturnsCatalogSortedByName()
    {
        var root = TempSkillsRoot();
        try
        {
            var service = new SkillsService(root);
            var catalog = service.List();
            Assert.Equal(["alpha", "beta"], catalog.Select(s => s.Name));
            Assert.Equal("Alpha skill", catalog[0].Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SkillTool_LoadsBodyThroughOutputContract()
    {
        var root = TempSkillsRoot();
        try
        {
            await using var harness = TestHarness.Create();
            harness.Tools.Register(new SkillTool(new SkillsService(root)));

            var result = await harness.Tools.Execute(WebAndContextKit.Input("skill", new { name = "beta" }));
            Assert.False(result.IsError);
            Assert.Equal("beta", WebAndContextKit.Json(result).GetProperty("name").GetString());
            Assert.Contains("Beta body: check the diff line by line.", WebAndContextKit.Json(result).GetProperty("body").GetString());
            Assert.Contains("Beta body", WebAndContextKit.Text(result));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SkillTool_UnknownSkill_FailsClosed()
    {
        var root = TempSkillsRoot();
        try
        {
            await using var harness = TestHarness.Create();
            harness.Tools.Register(new SkillTool(new SkillsService(root)));

            var result = await harness.Tools.Execute(WebAndContextKit.Input("skill", new { name = "nope" }));
            Assert.True(result.IsError);
            Assert.Equal("UNKNOWN_SKILL", result.Error!.Info!.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SkillPlugin_RegistersToolAndPromptSection()
    {
        var root = TempSkillsRoot();
        try
        {
            await using var harness = TestHarness.Create();
            new SkillPlugin(new SkillsService(root)).Apply(harness.Ctx);

            Assert.NotNull(harness.Tools.Get("skill"));
            var section = harness.Prompt.Assemble(null, null).Sections.Single(s => s.Name == "skills");
            Assert.Equal(108, section.Order);
            Assert.Contains("alpha", section.Text);
            Assert.Contains("beta", section.Text);
            Assert.Contains("call the skill tool", section.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class AskUserToolTests
{
    private static object AskPayload() => new
    {
        questions = new object[]
        {
            new
            {
                id = "q1",
                question = "Proceed with deployment?",
                header = "Confirm",
                options = new object[]
                {
                    new { label = "Yes (Recommended)", description = "Ship it" },
                    new { label = "No" },
                },
                multi_select = false,
            },
        },
    };

    [Fact]
    public async Task AskUser_ForwardsQuestionsAndRendersAnswers()
    {
        await using var harness = TestHarness.Create();
        var service = UserQuestionsService.Mount(harness.Ctx);
        IReadOnlyList<AskQuestion>? asked = null;
        service.SetProvider((questions, _) =>
        {
            asked = questions;
            return Task.FromResult<IReadOnlyList<AskAnswer>>([new AskAnswer("q1", "yes")]);
        });
        new AskUserPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();

        var args = JsonSerializer.SerializeToElement(AskPayload());
        Assert.Equal(ToolRuntime.Mode.Exclusive, harness.Tools.ExecutionMode("ask_user_question", args, null));

        var result = await harness.Tools.Execute(WebAndContextKit.Input("ask_user_question", AskPayload(), agent));
        Assert.False(result.IsError);
        Assert.Contains("q1: yes", WebAndContextKit.Text(result));

        Assert.NotNull(asked);
        var question = Assert.Single(asked);
        Assert.Equal("q1", question.Id);
        Assert.Equal("Proceed with deployment?", question.Question);
        Assert.Equal("Confirm", question.Header);
        Assert.False(question.MultiSelect);
        Assert.Equal(2, question.Options!.Count);
        Assert.Equal("Yes (Recommended)", question.Options[0].Label);
        Assert.Equal("Ship it", question.Options[0].Description);
    }

    [Fact]
    public async Task AskUser_WithoutProviderMount_FailsClosed()
    {
        await using var harness = TestHarness.Create();
        new AskUserPlugin().Apply(harness.Ctx);
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(WebAndContextKit.Input("ask_user_question", AskPayload(), agent));
        Assert.True(result.IsError);
        Assert.Equal("NO_USER_QUESTIONS_PROVIDER", result.Error!.Info!.Code);
        Assert.Contains("no user interface is available", result.Error.Message);
    }
}

public class SessionQueryTests
{
    private static async Task<Agent> RunAgent(TestHarness harness, string userText)
    {
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText(userText));
        await agent.WhenIdleAsync();
        return agent;
    }

    [Fact]
    public async Task Search_FindsUserMessageInLiveSessions()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("done"));
        var needleAgent = await RunAgent(harness, "needle in a haystack");
        var otherAgent = await RunAgent(harness, "unrelated small talk");
        new SessionQueryPlugin().Apply(harness.Ctx);

        var result = await harness.Tools.Execute(WebAndContextKit.Input("session_search", new { query = "needle" }, needleAgent));
        Assert.False(result.IsError);
        var matches = WebAndContextKit.Json(result).GetProperty("matches");
        Assert.Equal(1, matches.GetArrayLength());
        var match = matches[0];
        Assert.Equal(needleAgent.Session.Id, match.GetProperty("sessionId").GetString());
        Assert.Equal("user", match.GetProperty("kind").GetString());
        Assert.Contains("needle", match.GetProperty("snippet").GetString());
        Assert.DoesNotContain(otherAgent.Session.Id, WebAndContextKit.Text(result));
        Assert.Contains($"{needleAgent.Session.Id} | user |", WebAndContextKit.Text(result));
    }

    [Fact]
    public async Task Search_MatchesAssistantText()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("the special thing is a needle"));
        var agent = await RunAgent(harness, "where is the special thing?");
        new SessionQueryPlugin().Apply(harness.Ctx);

        var result = await harness.Tools.Execute(WebAndContextKit.Input("session_search", new { query = "needle" }, agent));
        Assert.False(result.IsError);
        var match = Assert.Single(WebAndContextKit.Json(result).GetProperty("matches").EnumerateArray());
        Assert.Equal(agent.Session.Id, match.GetProperty("sessionId").GetString());
        Assert.Equal("assistant", match.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Search_WithoutAgent_FailsClosed()
    {
        await using var harness = TestHarness.Create();
        new SessionQueryPlugin().Apply(harness.Ctx);

        var result = await harness.Tools.Execute(WebAndContextKit.Input("session_search", new { query = "needle" }));
        Assert.True(result.IsError);
        Assert.Equal("NO_AGENT", result.Error!.Info!.Code);
    }

    [Fact]
    public async Task Search_AlsoScansPersistedSessions()
    {
        await using var harness = TestHarness.Create();
        await using var child = harness.Ctx.Extend();
        var persistence = new MemoryPersistence();
        SessionStore.Mount(child, persistence);
        var header = new SessionHeader { Id = "session-persisted", CreatedAt = 5_000, Cwd = Directory.GetCurrentDirectory() };
        persistence.Store[header.Id] = (header, new List<SessionEvent>
        {
            new()
            {
                Type = SessionEventTypes.UserMessage,
                Seq = 0,
                Time = 5_000,
                Data = SessionJson.ToElement(Message.CreateUserText("a persisted needle lives here")),
                SurfaceOp = new SurfaceOp.Append(),
            },
        });
        new SessionQueryPlugin().Apply(child);
        var agent = harness.CreateAgent();

        var result = await harness.Tools.Execute(WebAndContextKit.Input("session_search", new { query = "persisted needle" }, agent));
        Assert.False(result.IsError);
        Assert.Contains("session-persisted", WebAndContextKit.Text(result));
        var match = Assert.Single(WebAndContextKit.Json(result).GetProperty("matches").EnumerateArray());
        Assert.Equal("session-persisted", match.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task EventRead_ReturnsSeqTypeLinesAndHonorsRange()
    {
        await using var harness = TestHarness.Create(_ => ReplayScript.Text("done"));
        var agent = await RunAgent(harness, "read my events");
        new SessionQueryPlugin().Apply(harness.Ctx);

        var result = await harness.Tools.Execute(
            WebAndContextKit.Input("session_event_read", new { session_id = agent.Session.Id }, agent));
        Assert.False(result.IsError);
        var lines = WebAndContextKit.Text(result).Split('\n');
        Assert.Contains(lines, l => l.EndsWith(" " + SessionEventTypes.TurnStart));
        Assert.Contains(lines, l => l.EndsWith(" " + SessionEventTypes.UserMessage));
        Assert.Contains(lines, l => l.EndsWith(" " + SessionEventTypes.TurnEnd));
        Assert.All(lines, l => Assert.Matches(@"^\d+ \S+$", l));

        var bounded = await harness.Tools.Execute(
            WebAndContextKit.Input("session_event_read", new { session_id = agent.Session.Id, from_seq = 1, to_seq = 2 }, agent));
        Assert.False(bounded.IsError);
        var seqs = WebAndContextKit.Json(bounded).GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("seq").GetInt32()).ToList();
        Assert.Equal([1, 2], seqs);
        Assert.Equal(2, WebAndContextKit.Json(bounded).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task EventRead_UnknownSession_FailsClosed()
    {
        await using var harness = TestHarness.Create();
        new SessionQueryPlugin().Apply(harness.Ctx);

        var result = await harness.Tools.Execute(
            WebAndContextKit.Input("session_event_read", new { session_id = "session-missing" }));
        Assert.True(result.IsError);
        Assert.Equal("SESSION_NOT_FOUND", result.Error!.Info!.Code);
    }

    internal sealed class MemoryPersistence : ISessionPersistence
    {
        public readonly Dictionary<string, (SessionHeader Header, List<SessionEvent> Events)> Store = new(StringComparer.Ordinal);

        public Task CreateAsync(SessionHeader header, CancellationToken ct = default)
        {
            Store[header.Id] = (header, []);
            return Task.CompletedTask;
        }

        public Task AppendAsync(string sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct = default)
        {
            if (!Store.TryGetValue(sessionId, out var entry)) throw new InvalidOperationException("unknown session");
            entry.Events.AddRange(events);
            Store[sessionId] = entry;
            return Task.CompletedTask;
        }

        public Task<(SessionHeader Header, IReadOnlyList<SessionEvent> Events)> LoadAsync(string sessionId, CancellationToken ct = default)
            => Store.TryGetValue(sessionId, out var entry)
                ? Task.FromResult<(SessionHeader, IReadOnlyList<SessionEvent>)>((entry.Header, entry.Events))
                : throw new Kernel.HarnessException("SESSION_NOT_FOUND", $"session '{sessionId}' is not persisted");

        public Task<IReadOnlyList<SessionHeader>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionHeader>>([.. Store.Values.Select(e => e.Header)]);

        public Task FlushAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

        public Task FlushAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
