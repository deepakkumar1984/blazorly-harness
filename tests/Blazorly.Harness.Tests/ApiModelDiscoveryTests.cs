using System.Net;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>API-driven model lists: GET /models is the source; the catalog only adds metadata.</summary>
[Collection("BlazorlyHome")]
public class ApiModelDiscoveryTests : BootstrapperTestBase
{
    /// <summary>A local stand-in for a provider's OpenAI-compatible /models endpoint.</summary>
    private sealed class FakeModelsServer : IDisposable
    {
        private HttpListener _listener = new();
        public string BaseUrl { get; private set; } = "";
        public string? SeenAuthorization;

        public FakeModelsServer(params string[] modelIds)
            : this(modelIds.Select(id => $$"""{"id":"{{id}}"}""").ToArray(), rawItems: true)
        {
        }

        /// <summary>Serves full per-model JSON objects (for endpoints that publish sizes).</summary>
        public static FakeModelsServer WithItems(params string[] itemJson) => new(itemJson, rawItems: true);

        private FakeModelsServer(string[] items, bool rawItems)
        {
            // Same port range as the other test fakes — retry on collision instead of failing.
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    _listener.Start();
                    BaseUrl = $"http://127.0.0.1:{port}/v1";
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                    _listener.Close();
                }
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync();
                        SeenAuthorization = context.Request.Headers["Authorization"];
                        var joined = string.Join(",", items);
                        var body = Encoding.UTF8.GetBytes($$"""{"object":"list","data":[{{joined}}]}""");
                        context.Response.ContentType = "application/json";
                        context.Response.OutputStream.Write(body);
                        context.Response.Close();
                    }
                }
                catch (ObjectDisposedException) { }
                catch (HttpListenerException) { }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }

    private static HarnessBootstrapper Boot(string provider, string baseUrl, string? apiKey = null)
    {
        File.WriteAllText(Path.Combine(
            Environment.GetEnvironmentVariable("BLAZORLY_HOME")!, "settings.json"),
            JsonSerializer.Serialize(new
            {
                provider,
                model = "deepseek-v4-flash",
                apiKey,
                baseUrl,
                workspaceRoot = Path.Combine(Path.GetTempPath(), "ws-" + Guid.NewGuid().ToString("N")[..8]),
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return new HarnessBootstrapper();
    }

    /// <summary>A server that accepts the request and never answers — the wedge that kept the
    /// Settings "Load models" button on "Loading…" forever (the streaming client has no timeout).</summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; } = "";

        public SilentServer()
        {
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    BaseUrl = $"http://127.0.0.1:{port}/v1";
                    break;
                }
                catch (HttpListenerException) when (attempt < 20) { }
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        // Take the context and deliberately never respond.
                        var context = await _listener.GetContextAsync();
                        _ = context;
                    }
                    catch { break; }
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }

    [Fact]
    public async Task Discover_StalledModelsEndpoint_ReturnsATimeoutError_InsteadOfHanging()
    {
        using var server = new SilentServer();
        var boot = Boot("deepseek", server.BaseUrl, "sk-test");
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ids, error) = await boot.DiscoverModelsAsync("deepseek", timeout: TimeSpan.FromSeconds(1));
            sw.Stop();
            Assert.Empty(ids);
            Assert.NotNull(error);
            Assert.Contains("timed out", error);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"returned in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_PersistsApiListAndReplacesSeeds()
    {
        using var server = new FakeModelsServer("brand-new-model", "deepseek-v4-flash");
        var boot = Boot("deepseek", server.BaseUrl, "sk-test");
        await boot.StartAsync(CancellationToken.None);
        try
        {
            // Before discovery the catalog seeds serve as the fallback list.
            Assert.Contains(boot.RuntimeModels("deepseek"), m => m.Id == "deepseek-v4-pro");

            var (ids, error) = await boot.DiscoverModelsAsync("deepseek");
            Assert.Null(error);
            Assert.Equal(["brand-new-model", "deepseek-v4-flash"], [.. ids]);

            var runtime = boot.RuntimeModels("deepseek");
            Assert.Equal(2, runtime.Count); // seeds are replaced by the API list
            Assert.Equal("brand-new-model", runtime[0].Id);
            // Catalog metadata sticks to known ids (windows, efforts) while API order wins.
            var flash = runtime.Single(m => m.Id == "deepseek-v4-flash");
            Assert.Equal(1_000_000, flash.ContextWindowTokens);
            Assert.NotNull(flash.ReasoningEfforts);

            // The key was sent as a bearer token to the provider.
            Assert.Equal("Bearer sk-test", server.SeenAuthorization);

            // Persistence: the discovered list survives a restart (cold start reads settings.json).
            var home = Environment.GetEnvironmentVariable("BLAZORLY_HOME")!;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "settings.json")));
            Assert.True(doc.RootElement.TryGetProperty("discoveredModels", out var discovered));
            Assert.Equal(JsonValueKind.Object, discovered.ValueKind);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_ColdStartKeepsApiListWithoutSeeds()
    {
        using var server = new FakeModelsServer("only-from-api");
        var boot = Boot("deepseek", server.BaseUrl, "sk-test");
        await boot.StartAsync(CancellationToken.None);
        var (ids, error) = await boot.DiscoverModelsAsync("deepseek");
        await boot.DisposeAsync();
        Assert.Null(error);
        Assert.Equal(["only-from-api"], [.. ids]);

        var second = new HarnessBootstrapper();
        await second.StartAsync(CancellationToken.None);
        try
        {
            var runtime = second.RuntimeModels("deepseek");
            Assert.Equal(["only-from-api"], [.. runtime.Select(m => m.Id)]);
            Assert.Equal("deepseek", runtime[0].Provider);
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_ApiSizesFlowToUnknownModelsAndPersist()
    {
        using var server = FakeModelsServer.WithItems(
            """{"id":"mystery-1m","context_length":1048576,"max_completion_tokens":65536}""",
            """{"id":"plain-model"}""");
        var boot = Boot("deepseek", server.BaseUrl, "sk-test");
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var (ids, error) = await boot.DiscoverModelsAsync("deepseek");
            Assert.Null(error);
            Assert.Equal(["mystery-1m", "plain-model"], [.. ids]);
            var runtime = boot.RuntimeModels("deepseek");
            var mystery = runtime.Single(m => m.Id == "mystery-1m");
            Assert.Equal(1_048_576, mystery.ContextWindowTokens);
            Assert.Equal(65_536, mystery.MaxOutputTokens);
            Assert.Null(runtime.Single(m => m.Id == "plain-model").ContextWindowTokens);
            Assert.Equal("high", mystery.EffectiveDefaultEffort);
        }
        finally
        {
            await boot.DisposeAsync();
        }

        var second = new HarnessBootstrapper();
        await second.StartAsync(CancellationToken.None);
        try
        {
            var mystery = second.RuntimeModels("deepseek").Single(m => m.Id == "mystery-1m");
            Assert.Equal(1_048_576, mystery.ContextWindowTokens);
            Assert.Equal(65_536, mystery.MaxOutputTokens);
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_TypedKeyWorksBeforeSave()
    {
        using var server = new FakeModelsServer("m1");
        var boot = Boot("deepseek", "http://127.0.0.1:1/v1", apiKey: null); // wrong stored base, no stored key
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var (ids, error) = await boot.DiscoverModelsAsync("deepseek", server.BaseUrl, "sk-just-typed");
            Assert.Null(error);
            Assert.Equal(["m1"], [.. ids]);
            Assert.Equal("Bearer sk-just-typed", server.SeenAuthorization);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_GuardAndTransportErrorsReturnMessages()
    {
        var boot = Boot("deepseek", "http://127.0.0.1:1/v1");
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var (_, guardError) = await boot.DiscoverModelsAsync(" ");
            Assert.NotNull(guardError);

            var (_, transportError) = await boot.DiscoverModelsAsync("deepseek", "http://127.0.0.1:1/v1", "sk-x");
            Assert.NotNull(transportError);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_UpdatesCustomProviderModels()
    {
        using var server = new FakeModelsServer("gw-model-a", "gw-model-b");
        var home = Environment.GetEnvironmentVariable("BLAZORLY_HOME")!;
        File.WriteAllText(Path.Combine(home, "settings.json"), JsonSerializer.Serialize(new
        {
            provider = "deepseek",
            baseUrl = "http://127.0.0.1:1/v1",
            customProviders = new[]
            {
                new { name = "mygw", baseUrl = server.BaseUrl, models = new[] { "old-model" } },
            },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var (ids, error) = await boot.DiscoverModelsAsync("mygw");
            Assert.Null(error);
            Assert.Equal(["gw-model-a", "gw-model-b"], [.. ids]);
            var custom = boot.Settings.CustomProviders.Single(c => c.Name == "mygw");
            Assert.Contains("old-model", custom.Models); // existing entries are kept
            Assert.Contains("gw-model-a", custom.Models);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }
}
