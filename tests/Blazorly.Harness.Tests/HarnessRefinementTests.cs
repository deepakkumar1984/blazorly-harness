using System.Text.Json;
using Blazorly.Harness.Cli;
using Blazorly.Harness.Core.Harnessing;

namespace Blazorly.Harness.Tests;

public sealed class HarnessRefinementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blz-refine-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private HarnessRefinementOptions Options()
    {
        var tasks = Path.Combine(_root, "tasks");
        foreach (var id in new[] { "a", "b" })
        {
            Directory.CreateDirectory(Path.Combine(tasks, id));
            File.WriteAllText(Path.Combine(tasks, id, "task.json"), """
                {"description":"Blazor component behavior","prompt":"Fix the component behavior",
                 "checks":[{"name":"private assertion","run":"SECRET_GRADER_MUST_NOT_ENTER_EDITOR"}]}
                """);
        }
        return new()
        {
            Eval = new() { TasksDir = tasks, OutDir = Path.Combine(_root, "run"), Out = new StringWriter() },
            Workspace = Path.Combine(_root, "workspace"),
            LibraryDirectory = Path.Combine(_root, "library"),
        };
    }

    private static Task<EvalSummary> Evaluate(EvalOptions options, CancellationToken ct)
    {
        var profile = HarnessProfile.Load(options.ProfilePath!);
        var a = profile.Instructions != "workflow";
        var b = profile.ToolGuidance == "domain" || profile.Instructions == "workflow";
        var results = new[] { (Id: "a", Pass: a), (Id: "b", Pass: b) }.Select(item =>
        {
            var evidence = Path.Combine(options.OutDir, "evidence", "none", item.Id);
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "session.jsonl"), "{\"seq\":2,\"data\":\"captured component execution\"}\n");
            return new EvalTaskResult(item.Id, item.Pass, "completed", 0, "session", "answer", 10, 50, 5,
                [new("check", item.Pass, item.Pass ? 0 : 1, "private grade")], null);
        }).ToList();
        HarnessArtifacts.WriteJson(Path.Combine(options.OutDir, "environment.json"), new { profile.Id });
        return Task.FromResult(new EvalSummary(2, results.Count(r => r.Pass), results.Count(r => !r.Pass), 20, 100, 10,
            results, 0, ["none"], new Dictionary<string, string>()));
    }

    [Fact]
    public async Task RefineRepairsInvalidProposalsProbesAndRetainsAProfileWithoutBaselineRegressions()
    {
        var prompts = new List<string>();
        var evaluations = 0;
        Task<HarnessEditorReply> Complete(string prompt, CancellationToken ct)
        {
            prompts.Add(prompt);
            Assert.DoesNotContain("SECRET_GRADER", prompt);
            Assert.DoesNotContain("private grade", prompt);
            var text = prompts.Count switch
            {
                1 => "{\"settings\":{\"sandboxMode\":\"full-access\"}}",
                2 => JsonSerializer.Serialize(new HarnessProfile { Instructions = "workflow" }, HarnessArtifacts.Json),
                3 => JsonSerializer.Serialize(new HarnessProfile { ToolGuidance = "domain" }, HarnessArtifacts.Json),
                4 => JsonSerializer.Serialize(new HarnessProfile { Instructions = "composed" }, HarnessArtifacts.Json),
                _ => """
                    {"cue":"Blazor component changes","component":"toolGuidance","lesson":"Inspect the component behavior before editing",
                     "verdict":"helped","confidence":"medium","evidence":[
                       {"runId":"baseline/1/none/b","eventSeq":2,"observation":"baseline omitted component guidance"},
                       {"runId":"domain/1/none/b","eventSeq":2,"observation":"candidate applied component guidance"}]}
                    """,
            };
            return Task.FromResult(new HarnessEditorReply(text, 10, 10));
        }
        Task<EvalSummary> Run(EvalOptions eval, CancellationToken ct) { evaluations++; return Evaluate(eval, ct); }
        var options = Options();
        var report = await HarnessRefinement.RunAsync(options, Complete, Run);
        Assert.Equal(4, evaluations); // baseline, two probes, composed execution
        Assert.Equal(5, report.EditorCalls); // one repair, three valid drafts, one reflection
        Assert.Contains("previous proposal was rejected", prompts[1]);
        Assert.Equal(2, report.Stages[0].Attempts);
        var adopted = HarnessProfile.Load(Path.Combine(options.Eval.OutDir, "adopted-profile.json"));
        Assert.Equal("domain", adopted.ToolGuidance);
        Assert.NotNull(report.LessonId);
        Assert.Single(new HarnessExperienceLibrary(options.LibraryDirectory!).List(
            HarnessExperienceLibrary.WorkspaceScope(options.Workspace), "coding"));
        var retained = Path.Combine(options.LibraryDirectory!, "packages", adopted.Id, "profile.json");
        Assert.Equal(adopted.Id, HarnessProfile.Load(retained).Id);
        var described = new HarnessExperienceLibrary(options.LibraryDirectory!).Describe(report.LessonId!);
        Assert.Contains("Inspect the component behavior before editing", described);
        Assert.Contains("baseline omitted component guidance", described);
        Assert.Contains("captured component execution", described);
        Assert.Contains(retained, described);
        Assert.Equal(2, report.Comparison.Comparisons.Single(c => c.ProfileId == report.AdoptedProfileId).Passed);
    }

    [Fact]
    public async Task InvalidDraftsFallBackAndUnchangedProfilesReuseTheBaselineExecution()
    {
        var calls = 0;
        var executions = 0;
        Task<HarnessEditorReply> Complete(string prompt, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new HarnessEditorReply("invalid JSON", 1, 1));
        }
        Task<EvalSummary> Run(EvalOptions eval, CancellationToken ct) { executions++; return Evaluate(eval, ct); }
        var report = await HarnessRefinement.RunAsync(Options(), Complete, Run);
        Assert.Equal(6, calls);
        Assert.Equal(1, executions);
        Assert.Equal(HarnessProfile.Baseline.Id, report.AdoptedProfileId);
        Assert.All(report.Stages, s => Assert.True(s.FellBack));
        Assert.Null(report.LessonId);
    }

    [Fact]
    public async Task MalformedReflectionLeavesTheAdoptedProfileAndRecordsTheError()
    {
        var options = Options();
        var calls = 0;
        Task<HarnessEditorReply> Complete(string prompt, CancellationToken ct)
        {
            var text = ++calls switch
            {
                2 => JsonSerializer.Serialize(new HarnessProfile { ToolGuidance = "domain" }, HarnessArtifacts.Json),
                4 => """{"component":"toolGuidance","verdict":"helped","evidence":null}""",
                _ => "KEEP_BASE",
            };
            return Task.FromResult(new HarnessEditorReply(text, 1, 1));
        }
        var report = await HarnessRefinement.RunAsync(options, Complete, Evaluate);
        Assert.Null(report.LessonId);
        Assert.NotNull(report.ReflectionError);
        Assert.Equal("domain", HarnessProfile.Load(Path.Combine(options.Eval.OutDir, "adopted-profile.json")).ToolGuidance);
        Assert.True(File.Exists(Path.Combine(options.Eval.OutDir, "refinement.json")));
    }

    [Fact]
    public async Task ExhaustedBudgetStopsBeforeFurtherModelCallsAndLeavesTheBaselineSnapshot()
    {
        var options = Options() with { MaxTokens = 1 };
        var calls = 0;
        Task<HarnessEditorReply> Complete(string prompt, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new HarnessEditorReply("KEEP_BASE"));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => HarnessRefinement.RunAsync(options, Complete, Evaluate));
        Assert.Equal(0, calls);
        Assert.Equal(HarnessProfile.Baseline.Id, HarnessProfile.Load(Path.Combine(options.Eval.OutDir, "adopted-profile.json")).Id);
    }

    [Fact]
    public async Task EqualBudgetRepeatsTheBaselineAndRequiresARealImprovement()
    {
        var options = Options() with { EqualBudget = true };
        var evaluations = 0;
        var prompts = new List<string>();
        Task<HarnessEditorReply> Complete(string prompt, CancellationToken ct)
        {
            prompts.Add(prompt);
            var text = prompts.Count switch
            {
                1 => "{\"settings\":{\"sandboxMode\":\"full-access\"}}",
                2 => JsonSerializer.Serialize(new HarnessProfile { Instructions = "workflow" }, HarnessArtifacts.Json),
                3 => JsonSerializer.Serialize(new HarnessProfile { ToolGuidance = "domain" }, HarnessArtifacts.Json),
                4 => JsonSerializer.Serialize(new HarnessProfile { Instructions = "composed" }, HarnessArtifacts.Json),
                _ => """
                    {"cue":"Blazor component changes","component":"toolGuidance","lesson":"Inspect the component behavior before editing",
                     "verdict":"helped","confidence":"medium","evidence":[
                       {"runId":"baseline/1/none/b","eventSeq":2,"observation":"baseline omitted component guidance"},
                       {"runId":"domain/1/none/b","eventSeq":2,"observation":"candidate applied component guidance"}]}
                    """,
            };
            return Task.FromResult(new HarnessEditorReply(text, 10, 10));
        }
        Task<EvalSummary> Run(EvalOptions eval, CancellationToken ct)
        {
            evaluations++;
            return Evaluate(eval, ct);
        }
        var report = await HarnessRefinement.RunAsync(options, Complete, Run);
        Assert.Equal(7, evaluations); // four profile executions plus three baseline repeats
        Assert.Equal(5, report.EditorCalls);
        Assert.Equal(1, report.EqualBudgetPasses);
        Assert.Equal(110, report.EqualBudgetTokens);
        Assert.True(report.BeatEqualBudget);
        Assert.Contains("Equal-budget baseline: 4 attempts", File.ReadAllText(Path.Combine(options.Eval.OutDir, "comparison.md")));
        Assert.Equal("domain", HarnessProfile.Load(Path.Combine(options.Eval.OutDir, "adopted-profile.json")).ToolGuidance);
    }
}
