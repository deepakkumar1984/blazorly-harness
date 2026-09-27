using System.Text;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

public sealed record SkillSummary(string Name, string Description);

/// <summary>
/// Scans skill roots for &lt;dir&gt;/SKILL.md files: frontmatter between leading --- lines supplies
/// name and description; the body is the full instruction markdown.
/// </summary>
public sealed class SkillsService(params string[] roots)
{
    public IReadOnlyList<string> Roots { get; } = roots;

    /// <summary>Skill discovery roots, in precedence order (first hit wins on name
    /// collisions): the harness-native folder, the shared ~/.agents convention (the
    /// same SKILL.md format other agent tools read, so one collection serves them all),
    /// and a project-local folder.</summary>
    public static string[] DefaultRoots() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly", "skills"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills"),
        Path.Combine(Environment.CurrentDirectory, ".blazorly", "skills"),
    ];

    public IReadOnlyList<SkillSummary> List()
    {
        var byName = new Dictionary<string, SkillSummary>(StringComparer.Ordinal);
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var file = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(file)) continue;
                var (name, description) = ParseFrontmatter(File.ReadAllLines(file));
                if (name is null || name.Length == 0) continue;
                byName.TryAdd(name, new SkillSummary(name, description ?? ""));
            }
        }
        return [.. byName.Values.OrderBy(s => s.Name, StringComparer.Ordinal)];
    }

    /// <summary>Ranked lexical search over name + description. Empty query returns
    /// everything by name; otherwise every query token must appear in the name or
    /// description (case-insensitive), name hits ranking first.</summary>
    public IReadOnlyList<SkillSummary> Search(string query, int limit = 10)
    {
        var all = List();
        query = (query ?? "").Trim();
        if (query.Length == 0) return all.Take(Math.Max(1, limit)).ToList();
        var tokens = query.Split([' ', '\t', ',', ';', '/', '-'], StringSplitOptions.RemoveEmptyEntries);
        var ranked = new List<(SkillSummary Skill, int Score)>();
        foreach (var skill in all)
        {
            var score = 0;
            var matchedAll = true;
            foreach (var rawToken in tokens)
            {
                var token = rawToken.Trim();
                if (token.Length == 0) continue;
                var inName = skill.Name.Contains(token, StringComparison.OrdinalIgnoreCase);
                var inDesc = skill.Description.Contains(token, StringComparison.OrdinalIgnoreCase);
                if (!inName && !inDesc) { matchedAll = false; break; }
                score += inName ? 3 : 0;
                score += inDesc ? 1 : 0;
                if (string.Equals(skill.Name, token, StringComparison.OrdinalIgnoreCase)) score += 5;
            }
            if (matchedAll) ranked.Add((skill, score));
        }
        return [.. ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Skill.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(r => r.Skill)];
    }

    public string? ReadBody(string name)
    {
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(file)) continue;
                var (found, _) = ParseFrontmatter(File.ReadAllLines(file));
                if (string.Equals(found, name, StringComparison.OrdinalIgnoreCase))
                    return File.ReadAllText(file);
            }
        }
        return null;
    }

    private static (string? Name, string? Description) ParseFrontmatter(string[] lines)
    {
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, null);
        string? name = null;
        string? description = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---") break;
            if (name is null && line.StartsWith("name:", StringComparison.Ordinal))
                name = line["name:".Length..].Trim();
            else if (description is null && line.StartsWith("description:", StringComparison.Ordinal))
                description = line["description:".Length..].Trim();
        }
        return (name, description);
    }
}

public sealed record SkillArgs(string Name);

public sealed record SkillOutput(string Name, string Description, string Body);

/// <summary>skill: load one skill's full instruction markdown by name.</summary>
public sealed class SkillTool(SkillsService skills) : ToolDefinition<SkillArgs, SkillOutput>
{
    public override string Name => "skill";

    public override string Description =>
        "Load a skill's full instructions by name. Call search_skills first to find the "
        + "matching skill, then call this with its name before starting the task it covers.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["name"] = JsonSchema.String("Name of the skill to load, as returned by search_skills."),
        },
        required: ["name"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["name"] = JsonSchema.String(),
            ["description"] = JsonSchema.String(),
            ["body"] = JsonSchema.String(),
        },
        required: ["name", "description", "body"]);

    protected override bool IsConcurrencySafeTyped(SkillArgs args) => true;

    protected override Task<SkillOutput> ExecuteTyped(SkillArgs args, ToolRunContext exec)
    {
        var summary = skills.List().FirstOrDefault(s => string.Equals(s.Name, args.Name, StringComparison.OrdinalIgnoreCase));
        if (summary is null)
            throw new ToolException("UNKNOWN_SKILL", $"no skill named '{args.Name}' is installed");
        var body = skills.ReadBody(summary.Name)
            ?? throw new ToolException("UNKNOWN_SKILL", $"skill '{summary.Name}' could not be read");
        return Task.FromResult(new SkillOutput(summary.Name, summary.Description, body));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SkillArgs args, SkillOutput output)
        => [new TextBlock(output.Body)];

    protected override ToolCallView? PresentCallTyped(SkillArgs args) => new()
    {
        Card = "generic",
        Kind = "read",
        Title = args.Name,
        Description = "load skill instructions",
    };
}

public sealed record SearchSkillsArgs(string Query, int? Limit = null);

public sealed record SearchSkillsOutput(IReadOnlyList<SkillSummary> Matches, int TotalInstalled);

/// <summary>search_skills: keyword search over installed skill names + descriptions.
/// Returns only name/description pairs; call skill with the name to load the body.</summary>
public sealed class SearchSkillsTool(SkillsService skills) : ToolDefinition<SearchSkillsArgs, SearchSkillsOutput>
{
    public const int MaxLimit = 20;

    public override string Name => "search_skills";

    public override string Description =>
        "Search installed skills by task keywords over skill names and descriptions. "
        + "Returns name + description matches only; call skill with the name to load full instructions.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["query"] = JsonSchema.String("Keywords describing the task, e.g. 'release notes' or 'pdf'. Empty lists skills by name."),
            ["limit"] = JsonSchema.Integer("Max matches to return. Defaults to 10, capped at 20."),
        },
        required: ["query"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["matches"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["name"] = JsonSchema.String(),
                    ["description"] = JsonSchema.String(),
                },
                Required = ["name", "description"],
                AdditionalProperties = false,
            }),
            ["totalInstalled"] = JsonSchema.Integer(),
        },
        required: ["matches", "totalInstalled"]);

    protected override bool IsConcurrencySafeTyped(SearchSkillsArgs args) => true;

    protected override Task<SearchSkillsOutput> ExecuteTyped(SearchSkillsArgs args, ToolRunContext exec)
    {
        var limit = Math.Clamp(args.Limit ?? 10, 1, MaxLimit);
        var matches = skills.Search(args.Query ?? "", limit);
        return Task.FromResult(new SearchSkillsOutput(matches, skills.List().Count));
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(SearchSkillsArgs args, SearchSkillsOutput output)
    {
        if (output.Matches.Count == 0)
            return [new TextBlock($"No skills match '{args.Query}' ({output.TotalInstalled} installed).")];
        var builder = new StringBuilder();
        foreach (var match in output.Matches)
            builder.Append("- ").Append(match.Name).Append(": ").AppendLine(match.Description);
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(SearchSkillsArgs args) => new()
    {
        Card = "generic",
        Kind = "search",
        Title = args.Query,
        Description = "search skills",
    };
}

/// <summary>Mounts the skill search + load tools plus a fixed-size system-prompt pointer.
/// The catalog itself is never injected: the prompt carries only the installed count,
/// and name/description pairs return via search_skills on demand.</summary>
public sealed class SkillPlugin : HarnessPlugin
{
    public override string Name => "skills";
    public override string[] Inject { get; } = ["tools", "systemPrompt"];

    public SkillsService Skills { get; }

    public SkillPlugin() : this(new SkillsService(SkillsService.DefaultRoots())) { }

    public SkillPlugin(SkillsService skills) => Skills = skills;

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        ctx.Provide("skills", Skills);
        var tools = ctx.Get<ToolRuntime>("tools");
        ctx.Effect(tools.Register(new SkillTool(Skills)).Dispose);
        ctx.Effect(tools.Register(new SearchSkillsTool(Skills)).Dispose);
        var prompt = ctx.Get<Core.SystemPrompt.SystemPromptService>("systemPrompt");
        var section = prompt.RegisterSection("skills", 108, _ => RenderPointer(Skills.List().Count));
        ctx.Effect(section.Dispose);
        return Task.CompletedTask;
    }

    public static string RenderPointer(int installed)
    {
        if (installed == 0) return "";
        return $"{installed} skills installed. Call search_skills with task keywords to find one, "
            + "then skill with its name to load full instructions before starting the task it covers.";
    }

    public static string RenderCatalog(IReadOnlyList<SkillSummary> skills) => RenderPointer(skills.Count);
}
