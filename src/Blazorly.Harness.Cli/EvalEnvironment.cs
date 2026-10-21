using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blazorly.Harness.Llm;
using Blazorly.Harness.Tools;
using Blazorly.Harness.Web;
using Blazorly.Harness.Web.Services;
using Blazorly.Harness.Core.Harnessing;
using Blazorly.Harness.Core.SystemPrompt;

namespace Blazorly.Harness.Cli;

/// <summary>What one backend contributed to a run: its tool surface, plugin set and pinned settings.</summary>
public sealed record EvalBackendManifest(
    string Backend,
    string ToolSchemaHash,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> AppliedPlugins,
    IReadOnlyDictionary<string, object?> Settings,
    string? Error)
{
    public string? ProfileId { get; init; }
    public string? SystemPromptHash { get; init; }
    public string? ConfigurationHash { get; init; }
    public IReadOnlyDictionary<string, string> SkillHashes { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The fixed environment an eval score was measured against. Without this a score change is
/// ambiguous: it can come from the agent loop, or from the sandbox backend, the plugin set, the
/// published tool schemas, or the host itself.
/// </summary>
public sealed record EvalEnvironment(
    string GeneratedAt,
    string HarnessVersion,
    string RuntimeVersion,
    string Os,
    string Arch,
    string? GitSha,
    bool GitDirty,
    bool LandlockSupported,
    bool E2bConfigured,
    string DefaultBackend,
    IReadOnlyList<string> Backends,
    string? Provider,
    string? Model,
    IReadOnlyDictionary<string, EvalBackendManifest> Manifests);

public static class EvalEnvironmentCapture
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Captures host facts plus one manifest per backend (each needs a real boot).</summary>
    public static async Task<EvalEnvironment> CaptureAsync(
        IReadOnlyList<string> backends,
        Func<string, string> homeOf,
        HarnessSettings ambient,
        CancellationToken ct = default, HarnessProfile? profile = null)
    {
        var manifests = new Dictionary<string, EvalBackendManifest>(StringComparer.Ordinal);
        foreach (var backend in backends)
        {
            var settings = EvalSandbox.PinnedSettings(backend);
            EvalSandbox.InheritRoutes(settings, ambient);
            if (profile is not null) HarnessBootstrapper.ApplyProfile(settings, profile);
            if (EvalSandbox.UnavailableReason(backend, settings) is { } reason)
            {
                // No boot: the backend cannot run here, and the manifest must say why.
                manifests[backend] = new EvalBackendManifest(backend, "", [], [],
                    EvalSandbox.RedactedSnapshot(settings), reason);
                continue;
            }
            manifests[backend] = await CaptureBackendAsync(backend, settings, homeOf(backend), profile, ct).ConfigureAwait(false);
        }

        var (sha, dirty) = GitStamp();
        return new EvalEnvironment(
            DateTimeOffset.UtcNow.ToString("o"),
            UiVersion.Text,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            sha,
            dirty,
            SandboxPolicy.ConfinementSupported,
            !string.IsNullOrWhiteSpace(ambient.ResolveE2bApiKey()),
            EvalSandbox.Default,
            backends,
            ambient.Provider,
            ambient.Model,
            manifests);

        static async Task<EvalBackendManifest> CaptureBackendAsync(string backend, HarnessSettings settings, string home,
            HarnessProfile? profile, CancellationToken ct)
        {
            var package = profile?.Materialize(home);
            var roots = package is null ? Array.Empty<string>() : new[] { Path.Combine(package, "skills") };
            var bootstrapper = new HarnessBootstrapper(new(home, roots, profile, package));
            try
            {
                await bootstrapper.StartAsync(ct).ConfigureAwait(false);
                var schemas = bootstrapper.Tools.Schemas();
                var assembly = bootstrapper.Context.Get<SystemPromptService>(SystemPromptService.ServiceKey).Assemble(null, null);
                var prompt = SystemPromptService.RenderPrompt(assembly);
                if (package is not null) prompt = prompt.Replace(package, "{{profileRoot}}", StringComparison.Ordinal);
                var promptHash = HarnessArtifacts.HashText(prompt);
                var skills = bootstrapper.Context.TryGet<SkillsService>("skills");
                var skillHashes = skills?.List().ToDictionary(s => s.Name, s => HarnessArtifacts.HashText(skills.ReadBody(s.Name) ?? ""))
                    ?? new Dictionary<string, string>();
                var snapshot = EvalSandbox.RedactedSnapshot(settings);
                var configurationHash = HarnessArtifacts.Hash(JsonSerializer.SerializeToElement(new
                {
                    assets = (profile ?? HarnessProfile.Baseline).Files.ToDictionary(p => p.Key, p => HarnessArtifacts.HashText(p.Value)),
                    promptHash,
                    toolSchemaHash = ToolSchemaHash(schemas),
                    skillHashes,
                    settings = snapshot,
                }));
                return new EvalBackendManifest(
                    backend,
                    ToolSchemaHash(schemas),
                    schemas.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                    bootstrapper.AppliedPlugins,
                    snapshot,
                    null)
                {
                    ProfileId = (profile ?? HarnessProfile.Baseline).Id,
                    SystemPromptHash = promptHash,
                    ConfigurationHash = configurationHash,
                    SkillHashes = skillHashes,
                };
            }
            catch (Exception exception)
            {
                // A backend that cannot boot is a manifest fact, not a reason to lose the run.
                return new EvalBackendManifest(backend, "", [], [],
                    EvalSandbox.RedactedSnapshot(settings), exception.Message);
            }
            finally
            {
                await bootstrapper.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// SHA-256 over the canonicalized published tool surface (name, description, parameters with
    /// recursively sorted keys). Any schema drift — a renamed argument, a new required property, a
    /// description edit — changes the hash, so two scores with different hashes are not comparable.
    /// </summary>
    public static string ToolSchemaHash(IReadOnlyList<ToolSchema> schemas)
    {
        var builder = new StringBuilder();
        foreach (var schema in schemas.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            builder.Append(schema.Name).Append('\n')
                .Append(schema.Description).Append('\n')
                .Append(Canonicalize(schema.Parameters)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>Stable JSON: object keys sorted at every level, arrays keep their order.</summary>
    public static string Canonicalize(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, element);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteRawValue(element.GetRawText());
                break;
        }
    }

    /// <summary>Writes environment.json beside results.json.</summary>
    public static async Task WriteAsync(EvalEnvironment environment, string outDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "environment.json"),
            JsonSerializer.Serialize(environment, ManifestJson), ct).ConfigureAwait(false);
    }

    /// <summary>Commit + dirty flag when evals run inside a checkout; nulls for an installed app.</summary>
    private static (string? Sha, bool Dirty) GitStamp()
    {
        var sha = Git("rev-parse", "HEAD");
        if (sha is null) return (null, false);
        var status = Git("status", "--porcelain");
        return (sha, status is { Length: > 0 });

        static string? Git(string command, string argument)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"{command} {argument}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                });
                if (process is null) return null;
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                return process.ExitCode == 0 ? output : null;
            }
            catch (Exception)
            {
                return null; // no git on PATH, or not a repository
            }
        }
    }
}
