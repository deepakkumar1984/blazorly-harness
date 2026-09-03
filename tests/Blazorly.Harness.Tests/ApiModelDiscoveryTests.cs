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
                        var ids = string.Join(",", modelIds.Select(id => $$"""{"id":"{{id}}"}"""));
                        var body = Encoding.UTF8.GetBytes($$"""{"object":"list","data":[{{ids}}]}""");
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
