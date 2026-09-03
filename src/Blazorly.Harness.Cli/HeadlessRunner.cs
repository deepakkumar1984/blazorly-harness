using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Agent;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Web.Services;

namespace Blazorly.Harness.Cli;

public sealed record HeadlessOptions
{
    public string Job { get; init; } = "";
    /// <summary>Workspace root; defaults to the invoking directory (dsh launcher behavior).</summary>
    public string? WorkspacePath { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ResumeSessionId { get; init; }
    public int? TimeoutSeconds { get; init; }
    /// <summary>User-style stop of the running turn after this many milliseconds. Same
    /// mechanism as the timeout (agent.Cancel with the user cause) but labeled separately
    /// so eval tasks can distinguish "user pressed stop" from "watchdog fired".</summary>
    public int? CancelAfterMs { get; init; }
    public bool Json { get; init; }
    public bool Quiet { get; init; }
    /// <summary>ACP permission mode: auto (default) or ask (route tool calls to the client).</summary>
    public string? Permission { get; init; }
    public TextWriter Out { get; init; } = Console.Out;
}

public sealed record HeadlessUsage(long Input, long Output, long CacheRead, long CacheWrite);

public sealed record HeadlessResult
{
    public required int ExitCode { get; init; }
    public string? SessionId { get; init; }
    public string Response { get; init; } = "";
    public string Finish { get; init; } = "completed";
    public HeadlessUsage? Usage { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// One headless run: boot the full composition (same home/settings/persistence as the web
/// app), use the invoking directory as the workspace, run the job as a real turn-set, and
/// report. Exit contract: 0 completed/max-tokens, 2 turn error/blocked, 3 aborted, 1 failure.
/// </summary>
public static class HeadlessRunner
{
    public static async Task<HeadlessResult> RunAsync(HeadlessOptions options)
    {
        var bootstrapper = new HarnessBootstrapper();
        bootstrapper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            // Route overrides must land before the selection is applied.
            if (!string.IsNullOrWhiteSpace(options.Provider)) bootstrapper.Settings.Provider = options.Provider;
            if (!string.IsNullOrWhiteSpace(options.Model)) bootstrapper.Settings.Model = options.Model;
            bootstrapper.ApplyProviderSelection();
            bootstrapper.ApplyDefaultSelection();

            var root = options.WorkspacePath ?? Environment.CurrentDirectory;
            var workspace = bootstrapper.Workspaces.Ensure(root);
            var route = new AgentOptions(bootstrapper.Settings.Provider, bootstrapper.Settings.Model, null);

            var agent = options.ResumeSessionId is { } resume
                ? await bootstrapper.Loop.ResumeAsync(resume, route).ConfigureAwait(false)
                : bootstrapper.Loop.Create(new SessionMeta(Cwd: workspace.Root), route);

            if (!options.Quiet && !options.Json)
            {
                StreamTextTo(options.Out, agent);
            }

            // Deterministic completion: the driver may open its activity slightly after
            // Followup, so gate on the durable turn/end event before awaiting idle.
            var startSeq = agent.Session.Events.Count > 0 ? agent.Session.Events[^1].Seq : -1;
            var turnEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscription = agent.Session.Subscribe(@event =>
            {
                if (@event.Type == SessionEventTypes.TurnEnd && @event.Seq > startSeq) turnEnded.TrySetResult();
            });

            agent.Followup(Message.CreateUserText(options.Job));

            using var timeoutCts = options.TimeoutSeconds is { } seconds
                ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds))
                : null;
            using var cancelCts = options.CancelAfterMs is { } cancelMs && cancelMs > 0
                ? new CancellationTokenSource(TimeSpan.FromMilliseconds(cancelMs))
                : null;
            if (timeoutCts is not null || cancelCts is not null)
            {
                var racers = new List<Task> { turnEnded.Task };
                if (timeoutCts is not null)
                    racers.Add(Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));
                if (cancelCts is not null)
                    racers.Add(Task.Delay(Timeout.InfiniteTimeSpan, cancelCts.Token));
                var winner = await Task.WhenAny(racers).ConfigureAwait(false);
                if (winner != turnEnded.Task)
                {
                    agent.Cancel(AgentCancelCause.User());
                    await turnEnded.Task.ConfigureAwait(false); // the aborted turn/end is written durably
                }
            }
            else
            {
                await turnEnded.Task.ConfigureAwait(false);
            }
            subscription.Dispose();
            await agent.WhenIdleAsync().ConfigureAwait(false);

            await FlushAsync(bootstrapper).ConfigureAwait(false);

            var finish = FinishOf(agent);
            var response = LastAssistantText(agent);
            var usage = bootstrapper.Meter?.Measure(agent);

            if (options.Json)
            {
                await options.Out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    sessionId = agent.Session.Id,
                    response,
                    finish,
                    usage = usage is null ? null : new
                    {
                        input = usage.TotalInputTokens,
                        output = usage.TotalOutputTokens,
                        cacheRead = usage.TotalCacheReadTokens,
                        cacheWrite = usage.TotalCacheWriteTokens,
                    },
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).ConfigureAwait(false);
            }

            var exitCode = finish switch
            {
                "completed" or "max-tokens" => 0,
                "aborted" or "interrupted" => 3,
                _ => 2,
            };
            return new HeadlessResult
            {
                ExitCode = exitCode,
                SessionId = agent.Session.Id,
                Response = response,
                Finish = finish,
                Usage = usage is null ? null : new HeadlessUsage(usage.TotalInputTokens, usage.TotalOutputTokens, usage.TotalCacheReadTokens, usage.TotalCacheWriteTokens),
            };
        }
        catch (Exception ex)
        {
            return new HeadlessResult { ExitCode = 1, Error = ex.Message };
        }
        finally
        {
            await bootstrapper.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task FlushAsync(HarnessBootstrapper bootstrapper)
    {
        if (bootstrapper.Sessions.Persistence is not null) await bootstrapper.Sessions.Persistence.FlushAllAsync().ConfigureAwait(false);
    }

    private static void StreamTextTo(TextWriter output, Agent agent)
    {
        _ = agent.Session.Subscribe(@event =>
        {
            try
            {
                if (@event.Type != SessionEventTypes.AssistantChunk) return;
                var chunk = @event.Data.GetProperty("chunk");
                if (chunk.TryGetProperty("type", out var type) && type.GetString() == "text-delta"
                    && chunk.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    output.Write(text.GetString());
                }
                else if (chunk.TryGetProperty("type", out var finish) && finish.GetString() == "finish")
                {
                    output.WriteLine();
                }
            }
            catch
            {
                // streaming must never break the run
            }
        });
    }

    private static string FinishOf(Agent agent)
    {
        var reason = agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.TurnEnd)
            .Select(SessionEventRead.TurnEndReasonOf)
            .LastOrDefault();
        if (reason is null) return "error";
        return reason switch
        {
            TurnEndReason.Error => "error",
            TurnEndReason.Aborted => "aborted",
            TurnEndReason.MaxTokens => "max-tokens",
            TurnEndReason.Blocked => "blocked",
            TurnEndReason.Interrupted => "interrupted",
            _ => "completed",
        };
    }

    private static string LastAssistantText(Agent agent)
    {
        var message = agent.Session.Events
            .Where(e => e.Type == SessionEventTypes.AssistantMessage)
            .Select(SessionEventRead.AssistantMessageOf)
            .Where(a => a.Interrupted != true)
            .LastOrDefault(a => a.Message.Content.OfType<TextBlock>().Any());
        if (message is null) return string.Empty;
        return string.Join("", message.Message.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
    }

    public static async Task<int> ListSessionsAsync(HeadlessOptions options)
    {
        var bootstrapper = new HarnessBootstrapper();
        bootstrapper.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var headers = await bootstrapper.Sessions.ListPersistedAsync().ConfigureAwait(false);
            var filterRoot = options.WorkspacePath is null ? null : Path.GetFullPath(options.WorkspacePath);
            foreach (var header in headers.Where(h => filterRoot is null || string.Equals(h.Cwd, filterRoot, StringComparison.OrdinalIgnoreCase)).Take(50))
            {
                var when = DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                await options.Out.WriteLineAsync($"{header.Id}  {when}  {header.Cwd ?? "(no cwd)"}").ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception ex)
        {
            await options.Out.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            await bootstrapper.DisposeAsync().ConfigureAwait(false);
        }
    }
}
