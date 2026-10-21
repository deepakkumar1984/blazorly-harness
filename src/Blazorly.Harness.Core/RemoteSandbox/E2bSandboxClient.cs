using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.RemoteSandbox;

public sealed record E2bOptions
{
    /// <summary>Management API base; the published E2B endpoint. Override for self-hosted or tests.</summary>
    public string BaseUrl { get; init; } = "https://api.e2b.app";
    public required string ApiKey { get; init; }
    /// <summary>Sandbox image/template id passed at create; E2B's default template is "base".</summary>
    public string Template { get; init; } = "base";
    /// <summary>Sandbox keep-alive seconds requested at create.</summary>
    public int TimeoutSeconds { get; init; } = 300;
    public int ExecTimeoutSeconds { get; init; } = 120;
    /// <summary>Overrides the derived per-sandbox envd base (self-hosted / test fake servers).</summary>
    public string? EnvdBaseUrl { get; init; }
}

public sealed record E2bExecResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// E2B remote-sandbox client (dsh packages/e2b parity, REST form). Lifecycle follows the
/// published management API (<c>POST /sandboxes</c>, <c>DELETE /sandboxes/{id}</c>, X-API-Key
/// auth). Command execution posts to the per-sandbox envd process service
/// (<c>/process.Process/Start</c>, connect+json) and reads the initial response events. The
/// wire is exercised against a fake server in tests; live behavior additionally requires a
/// real E2B API key, so exec parsing is deliberately defensive.
/// </summary>
public sealed class E2bSandboxClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly E2bOptions _options;

    public E2bSandboxClient(HttpClient httpClient, E2bOptions options)
    {
        _http = httpClient;
        _options = options;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", options.ApiKey);
    }

    /// <summary>Creates a sandbox; returns its id.</summary>
    public async Task<string> CreateAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(_options.BaseUrl.TrimEnd('/') + "/sandboxes", new
        {
            templateID = _options.Template,
            timeout = _options.TimeoutSeconds,
        }, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, "create sandbox").ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        var id = payload.TryGetProperty("sandboxID", out var idElement) ? idElement.GetString() : null;
        return id is { Length: > 0 } ? id : throw new Kernel.HarnessException("E2B_BAD_RESPONSE", "sandbox create response carried no sandboxID");
    }

    /// <summary>Runs a command via the sandbox's envd process service.</summary>
    public async Task<E2bExecResult> ExecAsync(string sandboxId, string command, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{EnvdBase(sandboxId)}/process.Process/Start");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            process = new
            {
                cmd = "/bin/bash",
                args = new[] { "-lc", command },
                cwd = "/home/user",
            },
            stdin = false,
        }), Encoding.UTF8, "application/connect+json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, "exec command").ConfigureAwait(false);
        return ParseExecResponse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Kills the sandbox.</summary>
    public async Task KillAsync(string sandboxId, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(_options.BaseUrl.TrimEnd('/') + "/sandboxes/" + Uri.EscapeDataString(sandboxId), ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, "kill sandbox").ConfigureAwait(false);
    }

    private string EnvdBase(string sandboxId)
    {
        if (_options.EnvdBaseUrl is { } overriden) return overriden.TrimEnd('/');
        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        var host = $"{49983}-{sandboxId}." + baseUri.Host;
        return $"{baseUri.Scheme}://{host}";
    }

    /// <summary>
    /// envd streams connect+json envelopes; the exit code is carried by the first event that
    /// has one. Bytes that do not parse defensively degrade to exit-code-1 with raw text.
    /// </summary>
    public static E2bExecResult ParseExecResponse(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = (int?)null;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonElement envelope;
            try
            {
                envelope = JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }
            foreach (var candidate in new[] { envelope, envelope.TryGetProperty("event", out var nested) ? nested : default })
            {
                if (candidate.ValueKind != JsonValueKind.Object) continue;
                if (candidate.TryGetProperty("stdout", out var stdoutElement) && stdoutElement.ValueKind == JsonValueKind.String)
                    stdout.Append(stdoutElement.GetString());
                if (candidate.TryGetProperty("stderr", out var stderrElement) && stderrElement.ValueKind == JsonValueKind.String)
                    stderr.Append(stderrElement.GetString());
                if (candidate.TryGetProperty("exitCode", out var exitElement) && exitElement.TryGetInt32(out var code))
                    exitCode = code;
            }
        }
        if (exitCode is null && stdout.Length == 0 && stderr.Length == 0)
            return new E2bExecResult(1, "", text.Length > 0 ? text : "");
        return new E2bExecResult(exitCode ?? 0, stdout.ToString(), stderr.ToString());
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new Kernel.HarnessException("E2B_REQUEST_FAILED", $"failed to {action}: HTTP {(int)response.StatusCode} {body.Trim()}");
    }

    public void Dispose() => _http.Dispose();
}
