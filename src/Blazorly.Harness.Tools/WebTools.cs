using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blazorly.Harness.Core.Tools;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Tools;

/// <summary>Provider seam for web access; production speaks HTTP, tests inject canned servers.</summary>
public interface IWebProvider
{
    Task<WebFetchResult> FetchAsync(string url, CancellationToken ct);

    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken ct);
}

public sealed record WebFetchResult(string Url, int Status, string ContentType, string Text);

public sealed record WebSearchResult(string Title, string Url, string Snippet);

public static class WebLimits
{
    public const int MaxFetchChars = 20_000;
    public const int MaxSearchResults = 8;
}

/// <summary>Default HTTP backend: plain GETs with a tagged user agent and a DuckDuckGo HTML endpoint.</summary>
public sealed partial class HttpWebProvider(Uri? searchBase = null) : IWebProvider, IDisposable
{
    public const string DefaultSearchBase = "https://html.duckduckgo.com/html/";
    public const string UserAgent = "blazorly-harness/1.0";

    private readonly Uri _searchBase = searchBase ?? new Uri(DefaultSearchBase);
    private readonly HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public void Dispose() => _client.Dispose();

    public async Task<WebFetchResult> FetchAsync(string url, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ToolException("WEB_FETCH_FAILED", $"HTTP {(int)response.StatusCode}");
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        string text;
        if (IsHtml(contentType))
        {
            text = StripHtml(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        else if (IsTextual(contentType))
        {
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        else
        {
            return new WebFetchResult(url, (int)response.StatusCode, contentType, "(binary content)");
        }
        if (text.Length > WebLimits.MaxFetchChars) text = text[..WebLimits.MaxFetchChars];
        return new WebFetchResult(url, (int)response.StatusCode, contentType, text);
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        var separator = string.IsNullOrEmpty(_searchBase.Query) ? "?" : "&";
        var url = _searchBase.ToString() + separator + "q=" + Uri.EscapeDataString(query);
        using var response = await _client.GetAsync(url, ct).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var anchors = ResultAnchorRegex().Matches(html);
        var snippets = ResultSnippetRegex().Matches(html);
        var results = new List<WebSearchResult>();
        for (var i = 0; i < anchors.Count && results.Count < WebLimits.MaxSearchResults; i++)
        {
            var title = StripHtml(anchors[i].Groups[2].Value);
            var href = ResolveHref(anchors[i].Groups[1].Value);
            var snippet = i < snippets.Count ? StripHtml(snippets[i].Groups[1].Value) : "";
            results.Add(new WebSearchResult(title, href, snippet));
        }
        if (results.Count == 0) throw new ToolException("WEB_SEARCH_EMPTY", "no results");
        return results;
    }

    /// <summary>ddg result anchors point at a redirect carrying the real target in uddg; unwrap it.</summary>
    internal static string ResolveHref(string href)
    {
        if (href.Contains("duckduckgo.com/l/", StringComparison.OrdinalIgnoreCase))
        {
            var uddg = UddgParamRegex().Match(href);
            if (uddg.Success) return Uri.UnescapeDataString(uddg.Groups[1].Value);
        }
        return href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href;
    }

    internal static string StripHtml(string html)
    {
        var text = WebUtility.HtmlDecode(ScriptBlockRegex().Replace(html, " "));
        return WhitespaceRegex().Replace(TagRegex().Replace(text, " "), " ").Trim();
    }

    internal static bool IsHtml(string contentType) => contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTextual(string contentType) => contentType.Length == 0
        || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("csv", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?is)<(script|style)[^>]*>.*?</\1>")]
    private static partial Regex ScriptBlockRegex();

    [GeneratedRegex(@"(?s)<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<a[^>]*class=""result__a""[^>]*href=""([^""]*)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex ResultAnchorRegex();

    [GeneratedRegex(@"class=""result__snippet""[^>]*>(.*?)</", RegexOptions.Singleline)]
    private static partial Regex ResultSnippetRegex();

    [GeneratedRegex(@"[?&]uddg=([^&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UddgParamRegex();
}

/// <summary>ctx.web — the mounted web provider, shared by tools and any UI surfaces.</summary>
public sealed class WebRuntime
{
    public const string ServiceKey = "web";

    public IWebProvider Provider { get; }

    public WebRuntime(IWebProvider provider) => Provider = provider;

    public static WebRuntime Mount(HarnessContext ctx, IWebProvider provider)
    {
        var runtime = new WebRuntime(provider);
        ctx.Provide(ServiceKey, runtime);
        return runtime;
    }
}

/// <summary>Tavily Search API backend (keyed). Fetch delegates to direct HTTP.</summary>
public sealed class TavilySearchProvider : IWebProvider, IDisposable
{
    public const string DefaultEndpoint = "https://api.tavily.com";

    private readonly string _apiKey;
    private readonly Uri _endpoint;
    private readonly HttpClient _client = CreateClient();
    private readonly HttpWebProvider _fetch = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpWebProvider.UserAgent);
        return client;
    }

    public TavilySearchProvider(string apiKey, Uri? endpoint = null)
    {
        _apiKey = apiKey;
        _endpoint = endpoint ?? new Uri(DefaultEndpoint);
    }

    public void Dispose()
    {
        _client.Dispose();
        _fetch.Dispose();
    }

    public Task<WebFetchResult> FetchAsync(string url, CancellationToken ct) => _fetch.FetchAsync(url, ct);

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "/search"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { api_key = _apiKey, query, max_results = WebLimits.MaxSearchResults, include_answer = false }),
                Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Classify(response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var results = new List<WebSearchResult>();
        if (doc.RootElement.TryGetProperty("results", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (results.Count >= WebLimits.MaxSearchResults) break;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (url.Length == 0) continue;
                results.Add(new WebSearchResult(title, url, SnippetOf(snippet)));
            }
        }
        if (results.Count == 0) throw new ToolException("WEB_SEARCH_EMPTY", "no results");
        return results;
    }

    private static ToolException Classify(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => new ToolException("WEB_SEARCH_AUTH", $"tavily rejected the API key ({(int)status})"),
        429 => new ToolException("WEB_SEARCH_RATE_LIMIT", "tavily rate limit (429)"),
        _ => new ToolException("WEB_SEARCH_FAILED", $"tavily search failed ({(int)status})"),
    };

    private static string SnippetOf(string content)
        => content.Length > 500 ? content[..500] + "…" : content;
}

/// <summary>Brave Search API backend (keyed). Fetch delegates to direct HTTP.</summary>
public sealed class BraveSearchProvider : IWebProvider, IDisposable
{
    public const string DefaultEndpoint = "https://api.search.brave.com";

    private readonly string _apiKey;
    private readonly Uri _endpoint;
    private readonly HttpClient _client = CreateClient();
    private readonly HttpWebProvider _fetch = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpWebProvider.UserAgent);
        return client;
    }

    public BraveSearchProvider(string apiKey, Uri? endpoint = null)
    {
        _apiKey = apiKey;
        _endpoint = endpoint ?? new Uri(DefaultEndpoint);
    }

    public void Dispose()
    {
        _client.Dispose();
        _fetch.Dispose();
    }

    public Task<WebFetchResult> FetchAsync(string url, CancellationToken ct) => _fetch.FetchAsync(url, ct);

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        var url = new Uri(_endpoint, $"/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={WebLimits.MaxSearchResults}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Subscription-Token", _apiKey);
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Classify(response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var results = new List<WebSearchResult>();
        if (doc.RootElement.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (results.Count >= WebLimits.MaxSearchResults) break;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var link = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                if (link.Length == 0) continue;
                results.Add(new WebSearchResult(title, link, snippet));
            }
        }
        if (results.Count == 0) throw new ToolException("WEB_SEARCH_EMPTY", "no results");
        return results;
    }

    private static ToolException Classify(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => new ToolException("WEB_SEARCH_AUTH", $"brave rejected the API key ({(int)status})"),
        429 => new ToolException("WEB_SEARCH_RATE_LIMIT", "brave rate limit (429)"),
        _ => new ToolException("WEB_SEARCH_FAILED", $"brave search failed ({(int)status})"),
    };
}

public sealed record WebSearchArgs(string Query);

public sealed record WebSearchOutput(string Query, IReadOnlyList<WebSearchResult> Results);

/// <summary>web_search: query the configured search endpoint, top 8 results.</summary>
public sealed class WebSearchTool(IWebProvider web) : ToolDefinition<WebSearchArgs, WebSearchOutput>
{
    public override string Name => "web_search";

    public override string Description =>
        "Search the web and return the top results (title, url, snippet), up to 8. "
        + "Use web_fetch on a promising url for the full page text.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["query"] = JsonSchema.String("Search query."),
        },
        required: ["query"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["query"] = JsonSchema.String(),
            ["results"] = JsonSchema.Array(new JsonSchema.Schema
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchema.Schema>
                {
                    ["title"] = JsonSchema.String(),
                    ["url"] = JsonSchema.String(),
                    ["snippet"] = JsonSchema.String(),
                },
                Required = ["title", "url", "snippet"],
                AdditionalProperties = false,
            }),
        },
        required: ["query", "results"]);

    public override int? TimeoutMs => 25_000;

    protected override bool IsConcurrencySafeTyped(WebSearchArgs args) => true;

    protected override async Task<WebSearchOutput> ExecuteTyped(WebSearchArgs args, ToolRunContext exec)
        => new(args.Query, await web.SearchAsync(args.Query, exec.Signal).ConfigureAwait(false));

    protected override IReadOnlyList<ContentBlock> RenderTyped(WebSearchArgs args, WebSearchOutput output)
    {
        if (output.Results.Count == 0) return [new TextBlock("No results found.")];
        var builder = new StringBuilder();
        foreach (var result in output.Results)
        {
            builder.Append(result.Title).Append(" — ").AppendLine(result.Url);
            if (result.Snippet.Length > 0) builder.AppendLine(result.Snippet);
        }
        return [new TextBlock(builder.ToString().TrimEnd())];
    }

    protected override ToolCallView? PresentCallTyped(WebSearchArgs args) => new()
    {
        Card = "generic",
        Kind = "search",
        Title = args.Query,
        Description = "web search",
    };

    protected override ToolResultView? PresentResultTyped(WebSearchArgs args, ToolExecutionResult result)
        => new() { Card = "search", Title = args.Query, Text = result.Content.OfType<TextBlock>().FirstOrDefault()?.Text };
}

public sealed record WebFetchArgs(string Url);

public sealed record WebFetchOutput(string Url, int Status, string ContentType, string Text, bool Truncated);

/// <summary>web_fetch: GET a url, html stripped to text, capped at 20000 characters.</summary>
public sealed class WebFetchTool(IWebProvider web) : ToolDefinition<WebFetchArgs, WebFetchOutput>
{
    public override string Name => "web_fetch";

    public override string Description =>
        "Fetch a url and return its text. HTML is stripped to readable text (scripts, styles and tags removed); "
        + $"other textual content is returned raw; binary content is noted as such. Capped at {WebLimits.MaxFetchChars} characters.";

    public override JsonSchema.Schema Parameters { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["url"] = JsonSchema.String("Absolute http(s) url to fetch."),
        },
        required: ["url"]);

    public override JsonSchema.Schema Output { get; } = JsonSchema.Object(
        properties: new Dictionary<string, JsonSchema.Schema>
        {
            ["url"] = JsonSchema.String(),
            ["status"] = JsonSchema.Integer(),
            ["contentType"] = JsonSchema.String(),
            ["text"] = JsonSchema.String(),
            ["truncated"] = JsonSchema.Boolean(),
        },
        required: ["url", "status", "contentType", "text", "truncated"]);

    public override int? TimeoutMs => 25_000;

    protected override bool IsConcurrencySafeTyped(WebFetchArgs args) => true;

    protected override async Task<WebFetchOutput> ExecuteTyped(WebFetchArgs args, ToolRunContext exec)
    {
        var fetched = await web.FetchAsync(args.Url, exec.Signal).ConfigureAwait(false);
        var truncated = fetched.Text.Length >= WebLimits.MaxFetchChars && fetched.Text != "(binary content)";
        return new WebFetchOutput(fetched.Url, fetched.Status, fetched.ContentType, fetched.Text, truncated);
    }

    protected override IReadOnlyList<ContentBlock> RenderTyped(WebFetchArgs args, WebFetchOutput output)
    {
        var builder = new StringBuilder(output.Text);
        if (output.Truncated) builder.Append("\n…(truncated at ").Append(WebLimits.MaxFetchChars).Append(" characters)");
        return [new TextBlock(builder.ToString())];
    }

    protected override ToolCallView? PresentCallTyped(WebFetchArgs args) => new()
    {
        Card = "generic",
        Kind = "fetch",
        Title = args.Url,
        Description = "web fetch",
    };

    protected override ToolResultView? PresentResultTyped(WebFetchArgs args, ToolExecutionResult result)
        => new() { Card = "web", Title = args.Url, Text = result.Content.OfType<TextBlock>().FirstOrDefault()?.Text };
}

/// <summary>Mounts the web tools and the web runtime service; tests inject their own provider.</summary>
public sealed class WebPlugin : HarnessPlugin
{
    public override string Name => "web";
    public override string[] Inject { get; } = ["tools"];

    private readonly IWebProvider _provider;
    private readonly bool _ownsProvider;

    public WebPlugin() : this(new HttpWebProvider(), ownsProvider: true) { }

    public WebPlugin(IWebProvider provider) : this(provider, ownsProvider: false) { }

    public WebPlugin(IWebProvider provider, bool ownsProvider)
    {
        _provider = provider;
        _ownsProvider = ownsProvider;
    }

    protected override Task ApplyAsync(HarnessContext ctx)
    {
        WebRuntime.Mount(ctx, _provider);
        var tools = ctx.Get<ToolRuntime>("tools");
        ctx.Effect(tools.Register(new WebSearchTool(_provider)).Dispose);
        ctx.Effect(tools.Register(new WebFetchTool(_provider)).Dispose);
        if (_ownsProvider) ctx.Effect(() => (_provider as IDisposable)?.Dispose());
        return Task.CompletedTask;
    }
}
