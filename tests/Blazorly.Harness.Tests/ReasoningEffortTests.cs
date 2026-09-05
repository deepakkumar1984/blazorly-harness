using System.Text.Json;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>Reasoning-effort wire contract (dsh llm-deepseek serialize.ts parity).</summary>
public class ReasoningEffortTests
{
    private static GenerateOptions Options(string provider, string? effort, string? purpose = null) => new()
    {
        Provider = provider,
        Model = "test-model",
        Messages = [Message.CreateUserText("hi")],
        ReasoningEffort = effort,
        Purpose = purpose,
    };

    private static JsonElement Body(GenerateOptions options)
    {
        var adapter = new OpenAiCompatibleAdapter("test", "http://localhost", "k", [], new HttpClient());
        return JsonSerializer.SerializeToElement(adapter.BuildWireBody(options));
    }

    [Fact]
    public void DeepSeek_Off_DisablesThinkingWithoutEffort()
    {
        var body = Body(Options("deepseek", "off"));
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("high")]
    [InlineData("max")]
    public void DeepSeek_NonOff_EnablesThinkingWithEffort(string effort)
    {
        var body = Body(Options("deepseek", effort));
        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(effort, body.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void DeepSeek_NoEffort_SendsNeitherField()
    {
        var body = Body(Options("deepseek", null));
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void DeepSeek_SessionTitle_AlwaysThinkingDisabled()
    {
        var body = Body(Options("deepseek", "max", purpose: "session-title"));
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void GenericRoute_PassesEffortThrough_WithoutThinkingField()
    {
        var body = Body(Options("openai-compatible", "xhigh"));
        Assert.Equal("xhigh", body.GetProperty("reasoning_effort").GetString());
        Assert.False(body.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void GenericRoute_NoEffort_SendsNothing()
    {
        var body = Body(Options("openai-compatible", null));
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void ResponsesApi_WrapsEffortInReasoningObject()
    {
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "k", [], new HttpClient());
        var body = JsonSerializer.SerializeToElement(adapter.BuildWireBody(Options("xai", "high")));
        Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void ResponsesApi_OffMapsToLow()
    {
        var adapter = new ResponsesApiAdapter("xai", "https://api.x.ai/v1", "k", [], new HttpClient());
        var body = JsonSerializer.SerializeToElement(adapter.BuildWireBody(Options("xai", "off")));
        Assert.Equal("low", body.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void Catalog_OffersEffortsForReasoningModels()
    {
        var pro = Web.Services.ProviderCatalog.For("deepseek", "https://api.deepseek.com").Single(m => m.Id == "deepseek-v4-pro");
        Assert.Equal(new[] { "off", "low", "high", "max" }, pro.ReasoningEfforts);
        Assert.Equal("high", pro.DefaultEffort);
        var plain = Web.Services.ProviderCatalog.For("openai", "").Single(m => m.Id == "gpt-4.1");
        Assert.True(plain.ReasoningEfforts is null || plain.ReasoningEfforts.Length == 0);
    }

    [Fact]
    public void AgentOptions_EffortOverridableAndResettable()
    {
        var options = new Blazorly.Harness.Core.Agent.AgentOptions(Provider: "deepseek", Model: "deepseek-v4-pro");
        var overridden = options.OverriddenBy(new(ReasoningEffort: "max"));
        Assert.Equal("max", overridden.ReasoningEffort);
        Assert.Equal("deepseek", overridden.Provider);
        var reset = overridden.OverriddenBy(new(ReasoningEffort: null));
        Assert.Equal("max", reset.ReasoningEffort); // null = keep
    }
}
