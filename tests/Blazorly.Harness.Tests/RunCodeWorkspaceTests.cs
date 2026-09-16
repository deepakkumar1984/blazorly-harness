using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// run_code executes in-process under danger-full-access, and Directory.GetCurrentDirectory is
/// process-global: relative paths used to resolve against wherever the host binary was launched
/// (e.g. src/Blazorly.Harness.Web) instead of the session workspace, so a script's
/// File.ReadAllText("src/schema.js") failed with a path from a different directory tree.
/// </summary>
public class RunCodeWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-runcode-" + Guid.NewGuid().ToString("N")[..8]);


    public RunCodeWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    [Fact]
    public async Task RelativePaths_ResolveAgainstTheSessionWorkspace()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "schema.js"), "export const schema = {};\n");
        await using var harness = CreateHarness(SandboxPolicy.DangerFullAccess);
        var agent = harness.CreateAgent(_root);

        var result = await Run(harness, agent,
            "return File.ReadAllText(Path.Combine(\"src\", \"schema.js\"));");

        Assert.False(result.IsError, result.Error?.Message);
        Assert.Contains("export const schema", result.Value!.Value.GetProperty("result").GetString());
    }

    [Fact]
    public async Task WritesLandInTheWorkspace_AndTheHostCwdIsRestored()
    {
        await using var harness = CreateHarness(SandboxPolicy.DangerFullAccess);
        var agent = harness.CreateAgent(_root);
        var hostCwd = Directory.GetCurrentDirectory();

        var result = await Run(harness, agent,
            "File.WriteAllText(\"probe.txt\", \"written\");\nreturn Directory.GetCurrentDirectory();");

        Assert.False(result.IsError, result.Error?.Message);
        // The OS resolves the temp-dir symlink, so compare the unique leaf rather than the raw path.
        var scriptCwd = result.Value!.Value.GetProperty("result").GetString()!;
        Assert.EndsWith(Path.GetFileName(_root), scriptCwd);
        Assert.Equal("written", await File.ReadAllTextAsync(Path.Combine(_root, "probe.txt")));
        Assert.Equal(hostCwd, Directory.GetCurrentDirectory()); // the swap is scoped to the run
    }

    [Fact]
    public async Task WorkspaceGlobal_ExposesTheSessionRoot()
    {
        await using var harness = CreateHarness(SandboxPolicy.DangerFullAccess);
        var agent = harness.CreateAgent(_root);

        var result = await Run(harness, agent, "return Workspace;");

        Assert.False(result.IsError, result.Error?.Message);
        Assert.Equal(_root, result.Value!.Value.GetProperty("result").GetString());
    }

    [Fact]
    public async Task MissingFile_ErrorNamesTheCwdItResolvedAgainst()
    {
        await using var harness = CreateHarness(SandboxPolicy.DangerFullAccess);
        var agent = harness.CreateAgent(_root);

        var result = await Run(harness, agent, "return File.ReadAllText(\"nope.txt\");");

        Assert.True(result.IsError);
        Assert.Equal("RUN_CODE_FAILED", result.Error!.Info!.Code);
        Assert.Contains(Path.GetFileName(_root), result.Error.Message);
        Assert.Contains("run_code cwd:", result.Error.Message);
    }

    [Fact]
    public async Task ForwardedToolCalls_UseThePublishedSchemaNames()
    {
        // The in-process host camelCased forwarded arguments while the confined runner did not,
        // so the same script behaved differently per sandbox mode.
        await using var harness = CreateHarness(SandboxPolicy.DangerFullAccess);
        var agent = harness.CreateAgent(_root);
        var target = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(target, "first\nsecond\n");

        var written = await Run(harness, agent,
            $$"""
              var path = {{JsonSerializer.Serialize(target)}};
              await Tools.CallAsync("read", new { file_path = path });
              var edit = await Tools.CallAsync("edit", new { file_path = path, old_string = "first", new_string = "1st" });
              return edit.ToString();
              """);

        Assert.False(written.IsError, written.Error?.Message);
        Assert.Equal("1st\nsecond\n", await File.ReadAllTextAsync(target));
    }

    private static TestHarness CreateHarness(string mode)
    {
        var harness = TestHarness.Create();
        harness.Sandbox.DefaultMode = mode;
        new CodeModePlugin().Apply(harness.Ctx);
        return harness;
    }

    private static Task<ToolExecutionResult> Run(TestHarness harness, Agent agent, string code)
        => harness.Tools.Execute(new ToolExecutionInput
        {
            Name = "run_code",
            Arguments = JsonSerializer.SerializeToElement(new { code, description = "workspace cwd check" }),
            CallId = Ids.NewCallId(),
            Signal = CancellationToken.None,
            Agent = agent,
        });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
