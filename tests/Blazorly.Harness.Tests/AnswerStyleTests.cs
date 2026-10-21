using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.SystemPrompt;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Answer style (Settings → Answers): plain-first writing for non-technical readers, chosen
/// globally in settings.json and delivered to the model as its own prompt section so a
/// workspace's custom instructions cannot drop it.
/// </summary>
public class AnswerStyleTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("SIMPLE", "simple")]
    [InlineData(" technical ", "technical")]
    [InlineData("layered", "layered")]
    [InlineData("", "layered")]
    [InlineData(null, "layered")]
    [InlineData("fancy", "layered")]
    public void Normalize_FallsBackToLayered(string? input, string expected)
        => Assert.Equal(expected, AnswerStyle.Normalize(input));

    [Fact]
    public void SectionText_LayeredIsPlainFirstWithDetailsBelow()
    {
        var text = AnswerStyle.SectionText(AnswerStyle.Layered);
        Assert.Contains("**What happened**", text);
        Assert.Contains("**Technical details**", text);
        Assert.Contains("20 words or fewer", text);
        Assert.Contains("```mermaid", text);
    }

    [Fact]
    public void SectionText_SimpleForbidsTheTechnicalSection()
    {
        var text = AnswerStyle.SectionText(AnswerStyle.Simple);
        Assert.Contains("**What happened**", text);
        Assert.Contains("Do not add a technical-details section", text);
    }

    [Fact]
    public void SectionText_TechnicalIsTheEngineerReport()
    {
        var text = AnswerStyle.SectionText(AnswerStyle.Technical);
        Assert.Contains("Verification", text);
        Assert.Contains("Risks & Limitations", text);
        Assert.DoesNotContain("**What happened**", text);
    }

    [Fact]
    public void SectionText_EveryStyleExemptsMachineReadableFormats()
    {
        // Structured subagent results (schema-validated JSON for a parent agent) must never
        // be wrapped in answer prose by any style.
        foreach (var style in AnswerStyle.All)
            Assert.Contains("exact machine-readable format", AnswerStyle.SectionText(style));
    }

    [Fact]
    public void SectionText_UnknownStyleUsesLayered()
        => Assert.Equal(AnswerStyle.SectionText(AnswerStyle.Layered), AnswerStyle.SectionText("bogus"));

    [Fact]
    public void DefaultInstructions_UseTheLayeredFormat()
    {
        Assert.Contains("**What happened**", HarnessDefaultInstructions.Body);
        Assert.Contains("**Technical details**", HarnessDefaultInstructions.Body);
        Assert.DoesNotContain("For implementation tasks:", HarnessDefaultInstructions.Body);
    }

    [Fact]
    public void Settings_DefaultIsLayered_AndRoundTripsAsCamelCase()
    {
        Assert.Equal(AnswerStyle.Layered, new HarnessSettings().AnswerStyle);

        var settings = new HarnessSettings { AnswerStyle = AnswerStyle.Technical };
        var json = JsonSerializer.Serialize(settings, CamelCase);
        Assert.Contains("\"answerStyle\":\"technical\"", json);
        Assert.Equal(AnswerStyle.Technical, JsonSerializer.Deserialize<HarnessSettings>(json, CamelCase)!.AnswerStyle);
    }

    [Fact]
    public void Settings_LegacyOrInvalidStyleLoadsAsLayered()
    {
        // A settings.json written before the setting existed has no answerStyle at all.
        var legacy = JsonSerializer.Deserialize<HarnessSettings>("""{"provider":"deepseek"}""", CamelCase)!;
        Assert.Equal(AnswerStyle.Layered, legacy.AnswerStyle);

        var invalid = JsonSerializer.Deserialize<HarnessSettings>("""{"answerStyle":"shouty"}""", CamelCase)!;
        HarnessBootstrapper.MigrateLegacySettings(invalid);
        Assert.Equal(AnswerStyle.Layered, invalid.AnswerStyle);

        var nullStyle = JsonSerializer.Deserialize<HarnessSettings>("""{"answerStyle":null}""", CamelCase)!;
        HarnessBootstrapper.MigrateLegacySettings(nullStyle);
        Assert.Equal(AnswerStyle.Layered, nullStyle.AnswerStyle);
    }

    [Fact]
    public void PromptAssembly_CarriesTheStyleAsItsOwnSection()
    {
        var harness = TestHarness.Create();
        var agent = harness.CreateAgent();
        var assembly = harness.Prompt.Assemble(agent, agent.Session.Header.Cwd);

        var section = Assert.Single(assembly.Sections, s => s.Name == "harness:answer-style");
        Assert.Contains("**What happened**", section.Text); // no provider wired: layered default
        // The section must come after the identity/instruction body so it reads as the last
        // word on format (and so a custom instruction body cannot bury it).
        var identity = Assert.Single(assembly.Sections, s => s.Name == "harness:identity");
        Assert.True(section.Order > identity.Order);
    }

    [Fact]
    public void PromptAssembly_ProviderSwitchesStyle_AndSurvivesCustomInstructions()
    {
        var harness = TestHarness.Create();
        harness.Loop.IdentityOverride = _ => "Custom workspace identity.";
        harness.Loop.AnswerStyleProvider = _ => AnswerStyle.Technical;
        var agent = harness.CreateAgent();
        var text = SystemPromptService.RenderPrompt(harness.Prompt.Assemble(agent, agent.Session.Header.Cwd));

        Assert.Contains("Custom workspace identity.", text);
        Assert.Contains("answer style: technical", text);
        Assert.True(text.IndexOf("Custom workspace identity.", StringComparison.Ordinal)
            < text.IndexOf("answer style: technical", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptAssembly_InvalidProviderValueFallsBackToLayered()
    {
        var harness = TestHarness.Create();
        harness.Loop.AnswerStyleProvider = _ => "not-a-style";
        var agent = harness.CreateAgent();
        var section = Assert.Single(
            harness.Prompt.Assemble(agent, agent.Session.Header.Cwd).Sections,
            s => s.Name == "harness:answer-style");
        Assert.Contains("plain first, details below", section.Text);
    }

    [Fact]
    public void PromptAssembly_EmptyProviderValueOmitsTheSection()
    {
        // The explicit off switch: hosts/tests that pin a minimal prompt get no style section.
        var harness = TestHarness.Create();
        harness.Loop.AnswerStyleProvider = _ => "";
        var agent = harness.CreateAgent();
        Assert.DoesNotContain(
            harness.Prompt.Assemble(agent, agent.Session.Header.Cwd).Sections,
            s => s.Name == "harness:answer-style");
    }

    [Fact]
    public void Markdown_MermaidFenceKeepsItsClassForTheChatRenderer()
    {
        // app.js (blazorly.renderMermaid) finds diagrams by this exact markup, which the
        // Markdig diagrams extension emits for mermaid fences; if Markdig ever stops
        // emitting it, diagrams would silently go back to raw text.
        var html = new MarkdownService().ToHtml("```mermaid\nflowchart TD\n  A --> B\n```");
        Assert.Contains("<pre class=\"mermaid\">", html);
    }
}
