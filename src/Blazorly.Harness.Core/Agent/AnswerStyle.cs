namespace Blazorly.Harness.Core.Agent;

/// <summary>
/// How final answers are written for the reader. Most harness users are not engineers, so the
/// default (<see cref="Layered"/>) puts a short plain-language result first and keeps exact
/// technical detail in a clearly marked section below it. The style is a global host setting
/// (Settings → Answers, persisted as <c>answerStyle</c> in settings.json) and reaches the model
/// as its own system-prompt section, so it still applies when a workspace replaces the built-in
/// instruction body with custom instructions.
/// </summary>
/// <remarks>
/// The plain-language rules adapt ASD-STE100 Simplified Technical English (short sentences,
/// active voice, one idea per sentence, no slang or metaphor) without its strict dictionary:
/// file names, commands, and error text must stay exact, so precision lives in the technical
/// section instead of being simplified away.
/// </remarks>
public static class AnswerStyle
{
    /// <summary>Plain language only; technical detail only when the user asks for it.</summary>
    public const string Simple = "simple";
    /// <summary>Plain result first, exact technical detail below. The default.</summary>
    public const string Layered = "layered";
    /// <summary>The engineer report format (summary, implementation, verification, risks).</summary>
    public const string Technical = "technical";

    public static readonly IReadOnlyList<string> All = [Simple, Layered, Technical];

    /// <summary>Unknown, empty, or misspelled values fall back to <see cref="Layered"/>.</summary>
    public static string Normalize(string? style) => style?.Trim().ToLowerInvariant() switch
    {
        Simple => Simple,
        Technical => Technical,
        _ => Layered,
    };

    private const string PlainRules =
        """
        Plain-language rules for every user-visible sentence, including short progress updates while you work:
        - Write short sentences (20 words or fewer). Put one idea in each sentence.
        - Use the active voice: "I changed the login page", not "The login page was changed".
        - Use common words. If you must use a technical word, explain it in the same sentence the first time.
        - Do not use slang, metaphors, abbreviations, or jargon without an explanation.
        - Put steps and lists in bullet points or numbered steps, not in long paragraphs.
        - Say the result first. Say clearly if work is done, partly done, or failed. Never write that something worked when it did not.
        - Keep file names, commands, and error messages exact. Do not simplify or paraphrase them.
        """;

    private const string FormatGuard =
        "Exception: when a tool, schema, or delegating agent requires an exact machine-readable format (for example a JSON result for a parent agent), produce exactly that format and skip the answer structure below.";

    private const string DiagramRule =
        """
        Diagrams: when you explain a process with three or more steps, or a choice between paths, add one small diagram in a ```mermaid fenced block (flowchart TD). Use short, plain labels inside the diagram. The app draws the diagram as a picture in the chat. Add at most one diagram per answer, and skip it for simple answers.
        """;

    /// <summary>The system-prompt section text for one style. It is worded to win over any
    /// response-format instruction in the body above it, because a workspace's custom
    /// instructions replace that body but never this section.</summary>
    public static string SectionText(string? style) => Normalize(style) switch
    {
        Simple =>
            $"""
            ## How to write your answers (answer style: simple)

            This section overrides any response-format instruction above. The reader is not an engineer. {FormatGuard}

            {PlainRules}

            Answer structure:
            1. Start with the result in one to three short sentences under the heading **What happened**.
            2. If the user must do something, add **What you need to do** with numbered steps.
            3. Do not add a technical-details section (file lists, test output, design notes) unless the user asks for it. Exception: if something failed, give the exact error message in one short line so the user can report it.

            {DiagramRule}
            """,
        Technical =>
            $"""
            ## How to write your answers (answer style: technical)

            This section overrides any response-format instruction above. The reader is an engineer. {FormatGuard}

            Structure implementation answers as: Summary → Implementation (files and design decisions) → Verification (tests and checks you actually ran) → Risks & Limitations → Next Steps (only genuinely required follow-ups). For planning: Problem → Design → Decisions/Trade-offs → Implementation Steps → Verification → Risks. For debugging: Problem → Evidence/Root Cause → Fix → Verification → Remaining Uncertainty. Report exactly what you implemented and what you verified; never claim a test passed unless it completed successfully. Keep sentences direct and specific even in this style.

            {DiagramRule}
            """,
        _ =>
            $"""
            ## How to write your answers (answer style: plain first, details below)

            This section overrides any response-format instruction above. Most readers are not engineers, so every answer has two layers: a plain result first, then exact technical detail for anyone who wants it. {FormatGuard}

            {PlainRules}

            Answer structure:
            1. Start with **What happened**: the result in one to three short, plain sentences. A non-technical reader must understand the outcome from this part alone and can stop reading here.
            2. If the user must do something next, add **What you need to do** with numbered steps in plain language.
            3. End with **Technical details** only when there is real detail to report: files changed, tests and checks you actually ran and their results, exact errors, risks, and limits. Keep this part exact; do not move file names, commands, or error text into the plain part unless the reader needs them there.
            For a short question or a small task, the plain part alone is enough — do not add empty sections.

            {DiagramRule}
            """,
    };
}
