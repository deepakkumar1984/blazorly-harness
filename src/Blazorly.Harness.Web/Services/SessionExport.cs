using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Core.Sessions;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Web.Services;

/// <summary>
/// Session export: a portable ZIP of one session (machine-readable log + human transcript).
/// The log mirrors the JSONL envelope shape (header line, then one event per line); it is an
/// interchange artifact, not a backup — reimport reads it through the normal JSON envelope.
/// </summary>
public static class SessionExport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] BuildZip(SessionHeader header, IReadOnlyList<SessionEvent> events)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var log = new StringBuilder();
            log.AppendLine(JsonSerializer.Serialize(header, Json));
            foreach (var e in events)
            {
                log.AppendLine(JsonSerializer.Serialize(new
                {
                    e.Type,
                    e.Seq,
                    e.Time,
                    data = e.Data,
                    e.SourceEventSeqs,
                }, Json));
            }
            WriteEntry(archive, "session.jsonl", log.ToString());
            WriteEntry(archive, "transcript.md", RenderTranscript(header, events));
        }
        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    internal static string RenderTranscript(SessionHeader header, IReadOnlyList<SessionEvent> events)
    {
        var builder = new StringBuilder();
        builder.Append("# Session ").AppendLine(header.Id);
        builder.Append("Workspace: ").AppendLine(header.Cwd ?? "(none)");
        builder.AppendLine();
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case SessionEventTypes.TurnStart when TurnOf(e) is { } turn:
                    builder.Append("## Turn ").AppendLine(turn.ToString());
                    break;
                case SessionEventTypes.UserMessage:
                    AppendMessage(builder, "User", TryFlattenUser(e));
                    break;
                case SessionEventTypes.AssistantMessage:
                    AppendMessage(builder, "Assistant", TryFlattenAssistant(e));
                    break;
                case SessionEventTypes.ToolCall when ToolCallOf(e) is { } call:
                    builder.Append("**`").Append(call.Name).Append("`** ");
                    AppendTruncated(builder, call.Arguments);
                    builder.AppendLine().AppendLine();
                    break;
                case SessionEventTypes.ToolResult when ToolResultText(e) is { } text:
                    builder.Append("> ").AppendLine("Result:");
                    AppendQuoted(builder, text);
                    break;
                case SessionEventTypes.TurnEnd:
                    builder.Append("*Turn ended: ").Append(ReasonOf(e)).AppendLine("*").AppendLine();
                    break;
            }
        }
        return builder.ToString();
    }

    private static void AppendMessage(StringBuilder builder, string role, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        builder.Append("**").Append(role).AppendLine(":**");
        builder.AppendLine(text.Trim());
        builder.AppendLine();
    }

    private static void AppendTruncated(StringBuilder builder, string text)
        => builder.Append(text.Length > 500 ? text[..500] + "…" : text);

    private static void AppendQuoted(StringBuilder builder, string text)
    {
        var snippet = text.Length > 500 ? text[..500] + "…" : text;
        foreach (var line in snippet.Split('\n'))
            builder.Append("> ").AppendLine(line);
        builder.AppendLine();
    }

    private static int? TurnOf(SessionEvent e)
        => e.Data.ValueKind == JsonValueKind.Object
            && e.Data.TryGetProperty("turn", out var t)
            && t.ValueKind == JsonValueKind.Number
            && t.TryGetInt32(out var turn) ? turn : null;

    private static string? TryFlattenUser(SessionEvent e)
    {
        try { return SessionEventRead.MessageOf(e).FlattenText(); }
        catch { return null; }
    }

    private static string? TryFlattenAssistant(SessionEvent e)
    {
        try { return SessionEventRead.AssistantMessageOf(e).Message.FlattenText(); }
        catch { return null; }
    }

    private static SessionPayloads.ToolCall? ToolCallOf(SessionEvent e)
    {
        try { return SessionEventRead.ToolCallOf(e); }
        catch { return null; }
    }

    private static string? ToolResultText(SessionEvent e)
    {
        try { return SessionEventRead.ToolResultOf(e).Message.FlattenText(); }
        catch { return null; }
    }

    private static string ReasonOf(SessionEvent e)
    {
        try { return SessionEventRead.TurnEndReasonOf(e) switch
        {
            TurnEndReason.Completed => "completed",
            TurnEndReason.Error error => $"error: {error.Message}",
            TurnEndReason.Interrupted => "interrupted",
            TurnEndReason.Aborted aborted => $"aborted ({aborted.Cause})",
            TurnEndReason.MaxTokens => "max-tokens",
            TurnEndReason.Blocked => "blocked",
            _ => "unknown",
        }; }
        catch { return "unknown"; }
    }
}
