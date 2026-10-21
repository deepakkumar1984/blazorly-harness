using Blazorly.Harness.Core.Instructions;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;
using Xunit;

namespace Blazorly.Harness.Tests;

public class AiDocsDrafterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blazorly-aidocs-" + Guid.NewGuid().ToString("N")[..8]);

    public AiDocsDrafterTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "package.json"), """{"name":"demo","scripts":{"test":"vitest run"}}""");
        Directory.CreateDirectory(Path.Combine(_root, "backend"));
        File.WriteAllText(Path.Combine(_root, "backend", "server.ts"), "export {};\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task DraftAsync_CleanDraft_StampsAndReportsNothing()
    {
        string? seenPrompt = null;
        Task<string> Complete(string prompt, CancellationToken ct)
        {
            seenPrompt = prompt;
            return Task.FromResult("# demo\n\nSee `backend/server.ts` and run `npm run test`.\n");
        }

        var result = await AiDocsDrafter.DraftAsync(_root, 0, "test/model", Complete);

        var doc = Assert.Single(result.Docs);
        Assert.Equal("test/model", result.Model);
        Assert.Contains("backend/server.ts", doc.Content);
        Assert.Contains("by blazorly init", doc.Content);
        Assert.Contains(DocsInitService.ManualMarker, doc.Content);
        Assert.Empty(doc.Unresolved);
        Assert.Contains("package.json", seenPrompt);
    }

    [Fact]
    public async Task DraftAsync_InventedPath_RetriesOnceWithFeedback()
    {
        var calls = new List<string>();
        Task<string> Complete(string prompt, CancellationToken ct)
        {
            calls.Add(prompt);
            return calls.Count == 1
                ? Task.FromResult("# demo\n\nSee `imaginary/deep.ts`.\n")
                : Task.FromResult("# demo\n\nSee `backend/server.ts`.\n");
        }

        var result = await AiDocsDrafter.DraftAsync(_root, 0, "test/model", Complete);

        Assert.Equal(2, calls.Count);
        Assert.Contains("imaginary/deep.ts", calls[1]);
        Assert.Empty(Assert.Single(result.Docs).Unresolved);
    }

    [Fact]
    public async Task DraftAsync_PersistentHallucination_IsReported()
    {
        Task<string> Complete(string prompt, CancellationToken ct)
            => Task.FromResult("# demo\n\nSee `imaginary/deep.ts`.\n");

        var result = await AiDocsDrafter.DraftAsync(_root, 0, "test/model", Complete);

        var unresolved = Assert.Single(Assert.Single(result.Docs).Unresolved);
        Assert.Equal("imaginary/deep.ts", unresolved.Reference);
    }

    [Fact]
    public async Task CompleteWith_CollectsTextThroughRealWaterfall()
    {
        await using var ctx = HarnessContext.CreateRoot();
        var llm = LlmRuntime.Mount(ctx);
        llm.RegisterAdapter(new ScriptedAdapter(["# demo\n", "body\n"], error: null));

        var complete = AiDocsDrafter.CompleteWith(llm, "test", "m");
        var text = await complete("prompt", CancellationToken.None);

        Assert.Equal("# demo\nbody\n", text);
    }

    [Fact]
    public async Task CompleteWith_ErrorFinish_ThrowsLlmException()
    {
        await using var ctx = HarnessContext.CreateRoot();
        var llm = LlmRuntime.Mount(ctx);
        llm.RegisterAdapter(new ScriptedAdapter([], error: "boom"));

        var complete = AiDocsDrafter.CompleteWith(llm, "test", "m");
        var ex = await Assert.ThrowsAsync<LlmException>(() => complete("prompt", CancellationToken.None));

        Assert.Contains("boom", ex.Message);
    }

    private sealed class ScriptedAdapter(List<string> deltas, string? error) : LlmAdapter
    {
        public override string Provider => "test";

        public override async IAsyncEnumerable<StreamChunk> Stream(
            GenerateOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var text in deltas)
            {
                yield return new TextDeltaChunk(0, text);
                await Task.Yield();
            }
            yield return error is null
                ? new FinishChunk(FinishReason.Stop)
                : new FinishChunk(FinishReason.Error, new LlmFailure(error, "TEST_ERROR"));
        }
    }

    [Fact]
    public async Task DraftAndApplyAsync_WritesDraft_PreservesManual()
    {
        Task<string> Complete(string prompt, CancellationToken ct)
            => Task.FromResult("```markdown\n# demo\n\nReal content.\n```\n");

        var result = await AiDocsDrafter.DraftAndApplyAsync(_root, 0, "test/model", Complete);

        Assert.Equal("AGENTS.md", Assert.Single(result.Written));
        var path = Path.Combine(_root, "AGENTS.md");
        var written = File.ReadAllText(path);
        Assert.Contains("Real content.", written);
        Assert.DoesNotContain("```", written);

        File.AppendAllText(path, "Hand note.\n");
        await AiDocsDrafter.DraftAndApplyAsync(_root, 0, "test/model", Complete);
        Assert.Contains("Hand note.", File.ReadAllText(path));
    }
}
