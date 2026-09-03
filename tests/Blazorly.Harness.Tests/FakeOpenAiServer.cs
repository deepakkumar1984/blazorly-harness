using System.Net;
using System.Text;

namespace Blazorly.Harness.Tests;

/// <summary>
/// A local OpenAI-compatible stand-in for tests that boot the real harness in another process:
/// POST /v1/chat/completions streams the canonical two-step flow (bash + todo_write tool calls,
/// then a summary once tool results return). GET /v1/models lists the scripted model.
/// </summary>
public sealed class FakeOpenAiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string BaseUrl { get; }
    public int Requests { get; private set; }
    private readonly int _chunkDelayMs;

    public FakeOpenAiServer(int chunkDelayMs = 0)
    {
        var port = Random.Shared.Next(20000, 60000);
        while (true)
        {
            try
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                port = Random.Shared.Next(20000, 60000);
            }
        }
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _chunkDelayMs = chunkDelayMs;
        _ = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => Handle(ctx));
                }
            }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        });
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = reader.ReadToEnd();
            Requests++;
            if (ctx.Request.Url!.AbsolutePath.EndsWith("/models"))
            {
                Write(ctx, """{"object":"list","data":[{"id":"test"}]}""", "application/json");
                return;
            }
            var hasToolResults = body.Contains("\"role\":\"tool\"", StringComparison.Ordinal)
                || body.Contains("\"role\": \"tool\"", StringComparison.Ordinal);
            var sse = hasToolResults ? SummaryStream() : ToolCallStream();
            ctx.Response.ContentType = "text/event-stream";
            foreach (var line in sse)
            {
                // SSE events are delimited by a blank line; the adapter's parser emits per blank line.
                var bytes = Encoding.UTF8.GetBytes(line + "\n\n");
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.OutputStream.Flush();
                if (_chunkDelayMs > 0) Thread.Sleep(_chunkDelayMs);
            }
            ctx.Response.Close();
        }
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private static void Write(HttpListenerContext ctx, string body, string contentType)
    {
        ctx.Response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static IEnumerable<string> SummaryStream()
    {
        yield return Sse("""{"choices":[{"delta":{"content":"The scripted run completed: I executed `bash` and updated the todo list."}}]}""");
        yield return Sse("""{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":120,"completion_tokens":18,"total_tokens":138,"prompt_tokens_details":{"cached_tokens":0},"completion_tokens_details":{"reasoning_tokens":0}}}""");
        yield return "data: [DONE]";
    }

    private static IEnumerable<string> ToolCallStream()
    {
        // Mirrors ScriptedDemoFlow.Respond: tools first with no leading text, then a summary
        // once tool results return (keeps agent_message_chunk counts/order identical).
        var bash = JsonArg("{\"command\":\"sleep 2.5 && echo \\\"hello from blazorly harness\\\" && date\",\"description\":\"Greet and print the date\"}");
        var todos = JsonArg("{\"todos\":[{\"content\":\"Run the scripted greeting\",\"status\":\"completed\"},{\"content\":\"Summarize the result\",\"status\":\"in_progress\"}]}");
        yield return Sse($"{{\"choices\":[{{\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"call_bash\",\"type\":\"function\",\"function\":{{\"name\":\"bash\",\"arguments\":\"{bash}\"}}}},{{\"index\":1,\"id\":\"call_todo\",\"type\":\"function\",\"function\":{{\"name\":\"todo_write\",\"arguments\":\"{todos}\"}}}}]}}}}]}}");
        yield return Sse("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":80,"completion_tokens":30,"total_tokens":110,"prompt_tokens_details":{"cached_tokens":0},"completion_tokens_details":{"reasoning_tokens":0}}}""");
        yield return "data: [DONE]";
    }

    private static string Sse(string json) => "data: " + json;

    /// <summary>Escapes raw JSON text for embedding as a JSON string value (backslashes first).</summary>
    private static string JsonArg(string json) => json.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

/// <summary>Settings/home helpers for tests that boot the real composition.</summary>
public static class ScriptedSettings
{
    /// <summary>Writes a home settings.json routing the "scripted" provider at a fake OpenAI-compatible server.</summary>
    public static void WriteFakeRoute(string home, string baseUrl, bool autoTitles = false, string workspaceRoot = "http://unused")
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            provider = "scripted",
            model = "test",
            baseUrl,
            apiKey = "test-key",
            enableAutoTitles = autoTitles,
            customProviders = new[]
            {
                new { name = "scripted", baseUrl, apiKey = "test-key", models = new[] { "test" } },
            },
        }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }
}
