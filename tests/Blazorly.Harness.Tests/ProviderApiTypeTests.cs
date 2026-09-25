using System.Net;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Llm.Adapters;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>Per-route API types: OpenAI-compatible by default, Anthropic Messages where selected.</summary>
public class ProviderApiTypeTests
{
    [Theory]
    [InlineData(null, "openai")]
    [InlineData("", "openai")]
    [InlineData("openai", "openai")]
    [InlineData("OPENAI", "openai")]
    [InlineData("responses", "responses")]
    [InlineData("Responses", "responses")]
    [InlineData("bogus", "openai")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("Anthropic", "anthropic")]
    public void NormalizeApiType_FallsBackToOpenAi(string? stored, string expected)
        => Assert.Equal(expected, ProviderCatalog.NormalizeApiType(stored));

    [Theory]
    [InlineData(null, "OpenAI-compatible")]
    [InlineData("openai", "OpenAI-compatible")]
    [InlineData("anthropic", "Anthropic")]
    [InlineData("responses", "OpenAI Responses")]
    public void ApiTypeLabel_ShortDisplayName(string? stored, string expected)
        => Assert.Equal(expected, ProviderCatalog.ApiTypeLabel(stored));

    [Fact]
    public void ResolveApiType_BuiltinDefaultsToCatalog()
    {
        var settings = new HarnessSettings();
        Assert.Equal("anthropic", HarnessBootstrapper.ResolveApiType(settings, "anthropic"));
        Assert.Equal("openai", HarnessBootstrapper.ResolveApiType(settings, "deepseek"));
        Assert.Equal("responses", HarnessBootstrapper.ResolveApiType(settings, "openai"));
        Assert.Equal("responses", HarnessBootstrapper.ResolveApiType(settings, "xai"));
        Assert.Equal("responses", HarnessBootstrapper.ResolveApiType(settings, "zai-coding"));
        Assert.Equal("openai", HarnessBootstrapper.ResolveApiType(settings, "ollama"));
        Assert.Equal("openai", HarnessBootstrapper.ResolveApiType(settings, "openai-compatible"));
    }

    [Fact]
    public void ResolveApiType_CustomGatewayCarriesItsOwn()
    {
        var settings = new HarnessSettings();
        settings.CustomProviders.Add(new CustomProviderConfig { Name = "gw-openai", BaseUrl = "https://gw.internal/v1" });
        settings.CustomProviders.Add(new CustomProviderConfig { Name = "gw-anthropic", BaseUrl = "https://gw.internal", ApiType = "anthropic" });
        settings.CustomProviders.Add(new CustomProviderConfig { Name = "gw-responses", BaseUrl = "https://gw.internal/v1", ApiType = "responses" });
        settings.CustomProviders.Add(new CustomProviderConfig { Name = "gw-garbage", BaseUrl = "https://gw.internal/v1", ApiType = "bogus" });

        Assert.Equal("openai", HarnessBootstrapper.ResolveApiType(settings, "gw-openai"));
        Assert.Equal("anthropic", HarnessBootstrapper.ResolveApiType(settings, "gw-anthropic"));
        Assert.Equal("responses", HarnessBootstrapper.ResolveApiType(settings, "gw-responses"));
        Assert.Equal("openai", HarnessBootstrapper.ResolveApiType(settings, "gw-garbage"));
    }

    [Fact]
    public void ResolveApiType_ProviderOverrideBeatsCatalogDefault()
    {
        var settings = new HarnessSettings();
        settings.ProviderApiTypes["deepseek"] = "anthropic";
        settings.ProviderApiTypes["anthropic"] = "openai";
        settings.ProviderApiTypes["xai"] = "bogus";

        Assert.Equal("anthropic", HarnessBootstrapper.ResolveApiType(settings, "deepseek"));
        Assert.Equal("openai", HarnessBootstrapper.ResolveApiType(settings, "anthropic"));
        Assert.Equal("responses", HarnessBootstrapper.ResolveApiType(settings, "xai"));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("zai-coding")]
    public void LegacyOpenAiSetting_KeepsMigratedProviderOnResponses(string provider)
    {
        var settings = new HarnessSettings();
        settings.ProviderApiTypes[provider] = "openai";
        Assert.Equal("responses", HarnessBootstrapper.ResolveApiType(settings, provider));
    }

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Theory]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public void CustomProviderConfig_ApiTypeRoundTripsThroughJson(string apiType)
    {
        var settings = new HarnessSettings();
        settings.CustomProviders.Add(new CustomProviderConfig { Name = "gw", BaseUrl = "https://gw.internal", ApiType = apiType });
        var json = JsonSerializer.Serialize(settings, CamelCase);
        Assert.Contains($"\"apiType\":\"{apiType}\"", json);

        var back = JsonSerializer.Deserialize<HarnessSettings>(json, CamelCase)!;
        Assert.Equal(apiType, back.CustomProviders.Single().ApiType);
        Assert.Equal(apiType, HarnessBootstrapper.ResolveApiType(back, "gw"));
    }

    [Fact]
    public void CustomProviderConfig_LegacyJsonWithoutApiTypeDefaultsToOpenAi()
    {
        var back = JsonSerializer.Deserialize<HarnessSettings>(
            """{"customProviders":[{"name":"gw","baseUrl":"https://gw.internal/v1","models":["a"]}]}""", CamelCase)!;
        Assert.Equal("openai", back.CustomProviders.Single().ApiType);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public void ProviderApiTypes_RoundTripThroughJson(string apiType)
    {
        var settings = new HarnessSettings();
        settings.ProviderApiTypes["deepseek"] = apiType;
        var back = JsonSerializer.Deserialize<HarnessSettings>(JsonSerializer.Serialize(settings, CamelCase), CamelCase)!;
        Assert.Equal(apiType, back.ProviderApiTypes["deepseek"]);
        Assert.Equal(apiType, HarnessBootstrapper.ResolveApiType(back, "deepseek"));
    }
}

/// <summary>Route registration and discovery follow the resolved API type.</summary>
[Collection("BlazorlyHome")]
public class ProviderApiTypeRouteTests : BootstrapperTestBase
{
    private static void WriteSettings(string home, object settings)
        => File.WriteAllText(Path.Combine(home, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    [Fact]
    public async Task ResponsesSelection_UsesResponsesForCustomAliasesAndSurvivesReload()
    {
        WriteSettings(Home, new
        {
            provider = "deepseek",
            baseUrl = "http://127.0.0.1:1/v1",
            providerApiTypes = new Dictionary<string, string> { ["deepseek"] = "responses" },
            customProviders = new[]
            {
                new { name = "gw-responses", baseUrl = "https://gw.test/v1", apiType = "responses", models = new[] { "deployment-alias" } },
            },
        });
        for (var reload = 0; reload < 2; reload++)
        {
            var boot = new HarnessBootstrapper();
            await boot.StartAsync(CancellationToken.None);
            try
            {
                Assert.IsType<ResponsesApiAdapter>(boot.Llm.GetAdapter("deepseek"));
                Assert.IsType<ResponsesApiAdapter>(boot.Llm.GetAdapter("gw-responses"));
                Assert.Equal("responses", boot.ApiTypeFor("gw-responses"));
                boot.SaveSettings();
            }
            finally { await boot.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task ApplyProviderSelection_AnthropicRoutesUseTheMessagesAdapter()
    {
        WriteSettings(Home, new
        {
            provider = "deepseek",
            model = "deepseek-v4-flash",
            baseUrl = "https://api.deepseek.com",
            providerApiTypes = new Dictionary<string, string> { ["deepseek"] = "anthropic" },
            customProviders = new[]
            {
                new { name = "gw-anthropic", baseUrl = "https://gw.internal", apiType = "anthropic", models = new[] { "claude-x" } },
                new { name = "gw-openai", baseUrl = "https://gw.internal/v1", apiType = "openai", models = new[] { "gpt-x" } },
            },
        });
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            // The agent's only provider-specific branch is adapter selection: these routes
            // now speak the Anthropic Messages API end to end (tools included).
            Assert.IsType<AnthropicAdapter>(boot.Llm.GetAdapter("deepseek"));
            Assert.IsType<AnthropicAdapter>(boot.Llm.GetAdapter("gw-anthropic"));
            Assert.IsType<OpenAiCompatibleAdapter>(boot.Llm.GetAdapter("gw-openai"));
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task ApplyProviderSelection_BuiltinAnthropicKeepsTheMessagesAdapter()
    {
        WriteSettings(Home, new
        {
            provider = "anthropic",
            model = "claude-sonnet-4-5",
            baseUrl = "https://api.anthropic.com",
            providerKeys = new Dictionary<string, string> { ["anthropic"] = "sk-test" },
        });
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            Assert.IsType<AnthropicAdapter>(boot.Llm.GetAdapter("anthropic"));
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    /// <summary>Records the models path and auth headers of the last request it served.</summary>
    private sealed class FakeAnthropicModelsServer : IDisposable
    {
        private HttpListener _listener = new();
        public string BaseUrl { get; private set; } = "";
        public string? SeenPath;
        public string? SeenApiKey;
        public string? SeenVersion;
        public string? SeenAuthorization;

        public FakeAnthropicModelsServer(params string[] modelIds)
        {
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    _listener.Start();
                    BaseUrl = $"http://127.0.0.1:{port}";
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
                        SeenPath = context.Request.Url?.AbsolutePath;
                        SeenApiKey = context.Request.Headers["x-api-key"];
                        SeenVersion = context.Request.Headers["anthropic-version"];
                        SeenAuthorization = context.Request.Headers["Authorization"];
                        var joined = string.Join(",", modelIds.Select(id => $$"""{"id":"{{id}}"}"""));
                        var body = Encoding.UTF8.GetBytes($$"""{"data":[{{joined}}]}""");
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

    [Fact]
    public async Task Discover_AnthropicCustomGateway_UsesVersionedPathAndKeyHeader()
    {
        using var server = new FakeAnthropicModelsServer("claude-x");
        WriteSettings(Home, new
        {
            provider = "deepseek",
            baseUrl = "http://127.0.0.1:1/v1",
            customProviders = new[]
            {
                new { name = "mygw", baseUrl = server.BaseUrl, apiKey = "sk-gw", apiType = "anthropic", models = Array.Empty<string>() },
            },
        });
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var (ids, error) = await boot.DiscoverModelsAsync("mygw");
            Assert.Null(error);
            Assert.Equal(["claude-x"], [.. ids]);
            Assert.Equal("/v1/models", server.SeenPath);
            Assert.Equal("sk-gw", server.SeenApiKey);
            Assert.Equal("2023-06-01", server.SeenVersion);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Preview_DoesNotPersistTheList()
    {
        using var server = new FakeAnthropicModelsServer("claude-preview");
        WriteSettings(Home, new
        {
            provider = "deepseek",
            baseUrl = "http://127.0.0.1:1/v1",
        });
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            // The Add-provider dialog previews a typed endpoint before anything is saved.
            var (models, error) = await boot.PreviewModelsAsync("deepseek", server.BaseUrl, "sk-x", apiType: "anthropic");
            Assert.Null(error);
            Assert.Equal(["claude-preview"], [.. models!.Select(m => m.Id)]);
            Assert.False(boot.Settings.DiscoveredModels.ContainsKey("deepseek"));
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Preview_ResponsesGateway_UsesModelsPathAndBearerAuth()
    {
        using var server = new FakeAnthropicModelsServer("deployment-alias");
        WriteSettings(Home, new { provider = "deepseek", baseUrl = "http://127.0.0.1:1/v1" });
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            var (models, error) = await boot.PreviewModelsAsync("gw", server.BaseUrl + "/v1/responses", "sk-gw", apiType: "responses");
            Assert.Null(error);
            Assert.Equal("deployment-alias", Assert.Single(models!).Id);
            Assert.Equal("/v1/models", server.SeenPath);
            Assert.Equal("Bearer sk-gw", server.SeenAuthorization);
            Assert.Null(server.SeenApiKey);
            Assert.Null(server.SeenVersion);
        }
        finally { await boot.DisposeAsync(); }
    }
}
