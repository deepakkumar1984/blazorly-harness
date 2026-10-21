using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Harnessing;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Cli;

public sealed record HarnessExperimentOptions
{
    public required EvalOptions Eval { get; init; }
    public required IReadOnlyList<HarnessProfile> Profiles { get; init; }
    public int Trials { get; init; } = 3;
    public bool Resume { get; init; }
    /// <summary>When true, a candidate that fails a task the baseline passed is not recommended.</summary>
    public bool ProtectBaseline { get; init; } = true;
}

public sealed record HarnessTrial(string ProfileId, string ProfileName, int Trial, string Directory, EvalSummary Result, bool Cached);
public sealed record HarnessComparison(string ProfileId, string Name, int Trials, int Executed, int Passed, int Skipped,
    long InputTokens, long OutputTokens, long DurationMs, long CacheReadTokens = 0, long CacheWriteTokens = 0,
    int Compactions = 0, int Retries = 0, bool UsageComplete = true)
{
    public double? PassRate => Executed == 0 ? null : (double)Passed / Executed;
    public double? MeanDurationMs => Executed == 0 ? null : (double)DurationMs / Executed;
    // Provider billing rates are not known here. Zero would falsely imply a free execution.
    public decimal? CostUsd => null;
}
public sealed record HarnessExperimentResult(string Identity, IReadOnlyList<HarnessTrial> Trials,
    IReadOnlyList<HarnessComparison> Comparisons, string? BestProfileId, IReadOnlyList<string>? Regressions = null);

/// <summary>Repeated paired trials, explicit dependencies, and artifact-checked resume.</summary>
public static class HarnessExperiments
{
    private sealed record Completion(string Identity, string Artifacts);

    public static Task<HarnessExperimentResult> RunAsync(HarnessExperimentOptions options, CancellationToken ct = default)
        => RunAsync(options, EvalRunner.RunAsync, ct);

    internal static async Task<HarnessExperimentResult> RunAsync(HarnessExperimentOptions options,
        Func<EvalOptions, CancellationToken, Task<EvalSummary>> evaluate, CancellationToken ct = default)
    {
        if (options.Trials is < 1 or > 10) throw new ArgumentException("trials must be between 1 and 10");
        if (options.Profiles.Count is < 2 or > 8) throw new ArgumentException("compare requires 2–8 profiles, including a baseline");
        foreach (var profile in options.Profiles) profile.Validate();
        if (options.Profiles.Select(p => p.Id).Distinct().Count() != options.Profiles.Count)
            throw new ArgumentException("profiles must have different behavioral contents");
        var root = Path.GetFullPath(options.Eval.OutDir);
        var tasks = Path.GetFullPath(options.Eval.TasksDir);
        if (!Directory.Exists(tasks)) throw new ArgumentException("tasks directory does not exist");
        var relativeOutput = Path.GetRelativePath(tasks, root);
        if (!Path.IsPathRooted(relativeOutput) && relativeOutput != ".."
            && !relativeOutput.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("experiment output must be outside the tasks directory");

        // Read ambient routes once, before any trial homes are created. No environment mutation.
        var ambient = options.Eval.AmbientSettings ?? EvalSandbox.LoadAmbient(options.Eval.AmbientHomeDirectory);
        var identity = HarnessArtifacts.Hash(JsonSerializer.SerializeToElement(new
        {
            version = 1,
            runtime = new[] { typeof(HarnessProfile).Assembly, typeof(HarnessExperiments).Assembly,
                typeof(HarnessBootstrapper).Assembly, typeof(Blazorly.Harness.Tools.SkillsService).Assembly,
                typeof(Blazorly.Harness.Llm.LlmRuntime).Assembly, typeof(Blazorly.Harness.Kernel.HarnessContext).Assembly,
                typeof(Blazorly.Harness.Persistence.JsonlSessionPersistence).Assembly }.Select(a => a.ManifestModule.ModuleVersionId),
            host = new
            {
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture,
                defaultBackend = EvalSandbox.Default,
            },
            profiles = options.Profiles.Select(p => new { p.Id, p.Name }),
            options.Trials,
            options.Eval.Provider,
            options.Eval.Model,
            options.Eval.DefaultTimeoutSeconds,
            options.Eval.Sandbox,
            tasks = HashTree(tasks),
            route = EvalSandbox.RedactedSnapshot(ambient),
            ambient.ProviderApiTypes,
            ambient.ProviderBaseUrls,
        }));
        var identityPath = Path.Combine(root, "experiment.json");
        if (File.Exists(identityPath))
        {
            using var old = JsonDocument.Parse(File.ReadAllText(identityPath));
            if (old.RootElement.GetProperty("identity").GetString() != identity)
                throw new ArgumentException("experiment models, tasks, profiles or runtime changed; use a new --out directory");
        }
        HarnessArtifacts.WriteJson(identityPath, new
        {
            identity,
            trials = options.Trials,
            profiles = options.Profiles.Select(p => new { p.Id, p.Name })
        });

        var trials = new List<HarnessTrial>();
        for (var trial = 1; trial <= options.Trials; trial++)
        {
            // Rotate ordering to avoid always giving the same profile the first run.
            for (var offset = 0; offset < options.Profiles.Count; offset++)
            {
                ct.ThrowIfCancellationRequested();
                var profile = options.Profiles[(offset + trial - 1) % options.Profiles.Count];
                var directory = Path.Combine(root, "trials", profile.Id, trial.ToString());
                var cached = options.Resume ? ReadCompleted(directory, identity) : null;
                if (cached is not null)
                {
                    trials.Add(new(profile.Id, profile.Name, trial, directory, cached, true));
                    await options.Eval.Out.WriteLineAsync($"cached {profile.Name} trial {trial}/{options.Trials}").ConfigureAwait(false);
                    continue;
                }
                var profilePath = Path.Combine(directory, "profile.json");
                profile.Save(profilePath);
                await options.Eval.Out.WriteLineAsync($"running {profile.Name} trial {trial}/{options.Trials}").ConfigureAwait(false);
                var result = await evaluate(options.Eval with { OutDir = directory, ProfilePath = profilePath, AmbientSettings = ambient }, ct).ConfigureAwait(false);
                // Persist a completed outcome even when checks fail; failed trials are valid observations.
                HarnessArtifacts.WriteJson(Path.Combine(directory, "results.json"), result);
                HarnessArtifacts.WriteJson(Path.Combine(directory, ".complete.json"), new Completion(identity, HashArtifacts(directory)));
                trials.Add(new(profile.Id, profile.Name, trial, directory, result, false));
            }
        }
        var report = Summarise(identity, options.Profiles, trials, options.ProtectBaseline);
        HarnessArtifacts.WriteJson(Path.Combine(root, "comparison.json"), report);
        await File.WriteAllTextAsync(Path.Combine(root, "comparison.md"), Render(report), ct).ConfigureAwait(false);
        return report;
    }

    internal static HarnessExperimentResult Summarise(string identity, IReadOnlyList<HarnessProfile> profiles,
        IReadOnlyList<HarnessTrial> trials, bool protectBaseline = false)
    {
        var comparisons = profiles.Select(profile =>
        {
            var rows = trials.Where(t => t.ProfileId == profile.Id).ToList();
            var executed = rows.SelectMany(t => t.Result.Tasks.Where(r => !r.IsSkipped)).ToList();
            return new HarnessComparison(profile.Id, profile.Name, rows.Count, rows.Sum(t => t.Result.Executed),
                rows.Sum(t => t.Result.Passed), rows.Sum(t => t.Result.Skipped), rows.Sum(t => t.Result.InputTokens),
                rows.Sum(t => t.Result.OutputTokens), executed.Sum(r => r.DurationMs),
                rows.Sum(t => t.Result.Tasks.Sum(r => r.CacheReadTokens)), rows.Sum(t => t.Result.Tasks.Sum(r => r.CacheWriteTokens)),
                executed.Sum(r => r.Compactions), executed.Sum(r => r.Retries), executed.Count == 0 || executed.All(r => r.UsageComplete));
        }).ToList();
        var baselineTrials = trials.Where(t => t.ProfileId == profiles[0].Id).ToList();
        var baselineCoverage = Coverage(baselineTrials);
        var baselinePasses = Passes(baselineTrials);
        var best = comparisons.Where(c => c.Executed > 0 && c.Skipped == 0
                && Coverage(trials.Where(t => t.ProfileId == c.ProfileId)).SequenceEqual(baselineCoverage)
                && (!protectBaseline || baselinePasses.All(p => Passes(trials.Where(t => t.ProfileId == c.ProfileId)).GetValueOrDefault(p.Key) >= p.Value)))
            .OrderByDescending(c => c.PassRate)
            .ThenBy(c => c.UsageComplete ? c.InputTokens + c.OutputTokens + c.CacheReadTokens + c.CacheWriteTokens : long.MaxValue)
            .ThenBy(c => c.MeanDurationMs).FirstOrDefault();
        return new(identity, trials, comparisons, best?.ProfileId, RegressionLines(profiles, trials));
    }

    private static IReadOnlyList<string> RegressionLines(IReadOnlyList<HarnessProfile> profiles, IReadOnlyList<HarnessTrial> trials)
    {
        var baseline = Passes(trials.Where(t => t.ProfileId == profiles[0].Id));
        var lines = new List<string>();
        foreach (var profile in profiles.Skip(1))
        {
            var passes = Passes(trials.Where(t => t.ProfileId == profile.Id));
            foreach (var (task, count) in baseline.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (count > 0 && passes.GetValueOrDefault(task) < count)
                    lines.Add($"{profile.Name} regressed {task}: {passes.GetValueOrDefault(task)} passes against {count} baseline passes");
            }
        }
        return lines;
    }

    private static Dictionary<string, int> Passes(IEnumerable<HarnessTrial> trials) => trials.SelectMany(t => t.Result.Tasks.Where(r => !r.IsSkipped))
        .GroupBy(r => r.Sandbox + "|" + r.Id).ToDictionary(g => g.Key, g => g.Count(r => r.Pass));

    private static IEnumerable<string> Coverage(IEnumerable<HarnessTrial> trials) => trials
        .SelectMany(t => t.Result.Tasks.Where(r => !r.IsSkipped).Select(r => $"{t.Trial}|{r.Sandbox}|{r.Id}"))
        .OrderBy(s => s, StringComparer.Ordinal);

    private static EvalSummary? ReadCompleted(string directory, string identity)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<Completion>(File.ReadAllText(Path.Combine(directory, ".complete.json")), HarnessArtifacts.Json);
            if (marker?.Identity != identity || !File.Exists(Path.Combine(directory, "environment.json"))
                || marker.Artifacts != HashArtifacts(directory)) return null;
            return JsonSerializer.Deserialize<EvalSummary>(File.ReadAllText(Path.Combine(directory, "results.json")), HarnessArtifacts.Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { return null; }
    }

    internal static string HashTree(string root)
    {
        if (!Directory.Exists(root)) return HarnessArtifacts.HashText("absent");
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .Select(path => new { path = Path.GetRelativePath(root, path).Replace('\\', '/'), hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) });
        return HarnessArtifacts.Hash(JsonSerializer.SerializeToElement(entries));
    }

    private static string HashArtifacts(string directory) => HarnessArtifacts.Hash(JsonSerializer.SerializeToElement(new
    {
        results = File.ReadAllText(Path.Combine(directory, "results.json")),
        environment = File.ReadAllText(Path.Combine(directory, "environment.json")),
        profile = File.ReadAllText(Path.Combine(directory, "profile.json")),
        evidence = HashTree(Path.Combine(directory, "evidence")),
        outputs = HashTree(Path.Combine(directory, "workspaces")),
    }));

    internal static string Render(HarnessExperimentResult result)
    {
        var text = new StringBuilder("# Harness profile comparison\n\n");
        text.AppendLine("| Profile | Trials | Passed / executed | Skipped | Input tokens | Output tokens | Cache read | Cache write | Compactions | Retries | Usage | Mean task ms |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var row in result.Comparisons)
            text.AppendLine(FormattableString.Invariant($"| {row.Name.Replace('|', '/').Replace('\n', ' ')} | {row.Trials} | {row.Passed}/{row.Executed} | {row.Skipped} | {row.InputTokens} | {row.OutputTokens} | {row.CacheReadTokens} | {row.CacheWriteTokens} | {row.Compactions} | {row.Retries} | {(row.UsageComplete ? "complete" : "incomplete")} | {row.MeanDurationMs?.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"} |"));
        text.AppendLine("\nDollar cost is unavailable: provider billing rates are not configured. Token totals include parent calls, compaction, and child sessions when those calls were metered. Incomplete usage is a lower bound and is not used to rank a profile as cheaper.");
        if (result.Regressions is { Count: > 0 })
        {
            text.AppendLine("\nPer-task regressions against the baseline:");
            foreach (var line in result.Regressions) text.AppendLine("- " + line);
        }
        text.AppendLine("\nRepeated trials measure variability; they do not establish statistical significance. Compare on held-out tasks before promoting a profile.");
        text.AppendLine(result.BestProfileId is null ? "\nNo recommendation: complete comparable coverage is unavailable."
            : $"\nRecommended profile: `{result.BestProfileId}` (correctness first, then tokens and latency). The active runtime configuration is not changed.");
        return text.ToString();
    }
}
