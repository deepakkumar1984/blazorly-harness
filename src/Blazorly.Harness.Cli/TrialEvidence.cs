using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Core.Harnessing;
using Blazorly.Harness.Persistence;

namespace Blazorly.Harness.Cli;

/// <summary>Full captured event data, independently of the clipped human transcript.</summary>
public static class TrialEvidence
{
    private static readonly JsonSerializerOptions Json = new(HarnessArtifacts.Json) { WriteIndented = false };

    internal static async Task<HeadlessUsage> WritePersistedAsync(string directory, string sessionsRoot,
        string sessionId, string response, CancellationToken ct = default)
    {
        // The persistence reader drops a torn final line left by a killed process.
        var (header, events) = await new JsonlSessionPersistence(sessionsRoot).LoadAsync(sessionId, ct).ConfigureAwait(false);
        Write(directory, header, events, response);
        var usages = events.Where(e => e.Type == SessionEventTypes.AssistantMessage)
            .Select(e => SessionEventRead.AssistantMessageOf(e).Usage).Where(u => u is not null).ToList();
        return new(usages.Sum(u => u!.InputTokens), usages.Sum(u => u!.OutputTokens),
            usages.Sum(u => u!.CacheReadTokens ?? 0), usages.Sum(u => u!.CacheWriteTokens ?? 0));
    }

    public static void Write(string directory, SessionHeader header, IReadOnlyList<SessionEvent> events, string response)
    {
        Directory.CreateDirectory(directory);
        var log = new StringBuilder().AppendLine(JsonSerializer.Serialize(header, Json));
        foreach (var item in events)
            log.AppendLine(JsonSerializer.Serialize(item, Json));
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), log.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "answer.md"), response, new UTF8Encoding(false));
    }
}
