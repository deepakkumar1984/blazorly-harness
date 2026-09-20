using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Decisions;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;

namespace Blazorly.Harness.Tests;

/// <summary>
/// The tool gate is the hybrid selector: deterministic rules carry the easy cases with zero AI
/// calls, and the decision model only sees the ambiguous tail. Its safety contract is
/// degrade-to-full — every disable, under-threshold count, or failure must return the assembly
/// with the complete tool list.
/// </summary>
public class ToolGateTests
{
    private static ToolSchema Schema(string name, string description = "does things")
        => new(name, description, JsonDocument.Parse("{}").RootElement.Clone());

    private static PromptAssembly Assembly(IReadOnlyList<ToolSchema> schemas) => new()
    {
        Sections = [],
        ContextSections = [],
        Variables = new Dictionary<string, string>(),
        ToolSchemas = schemas,
    };

    private static async Task<Agent> AgentWithBriefAsync(TestHarness harness, string brief)
    {
        var agent = harness.CreateAgent();
        agent.Followup(Message.CreateUserText(brief));
        await agent.WhenIdleAsync();
        return agent;
    }

    [Fact]
    public async Task Disabled_Or_UnderThreshold_PassesThrough()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        var agent = await AgentWithBriefAsync(harness, "search the web");
        var assembly = Assembly(Enumerable.Range(0, 40).Select(i => Schema($"tool_{i}")).ToArray());

        var off = new ToolGateService(null, new ToolGateOptions { Enabled = false });
        Assert.Same(assembly, await off.FilterAsync(agent, assembly));

        var small = Assembly([Schema("read"), Schema("bash"), Schema("grep")]);
        var under = new ToolGateService(null, new ToolGateOptions { Enabled = true, MaxTools = 28 });
        Assert.Same(small, await under.FilterAsync(agent, small));

        var stats = under.Stats();
        Assert.Equal(1, stats.Requests);
        Assert.Equal(1, stats.PassedThrough);
        Assert.Equal(0, stats.Engaged);
    }

    [Fact]
    public async Task Engaged_KeepsCoreRecentAndLexical_TopTools()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        var agent = await AgentWithBriefAsync(harness, "find and fix the failing tests, then run the suite");
        var schemas = new List<ToolSchema>
        {
            Schema("read"), Schema("write"), Schema("edit"), Schema("bash"), Schema("grep"), Schema("glob"),
            Schema("todo_write"), Schema("ask_user_question"),
            Schema("test_runner", "run the unit test suite"),          // lexical hit: "tests"/"suite"
            Schema("failing_probe", "diagnose failing tests"),          // lexical hit: "failing tests"
            Schema("web_search", "search the open web"),
            Schema("terminal_send", "send keystrokes to a terminal"),
        };
        schemas.AddRange(Enumerable.Range(0, 30).Select(i => Schema($"mcp_tool_{i}", "an unrelated integration")));

        var gate = new ToolGateService(null, new ToolGateOptions { Enabled = true, MaxTools = 28, Keep = 14 });
        var filtered = await gate.FilterAsync(agent, Assembly([.. schemas]));

        var names = filtered.ToolSchemas.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        // core always kept
        Assert.Contains("read", names);
        Assert.Contains("bash", names);
        Assert.Contains("todo_write", names);
        // lexically relevant kept over the unrelated bulk
        Assert.Contains("test_runner", names);
        Assert.Contains("failing_probe", names);
        // shortlist honored: unrelated bulk pruned
        Assert.True(filtered.ToolSchemas.Count < schemas.Count, $"shortlist {filtered.ToolSchemas.Count} vs {schemas.Count}");
        Assert.DoesNotContain("mcp_tool_29", names);

        var stats = gate.Stats();
        Assert.Equal(1, stats.Engaged);
        Assert.Equal(schemas.Count, stats.ToolsIn);
        Assert.Equal(filtered.ToolSchemas.Count, stats.ToolsOut);
        Assert.True(stats.SchemaCharsSaved > 0.5); // most of the schema payload stopped being sent
    }

    [Fact]
    public async Task RecentlyUsedTools_StayKept_EvenWhenLexicallyUnrelated()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        var agent = await AgentWithBriefAsync(harness, "totally different direction now");
        // the agent used an MCP tool earlier in the session: it is load-bearing for the task
        var usedHarness = TestHarness.Create(options =>
        {
            var hasToolResults = options.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Any();
            return hasToolResults
                ? Scripted.Text("done with the ticket")
                : Scripted.ToolCall("mcp_jira_ticket", new { query = "BLZ-1" });
        });
        await using var _h = usedHarness;
        var used = usedHarness.CreateAgent();
        used.Followup(Message.CreateUserText("update the jira ticket BLZ-1"));
        await used.WhenIdleAsync();
        Assert.Contains(used.Session.Events, e => e.Type == SessionEventTypes.ToolCall);

        var schemas = Enumerable.Range(0, 40)
            .Select(i => i == 0 ? Schema("mcp_jira_ticket", "jira integration") : Schema($"filler_{i}"))
            .ToList();
        var gate = new ToolGateService(null, new ToolGateOptions { Enabled = true, MaxTools = 28, Keep = 20 });
        var filtered = await gate.FilterAsync(used, Assembly(schemas));
        Assert.Contains("mcp_jira_ticket", filtered.ToolSchemas.Select(s => s.Name));
    }

    [Fact]
    public async Task AmbiguousTail_ConsultsTheDecisionModel_AndMergesPicks()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        var agent = await AgentWithBriefAsync(harness, "help"); // no lexical signal at all

        var model = new ScriptedDecisionModel((state, questions) => new DecisionResult(
            "scripted",
            new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
            {
                [ToolGateService.ChoiceKey] = new DecisionAnswer(ToolGateService.ChoiceKey, DecisionAnswer.ChoiceKind)
                {
                    Options = new Dictionary<string, double>
                    {
                        ["mcp_wiki_search"] = 0.9,
                        ["mcp_calendar"] = 0.4,
                        ["filler_39"] = 0.05,
                    },
                },
            },
            LatencyMs: 12, StateChars: state.Chars));

        var decisions = new DecisionService(model, new DecisionOptions { Enabled = true, Seams = ["*"], CacheSeconds = 0 });
        var schemas = Enumerable.Range(0, 40)
            .Select(i => i == 0 ? Schema("mcp_wiki_search", "wiki") : i == 1 ? Schema("mcp_calendar", "calendar") : Schema($"filler_{i}"))
            .ToList();

        var gate = new ToolGateService(decisions, new ToolGateOptions { Enabled = true, MaxTools = 28, Keep = 12, MinLexical = 6 });
        var filtered = await gate.FilterAsync(agent, Assembly(schemas));

        var names = filtered.ToolSchemas.Select(s => s.Name).ToList();
        Assert.Contains("mcp_wiki_search", names); // top pick merged
        Assert.Contains("mcp_calendar", names);    // second pick merged
        Assert.Equal(1, gate.Stats().ModelTailCalls);
        Assert.Equal(1, decisions.Stats().PerSeam.GetValueOrDefault(DecisionSeams.ToolGate));
    }

    [Fact]
    public async Task DecisionFailure_FallsBackToTheDeterministicShortlist()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        var agent = await AgentWithBriefAsync(harness, "help");

        var model = new ScriptedDecisionModel((state, questions) => new DecisionResult(
            "scripted", new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal), 0, state.Chars,
            Degraded: true, Error: "endpoint down"));
        var decisions = new DecisionService(model, new DecisionOptions { Enabled = true, Seams = ["*"], CacheSeconds = 0 });
        var schemas = new List<ToolSchema> { Schema("read"), Schema("bash"), Schema("grep"), Schema("todo_write") };
        schemas.AddRange(Enumerable.Range(0, 40).Select(i => Schema($"filler_{i}")));

        var gate = new ToolGateService(decisions, new ToolGateOptions { Enabled = true, MaxTools = 28, Keep = 10, MinLexical = 6 });
        var filtered = await gate.FilterAsync(agent, Assembly(schemas));

        // degraded opinion → core tools still kept, request still filtered, nothing throws
        var names = filtered.ToolSchemas.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("read", names);
        Assert.Contains("bash", names);
        Assert.True(filtered.ToolSchemas.Count < schemas.Count);
    }

    [Fact]
    public async Task NothingScoresAtAll_PassesTheFullListThrough()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("ok"));
        var agent = await AgentWithBriefAsync(harness, "help");
        // no core tools present, no lexical signal, no decision model: an empty shortlist must
        // never be sent — the full list is the only safe answer
        var schemas = Enumerable.Range(0, 40).Select(i => Schema($"filler_{i}")).ToList();
        var gate = new ToolGateService(null, new ToolGateOptions { Enabled = true, MaxTools = 28, Keep = 10, MinLexical = 6 });
        var filtered = await gate.FilterAsync(agent, Assembly(schemas));
        Assert.Equal(schemas.Count, filtered.ToolSchemas.Count);
        Assert.Equal(1, gate.Stats().PassedThrough);
        Assert.Equal(0, gate.Stats().Engaged);
    }

    [Fact]
    public void Seams_Vocabulary_IncludesTheWiredToolGate()
    {
        Assert.Contains(DecisionSeams.ToolGate, DecisionSeams.Wired);
        Assert.Contains(DecisionSeams.ToolGate, DecisionSeams.Known);
        Assert.True(DecisionSeams.IsWired(DecisionSeams.ToolGate));
    }

    [Fact]
    public async Task DriverIntegration_RequestCarriesTheShortlistNotTheFullSet()
    {
        var seenTools = new List<GenerateOptions>();
        await using var harness = TestHarness.Create(options =>
        {
            if (options.Tools is { Count: > 0 }) seenTools.Add(options);
            return Scripted.Text("done");
        });
        var agent = harness.CreateAgent();

        // 40 visible tools, gate on, seam off → pure-code path in the real driver
        var gate = new ToolGateService(null, new ToolGateOptions { Enabled = true, MaxTools = 28, Keep = 16 });
        harness.Ctx.Provide(ToolGateService.ServiceKey, gate);
        for (var i = 0; i < 40; i++)
            harness.Tools.Register(new DummyTool($"dummy_{i}"));

        agent.Followup(Message.CreateUserText("read the config and summarize"));
        await agent.WhenIdleAsync();

        Assert.NotEmpty(seenTools);
        Assert.All(seenTools, o => Assert.True(o.Tools.Count <= 20, $"carried {o.Tools.Count} tools"));
        var last = seenTools[^1];
        Assert.Contains("read", last.Tools.Select(t => t.Name));
    }

    private sealed record DummyArgs(string Value = "");

    private sealed class DummyTool(string name) : Blazorly.Harness.Core.Tools.ToolDefinition<DummyArgs, string>
    {
        public override string Name { get; } = name;
        public override string Description => "dummy tool for gate tests";
        public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object();
        public override JsonSchema.Schema Output { get; } = JsonSchema.String();
        protected override Task<string> ExecuteTyped(DummyArgs args, ToolRunContext exec) => throw new InvalidOperationException("never called");
        protected override IReadOnlyList<ContentBlock> RenderTyped(DummyArgs args, string output) => [new TextBlock("dummy")];
    }
}
