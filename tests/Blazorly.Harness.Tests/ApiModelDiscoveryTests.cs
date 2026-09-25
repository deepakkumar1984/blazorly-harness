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
            // Before discovery, only the explicitly saved model is available.
            Assert.Equal("deepseek-v4-flash", Assert.Single(boot.RuntimeModels("deepseek")).Id);

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
            Assert.Null(mystery.EffectiveDefaultEffort);
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

    [Theory]
    [InlineData(" ")]
    [InlineData("gw.internal/v1")]
    [InlineData("just-a-hostname")]
    [InlineData("ftp://gw.internal/v1")]
    [InlineData("not a url at all %%%")]
    public async Task Discover_BadBaseUrl_ReturnsErrorInsteadOfThrowing(string typedBaseUrl)
    {
        var boot = Boot("deepseek", "http://127.0.0.1:1/v1", "sk-test");
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var (ids, error) = await boot.DiscoverModelsAsync("deepseek", typedBaseUrl, "[REDACTED]");
            Assert.Empty(ids);
            Assert.NotNull(error);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_PreCancelledToken_ReturnsEmptyWithoutError()
    {
        using var server = new SilentServer();
        var boot = Boot("deepseek", server.BaseUrl, "sk-test");
        await boot.StartAsync(CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ids, error) = await boot.DiscoverModelsAsync("deepseek", ct: cts.Token);
            sw.Stop();
            Assert.Empty(ids);
            Assert.Null(error); // caller-cancelled: silent, not a timeout error
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"returned in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_MidFlightCancel_AbortsAHungEndpoint()
    {
        using var server = new SilentServer();
        var boot = Boot("deepseek", server.BaseUrl, "sk-test");
        await boot.StartAsync(CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource();
            var pending = boot.DiscoverModelsAsync("deepseek", timeout: TimeSpan.FromMinutes(5), ct: cts.Token);
            await Task.Delay(200);
            cts.Cancel(); // the UI Cancel button path
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (ids, error) = await pending;
            sw.Stop();
            Assert.Empty(ids);
            Assert.Null(error);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"cancel took {sw.Elapsed.TotalSeconds:0.0}s");
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Discover_CustomProviderBlankUrl_ReturnsError()
    {
        var home = Environment.GetEnvironmentVariable("BLAZORLY_HOME")!;
        File.WriteAllText(Path.Combine(home, "settings.json"), JsonSerializer.Serialize(new
        {
            provider = "deepseek",
            baseUrl = "http://127.0.0.1:1/v1",
            customProviders = new[]
            {
                new { name = "mygw", baseUrl = " ", models = Array.Empty<string>() },
            },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var (ids, error) = await boot.DiscoverModelsAsync("mygw");
            Assert.Empty(ids);
            Assert.NotNull(error);
            Assert.Contains("base URL", error);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task RuntimeModels_CustomProvidersListTheirModels()
    {
        // The session picker reads RuntimeModels and hides empty groups: customs must resolve.
        var home = Environment.GetEnvironmentVariable("BLAZORLY_HOME")!;
        File.WriteAllText(Path.Combine(home, "settings.json"), JsonSerializer.Serialize(new
        {
            provider = "deepseek",
            baseUrl = "http://127.0.0.1:1/v1",
            customProviders = new[]
            {
                new { name = "mygw", baseUrl = "http://127.0.0.1:1/v1", models = new[] { "gw-a", "gw-b" } },
                new { name = "emptygw", baseUrl = "http://127.0.0.1:1/v1", models = Array.Empty<string>() },
            },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var listed = boot.RuntimeModels("mygw");
            Assert.Equal(["gw-a", "gw-b"], [.. listed.Select(m => m.Id)]);
            Assert.All(listed, m => Assert.Equal("mygw", m.Provider));
            // Same list the adapter was registered with: picker and route agree.
            Assert.Equal(["gw-a", "gw-b"], [.. boot.Llm.ListModels("mygw").Select(m => m.Id)]);
            var empty = boot.RuntimeModels("emptygw");
            Assert.Empty(empty);
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
            Assert.Equal(["gw-model-a", "gw-model-b"], custom.Models); // stale ids are removed
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discover_EmptyListClearsStaleModels_IncludingAfterRestart(bool custom)
    {
        using var server = new FakeModelsServer();
        var id = custom ? "empty-gateway" : "deepseek";
        await using (var boot = new HarnessBootstrapper())
        {
            if (custom)
                boot.Settings.CustomProviders.Add(new CustomProviderConfig
                {
                    Name = id, BaseUrl = server.BaseUrl, Models = ["stale-model"],
                });
            boot.Settings.Provider = id;
            boot.Settings.Model = "stale-model";
            boot.Settings.BaseUrl = server.BaseUrl;
            await boot.StartAsync(default);

            var (models, error) = await boot.DiscoverModelsAsync(id);
            Assert.Null(error);
            Assert.Empty(models);
            Assert.Empty(boot.RuntimeModels(id));
            Assert.Empty(boot.Llm.ListModels(id));
            Assert.Empty(boot.Settings.Model);
            Assert.Empty(boot.Loop.DefaultSelection.Model);
        }
        await using var restarted = new HarnessBootstrapper();
        await restarted.StartAsync(default);
        Assert.Empty(restarted.RuntimeModels(id));
        Assert.Empty(restarted.Settings.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discover_PreservesApiLimitsForKnownAndCustomModels_AcrossRestart(bool custom)
    {
        using var server = FakeModelsServer.WithItems(
            """{"id":"deepseek-v4-flash","context_length":16384,"max_completion_tokens":1024}""");
        var id = custom ? "sized-gateway" : "deepseek";
        await using (var boot = new HarnessBootstrapper())
        {
            boot.Settings.Provider = id;
            boot.Settings.BaseUrl = server.BaseUrl;
            if (custom)
                boot.Settings.CustomProviders.Add(new CustomProviderConfig { Name = id, BaseUrl = server.BaseUrl });
            await boot.StartAsync(default);
            var (_, error) = await boot.DiscoverModelsAsync(id);
            Assert.Null(error);
            var model = Assert.Single(boot.RuntimeModels(id));
            Assert.Equal(16384, model.ContextWindowTokens);
            Assert.Equal(1024, model.MaxOutputTokens);
            Assert.Equal(1024, HarnessBootstrapper.ResolveMaxOutputTokens(boot.Settings, boot.RuntimeModels(id), model.Id));
        }

        await using var restarted = new HarnessBootstrapper();
        await restarted.StartAsync(default);
        var persisted = Assert.Single(restarted.RuntimeModels(id));
        Assert.Equal(16384, persisted.ContextWindowTokens);
        Assert.Equal(1024, persisted.MaxOutputTokens);
    }

    [Fact]
    public async Task Discover_CustomSavedKeyHasTheSamePrecedenceAsGeneration()
    {
        const string envName = "BLAZORLY_TEST_MODEL_DISCOVERY_API_KEY";
        var previous = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "test-env-key");
            using var server = new FakeModelsServer("actual-model");
            await using var boot = new HarnessBootstrapper();
            boot.Settings.CustomProviders.Add(new CustomProviderConfig
            {
                Name = "authenticated-gateway", BaseUrl = server.BaseUrl,
                ApiKey = "test-saved-key", ApiKeyEnv = envName,
            });
            await boot.StartAsync(default);
            var (_, error) = await boot.DiscoverModelsAsync("authenticated-gateway");
            Assert.Null(error);
            Assert.Equal("Bearer test-saved-key", server.SeenAuthorization);
            Assert.Equal("test-saved-key", boot.Settings.ApiKeyFor("authenticated-gateway"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }
}
