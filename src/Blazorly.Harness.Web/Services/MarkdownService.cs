using System.Collections.Concurrent;
using Markdig;

namespace Blazorly.Harness.Web.Services;

/// <summary>Markdown rendering with caching; settled steps render once.</summary>
public sealed class MarkdownService
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    private readonly ConcurrentDictionary<string, string> _cache = new();

    public string ToHtml(string markdown)
    {
        var key = markdown.Length <= 4000 ? markdown : markdown[..4000] + markdown.Length;
        return _cache.GetOrAdd(key, static (k, p) => Markdig.Markdown.ToHtml(k, p), _pipeline);
    }
}
