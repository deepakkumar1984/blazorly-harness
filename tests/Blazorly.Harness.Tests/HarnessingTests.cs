using System.Text.Json;
using Blazorly.Harness.Cli;
using Blazorly.Harness.Core.Harnessing;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Persistence;
using Blazorly.Harness.Tools;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

public sealed class HarnessingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blz-harnessing-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private HarnessProfile SkillProfile() => new()
    {
        Name = "blazor-workflow",
        Instructions = "Use the component-test skill for UI changes.",
        Files = new Dictionary<string, string>
        {
            ["skills/component-test/SKILL.md"] = "---\nname: component-test\ndescription: Blazor UI component changes\n---\nRun scripts/check.py before editing.",
            ["skills/component-test/scripts/check.py"] = "print('inspect component tests')\n",
        },
    };

    [Fact]
    public void ProfileIdentity_IsStableAcrossOrderingAndChangesWithInstructionsOrAssets()
    {
        var first = HarnessProfile.Parse("""{"name":"one","settings":{"tokenSaverToolOutput":true,"answerStyle":"simple"}}""");
        var reordered = HarnessProfile.Parse("""{"settings":{"answerStyle":"simple","tokenSaverToolOutput":true},"name":"renamed"}""");
        Assert.Equal(first.Id, reordered.Id);
        Assert.NotEqual(first.Id, (first with { Instructions = "Run targeted checks" }).Id);
        var skill = SkillProfile();
        var changed = skill with { Files = skill.Files.ToDictionary(p => p.Key, p => p.Value + "\n") };
        Assert.NotEqual(skill.Id, changed.Id);
    }

    [Theory]
    [InlineData("{\"settings\":{\"sandboxMode\":\"full-access\"}}")]
    [InlineData("{\"apiKey\":\"not-a-profile-field\"}")]
    [InlineData("{\"name\":\"a\",\"name\":\"b\"}")]
    [InlineData("{\"files\":{\"../escape.py\":\"x\"}}")]
    [InlineData("{\"files\":{\"skills/demo/SKILL.md\":\"no frontmatter\"}}")]
    [InlineData("{\"instructions\":\"Run scripts/missing.py\"}")]
    [InlineData("{\"settings\":{\"compactionThreshold\":1}}")]
    [InlineData("{\"instructions\":null}")]
    public void ProfilesRejectUnsupportedSettingsAndBrokenPackages(string json)
        => Assert.ThrowsAny<Exception>(() => HarnessProfile.Parse(json));

    [Fact]
    public void SkillPackage_IsMaterializedInsideWorkspaceAndDiscoverable()
    {
        var profile = SkillProfile();
        var package = profile.Materialize(_root);
        var skills = new SkillsService(Path.Combine(package, "skills"));
        Assert.Equal("component-test", Assert.Single(skills.List()).Name);
        Assert.Contains("Run scripts/check.py", skills.ReadBody("component-test"));
        Assert.True(File.Exists(Path.Combine(package, "skills/component-test/scripts/check.py")));
        Assert.StartsWith(Path.GetFullPath(_root), package);
        profile.VerifyMaterialized(_root);
        File.WriteAllText(Path.Combine(package, "skills/component-test/scripts/check.py"), "modified by a task");
        Assert.Throws<ArgumentException>(() => profile.VerifyMaterialized(_root));
    }

    [Fact]
    public async Task ConcurrentBoots_UseExplicitHomesAndSkillRoots()
    {
        var homeA = Path.Combine(_root, "home-a");
        var homeB = Path.Combine(_root, "home-b");
        EvalSandbox.WriteHome(homeA, EvalSandbox.PinnedSettings(EvalSandbox.None));
        EvalSandbox.WriteHome(homeB, EvalSandbox.PinnedSettings(EvalSandbox.None));
        // An ambient skill in the explicit home must not enter an isolated baseline.
        Directory.CreateDirectory(Path.Combine(homeA, "skills/ambient"));
        File.WriteAllText(Path.Combine(homeA, "skills/ambient/SKILL.md"), "---\nname: ambient\ndescription: private\n---\nprivate body");
        var profile = SkillProfile();
        var package = profile.Materialize(Path.Combine(_root, "workspace"));
        await using var baseline = new HarnessBootstrapper(new(homeA, []));
        await using var candidate = new HarnessBootstrapper(new(homeB, [Path.Combine(package, "skills")], profile, package));
        var previousHome = Environment.GetEnvironmentVariable("BLAZORLY_HOME");
        await Task.WhenAll(baseline.StartAsync(CancellationToken.None), candidate.StartAsync(CancellationToken.None));
        Assert.Equal(previousHome, Environment.GetEnvironmentVariable("BLAZORLY_HOME"));
        Assert.Empty(baseline.Context.Get<SkillsService>("skills").List());
        Assert.Single(candidate.Context.Get<SkillsService>("skills").List());
        var prompt = candidate.Context.Get<SystemPromptService>(SystemPromptService.ServiceKey).Assemble(null, null);
        Assert.Contains(profile.Instructions, SystemPromptService.RenderPrompt(prompt));
        Assert.Equal(Path.GetFullPath(homeA), baseline.DataDirectory);
        Assert.Equal(Path.GetFullPath(homeB), candidate.DataDirectory);
    }

    [Fact]
    public async Task ManifestsIdentifyProfileBehaviorBeyondToolSchemas()
    {
        var ambient = new HarnessSettings();
        var profile = HarnessProfile.Compression;
        var a = new EvalOptions { TasksDir = _root, OutDir = Path.Combine(_root, "a") };
        var b = a with { OutDir = Path.Combine(_root, "b") };
        EvalRunner.SetupHome(a, "none", ambient);
        EvalRunner.SetupHome(b, "none", ambient, profile);
        var baseline = await EvalEnvironmentCapture.CaptureAsync(["none"], backend => EvalRunner.HomeDir(a, backend), ambient);
        var candidate = await EvalEnvironmentCapture.CaptureAsync(["none"], backend => EvalRunner.HomeDir(b, backend), ambient, profile: profile);
        Assert.Equal(baseline.Manifests["none"].ToolSchemaHash, candidate.Manifests["none"].ToolSchemaHash);
        Assert.NotEqual(baseline.Manifests["none"].ConfigurationHash, candidate.Manifests["none"].ConfigurationHash);
        Assert.Equal(profile.Id, candidate.Manifests["none"].ProfileId);
        Assert.True((bool)candidate.Manifests["none"].Settings["tokenSaverToolOutput"]!);
    }

    private HarnessExperimentOptions Experiment(int trials = 2)
    {
        var tasks = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(tasks);
        File.WriteAllText(Path.Combine(tasks, "fixture.json"), "{\"input\":1}");
        return new()
        {
            Profiles = [HarnessProfile.Baseline, HarnessProfile.Compression],
            Trials = trials,
            Eval = new() { TasksDir = tasks, OutDir = Path.Combine(_root, "experiment"), Out = new StringWriter() },
        };
    }

    private static Task<EvalSummary> Evaluate(EvalOptions options, CancellationToken ct)
    {
        var profile = HarnessProfile.Load(options.ProfilePath!);
        var pass = profile.Settings.TokenSaverToolOutput == true;
        var workspace = Path.Combine(options.OutDir, "workspaces/none/task");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "answer.txt"), pass ? "correct" : "wrong");
        HarnessArtifacts.WriteJson(Path.Combine(options.OutDir, "environment.json"), new { profile.Id });
        var result = new EvalTaskResult("task", pass, "completed", 0, "session", "answer", 20, 100, 10,
            [new("correctness", pass, pass ? 0 : 1, "check")], null);
        return Task.FromResult(new EvalSummary(1, pass ? 1 : 0, pass ? 0 : 1, 20, 100, 10, [result], 0, ["none"], new Dictionary<string, string>()));
    }

    [Fact]
    public async Task ComparisonsRotateTrialsAndResumeOnlyIntactMatchingArtifacts()
    {
        var options = Experiment();
        var order = new List<string>();
        Task<EvalSummary> Run(EvalOptions eval, CancellationToken ct)
        {
            order.Add(HarnessProfile.Load(eval.ProfilePath!).Name);
            return Evaluate(eval, ct);
        }
        var first = await HarnessExperiments.RunAsync(options, Run);
        Assert.Equal(["baseline", "compressed-output", "compressed-output", "baseline"], order);
        Assert.Equal(HarnessProfile.Compression.Id, first.BestProfileId);
        Assert.All(first.Comparisons, c => Assert.Equal(2, c.Trials));
        Assert.Null(first.Comparisons[0].CostUsd);
        var resumed = await HarnessExperiments.RunAsync(options with { Resume = true }, Run);
        Assert.Equal(4, order.Count);
        Assert.All(resumed.Trials, t => Assert.True(t.Cached));

        File.Delete(Path.Combine(first.Trials[0].Directory, "workspaces/none/task/answer.txt"));
        var repaired = await HarnessExperiments.RunAsync(options with { Resume = true }, Run);
        Assert.Equal(5, order.Count);
        Assert.Single(repaired.Trials, t => !t.Cached);
        var changed = options with { Profiles = [HarnessProfile.Baseline, HarnessProfile.Compression with { Instructions = "changed" }] };
        await Assert.ThrowsAsync<ArgumentException>(() => HarnessExperiments.RunAsync(changed, Run));
        Assert.Equal(5, order.Count);
    }

    [Fact]
    public async Task ComparisonDoesNotRecommendAProfileWithIncomparableCoverage()
    {
        var options = Experiment(1);
        async Task<EvalSummary> Run(EvalOptions eval, CancellationToken ct)
        {
            var result = await Evaluate(eval, ct);
            if (HarnessProfile.Load(eval.ProfilePath!).Name != "baseline") return result;
            var skipped = result.Tasks[0] with { Skipped = "backend unavailable", Pass = false };
            return result with { Tasks = [skipped], Passed = 0, Failed = 0, Skipped = 1 };
        }
        var report = await HarnessExperiments.RunAsync(options, Run);
        Assert.Null(report.BestProfileId);
    }

    [Fact]
    public async Task ComparisonFreezesRoutesAndRejectsResumeAfterRouteChanges()
    {
        var options = Experiment(1);
        var home = Path.Combine(_root, "ambient-home");
        EvalSandbox.WriteHome(home, new HarnessSettings { Provider = "original", Model = "fixed-model" });
        options = options with { Eval = options.Eval with { AmbientHomeDirectory = home } };
        Task<EvalSummary> Run(EvalOptions eval, CancellationToken ct)
        {
            Assert.Equal("original", eval.AmbientSettings!.Provider);
            EvalSandbox.WriteHome(home, new HarnessSettings { Provider = "changed", Model = "fixed-model" });
            return Evaluate(eval, ct);
        }
        var report = await HarnessExperiments.RunAsync(options, Run);
        Assert.Equal(2, report.Trials.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => HarnessExperiments.RunAsync(options with { Resume = true }, Run));
    }

    [Fact]
    public void LessonsRequireRealContrastingEventsAndStayScopedRevocableAndDeduplicated()
    {
        Directory.CreateDirectory(_root);
        var baseline = Path.Combine(_root, "baseline.jsonl");
        var candidate = Path.Combine(_root, "candidate.jsonl");
        File.WriteAllText(baseline, "{\"seq\":3,\"data\":\"missed component instructions\"}\n");
        File.WriteAllText(candidate, "{\"seq\":4,\"data\":\"loaded component instructions\"}\n");
        var trajectories = new Dictionary<string, string> { ["base"] = baseline, ["candidate"] = candidate };
        var library = new HarnessExperienceLibrary(Path.Combine(_root, "library"));
        var scope = HarnessExperienceLibrary.WorkspaceScope(Path.Combine(_root, "workspace-a"));
        var lesson = new HarnessLesson
        {
            Scope = scope,
            ProfileId = SkillProfile().Id,
            Cue = "Blazor component changes",
            Component = "instructions",
            Lesson = "Load component-test instructions before editing",
            Verdict = "helped",
            Evidence = [new("base", 3, "did not discover the instructions"), new("candidate", 4, "discovered the instructions")],
        };
        Assert.Null(library.Add(null, trajectories));
        Assert.Null(library.Add(lesson with { Verdict = "confounded" }, trajectories));
        Assert.Throws<ArgumentException>(() => library.Add(lesson with { Evidence = [new("base", 100, "invented"), new("candidate", 4, "loaded")] }, trajectories));
        var recorded = library.Add(lesson, trajectories)!;
        Assert.Equal(recorded.Id, library.Add(lesson, trajectories)!.Id);
        Assert.Single(library.List(scope, "coding"));
        Assert.Empty(library.List(HarnessExperienceLibrary.WorkspaceScope(Path.Combine(_root, "workspace-b")), "coding"));
        Assert.Empty(library.List(scope, "legal"));
        Assert.Contains("component-test", library.Render(Path.Combine(_root, "workspace-a"), "coding", "change Blazor component"));
        Assert.All(recorded.Evidence, e => Assert.NotNull(e.ArtifactHash));
        library.Revoke(recorded.Id);
        Assert.Empty(library.List(scope, "coding"));

        var otherScope = HarnessExperienceLibrary.WorkspaceScope(Path.Combine(_root, "workspace-b"));
        var unrelated = library.Add(lesson with { Scope = otherScope }, trajectories)!;
        var corruptPath = Path.Combine(_root, "library/corrupt.json");
        File.WriteAllText(corruptPath, "{not valid JSON");
        library.DeleteScope(scope);
        Assert.False(File.Exists(Path.Combine(_root, "library", recorded.Id + ".json")));
        Assert.Equal(unrelated.Id, Assert.Single(library.List(otherScope, "coding")).Id);
        Assert.True(File.Exists(corruptPath));
    }

    [Fact]
    public void EvidenceExporterWritesOneFullJsonObjectPerLine()
    {
        var content = new string('x', 4_000);
        var session = new Session(new() { Id = "evidence-test", CreatedAt = 0 });
        session.Append(SessionEventTypes.SessionTitle, new { title = content });
        TrialEvidence.Write(_root, session.Header, session.Events, content);
        var lines = File.ReadAllLines(Path.Combine(_root, "session.jsonl"));
        Assert.Equal(2, lines.Length);
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
        Assert.Contains(content, lines[1]);
        Assert.Equal(content, File.ReadAllText(Path.Combine(_root, "answer.md")));
    }

    [Fact]
    public async Task KilledRunEvidenceDropsTornTailAndIncludesAllCommittedUsage()
    {
        var session = new Session(new() { Id = "killed-run", CreatedAt = 0 });
        foreach (var (turn, text, tokens) in new[]
        {
            (1, "before kill", new TokenUsage(10, 5, 3)),
            (2, "after resume", new TokenUsage(20, 6, 4, 5)),
        })
        {
            session.Append(SessionEventTypes.TurnStart, new SessionPayloads.TurnStart(turn));
            session.Append(SessionEventTypes.StepStart, new SessionPayloads.StepStart(turn, 1));
            session.Append(SessionEventTypes.AssistantMessage,
                new SessionPayloads.AssistantMessage(turn, 1, Message.CreateAssistant("scripted", "test", [new TextBlock(text)]), tokens),
                new Session.AppendOptions(SurfaceOp: new SurfaceOp.Append()));
            session.Append(SessionEventTypes.StepEnd, new SessionPayloads.StepEnd(turn, 1));
            session.Append(SessionEventTypes.TurnEnd, new SessionPayloads.TurnEnd(turn, new TurnEndReason.Completed()));
        }
        var sessionsRoot = Path.Combine(_root, "sessions");
        var persistence = new JsonlSessionPersistence(sessionsRoot);
        await persistence.CreateAsync(session.Header);
        await persistence.AppendAsync(session.Id, session.Events);
        var persisted = Directory.GetFiles(sessionsRoot, "session.jsonl", SearchOption.AllDirectories).Single();
        await File.AppendAllTextAsync(persisted, "{\"torn");
        var evidence = Path.Combine(_root, "evidence");
        var usage = await TrialEvidence.WritePersistedAsync(evidence, sessionsRoot, session.Id, "after resume");
        Assert.Equal(new HeadlessUsage(30, 11, 7, 5), usage);
        var lines = File.ReadAllLines(Path.Combine(evidence, "session.jsonl"));
        Assert.Equal(session.Events.Count + 1, lines.Length);
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
        using var assistantEvent = JsonDocument.Parse(lines[3]);
        Assert.Equal("append", assistantEvent.RootElement.GetProperty("surfaceOp").GetString());
        Assert.Equal("after resume", File.ReadAllText(Path.Combine(evidence, "answer.md")));
    }

    [Fact]
    public void ExcerptReservesAShareForEveryRunAndKeepsWholeEvents()
    {
        var baseline = Path.Combine(_root, "baseline.jsonl");
        var workflow = Path.Combine(_root, "workflow.jsonl");
        Directory.CreateDirectory(_root);
        var wide = "{\"seq\":1,\"data\":\"" + new string('B', 2_000) + "\"}";
        File.WriteAllText(baseline, string.Join('\n', Enumerable.Repeat(wide, 70)) + "\n");
        File.WriteAllText(workflow, "{\"seq\":9,\"data\":\"workflow kept\"}\n");
        var text = TrialEvidence.Excerpt(new Dictionary<string, string> { ["baseline/1"] = baseline, ["workflow/1"] = workflow }, 100_000);
        Assert.Contains("workflow kept", text);
        Assert.Contains("omitted", text);
        Assert.Contains(baseline, text);
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith('{')) continue;
            using var _ = JsonDocument.Parse(line);
        }

        var huge = Path.Combine(_root, "huge.jsonl");
        File.WriteAllText(huge, new string('H', 80) + "\n{\"seq\":2,\"data\":\"older kept\"}\n");
        var clipped = TrialEvidence.Excerpt(new Dictionary<string, string> { ["huge"] = huge }, 50);
        Assert.Contains("older kept", clipped);
        Assert.DoesNotContain(new string('H', 80), clipped);

        var oversize = Path.Combine(_root, "oversize.jsonl");
        File.WriteAllText(oversize, "{\"seq\":7,\"data\":\"" + new string('Z', 21_000) + "\"}\n");
        var read = TrialEvidence.ReadEvent(oversize, 7);
        Assert.Contains("does not fit", read);
        Assert.DoesNotContain(new string('Z', 100), read);
    }

    [Fact]
    public void PromotionGuardRejectsBaselineRegressionsAndIgnoresIncompleteUsage()
    {
        EvalTaskResult Row(string id, bool pass, long tokens, bool complete = true) => new(
            id, pass, "completed", 0, "session", "", 1, tokens, tokens, [], null, UsageComplete: complete);
        EvalSummary Summary(params EvalTaskResult[] tasks) => new(
            tasks.Length, tasks.Count(t => t.Pass), tasks.Count(t => !t.Pass), 10,
            tasks.Sum(t => t.InputTokens), tasks.Sum(t => t.OutputTokens), tasks, 0, ["none"], new Dictionary<string, string>());
        HarnessTrial Trial(HarnessProfile profile, EvalSummary summary) => new(profile.Id, profile.Name, 1, "dir", summary, false);

        var baseline = HarnessProfile.Baseline;
        var candidate = HarnessProfile.Compression;
        var trials = new[]
        {
            Trial(baseline, Summary(Row("A", true, 10), Row("B", false, 10), Row("C", false, 10))),
            Trial(candidate, Summary(Row("A", false, 10), Row("B", true, 10), Row("C", true, 10))),
        };
        var guarded = HarnessExperiments.Summarise("identity", [baseline, candidate], trials, protectBaseline: true);
        Assert.Equal(baseline.Id, guarded.BestProfileId);
        Assert.Contains(guarded.Regressions!, line => line.Contains("compressed-output regressed none|A: 0 passes against 1 baseline passes"));
        Assert.Contains("Per-task regressions", HarnessExperiments.Render(guarded));

        var open = HarnessExperiments.Summarise("identity", [baseline, candidate], trials, protectBaseline: false);
        Assert.Equal(candidate.Id, open.BestProfileId);
        Assert.Contains(open.Regressions!, line => line.Contains("regressed none|A"));

        var terse = HarnessProfile.Terse;
        var equal = new[]
        {
            Trial(baseline, Summary(Row("A", true, 100))),
            Trial(terse, Summary(Row("A", true, 1, complete: false))),
        };
        Assert.Equal(baseline.Id, HarnessExperiments.Summarise("identity", [baseline, terse], equal, protectBaseline: true).BestProfileId);
        Assert.NotEqual(baseline.Id, terse.Id);
        Assert.NotEqual(candidate.Id, terse.Id);
    }

    [Fact]
    public async Task UsageLedgerIncludesCompactionChildrenAndMarksGaps()
    {
        static SessionEvent Ev(string type, object payload) => new() { Type = type, Data = SessionJson.ToElement(payload) };
        var parent = new[]
        {
            Ev(SessionEventTypes.AssistantMessage, new SessionPayloads.AssistantMessage(1, 1,
                Message.CreateAssistant("scripted", "test", [new TextBlock("parent")]), new TokenUsage(10, 4, 1, 2))),
            Ev(SessionEventTypes.CompactionSummary, new { usage = new TokenUsage(3, 1) }),
            Ev(SessionEventTypes.LlmRetry, new { attempt = 1 }),
            Ev(SessionEventTypes.SubagentStatus, new { childSessionId = "child-1" }),
            Ev("team/member", new { member = new { sessionId = "child-1" } }),
        };
        var child = new[]
        {
            Ev(SessionEventTypes.AssistantMessage, new SessionPayloads.AssistantMessage(1, 1,
                Message.CreateAssistant("scripted", "test", [new TextBlock("child")]), new TokenUsage(5, 2, 1))),
        };

        var usage = await TrialEvidence.AccountAsync(parent, (id, _) =>
            Task.FromResult<IReadOnlyList<SessionEvent>?>(id == "child-1" ? child : null));
        Assert.Equal(new HeadlessUsage(18, 7, 2, 2, 1, 1, true), usage);

        var missing = await TrialEvidence.AccountAsync(parent, (_, _) => Task.FromResult<IReadOnlyList<SessionEvent>?>(null));
        Assert.False(missing.Complete);
        Assert.Equal(13, missing.Input);
        Assert.Equal(5, missing.Output);
        Assert.Equal(1, missing.Compactions);
        Assert.Equal(1, missing.Retries);

        var incomplete = await TrialEvidence.AccountAsync([Ev(SessionEventTypes.CompactionSummary, new { summary = "no meter" })], null);
        Assert.False(incomplete.Complete);
        Assert.Equal(0, incomplete.Input);
        Assert.Equal(1, incomplete.Compactions);
    }

    [Fact]
    public void LessonsRetainPackagesAndHideIncompatibleRecords()
    {
        Directory.CreateDirectory(_root);
        var baseline = Path.Combine(_root, "base.jsonl");
        var candidate = Path.Combine(_root, "candidate.jsonl");
        File.WriteAllText(baseline, "{\"seq\":3,\"data\":\"missed the skill\"}\n");
        File.WriteAllText(candidate, "{\"seq\":4,\"data\":\"loaded the skill\"}\n");
        var trajectories = new Dictionary<string, string> { ["base"] = baseline, ["candidate"] = candidate };
        var library = new HarnessExperienceLibrary(Path.Combine(_root, "packages-library"));
        var scope = HarnessExperienceLibrary.WorkspaceScope(Path.Combine(_root, "workspace-a"));
        var profile = SkillProfile();
        var evidence = new List<HarnessLessonEvidence> { new("base", 3, "missed the skill"), new("candidate", 4, "loaded the skill") };
        var packaged = new HarnessLesson
        {
            Scope = scope,
            ProfileId = profile.Id,
            Cue = "Blazor component skill",
            Component = "skills/component-test/SKILL.md",
            Lesson = "Run the packaged component checker before editing",
            Verdict = "helped",
            Evidence = evidence,
        };
        Assert.Throws<ArgumentException>(() => library.Add(packaged, trajectories));
        var recorded = library.Add(packaged, trajectories, profile)!;
        var packagePath = Path.Combine(_root, "packages-library", "packages", profile.Id, "profile.json");
        var bytes = File.ReadAllBytes(packagePath);
        Assert.Equal(recorded.Id, library.Add(packaged, trajectories, profile)!.Id);
        Assert.Equal(bytes, File.ReadAllBytes(packagePath));
        var shown = library.Describe(recorded.Id);
        Assert.Contains("loaded the skill", shown);
        Assert.Contains("skills/component-test/scripts/check.py", shown);
        Assert.Contains(packagePath, shown);
        File.WriteAllText(packagePath, File.ReadAllText(packagePath).Replace("UI changes", "UI change"));
        Assert.Throws<ArgumentException>(() => library.Add(packaged, trajectories, profile));

        var revision = new string('a', 40);
        var specific = new HarnessLesson
        {
            Scope = scope,
            ProfileId = profile.Id,
            Cue = "revision bound counter",
            Component = "instructions",
            Lesson = "This repository revision stores the counter in currentCount",
            Verdict = "helped",
            SourceRevision = revision,
            Provider = "openai",
            Model = "fixed-model",
            RevisionSpecific = true,
            Evidence = evidence,
        };
        library.Add(specific, trajectories);
        var reusable = new HarnessLesson
        {
            Scope = scope,
            ProfileId = profile.Id,
            Cue = "reusable counter",
            Component = "instructions",
            Lesson = "Inspect the component contract before editing markup",
            Verdict = "helped",
            SourceRevision = revision,
            Provider = "openai",
            Model = "fixed-model",
            Evidence = evidence,
        };
        library.Add(reusable, trajectories);
        var query = "revision bound counter reusable counter";
        var same = new LessonCompatibility(revision, "openai", "fixed-model");
        var otherRevision = new LessonCompatibility(new string('b', 40), "openai", "fixed-model");
        var otherModel = new LessonCompatibility(revision, "openai", "other-model");
        var matched = library.Render(Path.Combine(_root, "workspace-a"), "coding", query, same);
        Assert.Contains("stores the counter", matched);
        Assert.Contains("Inspect the component contract", matched);
        var moved = library.Render(Path.Combine(_root, "workspace-a"), "coding", query, otherRevision);
        Assert.DoesNotContain("stores the counter", moved);
        Assert.Contains("Inspect the component contract", moved);
        Assert.DoesNotContain("stores the counter", library.Render(Path.Combine(_root, "workspace-a"), "coding", query, otherModel));
        Assert.Contains("stores the counter", library.Render(Path.Combine(_root, "workspace-a"), "coding", query));
    }

    [Fact]
    public void HelperSyntaxRejectsBrokenScriptsAndAcceptsThePackagedChecker()
    {
        Assert.Throws<ArgumentException>(() => HarnessProfile.Parse("""{"name":"bad-py","files":{"scripts/bad.py":"def ("}}"""));
        Assert.Throws<ArgumentException>(() => HarnessProfile.Parse("""{"name":"bad-csx","files":{"scripts/bad.csx":"class {"}}"""));
        var script = HarnessProfile.Parse("""{"name":"ok-csx","files":{"scripts/ok.csx":"var n = 1;\n"}}""");
        Assert.Contains("scripts/ok.csx", script.Files.Keys);
        SkillProfile().Validate();
    }

    [Fact]
    public void HiddenGradingMaterialIsRejectedWhenItLeaksOrIsTooShort()
    {
        Assert.False(EvalRunner.TranscriptContainsHidden("ordinary work", ["SECRET_GRADE_TOKEN"]));
        Assert.True(EvalRunner.TranscriptContainsHidden("saw SECRET_GRADE_TOKEN in the log", ["SECRET_GRADE_TOKEN"]));
        Assert.False(EvalRunner.TranscriptContainsHidden("short secret", ["short"]));
        var dir = Path.Combine(_root, "hidden-task");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), """
            {"description":"hidden grade","prompt":"do the work","checks":[{"name":"c","run":"true"}],"hidden":["short"]}
            """);
        var error = Assert.Throws<EvalLoadException>(() => EvalTask.Load(dir));
        Assert.Contains("at least 8 characters", error.Message);
    }
}
