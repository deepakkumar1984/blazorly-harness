using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Local models routinely emit an explicit <c>null</c> for a required string. That satisfied the
/// presence check and reached the tool body, where <c>string.Replace(old, null)</c> surfaced as
/// "Value cannot be null. (Parameter 'value')" — an unactionable TOOL_FAILED the model retried.
/// </summary>
public class ToolArgumentValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-args-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolArgumentValidationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RequiredString_ExplicitNull_IsRejectedByTheSchema()
    {
        var schema = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema.Schema> { ["new_string"] = JsonSchema.String() },
            required: ["new_string"]);
        using var args = JsonDocument.Parse("""{"new_string":null}""");

        var violation = JsonSchema.Validate(args.RootElement, schema);

        Assert.Equal("$: required property 'new_string' must not be null", violation);
    }

    [Fact]
    public void RequiredString_AbsentOrPresent_KeepsTheExistingContract()
    {
        var schema = JsonSchema.Object(
            properties: new Dictionary<string, JsonSchema.Schema> { ["file_path"] = JsonSchema.String() },
            required: ["file_path"]);
        using var missing = JsonDocument.Parse("{}");
        using var present = JsonDocument.Parse("""{"file_path":"a.txt"}""");

        Assert.Equal("$: missing required property 'file_path'", JsonSchema.Validate(missing.RootElement, schema));
        Assert.Null(JsonSchema.Validate(present.RootElement, schema));
    }

    [Fact]
    public async Task Edit_NullNewString_BecomesAnActionableInvalidArgsError()
    {
        await using var harness = TestHarness.Create(cwd: _root);
        var agent = harness.CreateAgent(_root);
        var target = Path.Combine(_root, "index.html");
        await File.WriteAllTextAsync(target, "<main>old</main>");
        await harness.Tools.Execute(Read(agent, target)); // satisfies the read-before-edit guard

        var result = await harness.Tools.Execute(Call(agent, "edit", new
        {
            file_path = target,
            old_string = "<main>old</main>",
            new_string = (string?)null,
        }));

        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGS", result.Error!.Info?.Code);
        Assert.Contains("new_string", result.Error.Message);
        Assert.Contains("<main>old</main>", await File.ReadAllTextAsync(target)); // file untouched
    }

    [Fact]
    public async Task Edit_SnakeCaseArguments_ApplyTheChange()
    {
        // The published schema says old_string/new_string; the serializer is camelCase, so without
        // [JsonPropertyName] the model's arguments bound to null and every edit failed.
        await using var harness = TestHarness.Create(cwd: _root);
        var agent = harness.CreateAgent(_root);
        var target = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(target, "const a = 1;\n");
        await harness.Tools.Execute(Read(agent, target));

        var result = await harness.Tools.Execute(Call(agent, "edit", new
        {
            file_path = target,
            old_string = "const a = 1;",
            new_string = "const a = 2;",
        }));

        Assert.False(result.IsError, result.Error?.Message);
        Assert.Equal("const a = 2;\n", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Write_NullContent_BecomesAnActionableInvalidArgsError()
    {
        await using var harness = TestHarness.Create(cwd: _root);
        var agent = harness.CreateAgent(_root);
        var target = Path.Combine(_root, "notes.txt");

        var result = await harness.Tools.Execute(Call(agent, "write", new { file_path = target, content = (string?)null }));

        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGS", result.Error!.Info?.Code);
        Assert.Contains("content", result.Error.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void SandboxUnavailable_TellsTheModelNotToToolHop()
    {
        var message = SandboxPolicy.ConfinementUnavailable("bash", SandboxPolicy.WorkspaceWrite);

        Assert.StartsWith("[sandbox: bash cannot run under 'workspace-write'", message);
        Assert.Contains("do not retry", message);
        Assert.Contains("do not switch tools", message);
        Assert.Contains("/permission full-access", message);
    }

    private static ToolExecutionInput Read(Agent agent, string path) => Call(agent, "read", new { file_path = path });

    private static ToolExecutionInput Call(Agent agent, string name, object arguments) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(arguments),
        CallId = $"call_av_{Guid.NewGuid().ToString("N")[..6]}",
        Signal = CancellationToken.None,
        Agent = agent,
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
