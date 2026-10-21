using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Context;

/// <summary>
/// Injects the current wall-clock time into the runtime-context snapshot (order 10, before
/// project instructions). Rendered at minute precision so the snapshot only churns when the
/// minute (or any other context section) changes.
/// </summary>
public sealed class TimeContextPlugin : HarnessPlugin
{
    public override string Name => "time";
    public override string[] Inject { get; } = [SystemPrompt.SystemPromptService.ServiceKey];

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        var prompt = ctx.Get<SystemPrompt.SystemPromptService>(SystemPrompt.SystemPromptService.ServiceKey);
        var registration = prompt.RegisterContext("time", 10, _ => Render());
        ctx.Effect(registration.Dispose);
        return Task.CompletedTask;
    }

    public static string Render(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.Now;
        return $"Current time: {t:yyyy-MM-dd (ddd) HH:mm} local ({t.Offset:hh\\:mm} UTC offset); "
            + $"{t.UtcDateTime:yyyy-MM-dd HH:mm} UTC";
    }
}
