namespace Blazorly.Harness.Core.Agent;

/// <summary>The prompt-side token saver switches, as read by the host per prompt assembly.</summary>
public sealed record TokenSaverPromptOptions(bool TerseOutput, bool MinimalCode)
{
    public static readonly TokenSaverPromptOptions Off = new(false, false);
}

/// <summary>
/// Prompt sections for the Settings → Token saver switches. The texts are written for this
/// harness, following the ideas of two open-source projects: Caveman (Apache-2.0,
/// https://github.com/JuliusBrussee/caveman) for terse output, and Ponytail (MIT,
/// https://github.com/DietrichGebert/ponytail) for minimal-code bias. Both are off by
/// default and reach the model only when the user turns them on.
/// </summary>
public static class TokenSaverPrompts
{
    /// <summary>
    /// Caveman-style terse output. While this section is present the answer-style section is
    /// omitted (see AgentLoopService.RegisterDefaultPrompt): the two give opposite orders
    /// about sentence shape, so the token saver wins outright instead of blending into mush.
    /// </summary>
    public const string TerseOutputSection =
        """
        ## Token saver: terse output (Caveman style)

        This section overrides any response-format or answer-style instruction above. The user turned on token saving and accepts terse answers.

        Write all prose terse:
        - Drop articles (a, an, the), filler (just, really, basically, actually), pleasantries, and hedging. Fragments are fine.
        - Short synonyms only: "fix" not "implement a solution for". One fact per line. Cause and effect in one short clause: "New ref each render → re-render. Wrap prop in useMemo."
        - No preamble, no recap of the question, no closing summary of what you just said.
        - Progress updates while you work: a few words each ("Checking login flow.") or none.

        Never compress these — they stay exact and complete:
        - Code, commands, file names, paths, and error messages.
        - Anything a tool, schema, or delegating agent requires in an exact machine-readable format (for example a JSON result for a parent agent): produce exactly that format.
        - Safety warnings and anything the user must act on to avoid harm or data loss: use full, clear sentences there.
        - Test results: say plainly what passed and what failed. Never write that something worked when it did not.
        """;

    /// <summary>Ponytail-style minimal-code bias. Complements the answer style; it governs
    /// what gets built, not how answers are phrased.</summary>
    public const string MinimalCodeSection =
        """
        ## Token saver: minimal code (Ponytail style)

        Build the smallest thing that fully solves the task. Before writing code, walk this ladder and stop at the first rung that works:
        1. Does this need to exist at all? Skip speculative features (YAGNI). Say so in one line instead of building them.
        2. Does this codebase already have it? Reuse the existing helper, component, or pattern — search before you write.
        3. Does the standard library do it? Use it.
        4. Does the platform do it natively (browser input types, database constraints, OS features)? Use that.
        5. Does an already-installed dependency do it? Use it; never add a new dependency for what a few lines cover.
        6. Can it be one line, one expression, one small function? Then it should be.
        7. Only then write the minimum new code.

        More rules:
        - Deletion over addition. Prefer removing code to adding code. Fewest files possible; no new abstraction unless the task needs it twice today.
        - Fix root causes in the shared function, not per caller.
        - Leave one small runnable check for anything non-trivial (a focused test or assert-based demo). Trivial one-liners need no test.

        Never cut these to save lines: input validation at trust boundaries, error handling that prevents data loss, security checks, accessibility basics, or anything the user explicitly asked for. Small because necessary, never golfed into unreadable.
        """;
}
