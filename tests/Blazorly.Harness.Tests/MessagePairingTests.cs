using System.Text.Json;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Llm.Adapters;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// Providers reject an unpaired tool message with a hard 400 rather than skipping it:
/// "Messages with role 'tool' must be a response to a preceding message with 'tool_calls'".
/// Compaction produces exactly that when its keep-boundary lands between an assistant tool_calls
/// message and the results, so the derivation drops what cannot be sent.
/// </summary>
public class MessagePairingTests
{
    private static Message Assistant(string text, params ToolCallBlock[] calls)
        => Message.CreateAssistant("scripted", "test", [.. calls.Select(c => (ContentBlock)c), new TextBlock(text)]);

    private static Message Result(string callId, string text)
        => Message.CreateToolResult(callId, [new TextBlock(text)]);

    private static ToolCallBlock Call(string id, string name = "bash") => new(id, name, "{\"command\":\"ls\"}");

    [Fact]
    public void OrphanedToolResult_IsDropped()
    {
        // The post-compaction shape: the summary replaced the assistant message that made the call.
        var history = new List<Message>
        {
            Message.CreateUserText("[Context compacted] Summary of the earlier conversation."),
            Result("call_shadowed", "file-a\nfile-b"),
            Message.CreateUserText("continue"),
        };

        var repaired = MessagePairing.Repair(history);

        Assert.Equal(2, repaired.Count);
        Assert.DoesNotContain(repaired, m => m.Content.OfType<ToolResultBlock>().Any());
        Assert.True(MessagePairing.HasOrphans(history));
        Assert.False(MessagePairing.HasOrphans(repaired));
    }

    [Fact]
    public void UnansweredToolCall_IsDropped_ButTheAssistantTextSurvives()
    {
        var history = new List<Message>
        {
            Assistant("let me look", Call("call_1")),
            Message.CreateUserText("continue"), // the result never arrived (killed mid-tool)
        };

        var repaired = MessagePairing.Repair(history);

        Assert.Equal(2, repaired.Count);
        var assistant = repaired[0];
        Assert.Equal("let me look", assistant.FlattenText());
        Assert.DoesNotContain(assistant.Content, b => b is ToolCallBlock);
    }

    [Fact]
    public void AssistantMessageThatWasOnlyAToolCall_IsRemovedEntirely()
    {
        var call = Call("call_1");
        var history = new List<Message>
        {
            Message.CreateAssistant("scripted", "test", [call]),
            Message.CreateUserText("continue"),
        };

        var repaired = MessagePairing.Repair(history);

        var only = Assert.Single(repaired);
        Assert.Equal("user", only.Role);
    }

    [Fact]
    public void PairedHistory_IsReturnedUnchanged()
    {
        var history = new List<Message>
        {
            Message.CreateUserText("list the files"),
            Assistant("", Call("call_1")),
            Result("call_1", "file-a"),
            Assistant("done"),
        };

        var repaired = MessagePairing.Repair(history);

        Assert.Same(history, repaired); // no allocation, no rewrite
        Assert.False(MessagePairing.HasOrphans(history));
    }

    [Fact]
    public void ParallelCalls_KeepOnlyTheAnsweredOnes()
    {
        var history = new List<Message>
        {
            Assistant("", Call("call_1"), Call("call_2")),
            Result("call_2", "second only"), // call_1's result was pruned away
            Assistant("ok"),
        };

        var repaired = MessagePairing.Repair(history);

        var calls = repaired[0].Content.OfType<ToolCallBlock>().Select(c => c.Id).ToList();
        Assert.Equal(["call_2"], calls);
        Assert.False(MessagePairing.HasOrphans(repaired));
    }

    [Fact]
    public void WireBody_NeverEmitsAToolMessageWithoutItsCall()
    {
        // The exact request DeepSeek answered with invalid_request_error.
        var adapter = new OpenAiCompatibleAdapter("deepseek", "https://api.deepseek.com", "k", [], new HttpClient());
        var history = new List<Message>
        {
            Message.CreateUserText("[Context compacted] Summary."),
            Result("call_shadowed", "stale output"),
            Message.CreateUserText("continue"),
        };

        var messages = WireMessages(adapter, MessagePairing.Repair(history));

        var openCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var role = message.GetProperty("role").GetString();
            if (role == "assistant" && message.TryGetProperty("tool_calls", out var calls))
            {
                foreach (var call in calls.EnumerateArray()) openCallIds.Add(call.GetProperty("id").GetString()!);
            }
            if (role == "tool")
                Assert.Contains(message.GetProperty("tool_call_id").GetString()!, openCallIds);
        }
        Assert.DoesNotContain(messages, m => m.GetProperty("role").GetString() == "tool");
    }

    [Fact]
    public void WireBody_KeepsPairedToolTraffic()
    {
        var adapter = new OpenAiCompatibleAdapter("deepseek", "https://api.deepseek.com", "k", [], new HttpClient());
        var history = new List<Message>
        {
            Message.CreateUserText("list"),
            Assistant("", Call("call_1")),
            Result("call_1", "file-a"),
        };

        var messages = WireMessages(adapter, MessagePairing.Repair(history));

        Assert.Equal(3, messages.Count);
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("call_1", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    /// <summary>Round-trips the wire body through JSON and returns the messages array.</summary>
    private static List<JsonElement> WireMessages(OpenAiCompatibleAdapter adapter, IReadOnlyList<Message> messages)
    {
        var body = (Dictionary<string, object?>)adapter.BuildWireBody(new GenerateOptions
        {
            Provider = "deepseek",
            Model = "deepseek-flash",
            Messages = messages,
        });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return [.. doc.RootElement.GetProperty("messages").EnumerateArray().Select(e => e.Clone())];
    }
}
