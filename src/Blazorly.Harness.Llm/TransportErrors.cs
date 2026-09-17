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
    /// <summary>
    /// Describes a failure raised before any response headers were received. A connection that never
    /// got established is named as such (unresolvable host, refused port, TLS rejection) because the
    /// upload-phase wording would point at the wrong cause; only a genuine mid-upload close describes
    /// the payload.
    /// </summary>
    public static string DescribeSendFailure(Exception exception, string endpoint, long bodyBytes)
        => DescribeConnectFailure(exception, endpoint)
            ?? $"the server at {endpoint} closed the connection while the {bodyBytes / 1024.0:F0} KB request body was "
                + "still uploading, so no HTTP status was ever received. Usual causes: the endpoint is cold/asleep or "
                + "crashed (OOM), a proxy rejected the payload size, or the prompt is larger than the model's context "
                + $"window. Underlying error: {RootMessage(exception)}";

    /// <summary>Describes a request that never completed: no response headers, no partial output.</summary>
    public static string DescribeTimeout(string endpoint, Exception? exception = null)
        => $"the request to {endpoint} timed out before the provider answered. Usual causes: the endpoint is "
            + "cold/asleep, the network or a proxy is dropping it, or the model is overloaded"
            + (exception is null ? "." : $". Underlying error: {RootMessage(exception)}");

    /// <summary>
    /// Names why a connection was never established, from the socket/TLS error .NET buries under
    /// HttpRequestException. Null when the failure happened after connecting (upload-phase close).
    /// </summary>
    private static string? DescribeConnectFailure(Exception exception, string endpoint)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case System.Net.Sockets.SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        System.Net.Sockets.SocketError.HostNotFound =>
                            $"the host for {endpoint} does not resolve (DNS NXDOMAIN). Check the base URL for a typo "
                                + $"and confirm the network can reach it. Underlying error: {RootMessage(exception)}",
                        System.Net.Sockets.SocketError.TryAgain =>
                            $"DNS for {endpoint} failed temporarily (SERVFAIL/timeout). Check the network, VPN or "
                                + $"resolver. Underlying error: {RootMessage(exception)}",
                        System.Net.Sockets.SocketError.ConnectionRefused =>
                            $"nothing accepted a connection at {endpoint} (connection refused). The server is down, "
                                + $"or the port or base URL is wrong. Underlying error: {RootMessage(exception)}",
                        System.Net.Sockets.SocketError.TimedOut =>
                            $"connecting to {endpoint} timed out. A firewall or proxy may be dropping it. "
                                + $"Underlying error: {RootMessage(exception)}",
                        System.Net.Sockets.SocketError.NetworkUnreachable =>
                            $"{endpoint} is unreachable from this machine (no route to the network). "
                                + $"Underlying error: {RootMessage(exception)}",
                        _ => $"the connection to {endpoint} failed ({socket.SocketErrorCode}). "
                            + $"Underlying error: {RootMessage(exception)}",
                    };
                case System.Security.Authentication.AuthenticationException tls:
                    return $"the TLS handshake with {endpoint} was rejected: {tls.Message}. A corporate proxy or an "
                        + $"expired/invalid certificate is the usual cause. Underlying error: {RootMessage(exception)}";
                case HttpRequestException http when http.InnerException is null:
                    // No socket error underneath: the request never reached the wire (bad scheme, no proxy, invalid URI).
                    return $"the request to {endpoint} could not be sent: {http.Message}";
            }
        }
        return null;
    }

    /// <summary>Describes a failure raised while reading the SSE response body.</summary>
    public static string DescribeStreamFailure(Exception exception, string endpoint, int eventsReceived)
        => eventsReceived == 0
            ? DescribeConnectFailure(exception, endpoint)
                ?? $"the server at {endpoint} closed the response stream before any token arrived. Underlying error: {RootMessage(exception)}"
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
