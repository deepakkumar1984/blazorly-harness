using System.Runtime.CompilerServices;

namespace Blazorly.Harness.Llm;

/// <summary>
/// Transport failures are the one class of provider error whose default .NET text is useless:
/// "Error while copying content to a stream." says nothing about which phase failed, which endpoint
/// dropped us, or how big the request was. Naming those turns a dead end into a diagnosis — an
/// upload-phase close means no HTTP status ever arrived (endpoint asleep, crashed, proxy body limit,
/// or a prompt past the model's context window), while a mid-stream close means partial output.
/// </summary>
public static class TransportErrors
{
    /// <summary>Describes a failure raised before any response headers were received.</summary>
    public static string DescribeSendFailure(Exception exception, string endpoint, long bodyBytes)
        => $"the server at {endpoint} closed the connection while the {bodyBytes / 1024.0:F0} KB request body was "
            + "still uploading, so no HTTP status was ever received. Usual causes: the endpoint is cold/asleep or "
            + "crashed (OOM), a proxy rejected the payload size, or the prompt is larger than the model's context "
            + $"window. Underlying error: {RootMessage(exception)}";

    /// <summary>Describes a failure raised while reading the SSE response body.</summary>
    public static string DescribeStreamFailure(Exception exception, string endpoint, int eventsReceived)
        => eventsReceived == 0
            ? $"the server at {endpoint} closed the response stream before any token arrived. Underlying error: {RootMessage(exception)}"
            : $"the connection to {endpoint} dropped mid-stream after {eventsReceived} SSE events; the partial "
                + $"response was kept. Underlying error: {RootMessage(exception)}";

    /// <summary>Wraps an SSE payload sequence so socket failures surface as classified transport errors.</summary>
    public static async IAsyncEnumerable<string?> GuardSse(
        IAsyncEnumerable<string?> source,
        string endpoint,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var received = 0;
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    throw new LlmException(LlmErrorCodes.Transport, DescribeStreamFailure(exception, endpoint, received));
                }
                if (!moved) break;
                received++;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Innermost message; .NET nests socket failures under two or three wrappers.</summary>
    public static string RootMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is { } inner && inner.Message != current.Message) current = inner;
        return $"{current.GetType().Name}: {current.Message}";
    }
}
