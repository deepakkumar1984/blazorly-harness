using System.Text;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.SystemPrompt;

/// <summary>Evaluation context for section/variable providers; resolves services from the agent scope down.</summary>
public sealed class SystemPromptContext(HarnessContext root, Agent.Agent? agent, string? cwd)
{
    public Agent.Agent? Agent { get; } = agent;
    public string? Cwd { get; } = cwd;

    public T Service<T>(string key) where T : class => (Agent?.Ctx ?? root).Get<T>(key);
}

public sealed record AssembledSection(string Name, int Order, string Text);

public sealed record PromptAssembly
{
    public required IReadOnlyList<AssembledSection> Sections { get; init; }
    public required IReadOnlyList<AssembledSection> ContextSections { get; init; }
    public required IReadOnlyDictionary<string, string> Variables { get; init; }
    public required IReadOnlyList<ToolSchema> ToolSchemas { get; init; }
}

/// <summary>
/// ctx.systemPrompt — prompt-section and tool-schema assembly. Sections sort by order
/// (convention: identity at -100, persona 0, tool guidance 100–199); dynamic context
/// sections render into the durable runtime-context snapshot, not the system prompt.
/// </summary>
public sealed class SystemPromptService
{
    public const string ServiceKey = "systemPrompt";

    private readonly HarnessContext _ctx;
    private readonly List<(string Name, int Order, Func<SystemPromptContext, string> Text)> _sections = [];
    private readonly List<(string Name, int Order, Func<SystemPromptContext, string> Text)> _contextSections = [];
    private readonly Dictionary<string, Func<SystemPromptContext, string>> _variables = new(StringComparer.Ordinal);
    private readonly List<Func<object?, IReadOnlyList<ToolSchema>>> _toolProviders = [];

    public SystemPromptService(HarnessContext ctx) => _ctx = ctx;

    public static SystemPromptService Mount(HarnessContext ctx)
    {
        var service = new SystemPromptService(ctx);
        ctx.Provide(ServiceKey, service);
        return service;
    }

    public IDisposable RegisterSection(string name, int order, Func<SystemPromptContext, string> text)
    {
        _sections.Add((name, order, text));
        return Disposable.Of(() => _sections.RemoveAll(s => s.Name == name));
    }

    public IDisposable RegisterContext(string name, int order, Func<SystemPromptContext, string> text)
    {
        _contextSections.Add((name, order, text));
        return Disposable.Of(() => _contextSections.RemoveAll(s => s.Name == name));
    }

    public IDisposable RegisterVariable(string name, Func<SystemPromptContext, string> provider)
    {
        _variables[name] = provider;
        return Disposable.Of(() => _variables.Remove(name));
    }

    public IDisposable RegisterToolProvider(Func<object?, IReadOnlyList<ToolSchema>> provider)
    {
        _toolProviders.Add(provider);
        return Disposable.Of(() => _toolProviders.Remove(provider));
    }

    public PromptAssembly Assemble(Agent.Agent? agent, string? cwd)
    {
        var context = new SystemPromptContext(_ctx, agent, cwd);
        var variables = _variables.ToDictionary(kv => kv.Key, kv => kv.Value(context), StringComparer.Ordinal);
        var sections = _sections
            .Select(s => new AssembledSection(s.Name, s.Order, Interpolate(s.Text(context), variables)))
            .Where(s => s.Text.Length > 0)
            .OrderBy(s => s.Order)
            .ToList();
        var contextSections = _contextSections
            .Select(s => new AssembledSection(s.Name, s.Order, Interpolate(s.Text(context), variables)))
            .Where(s => s.Text.Length > 0)
            .OrderBy(s => s.Order)
            .ToList();
        var schemas = _toolProviders.SelectMany(p => p(agent?.ScopeKey)).ToList();
        return new PromptAssembly
        {
            Sections = sections,
            ContextSections = contextSections,
            Variables = variables,
            ToolSchemas = schemas,
        };
    }

    /// <summary>Renders the system prompt: sections joined with blank lines; empty sections dropped.</summary>
    public static string RenderPrompt(PromptAssembly assembly)
        => string.Join("\n\n", assembly.Sections.Select(s => s.Text));

    /// <summary>Renders the runtime-context snapshot body (dynamic context sections only).</summary>
    public static string RenderContextSections(PromptAssembly assembly)
        => string.Join("\n\n", assembly.ContextSections.Select(s => s.Text));

    private static string Interpolate(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        var result = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                result.Append(template[i..]);
                break;
            }
            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                result.Append(template[i..]);
                break;
            }
            result.Append(template[i..open]);
            var name = template[(open + 2)..close].Trim();
            if (!variables.TryGetValue(name, out var value))
                throw new Kernel.HarnessException("PROMPT_VARIABLE", $"unknown prompt variable '{name}'");
            result.Append(value);
            i = close + 2;
        }
        return result.ToString();
    }
}
