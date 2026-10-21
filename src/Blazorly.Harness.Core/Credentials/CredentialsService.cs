using System.Text.Json;
using Blazorly.Harness.Kernel;

namespace Blazorly.Harness.Core.Credentials;

public sealed record CredentialResolution(string Name, string Value, string Source);

public sealed record CredentialDescriptor(string Name, string Source);

/// <summary>
/// ctx.credentials — a name-keyed secret store resolved env-first over a private JSON file
/// ({name: value}). Values leave the service only through Resolve; Describe names sources only.
/// </summary>
public sealed class CredentialsService
{
    public const string ServiceKey = "credentials";
    public const string SourceEnv = "env";
    public const string SourceFile = "file";

    private static readonly JsonSerializerOptions StoreJson = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _io = new(1, 1);

    public CredentialsService(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath();
    }

    public string FilePath => _filePath;

    public static string DefaultFilePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".blazorly", "credentials.json");

    public static CredentialsService Mount(HarnessContext ctx, string? filePath = null)
    {
        var service = new CredentialsService(filePath);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    /// <summary>Environment variable (non-empty) first, then the file store; null when neither has the name.</summary>
    public CredentialResolution? Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var fromEnv = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(fromEnv)) return new CredentialResolution(name, fromEnv, SourceEnv);
        var stored = ReadStore();
        return stored.TryGetValue(name, out var value) ? new CredentialResolution(name, value, SourceFile) : null;
    }

    /// <summary>Every stored name with the source it would resolve from; never the values.</summary>
    public IReadOnlyList<CredentialDescriptor> Describe()
    {
        var stored = ReadStore();
        var described = new List<CredentialDescriptor>(stored.Count);
        foreach (var name in stored.Keys)
        {
            var fromEnv = Environment.GetEnvironmentVariable(name);
            described.Add(new CredentialDescriptor(name, string.IsNullOrEmpty(fromEnv) ? SourceFile : SourceEnv));
        }
        described.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return described;
    }

    public Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return WrapIo(async () =>
        {
            var store = ReadStore();
            store[name] = value;
            await WriteStoreAsync(store, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task UnsetAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return WrapIo(async () =>
        {
            var store = ReadStore();
            if (store.Remove(name)) await WriteStoreAsync(store, ct).ConfigureAwait(false);
        }, ct);
    }

    private Dictionary<string, string> ReadStore()
    {
        if (!File.Exists(_filePath)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = File.OpenRead(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return loaded is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // an unreadable store resolves as empty rather than poisoning every lookup
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task WriteStoreAsync(Dictionary<string, string> store, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(store, StoreJson), ct).ConfigureAwait(false);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // permission hardening is best-effort; non-Unix hosts and odd filesystems skip it
        }
    }

    private async Task WrapIo(Func<Task> action, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _io.Release(); }
    }
}
