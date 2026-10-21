using System.Net;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Core.TokenSaver;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Token saver (Settings → Token saver): native RTK-style tool-output filters, Caveman and
/// Ponytail prompt sections, and Headroom proxy routing. Everything is off by default, so
/// several tests pin "no change at all" for the default configuration.
/// </summary>
public class TokenSaverTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---------- tool-output filters ----------

    private const string GitStatusLong =
        """
        On branch main
        Your branch is up to date with 'origin/main'.

        Changes to be committed:
          (use "git restore --staged <file>..." to unstage)
                modified:   src/App.cs
                new file:   src/Feature.cs

        Changes not staged for commit:
          (use "git add <file>..." to update what will be committed)
          (use "git restore <file>..." to discard changes in working directory)
                modified:   src/Program.cs
                modified:   README.md

        Untracked files:
          (use "git add <file>..." to include in what will be committed)
                notes.txt
                scratch/output.log
                tmp-report-with-a-long-name-to-push-past-the-minimum-size-threshold-0123456789.txt

        no changes added to commit (use "git add" and/or "git commit -a")
        """;

    [Fact]
    public void Compress_GitStatus_GroupsBySection()
    {
        var result = ToolOutputCompressor.Compress("bash", "git status", GitStatusLong, isError: false);
        Assert.NotNull(result);
        Assert.Contains("staged (2): modified src/App.cs; new file src/Feature.cs", result);
        Assert.Contains("unstaged (2): modified src/Program.cs; modified README.md", result);
        Assert.Contains("untracked (3): notes.txt", result);
        Assert.DoesNotContain("(use \"git restore", result);
        Assert.Contains("[compressed by token saver:", result);
        Assert.True(result.Length < GitStatusLong.Length);
    }

    [Fact]
    public void Compress_GitLog_KeepsHashAndSubjectOnly()
    {
        var log = string.Join("\n", Enumerable.Range(1, 5).Select(i =>
            $"""
            commit {i:x40}
            Author: Dev Example <dev@example.com>
            Date:   Mon Oct 13 10:00:00 2026 +0000

                Subject line number {i} with enough words to look real

                A body paragraph that explains the change in more detail than the
                subject ever could, wrapped over two lines like real commits are.
            """));
        var result = ToolOutputCompressor.Compress("bash", "git log", log, isError: false);
        Assert.NotNull(result);
        Assert.Contains("Subject line number 3", result);
        Assert.DoesNotContain("Author:", result);
        Assert.DoesNotContain("body paragraph", result);
    }

    [Fact]
    public void Compress_GitDiff_CutsContextAroundChanges()
    {
        var diffLines = new List<string>
        {
            "diff --git a/src/App.cs b/src/App.cs",
            "index 1111111..2222222 100644",
            "--- a/src/App.cs",
            "+++ b/src/App.cs",
            "@@ -1,20 +1,20 @@ class App",
        };
        for (var i = 1; i <= 14; i++) diffLines.Add($" unchanged context line number {i} with some padding words");
        diffLines.Add("-var oldValue = ComputeOld();");
        diffLines.Add("+var newValue = ComputeNew();");
        for (var i = 15; i <= 26; i++) diffLines.Add($" trailing context line number {i} with some padding words");
        var diff = string.Join("\n", diffLines);

        var result = ToolOutputCompressor.Compress("bash", "git diff", diff, isError: false);
        Assert.NotNull(result);
        Assert.Contains("-var oldValue = ComputeOld();", result);
        Assert.Contains("+var newValue = ComputeNew();", result);
        Assert.Contains("  …", result);
        Assert.DoesNotContain("unchanged context line number 7", result);
    }

    [Fact]
    public void Compress_GrepOutput_GroupsByFileAndTruncates()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 8; i++) lines.Add($"src/App.cs:{i * 3}:        var token{i} = ReadToken(input{i});");
        for (var i = 1; i <= 6; i++) lines.Add($"src/Auth.cs:{i * 7}:        if (token{i} is null) return;");
        lines.Add($"src/App.cs:99:        {"x".PadRight(300, 'y')}");
        var grep = string.Join("\n", lines);

        var result = ToolOutputCompressor.Compress("grep", null, grep, isError: false);
        Assert.NotNull(result);
        Assert.Contains("src/App.cs (9):", result);
        Assert.Contains("src/Auth.cs (6):", result);
        Assert.Contains("3: var token1 = ReadToken(input1);", result);
    }

    [Fact]
    public void Compress_Listing_GroupsByDirectory()
    {
        var a = Enumerable.Range(1, 12).Select(i => $"src/Components/Widget{i}.razor").ToList();
        var b = Enumerable.Range(1, 10).Select(i => $"src/Services/Service{i}.cs").ToList();
        var listing = string.Join("\n", a.Concat(b)) + "\n(showing 22 of 22 matches)";

        var result = ToolOutputCompressor.Compress("glob", null, listing, isError: false);
        Assert.NotNull(result);
        Assert.Contains("src/Components/: Widget1.razor", result);
        Assert.Contains("(12 files)", result);
        Assert.Contains("src/Services/: Service1.cs", result);
        Assert.Contains("(showing 22 of 22 matches)", result);
    }

    [Fact]
    public void Compress_RepetitiveLog_CollapsesRepeatRuns()
    {
        var log = string.Join("\n", Enumerable.Repeat("worker ready, waiting for work", 60)) + "\njob finished";
        var result = ToolOutputCompressor.Compress("bash", "docker logs app", log, isError: false);
        Assert.NotNull(result);
        Assert.Contains("same line repeated 57 more times", result);
        Assert.Contains("job finished", result);
        Assert.Equal(3, result.Split("worker ready, waiting for work").Length - 1);
    }

    [Fact]
    public void Compress_FailedCommand_KeepsSemanticContentUntouched()
    {
        var failed = GitStatusLong + "\n[exit code: 1]";
        var result = ToolOutputCompressor.Compress("bash", "git status", failed, isError: false);
        // Generic cleanup may still run, but the git-status reshape must not.
        Assert.True(result is null || result.Contains("Changes to be committed:"));
    }

    [Fact]
    public void Compress_PipelineCommand_GetsNoSemanticFilter()
    {
        var result = ToolOutputCompressor.Compress("bash", "git status | head -5", GitStatusLong, isError: false);
        Assert.True(result is null || result.Contains("Changes to be committed:"));
    }

    [Theory]
    [InlineData("read")]   // file contents stay exact
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("web_fetch")]
    public void Compress_NonFilterableTools_PassThrough(string tool)
        => Assert.Null(ToolOutputCompressor.Compress(tool, null, GitStatusLong, isError: false));

    [Fact]
    public void Compress_SmallOrUnknownShape_PassesThrough()
    {
        Assert.Null(ToolOutputCompressor.Compress("bash", "git status", "On branch main\nnothing to commit", isError: false));
        var prose = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"word{i}"));
        Assert.Null(ToolOutputCompressor.Compress("bash", "cat notes.txt", prose, isError: false));
    }

    [Fact]
    public async Task Policy_CompressesBashResults_WhenEnabledOnly()
    {
        await using var harness = TestHarness.Create(_ => Scripted.Text("unused"));
        var service = ToolOutputCompressionService.Mount(harness.Ctx, enabled: false);
        var agent = harness.CreateAgent();
        var args = JsonSerializer.SerializeToElement(new
        {
            command = "for i in $(seq 1 60); do echo \"worker ready, waiting for work\"; done; echo job-finished-unique",
            description = "noisy log",
        });

        async Task<string> RunBash()
        {
            var result = await harness.Tools.Execute(new ToolExecutionInput
            {
                Name = "bash", Arguments = args, CallId = $"call_{Guid.NewGuid():N}", Signal = CancellationToken.None, Agent = agent,
            });
            Assert.False(result.IsError);
            return string.Concat(result.Content.OfType<TextBlock>().Select(b => b.Text));
        }

        var offText = await RunBash();
        Assert.Equal(60, offText.Split("worker ready").Length - 1);
        Assert.DoesNotContain("compressed by token saver", offText);

        service.Enabled = true;
        var onText = await RunBash();
        Assert.Contains("compressed by token saver", onText);
        Assert.Contains("job-finished-unique", onText);
        Assert.True(onText.Length < offText.Length / 2, $"expected real compression, got {onText.Length} of {offText.Length}");
        Assert.Equal(1, service.CallsCompressed);
        Assert.True(service.CharsSaved > 0);
    }

    // ---------- prompt sections ----------

    private static IReadOnlyList<AssembledSection> Sections(TestHarness harness)
    {
        var agent = harness.CreateAgent();
        return harness.Prompt.Assemble(agent, agent.Session.Header.Cwd).Sections;
    }

    [Fact]
    public void Prompt_DefaultsOff_NoTokenSaverSections()
    {
        var harness = TestHarness.Create();
        var sections = Sections(harness);
        Assert.Contains(sections, s => s.Name == "harness:answer-style");
        Assert.DoesNotContain(sections, s => s.Name.StartsWith("harness:token-saver"));
    }

    [Fact]
    public void Prompt_TerseOn_ReplacesAnswerStyle()
    {
        var harness = TestHarness.Create();
        harness.Loop.TokenSaverProvider = _ => new TokenSaverPromptOptions(TerseOutput: true, MinimalCode: false);
        var sections = Sections(harness);
        Assert.DoesNotContain(sections, s => s.Name == "harness:answer-style");
        var terse = Assert.Single(sections, s => s.Name == "harness:token-saver-terse");
        Assert.Contains("Caveman style", terse.Text);
        Assert.Contains("exact machine-readable format", terse.Text);
        Assert.DoesNotContain(sections, s => s.Name == "harness:token-saver-code");
    }

    [Fact]
    public void Prompt_MinimalCodeOn_KeepsAnswerStyle()
    {
        var harness = TestHarness.Create();
        harness.Loop.TokenSaverProvider = _ => new TokenSaverPromptOptions(TerseOutput: false, MinimalCode: true);
        var sections = Sections(harness);
        Assert.Contains(sections, s => s.Name == "harness:answer-style");
        var code = Assert.Single(sections, s => s.Name == "harness:token-saver-code");
        Assert.Contains("Ponytail style", code.Text);
        Assert.Contains("Deletion over addition", code.Text);
    }

    // ---------- settings ----------

    [Fact]
    public void Settings_TokenSaverDefaultsOff_AndRoundTrips()
    {
        var settings = new HarnessSettings();
        Assert.False(settings.TokenSaverToolOutput);
        Assert.False(settings.TokenSaverTerseOutput);
        Assert.False(settings.TokenSaverMinimalCode);
        Assert.False(settings.TokenSaverHeadroom);
        Assert.Equal("http://localhost:8787", settings.TokenSaverHeadroomUrl);

        settings.TokenSaverToolOutput = true;
        settings.TokenSaverHeadroom = true;
        var json = JsonSerializer.Serialize(settings, CamelCase);
        Assert.Contains("\"tokenSaverToolOutput\":true", json);
        Assert.Contains("\"tokenSaverHeadroomUrl\":\"http://localhost:8787\"", json);
        var back = JsonSerializer.Deserialize<HarnessSettings>(json, CamelCase)!;
        Assert.True(back.TokenSaverToolOutput);
        Assert.True(back.TokenSaverHeadroom);
        Assert.False(back.TokenSaverTerseOutput);

        var legacy = JsonSerializer.Deserialize<HarnessSettings>("""{"provider":"deepseek"}""", CamelCase)!;
        Assert.False(legacy.TokenSaverToolOutput);

        var nullUrl = JsonSerializer.Deserialize<HarnessSettings>("""{"tokenSaverHeadroomUrl":null}""", CamelCase)!;
        HarnessBootstrapper.MigrateLegacySettings(nullUrl);
        Assert.Equal("http://localhost:8787", nullUrl.TokenSaverHeadroomUrl);
    }

    // ---------- Headroom routing ----------

    [Theory]
    [InlineData(false, "openai", "https://api.deepseek.com", "https://api.deepseek.com", null)]
    [InlineData(true, "openai", "https://api.deepseek.com", "http://localhost:8787/v1", "https://api.deepseek.com")]
    [InlineData(true, "responses", "https://api.openai.com/v1", "http://localhost:8787/v1", "https://api.openai.com/v1")]
    [InlineData(true, "anthropic", "https://api.anthropic.com", "http://localhost:8787", "https://api.anthropic.com")]
    [InlineData(true, "openai", "http://localhost:8787/v1", "http://localhost:8787/v1", null)] // already proxied manually
    [InlineData(true, "openai", "", "", null)]
    public void HeadroomRoute_ReshapesOnlyWhenOn(bool on, string apiType, string baseUrl, string expectedEffective, string? expectedUpstream)
    {
        var settings = new HarnessSettings { TokenSaverHeadroom = on, TokenSaverHeadroomUrl = "http://localhost:8787/v1/" };
        var (effective, upstream) = HarnessBootstrapper.ResolveHeadroomRoute(settings, apiType, baseUrl);
        Assert.Equal(expectedEffective, effective);
        Assert.Equal(expectedUpstream, upstream);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static async Task CaptureRequest(LlmAdapter adapter, string provider)
    {
        // Each test's handler records the request via closure; this helper only drives the stream.
        try
        {
            await foreach (var _ in adapter.Stream(new GenerateOptions
            {
                Provider = provider,
                Model = "m",
                Messages = [new Message("m1", "user", [new TextBlock("hi")], MessageSource.User())],
            }, CancellationToken.None)) { }
        }
        catch (LlmException) { /* response shape is irrelevant; the request was captured at send */ }
    }

    [Fact]
    public async Task Adapter_WithUpstream_SendsToProxyWithHeader()
    {
        string? url = null, header = null;
        var adapter = new OpenAiCompatibleAdapter("gw", "http://localhost:8787/v1", "k", [],
            new HttpClient(new StubHandler(request =>
            {
                url = request.RequestUri!.ToString();
                header = request.Headers.TryGetValues("x-headroom-base-url", out var values) ? values.Single() : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("", Encoding.UTF8, "text/event-stream"),
                };
            })),
            upstreamBaseUrl: "https://api.deepseek.com");
        await CaptureRequest(adapter, "gw");
        Assert.Equal("http://localhost:8787/v1/chat/completions", url);
        Assert.Equal("https://api.deepseek.com", header);
    }

    [Fact]
    public async Task Adapter_UpstreamWithV1Suffix_HeaderDropsItSoTheProxyPathDoesNotDouble()
    {
        // The proxy forwards to <header> + the original request path (/v1/chat/completions),
        // so an upstream configured as https://api.openai.com/v1 must travel without its /v1.
        string? header = null;
        var adapter = new OpenAiCompatibleAdapter("gw", "http://localhost:8787/v1", "k", [],
            new HttpClient(new StubHandler(request =>
            {
                header = request.Headers.TryGetValues("x-headroom-base-url", out var values) ? values.Single() : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("", Encoding.UTF8, "text/event-stream"),
                };
            })),
            upstreamBaseUrl: "https://api.openai.com/v1");
        await CaptureRequest(adapter, "gw");
        Assert.Equal("https://api.openai.com", header);
    }

    [Fact]
    public async Task Adapter_WithoutUpstream_SendsDirectlyWithoutHeader()
    {
        string? url = null, header = "unset";
        var adapter = new OpenAiCompatibleAdapter("gw", "https://api.deepseek.com", "k", [],
            new HttpClient(new StubHandler(request =>
            {
                url = request.RequestUri!.ToString();
                header = request.Headers.TryGetValues("x-headroom-base-url", out var values) ? values.Single() : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("", Encoding.UTF8, "text/event-stream"),
                };
            })));
        await CaptureRequest(adapter, "gw");
        Assert.Equal("https://api.deepseek.com/chat/completions", url);
        Assert.Null(header);
    }

    [Fact]
    public async Task AnthropicAdapter_WithUpstream_SendsToProxyWithHeader()
    {
        string? url = null, header = null;
        var adapter = new AnthropicAdapter("az", "http://localhost:8787", "k", [],
            new HttpClient(new StubHandler(request =>
            {
                url = request.RequestUri!.ToString();
                header = request.Headers.TryGetValues("x-headroom-base-url", out var values) ? values.Single() : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream"),
                };
            })),
            upstreamBaseUrl: "https://api.anthropic.com");
        await CaptureRequest(adapter, "az");
        Assert.Equal("http://localhost:8787/v1/messages", url);
        Assert.Equal("https://api.anthropic.com", header);
    }
}
