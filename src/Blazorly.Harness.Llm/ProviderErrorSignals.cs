namespace Blazorly.Harness.Llm;

/// <summary>Recognizes provider failures caused by a gateway dependency rather than the request.</summary>
public static class ProviderErrorSignals
{
    public static bool IsUpstreamDependencyFailure(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("badrequestfordependentservice")
            || lower.Contains("bad request for dependent service");
    }
}
