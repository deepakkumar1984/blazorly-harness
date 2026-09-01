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

    public static string[] DefaultRoots() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly", "skills"),
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
        "Load a skill's full instructions by name. The system prompt lists the available skills; "
        + "call this with the matching name before starting the task the skill covers.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["name"] = JsonSchema.String("Name of the skill to load, as listed in the skills catalog."),
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

/// <summary>Mounts the skill tool plus a system-prompt section listing the catalog.</summary>
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
        var prompt = ctx.Get<Core.SystemPrompt.SystemPromptService>("systemPrompt");
        var section = prompt.RegisterSection("skills", 108, _ => RenderCatalog(Skills.List()));
        ctx.Effect(section.Dispose);
        return Task.CompletedTask;
    }

    internal static string RenderCatalog(IReadOnlyList<SkillSummary> skills)
    {
        if (skills.Count == 0) return "";
        var builder = new StringBuilder("Available skills:");
        foreach (var skill in skills)
            builder.Append("\n- ").Append(skill.Name).Append(": ").Append(skill.Description);
        builder.Append("\n").Append("When a skill matches your task, call the skill tool with its name to load full instructions.");
        return builder.ToString();
    }
}
