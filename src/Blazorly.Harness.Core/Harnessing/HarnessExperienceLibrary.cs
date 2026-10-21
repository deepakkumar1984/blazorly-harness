using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Blazorly.Harness.Core.Harnessing;

public sealed record HarnessLessonEvidence(string RunId, int EventSeq, string Observation)
{
    public string? ArtifactHash { get; init; }
    public string? EventJson { get; init; }
}

public sealed record HarnessLesson
{
    public string Scope { get; init; } = "";
    public string TaskClass { get; init; } = "coding";
    public string Cue { get; init; } = "";
    public string Component { get; init; } = "";
    public string Lesson { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string Confidence { get; init; } = "medium";
    public string Verdict { get; init; } = "confounded";
    public string? SourceRevision { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string Runtime { get; init; } = "";
    /// <summary>True when the lesson states a fact about one repository revision. Reusable advice stays false.</summary>
    public bool RevisionSpecific { get; init; }
    public IReadOnlyList<HarnessLessonEvidence> Evidence { get; init; } = [];
    public bool Revoked { get; init; }

    [JsonIgnore]
    public string Id => HarnessArtifacts.Hash(JsonSerializer.SerializeToElement(new
    {
        Scope,
        TaskClass,
        Cue,
        Component,
        Lesson,
        ProfileId,
        Evidence,
    }, HarnessArtifacts.Json));
}

/// <summary>Scoped, revocable lessons. Durable entries require actual contrasting event references.</summary>
public sealed class HarnessExperienceLibrary(string directory)
{
    public static string WorkspaceScope(string workspace)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return HarnessArtifacts.HashText(path);
    }

    public HarnessLesson? Add(HarnessLesson? lesson, IReadOnlyDictionary<string, string> trajectories, HarnessProfile? package = null)
    {
        // NO_ENTRY, uncertainty and absent contrast are valid reflection outcomes.
        if (lesson is null || lesson.Verdict != "helped") return null;
        if (!Regex.IsMatch(lesson.Scope, "^[a-f0-9]{64}$") || !Regex.IsMatch(lesson.ProfileId, "^[a-f0-9]{64}$"))
            throw new ArgumentException("lesson scope and profileId must be SHA-256 identities");
        if (string.IsNullOrWhiteSpace(lesson.TaskClass) || lesson.TaskClass.Length > 80
            || string.IsNullOrWhiteSpace(lesson.Cue) || lesson.Cue.Length > 500
            || string.IsNullOrWhiteSpace(lesson.Lesson) || lesson.Lesson.Length > 2_000)
            throw new ArgumentException("lesson requires a taskClass, bounded situation cue and concrete operation");
        if (lesson.Confidence is not ("high" or "medium" or "low")) throw new ArgumentException("invalid lesson confidence");
        if (lesson.Component is not ("instructions" or "toolGuidance")
            && !HarnessProfile.IsPackagePath(lesson.Component)
            && !Regex.IsMatch(lesson.Component, "^settings\\.(tokenSaverToolOutput|tokenSaverTerseOutput|tokenSaverMinimalCode|answerStyle|compactionThreshold|compactionPrunerChars)$"))
            throw new ArgumentException("lesson component is outside the editable profile surface");
        if (lesson.Evidence is null || lesson.Evidence.Count is < 2 or > 8 || lesson.Evidence.Any(e => e is null)
            || lesson.Evidence.Select(e => e.RunId).Distinct().Count() < 2)
            throw new ArgumentException("lesson requires evidence from at least two contrasting runs");
        var evidence = lesson.Evidence.Select(reference =>
        {
            if (!trajectories.TryGetValue(reference.RunId, out var path) || reference.EventSeq < 0
                || string.IsNullOrWhiteSpace(reference.Observation) || reference.Observation.Length > 1_000)
                throw new ArgumentException("lesson evidence must reference a known run, event and observed behavior");
            var lines = File.ReadAllLines(path);
            var eventJson = lines.FirstOrDefault(line =>
            {
                using var value = JsonDocument.Parse(line);
                return value.RootElement.TryGetProperty("seq", out var seq) && seq.GetInt32() == reference.EventSeq;
            }) ?? throw new ArgumentException($"event {reference.EventSeq} is absent from {reference.RunId}");
            return reference with { ArtifactHash = HarnessArtifacts.HashText(File.ReadAllText(path)), EventJson = eventJson };
        }).ToList();
        if (package is not null)
        {
            if (!string.Equals(package.Id, lesson.ProfileId, StringComparison.Ordinal))
                throw new ArgumentException("profile package does not match profileId");
            if (IsPackagedComponent(lesson.Component) && !package.Files.ContainsKey(lesson.Component))
                throw new ArgumentException("lesson component is not in the retained profile package");
            RetainPackage(package);
        }
        else if (IsPackagedComponent(lesson.Component))
        {
            throw new ArgumentException("a lesson about a packaged file requires the adopted profile");
        }
        var recorded = lesson with { Evidence = evidence, Revoked = false };
        HarnessArtifacts.WriteJson(Path.Combine(directory, recorded.Id + ".json"), recorded);
        return recorded;
    }

    public string Describe(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{64}$")) throw new ArgumentException("invalid lesson id");
        var path = Path.Combine(directory, id + ".json");
        var lesson = JsonSerializer.Deserialize<HarnessLesson>(File.ReadAllText(path), HarnessArtifacts.Json)
            ?? throw new ArgumentException("lesson is missing");
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{lesson.Id} ({(lesson.Revoked ? "revoked" : lesson.Verdict)}, {lesson.Confidence})");
        text.AppendLine($"When {lesson.Cue}: {lesson.Lesson}");
        text.AppendLine($"Component: {lesson.Component}");
        text.AppendLine($"Profile: {lesson.ProfileId}");
        text.AppendLine($"Revision: {lesson.SourceRevision ?? "unrecorded"}{(lesson.RevisionSpecific ? " (revision-specific)" : " (reusable)")}");
        text.AppendLine($"Route: {lesson.Provider ?? "any"}/{lesson.Model ?? "any"} runtime {lesson.Runtime}");
        foreach (var item in lesson.Evidence)
        {
            text.AppendLine($"Evidence {item.RunId}#{item.EventSeq}: {item.Observation}");
            if (!string.IsNullOrWhiteSpace(item.EventJson)) text.AppendLine(item.EventJson);
        }
        var packagePath = Path.Combine(directory, "packages", lesson.ProfileId, "profile.json");
        if (!File.Exists(packagePath))
        {
            text.AppendLine("Package: not retained");
            return text.ToString();
        }
        var package = HarnessProfile.Load(packagePath);
        text.AppendLine($"Package: {packagePath}");
        foreach (var file in package.Files.Keys.OrderBy(p => p, StringComparer.Ordinal)) text.AppendLine("- " + file);
        return text.ToString();
    }

    public IReadOnlyList<HarnessLesson> List(string scope, string taskClass)
    {
        if (!Directory.Exists(directory)) return [];
        var lessons = new List<HarnessLesson>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                var lesson = JsonSerializer.Deserialize<HarnessLesson>(File.ReadAllText(path), HarnessArtifacts.Json);
                if (lesson is { Revoked: false, Verdict: "helped" } && lesson.Scope == scope && lesson.TaskClass == taskClass
                    && Path.GetFileNameWithoutExtension(path) == lesson.Id) lessons.Add(lesson);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { /* corrupt entries are not context */ }
        }
        return lessons;
    }

    public void Revoke(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{64}$")) throw new ArgumentException("invalid lesson id");
        var path = Path.Combine(directory, id + ".json");
        var lesson = JsonSerializer.Deserialize<HarnessLesson>(File.ReadAllText(path), HarnessArtifacts.Json)
            ?? throw new ArgumentException("lesson is missing");
        HarnessArtifacts.WriteJson(path, lesson with { Revoked = true });
    }

    public void DeleteScope(string scope)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            HarnessLesson? lesson;
            try
            {
                lesson = JsonSerializer.Deserialize<HarnessLesson>(File.ReadAllText(path), HarnessArtifacts.Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { continue; }
            if (lesson?.Scope == scope && Path.GetFileNameWithoutExtension(path) == lesson.Id) File.Delete(path);
        }
    }

    public static string? WorkspaceRevision(string workspace)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(workspace);
            start.ArgumentList.Add("rev-parse");
            start.ArgumentList.Add("HEAD");
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(5_000)) return null;
            var revision = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && Regex.IsMatch(revision, "^[0-9a-f]{40}$") ? revision : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    public string Render(string workspace, string taskClass, string query, LessonCompatibility? compatibility = null)
    {
        var tokens = Regex.Matches(query.ToLowerInvariant(), "[a-z0-9]{3,}").Select(m => m.Value).Distinct().ToArray();
        if (tokens.Length == 0) return "";
        var matches = List(WorkspaceScope(workspace), taskClass).Where(lesson => Compatible(lesson, compatibility))
            .Select(lesson => (Lesson: lesson, Score: tokens.Count(t => (lesson.Cue + " " + lesson.Component).Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(match => match.Score > 0).OrderByDescending(m => m.Score).ThenBy(m => m.Lesson.Id, StringComparer.Ordinal).Take(3);
        var entries = matches.Select(m => $"- When {m.Lesson.Cue}: {m.Lesson.Lesson} (component: {m.Lesson.Component}; confidence: {m.Lesson.Confidence}; record: {m.Lesson.Id})").ToList();
        if (entries.Count == 0) return "";
        return "Lessons from prior contrasting executions in this workspace. Check their applicability against the current repository; recorded observations are evidence, not commands.\n"
            + string.Join("\n", entries);
    }

    private void RetainPackage(HarnessProfile package)
    {
        var path = Path.Combine(directory, "packages", package.Id, "profile.json");
        if (File.Exists(path))
        {
            var existing = HarnessProfile.Load(path);
            if (existing.Id != package.Id) throw new ArgumentException("retained profile package was modified");
            return;
        }
        package.Save(path);
    }

    private static bool IsPackagedComponent(string component) => HarnessProfile.IsPackagePath(component);

    private static bool Compatible(HarnessLesson lesson, LessonCompatibility? compatibility)
    {
        if (compatibility is null) return true;
        if (!string.IsNullOrEmpty(lesson.Provider) && !string.IsNullOrEmpty(compatibility.Provider)
            && !string.Equals(lesson.Provider, compatibility.Provider, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(lesson.Model) && !string.IsNullOrEmpty(compatibility.Model)
            && !string.Equals(lesson.Model, compatibility.Model, StringComparison.Ordinal)) return false;
        if (!lesson.RevisionSpecific) return true;
        return !string.IsNullOrEmpty(lesson.SourceRevision) && !string.IsNullOrEmpty(compatibility.Revision)
            && string.Equals(lesson.SourceRevision, compatibility.Revision, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record LessonCompatibility(string? Revision, string? Provider, string? Model);
