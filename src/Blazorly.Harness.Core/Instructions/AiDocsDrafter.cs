using System.Text;
using Blazorly.Harness.Kernel;
using Blazorly.Harness.Llm;

namespace Blazorly.Harness.Core.Instructions;

/// <summary>
/// LLM-backed AGENTS.md drafting. The deterministic repo brief is the only grounding the
/// model gets; every draft passes through <see cref="DocsInitService.VerifyContent"/> and a
/// draft that invents paths gets one correction retry before being returned with its
/// remaining unresolved references flagged.
/// </summary>
public static class AiDocsDrafter
{
    public sealed record DraftedDoc(string RelativePath, string FullPath, string Content, bool Exists, IReadOnlyList<DocsInitService.StaleReference> Unresolved);
    public sealed record DraftResult(string Root, string Model, IReadOnlyList<DraftedDoc> Docs);
    public sealed record AiApplyResult(string Root, string Model, IReadOnlyList<string> Written, IReadOnlyList<string> Unchanged, IReadOnlyList<DocsInitService.StaleReference> Unresolved);

    public const string SystemPrompt =
        "You write AGENTS.md project-instruction files for AI coding agents. Grounding rules: " +
        "reference ONLY paths, files, commands, and dependencies that appear in the brief; never invent " +
        "architecture, databases, services, or files. If the brief lacks information for a section, omit " +
        "the section. Output ONLY the markdown document: no code fences, no preamble, no commentary.";

    /// <summary>Production completion seam over the adapter registry; throws LlmException on error finish.</summary>
    public static Func<string, CancellationToken, Task<string>> CompleteWith(
        LlmRuntime llm, string provider, string model, string? reasoningEffort = null)
    {
        return async (prompt, ct) =>
        {
            var options = new GenerateOptions
            {
                Provider = provider,
                Model = model,
                Messages = [Message.CreateUserText(prompt)],
                System = SystemPrompt,
                Temperature = 0.2,
                MaxTokens = 4096,
                Purpose = "docs-init",
                ReasoningEffort = reasoningEffort,
            };
            var text = new StringBuilder();
            await foreach (var chunk in llm.Stream(options, ct).ConfigureAwait(false))
            {
                if (chunk is TextDeltaChunk delta) text.Append(delta.Text);
                else if (chunk is FinishChunk finish && finish.Reason == FinishReason.Error)
                    throw new LlmException(finish.Failure ?? new LlmFailure("docs draft failed", LlmErrorCodes.Transport));
            }
            return text.ToString();
        };
    }

    public static async Task<DraftResult> DraftAsync(
        string root, int depth, string model, Func<string, CancellationToken, Task<string>> complete, CancellationToken ct = default)
    {
        var brief = DocsInitService.BuildBrief(root, depth);
        var full = brief.Root;
        var docs = new List<DraftedDoc>();
        foreach (var dir in DocsInitService.WalkDirs(full, depth))
        {
            ct.ThrowIfCancellationRequested();
            var isRoot = string.Equals(dir, full, StringComparison.Ordinal);
            var fullPath = Path.Combine(dir, "AGENTS.md");
            var content = Normalize(await complete(BuildPrompt(brief.Text, dir, full, isRoot), ct).ConfigureAwait(false), isRoot);
            var unresolved = DocsInitService.VerifyContent(dir, fullPath, content);
            if (unresolved.Count > 0)
            {
                content = Normalize(await complete(BuildRetryPrompt(brief.Text, content, unresolved), ct).ConfigureAwait(false), isRoot);
                unresolved = DocsInitService.VerifyContent(dir, fullPath, content);
            }
            var relative = Path.GetRelativePath(full, fullPath);
            docs.Add(new DraftedDoc(relative, fullPath, content, File.Exists(fullPath), unresolved));
        }
        return new DraftResult(full, model, docs);
    }

    public static async Task<AiApplyResult> DraftAndApplyAsync(
        string root, int depth, string model, Func<string, CancellationToken, Task<string>> complete,
        bool force = false, CancellationToken ct = default)
    {
        var draft = await DraftAsync(root, depth, model, complete, ct).ConfigureAwait(false);
        var written = new List<string>();
        var unchanged = new List<string>();
        var unresolved = new List<DocsInitService.StaleReference>();
        foreach (var doc in draft.Docs)
        {
            var content = doc.Exists && !force ? DocsInitService.MergeManual(doc.FullPath, doc.Content) : doc.Content;
            if (doc.Exists && !force && DocsInitService.ContentsEqual(doc.FullPath, content))
            {
                unchanged.Add(doc.RelativePath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(doc.FullPath)!);
                await File.WriteAllTextAsync(doc.FullPath, content, DocsInitService.FileEncoding, ct).ConfigureAwait(false);
                written.Add(doc.RelativePath);
            }
            unresolved.AddRange(doc.Unresolved);
        }
        return new AiApplyResult(draft.Root, model, written, unchanged, unresolved);
    }

    private static string BuildPrompt(string brief, string dir, string root, bool isRoot)
    {
        var name = Path.GetFileName(isRoot ? root : dir);
        var role = isRoot
            ? "the workspace ROOT index: map the whole repo, one table row per top-level area"
            : $"a module doc for `{Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/')}/`";
        var sections = isRoot
            ? "## Purpose (2-3 sentences grounded in the brief), ## Key Files (table, brief paths only), " +
              "## Subdirectories (table with what each area holds), ## For AI Agents (ONLY the brief's verified commands)"
            : "## Purpose (2-3 sentences), ## Key Files (table, brief paths only), " +
              "## For AI Agents (ONLY the brief's verified commands for this directory, if any)";
        return $"""
            Repository brief (every path below is real; invent nothing outside it):

            {brief}

            Write the AGENTS.md for {role}. Title it `# {name}`.
            Required sections: {sections}.
            Keep it under {(isRoot ? "5000" : "3500")} characters. Tables over prose.
            """;
    }

    private static string BuildRetryPrompt(string brief, string draft, IReadOnlyList<DocsInitService.StaleReference> unresolved)
    {
        var refs = string.Join("\n", unresolved.Select(u => $"- `{u.Reference}` (line {u.Line})"));
        return $"""
            Your draft referenced paths that do not exist:

            {refs}

            Rewrite the document using ONLY paths from this brief (remove or replace the bad references):

            {brief}

            Draft to fix:

            {draft}

            Output only the corrected markdown document.
            """;
    }

    /// <summary>Strips fences/preamble and stamps the header/footer deterministically.</summary>
    internal static string Normalize(string raw, bool isRoot)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            text = firstBreak >= 0 ? text[(firstBreak + 1)..] : "";
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text[..fence];
            text = text.Trim();
        }
        var head = new StringBuilder();
        if (!isRoot) head.AppendLine("<!-- Parent: ../AGENTS.md -->");
        head.AppendLine($"<!-- Generated: {DateTime.UtcNow:yyyy-MM-dd} by blazorly init -->");
        head.AppendLine();
        head.Append(text);
        var merged = head.ToString().TrimEnd() + "\n";
        if (!merged.Contains(DocsInitService.ManualMarker, StringComparison.Ordinal))
            merged += "\n" + DocsInitService.ManualMarker + " Custom project notes below this line are preserved on regeneration -->\n";
        return merged;
    }
}
