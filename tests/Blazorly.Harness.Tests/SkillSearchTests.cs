using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Tools;

namespace Blazorly.Harness.Tests;

/// <summary>Skills stay out of the system prompt: only a fixed-size pointer is injected,
/// and name/description pairs return on demand via search_skills.</summary>
public sealed class SkillSearchTests
{
    private static string WriteSkill(string root, string name, string description)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\nbody text");
        return dir;
    }

    private static SkillsService TwoSkills(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "blz-skillsearch-" + Guid.NewGuid().ToString("N"));
        WriteSkill(root, "release", "cut release notes and tags");
        WriteSkill(root, "pdf-forms", "fill pdf paperwork and notes");
        return new SkillsService(root);
    }

    [Fact]
    public void Search_MatchesNameOrDescription_RequiresEveryToken()
    {
        var service = TwoSkills(out var root);
        try
        {
            Assert.Equal("release", Assert.Single(service.Search("release")).Name);
            Assert.Equal("pdf-forms", Assert.Single(service.Search("paperwork")).Name);
            Assert.Empty(service.Search("release paperwork"));
            Assert.Equal(2, service.Search("notes").Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Search_EmptyQuery_ListsByName_AndRespectsLimit()
    {
        var service = TwoSkills(out var root);
        try
        {
            var all = service.Search("");
            Assert.Equal(["pdf-forms", "release"], all.Select(s => s.Name).ToList());
            Assert.Single(service.Search("", limit: 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SystemPrompt_Pointer_IsFixedSize_NeverListsCatalog()
    {
        var pointer = SkillPlugin.RenderPointer(2);
        Assert.Contains("search_skills", pointer);
        Assert.Contains("skill", pointer);
        Assert.DoesNotContain("release", pointer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paperwork", pointer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", SkillPlugin.RenderPointer(0));
        // Legacy entry point now delegates to the pointer.
        Assert.Equal(pointer, SkillPlugin.RenderCatalog(
            [new SkillSummary("release", "cut release notes"), new SkillSummary("pdf-forms", "fill pdf")]));
    }

    [Fact]
    public void SearchTool_IsGroupedWithSkills()
    {
        Assert.Equal(ToolGroups.Skills.Id, ToolGroups.Of("skill").Id);
        Assert.Equal(ToolGroups.Skills.Id, ToolGroups.Of("search_skills").Id);
    }
}
