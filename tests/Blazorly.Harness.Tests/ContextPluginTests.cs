using Blazorly.Harness.Kernel;
using Blazorly.Harness.Tools;
using Blazorly.Harness.Web.Services;
using Xunit;

namespace Blazorly.Harness.Tests;

public class ContextPluginTests
{
    [Fact]
    public void TimeRender_IncludesLocalAndUtc_AtMinutePrecision()
    {
        var now = new DateTimeOffset(2026, 9, 3, 14, 5, 6, TimeSpan.FromHours(2));
        var text = Core.Context.TimeContextPlugin.Render(now);
        Assert.Contains("2026-09-03 (Thu) 14:05", text); // minute precision, seconds dropped
        Assert.Contains("2026-09-03 12:05", text);       // the same instant in UTC
        Assert.DoesNotContain("14:05:06", text);     // no seconds → snapshot churn ≤ 1/min

        // Same minute renders identically (the snapshot diff stays quiet between steps).
        Assert.Equal(text, Core.Context.TimeContextPlugin.Render(now.AddSeconds(30)));
    }

    [Fact]
    public void TmuxListing_FailsSoft_WhenTmuxIsAbsentOrIdle()
    {
        // On this machine either state is acceptable: no binary, or no server → "".
        // A pane listing, if any, is one line per pane under a "tmux sessions:" header.
        var listing = TmuxContextPlugin.Listing();
        Assert.True(listing.Length == 0 || listing.StartsWith("tmux sessions:\n"), listing);
    }

    [Fact]
    public async Task TmuxSnapshot_CachesAcrossCalls()
    {
        var plugin = new TmuxContextPlugin(TimeSpan.FromMinutes(5));
        var first = plugin.Snapshot();
        var second = plugin.Snapshot();
        Assert.Equal(first, second); // same cached value, no second shell-out
    }
}

[Collection("BlazorlyHome")]
public class ContextPluginCompositionTests : BootstrapperTestBase
{
    [Fact]
    public async Task Boot_IncludesTimeAndTmuxContext_AndRegistersTheSection()
    {
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            Assert.Contains("time", boot.AppliedPlugins);
            Assert.Contains("tmux", boot.AppliedPlugins);
            var agent = boot.Loop.Create(new Core.Sessions.SessionMeta(Cwd: _workspace), new Core.Agent.AgentOptions("deepseek", "deepseek-v4-flash", null));
            var assembly = boot.Context.Get<Core.SystemPrompt.SystemPromptService>("systemPrompt").Assemble(agent, _workspace);
            Assert.Contains(assembly.ContextSections, s => s.Name == "time" && s.Text.Contains("Current time:"));
            // tmux contributes a section only when a server is running; presence is enough here.
            var applied = boot.AppliedPlugins.ToList();
            Assert.True(applied.IndexOf("systemPrompt") < applied.IndexOf("time"));
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    [Fact]
    public async Task Boot_TimeAndTmux_AreDisablable()
    {
        File.WriteAllText(Path.Combine(Home, "settings.json"),
            """{"provider":"deepseek","model":"deepseek-v4-flash","disabledPlugins":["time","tmux"]}""");
        var boot = new HarnessBootstrapper();
        await boot.StartAsync(CancellationToken.None);
        try
        {
            Assert.DoesNotContain("time", boot.AppliedPlugins);
            Assert.DoesNotContain("tmux", boot.AppliedPlugins);
        }
        finally
        {
            await boot.DisposeAsync();
        }
    }

    private static string _workspace = Directory.CreateTempSubdirectory("blazorly-ctx-").FullName;
}
