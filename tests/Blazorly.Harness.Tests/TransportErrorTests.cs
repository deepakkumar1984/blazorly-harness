using Blazorly.Harness.Llm;
using Xunit;

namespace Blazorly.Harness.Tests;

/// <summary>
/// "Error while copying content to a stream." is what .NET reports when the peer drops the
/// connection, and on its own it tells the user nothing. The wrappers name the phase, the
/// endpoint and the payload size so an asleep/cold endpoint or an oversized prompt is visible.
/// </summary>
public class TransportErrorTests
{
    [Fact]
    public void SendFailure_NamesEndpointPayloadSizeAndRootCause()
    {
        var exception = new HttpRequestException("Error while copying content to a stream.",
            new IOException("Unable to write data to the transport connection: Broken pipe"));

        var message = TransportErrors.DescribeSendFailure(exception, "https://example.modal.direct/v1/chat/completions", 458_000);

        Assert.Contains("https://example.modal.direct/v1/chat/completions", message);
        Assert.Contains("447 KB request body", message);
        Assert.Contains("no HTTP status was ever received", message);
        Assert.Contains("Broken pipe", message);
        Assert.DoesNotContain("Error while copying content to a stream.\"", message);
    }

    [Fact]
    public void SendFailure_NamesWhyTheConnectionNeverStarted()
    {
        // "closed the connection while the request body was still uploading" is the wrong diagnosis
        // when the socket never connected; the real cause is in the nested SocketException.
        var refused = new HttpRequestException("Connection refused (127.0.0.1:9999)",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        var refusedText = TransportErrors.DescribeSendFailure(refused, "http://127.0.0.1:9999/v1/chat/completions", 1024);
        Assert.Contains("nothing accepted a connection", refusedText);
        Assert.DoesNotContain("still uploading", refusedText);

        var dns = new HttpRequestException("nodename nor servname provided, or not known",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
        Assert.Contains("does not resolve", TransportErrors.DescribeSendFailure(dns, "https://api.example/v1", 1024));

        var tls = new HttpRequestException("The SSL connection could not be established",
            new System.Security.Authentication.AuthenticationException("The remote certificate is invalid"));
        Assert.Contains("TLS handshake", TransportErrors.DescribeSendFailure(tls, "https://api.example/v1", 1024));
    }

    [Fact]
    public void Timeout_NamesTheEndpoint()
    {
        var message = TransportErrors.DescribeTimeout("https://api.example/v1/chat/completions",
            new TaskCanceledException("A task was canceled."));
        Assert.Contains("https://api.example/v1/chat/completions", message);
        Assert.Contains("timed out before the provider answered", message);
    }

    [Fact]
    public void StreamFailure_DistinguishesEmptyFromPartialResponses()
    {
        var exception = new IOException("The response ended prematurely.");

        Assert.Contains("before any token arrived", TransportErrors.DescribeStreamFailure(exception, "https://api/x", 0));
        var partial = TransportErrors.DescribeStreamFailure(exception, "https://api/x", 42);
        Assert.Contains("after 42 SSE events", partial);
        Assert.Contains("partial response was kept", partial);
    }

    [Fact]
    public async Task GuardSse_ClassifiesSocketFailuresAsRetryableTransportErrors()
    {
        var exception = await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in TransportErrors.GuardSse(BrokenSource(), "https://api/x")) { }
        });

        Assert.Equal(LlmErrorCodes.Transport, exception.Failure.Code);
        Assert.True(LlmErrorCodes.IsRetryable(exception.Failure.Code));
        Assert.Contains("dropped mid-stream after 2 SSE events", exception.Failure.Message);
    }

    [Fact]
    public async Task GuardSse_PassesPayloadsThrough()
    {
        var payloads = new List<string?>();
        await foreach (var payload in TransportErrors.GuardSse(HealthySource(), "https://api/x")) payloads.Add(payload);

        Assert.Equal(["a", "b"], payloads);
    }

    private static async IAsyncEnumerable<string?> BrokenSource()
    {
        yield return "a";
        yield return "b";
        await Task.Yield();
        throw new IOException("The response ended prematurely.");
    }

    private static async IAsyncEnumerable<string?> HealthySource()
    {
        yield return "a";
        await Task.Yield();
        yield return "b";
    }
}
