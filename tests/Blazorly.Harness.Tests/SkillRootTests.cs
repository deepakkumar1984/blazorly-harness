using Blazorly.Harness.Tools;

namespace Blazorly.Harness.Tests;

/// <summary>Skill discovery across roots: the harness folder, the shared ~/.agents
/// convention, and the project folder — with harness-native skills winning collisions.</summary>
public sealed class SkillRootTests
{
    private static string WriteSkill(string root, string name, string description)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\nbody text");
        return dir;
    }

    [Fact]
    public void DefaultRoots_IncludeTheSharedAgentsConvention()
    {
        var roots = SkillsService.DefaultRoots().Select(Path.GetFileName).ToList();
        // every entry ends in "skills" — what distinguishes them is the parent folder
        var parents = SkillsService.DefaultRoots()
            .Select(r => Path.GetFileName(Path.GetDirectoryName(r)))
            .ToList();
        Assert.Equal(".blazorly", parents[0]); // harness-native first: wins collisions
        Assert.Equal(".agents", parents[1]); // shared ecosystem second
    }

    [Fact]
    public void List_MergesRoots_FirstRootWinsOnCollision()
    {
        var blazorlyRoot = Path.Combine(Path.GetTempPath(), "blz-skills-" + Guid.NewGuid().ToString("N"));
        var agentsRoot = Path.Combine(Path.GetTempPath(), "agents-skills-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteSkill(blazorlyRoot, "release", "native release skill");
            WriteSkill(agentsRoot, "release", "shared release skill"); // collision
            WriteSkill(agentsRoot, "only-shared", "exists only in the shared root");

            var service = new SkillsService(blazorlyRoot, agentsRoot);
            var names = service.List().Select(s => s.Name).ToList();
            Assert.Contains("release", names);
            Assert.Contains("only-shared", names);
            Assert.Equal(2, names.Count); // the collision resolves to ONE entry

            // Precedence: the first root's copy wins, and its body is what loads.
            Assert.Equal("native release skill",
                service.List().First(s => s.Name == "release").Description);
            Assert.Contains("body text", service.ReadBody("release"));
            Assert.Contains("native release skill", service.ReadBody("release"));
        }
        finally
        {
            Directory.Delete(blazorlyRoot, recursive: true);
            Directory.Delete(agentsRoot, recursive: true);
        }
    }

    [Fact]
    public void List_IgnoresExtraFrontmatterKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "blz-skills-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = Path.Combine(root, "leveled");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: leveled\ndescription: has extra keys\nlevel: 3\nallowed-tools: bash\n---\n\nbody");
            var skill = Assert.Single(new SkillsService(root).List());
            Assert.Equal("leveled", skill.Name);
            Assert.Equal("has extra keys", skill.Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
