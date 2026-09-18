# A decision layer for the harness (System One / Jev-shaped)

**Status:** analysis. Companion to `per-workspace-learning.md` (which this partially supersedes —
see §8).
**Trigger:** TypeSafe AI's "System One" models (`jev`) — unstructured state in, typed calibrated
decisions out (`Choice` / `Score` / `Noul`), one parallel forward pass, 70–500 ms, no generated
text. Trained with RLCD (RL for Calibrated Decisions). Waitlisted, vendor-reported numbers.

---

## 1. The actual opportunity has nothing to do with the vendor

Restated in harness terms, Jev is: *a cheap, fast, calibrated function from context to enum*.
The interesting question is not "should we buy it" — it is **"where does the harness currently
make a semantic judgment with a regex, a counter, a boolean, or nothing at all?"**

I went looking. There are nine such places, and several are load-bearing product weaknesses.

| # | Seam | What it does today | Code | Decision shape |
|---|---|---|---|---|
| 1 | **Tool risk gating** | Binary. `ask` routes **every** tool call through approval; otherwise auto-allow. No middle ground. | `Core/Tools/ToolPolicyService.cs` (`_askEveryTool` is a `HashSet<string>` of agent ids) | `Noul` — "could this destroy or expose data?" |
| 2 | **Auto-plan engagement** | 6 hand-tuned regex/keyword rules summing to 0–100, threshold 55 | `Tools/AutoPlanPlugin.cs` → `ComplexityScorer.Score` | `Noul` — "does this brief need a plan first?" |
| 3 | **Loop detection** | **Exact** string match on `name + raw args`, streak ≥ 3 | `Core/Guards/RepeatCallGuard.cs` | `Score` — "is the agent making no progress?" |
| 4 | **Compaction pruning** | Size + position only: `if (text.Length <= PrunerChars) continue` | `Core/Compaction/CompactionService.cs:148` | `Score` per block — "how load-bearing is this?" |
| 5 | **Goal round continuation** | Hard counter: `RoundsStarted >= MaxRounds` (default 5) | `Tools/GoalService.cs:297` | `Noul` — "is the objective actually met?" |
| 6 | **Model routing** | Static `provider`/`model` per session | `settings.json`, `Llm/LlmRuntime.cs` | `Choice` — which route for this step |
| 7 | **Retrieval reranking** | FTS5 lexical match, `LIMIT 20`, no ranking | `Core/Session/SessionSearchIndex.cs`, `Context/FileReferences.cs` | `Score` over candidates |
| 8 | **Edit verification** | Nothing. The generator self-reviews. | — | `Score` — "does this diff resolve the issue?" |
| 9 | **Steer classification** | Nothing. A queued message is just a message. | `Core/Agent/Inbox.cs` | `Choice` — correction / new task / clarification |

Only **two** auxiliary LLM calls exist in the entire harness today:
`Purpose = "compaction"` and `Purpose = "session-title"` (`Llm/GenerateOptions.cs:18`).
Everything else in that table is regex, counters, or absent. That is the gap.

**The single highest-value one is #1, and here is the evidence:** your own
`~/.blazorly/settings.json` has `"sandboxMode": "danger-full-access"`. Not because you want
unsandboxed writes — because ask-every-tool is unusable for real work, so the only two settings
are "unusable" and "unsafe". A calibrated risk gate is what makes a *third* setting possible:
auto-allow the confident 95%, park the rest. That is a product feature, not an optimisation.

---

## 2. Four hard constraints from your own architecture

These are non-negotiable and they rule out the naive integration sketch in the TypeSafe post.

**(a) The interruption contract.** Your paper is literally *"Interruption as a First-Class
State"* and §4.2 measures cancel-propagation latency. `agent/pre-step` and `tools/pre-execute`
are waterfalls that run **before** the model call and can block the turn. Putting a 70–500 ms
network await there puts it inside the measured cancel path. Any decision call must:
- take the ambient `CancellationToken` and actually observe it,
- have its own hard timeout (≤ 500 ms, mirroring `HooksService.TimeoutMs = 5_000` but tighter),
- be counted in the cancel-latency metric, not hidden from it.

**(b) Fail-closed philosophy.** Landlock "fails closed rather than run unsandboxed" (README).
A probabilistic gate that fails **open** on timeout contradicts the harness's core safety
stance. Resolution: **the deterministic policy stays authoritative; the decision model can only
make things stricter, never looser.**

```
final = deterministicPolicy(call)                    # authoritative, unchanged
if final == Allow && jev.risk > threshold:
    final = Ask                                    # escalate only
```

Jev can turn an allow into an ask. It can never turn an ask into an allow. Write this as an
invariant and test it.

**(c) Attributability.** `EvalRunner` records a `toolSchemaHash` per backend precisely so "a
score change is attributable." A stochastic gate in the loop destroys that unless every decision
is durable. So: a new session event type, e.g.

```
decision/result  { key, kind, question, options, probabilities, chosen,
                   latencyMs, impl, modelVersion, stateHash, timedOut }
```

with `Ignorable = true` (the log already supports ignorable plugin events — see the
`SubagentDescriptor` comment in `SessionEvent.cs`). Then eval can pin `impl: "heuristic"` and
scores stay comparable, and you get a free dataset of every decision the harness ever made.

**(d) Testability.** `ComplexityScorer` is documented as *"pure and deterministic on purpose:
the auto-plan decision must be free, instant, and reproducible in tests."* Do not replace it.
Put it **behind** an interface as the default implementation. Tests keep passing, the heuristic
stays the fail-open fallback, and the model becomes an opt-in upgrade.

---

## 3. Recommendation: build the seam, ship three implementations

Do not integrate Jev. Integrate `IDecisionModel`, and make Jev implementation #3 when access lands.

```csharp
// Core/Decisions/IDecisionModel.cs
public abstract record DecisionQuestion(string Key, string Description);
public sealed record Noul(string Key, string Description) : DecisionQuestion;         // P(yes)
public sealed record Choice(string Key, string Description, IReadOnlyList<string> Options);
public sealed record ScoreQ(string Key, string Description, int Min, int Max);

public sealed record DecisionResult(/* key → probability | choice | score */, long LatencyMs, string Impl);

public interface IDecisionModel
{
    string Impl { get; }                                        // "heuristic" | "llm" | "systemone"
    Task<DecisionResult> DecideAsync(
        DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct);
}
```

Mounted as `ctx.Provide("decisions", …)`, injected by plugins that need it. Three impls:

| Impl | Cost | Latency | Available | Purpose |
|---|---|---|---|---|
| **Heuristic** | free | ~0 | today | default, fallback on timeout, unit tests, eval pinning |
| **LLM structured output** | real tokens | 1–4 s | **today** | establishes the contract and the accuracy baseline using routes you already have (`glm-4.5-air`, `deepseek-flash`, `qwen3.6-flash`) + `ToolParameterSchemas.cs` |
| **System One / Jev** | ~free output | 70–500 ms | waitlist | the thing you're evaluating |

**Why the LLM impl matters more than it looks:** it lets you answer the only question that
actually decides the purchase. You have ~9 MB of real session logs. Run all three impls offline
over historical decision points where you *know* the answer (a turn the user interrupted, a tool
call that errored, a brief that should have planned) and you get:

- accuracy delta: Jev vs. structured-output-LLM vs. heuristic
- latency delta: measured on your states, not their workflow evals
- cost delta **as a function of state size** — the headline "445x cheaper" is per *output* token;
  your state is a 40 KB diff plus repo context, and input tokens are the ones you pay for

If a $0.0002 structured-output call gets you 95% of the accuracy, you've saved a vendor
dependency and a network hop in your cancel path. If Jev is 20 points more accurate at 1/400th
the cost, that's a real finding with your own data behind it. Either way you win — and you can
write it up.

**One call, many questions.** The parallel-questions property is the genuinely novel bit and you
should design for it: batch *all* pre-execute questions (destructive? reversible? in scope?
needs tests? out of policy?) into a single `DecideAsync` with 5 questions against one state
upload. That amortises state cost 5×, which is where their economics actually come from.

---

## 4. Ranked build order

Value ÷ (effort × risk), given the constraints above.

**Tier A — do these first**

1. **Risk-aware approvals (#1).** Biggest product win, and safe by construction because of the
   escalate-only invariant (§2b). Replaces the unusable binary. Ship behind a new permission
   preset `ask-on-risk` with a configurable threshold, defaulting to the heuristic impl.
2. **Semantic loop detection (#3).** `RepeatCallGuard` misses the common case entirely —
   `read a.ts` → `grep foo` → `read a.ts` → `read b.ts` → `read a.ts` has no exact-match streak.
   Low risk (advisory `Inbox.Insert`, exactly as today), high value, and it's already wired into
   `tools/result`.
3. **Compaction pruning priority (#4).** Context is your scarcest resource (`contextWindowTokens`
   256k, `compactionThreshold` 0.9). Today pruning is size+position — it can delete the one tool
   result the current plan depends on and keep 4 KB of stale `ls` output. Runs rarely (only at
   pressure), so 500 ms is free. Note: Jev can *rank* what to prune but cannot write the
   `Purpose = "compaction"` summary — that stays generative.

**Tier B — measure first**

4. **Goal continuation (#5).** A hard 5-round cap is both wasteful (stops when close) and blind
   (burns rounds when done). A `Noul` on "objective met" is a clean fit.
5. **Auto-plan (#2).** Clearest conceptual fit, but it fires on *every* fresh user turn, so it's
   the most latency-sensitive seam you have. Only worth it if the accuracy gain over
   `ComplexityScorer` is large — and you can measure that offline from logs, since every
   engagement is recorded with its score and reasons.
6. **Edit verification (#8).** High value, but this is the reward-hacking-adjacent one and it
   must never be the sole gate. Use it to *route* (cheap verifier says "uncertain" → escalate to
   frontier model), not to *decide*.

**Tier C — later**

7. **Model routing (#6).** Real money, but needs the telemetry baseline first and interacts with
   cache-hit rates (`CacheReadTokens` in `UsageTelemetryService`) — switching models mid-session
   can destroy prompt caching and cost more than it saves. Measure, don't assume.
8. **Retrieval reranking (#7).** Two-stage required: FTS5/embeddings to ~100 candidates, then
   `Score`. The 255-option cap means you cannot rank a large repo's file list directly.
9. **Steer classification (#9).** Useful for the learning loop, low standalone value.

---

## 5. Where it does not fit — say this out loud

- **Anything that must produce prose.** Session titles, compaction summaries, plan text,
  commit messages, the agent's replies. Jev generates nothing. `SessionTitleService` and the
  compaction summariser stay generative. (At most: generate N candidate titles, `Choice` the
  best — marginal.)
- **Code generation, planning, open-ended reasoning.** Not a generator, no chain-of-thought.
- **Sole authority on a destructive action.** §2b. Calibrated ≠ correct; a confidently wrong
  `Noul` on `rm -rf` is worse than no gate, because it manufactures trust.
- **Vision.** Text-only today, so it cannot gate on screenshots — which matters for your
  `web-perf` skill and the `AttachmentService` image path.
- **Every step of every turn.** Batch and sample. A 400 ms gate on 159 steps is a minute of
  pure latency added to one session (that's the real step count in `sessions/thrivv`).
- **Safety-critical without measured FNR.** For a risk gate the error that matters is the false
  negative. Build a labelled set of "looks safe, isn't" cases and report FNR before trusting any
  threshold. Your eval harness can host these as tasks.

---

## 6. The `state` design problem (under-discussed in the pitch)

"Quality depends on how well you build the state" is doing enormous work in that sentence. For
the harness, the natural state per seam:

| Seam | State | Approx size |
|---|---|---|
| Tool risk | command + cwd + `ToolCallView` + permission preset + last 3 tool results | 1–5 KB |
| Loop detection | last N `(tool, argsSummary, resultSummary)` triples + goal | 2–8 KB |
| Compaction | per-block `{role, toolName, chars, age, truncatedHead}` — **not** full bodies | 5–20 KB |
| Auto-plan | the brief + workspace file tree summary + `AGENTS.md` | 2–10 KB |
| Edit verification | the diff + the issue text + touched file heads | **10–200 KB** |

Note the last row. Edit verification is the highest-value seam and the most expensive state —
which is exactly where the "445x cheaper" claim is least likely to hold, because you're paying
input tokens on a 200 KB diff. **Cost must be reported per seam, not as a global multiplier.**

Design rules:
- Never put full file bodies in state when a summary will do (the compaction seam especially —
  rank *metadata*, then prune bodies).
- Include `stateHash` in the durable event so replays are reproducible.
- Redact before sending. Session state contains tool output that may include `.env` reads,
  `env` dumps, and credentials — and this is a **third-party cloud API**. Same redaction pass
  flagged in `per-workspace-learning.md` §8; here it's a hard prerequisite, not a nice-to-have.
  (Your `settings.json` currently holds live provider keys in plaintext — rotate them.)

---

## 7. Measurement plan

Reuse what exists rather than building new instrumentation.

1. **Offline replay over existing logs** — `sessions/thrivv` (35,218 events, 177 tool calls,
   159 steps, 6 turns, 10 retries) and `sessions/demo1`. Every seam in §1 has a ground-truth
   proxy already in the log: a tool call followed by `isError`, a turn ending `Interrupted`, a
   plan that engaged at score 61. Label ~200 decision points by hand. This is your benchmark and
   it costs nothing.
2. **Eval tasks** — add `eval/tasks/decision-*` with adversarial cases
   (`rm -rf` disguised, safe-looking force-push, a brief that reads simple but isn't). Scored by
   the existing shell-check runner.
3. **Cancel-latency regression** — re-run the paper's §4.2 measurement with the decision layer
   on. If p99 cancel latency moves, that's a blocking regression.
4. **A/B in product** — the escalate-only invariant makes this safe: run `ask-on-risk` at
   threshold 0.5 vs. 0.3 vs. heuristic-only, count approvals-parked per session and
   false-allows-that-errored.

Report as: accuracy / FNR / p50+p99 latency / cost-per-1k-decisions, **per seam**, for each of
the three impls.

---

## 8. Relationship to the RL question you asked earlier

This reframes it usefully. RLCD — *RL for Calibrated Decisions* — is the same family as the
"RLDC" you asked about, and it points at a better answer than the one I gave.

The earlier doc's conclusion was: you can't do parametric RL because you don't own the weights,
so you're stuck with non-parametric policy artifacts. The decision layer changes that:

- **TypeSafe owns the weights and does the RLCD.** You supply trajectories and get calibrated
  decisions back. The RL happens on their side of the API.
- **Your harness becomes the environment.** Append-only logs + eval checks + fork-replay is
  exactly the substrate an RLCD trainer needs — and they explicitly haven't disclosed "the
  training trick for generating questions from trajectories." You have 9 MB of real trajectories
  and a verifier. That's a partnership-shaped asset, not a customer-shaped one.
- **The per-workspace angle survives:** decisions are conditioned on `state`, and state includes
  the workspace. A per-workspace learned policy can be expressed as *per-workspace question
  sets and thresholds* rather than per-workspace artifacts — much cheaper than synthesising
  `AGENTS.local.md`, and it can't blow the 24k instruction budget.

So: the decision layer is the near-term, buildable version of the learning story. Do this first.

---

## 9. Immediate next actions

> **Status update — steps 1–4 are implemented.** See §10.

1. ~~**Add `Core/Decisions/IDecisionModel.cs` + `HeuristicDecisionModel`**~~ — done.
2. ~~**Add the `decision/result` session event type**~~ — done.
3. **Add `LlmDecisionModel`** using `Purpose = "decision"` and your existing cheap routes +
   `ToolParameterSchemas`. Now you have a working semantic gate today, no waitlist. *Not built —
   the seam is in place, so this is one class implementing `IDecisionModel`.*
4. ~~**Ship `ask-on-risk`**~~ — shipped as `enableRiskGate`, escalate-only, invariant tested.
5. **Apply for Jev early access** and run the §7 comparison with `blazorly decisions probe`.
6. **Write the redaction pass** before any state leaves the machine, and rotate the plaintext
   provider keys in `~/.blazorly/settings.json`. *Still outstanding — this is now the gating item
   for turning System One on against real workspaces.*

---

## 10. What was built

| Piece | Path |
|---|---|
| Decision contract (`Noul` / `Choice` / `Rating`, `DecisionState`, `IDecisionModel`, `NoOpDecisionModel`, seam names) | `Core/Decisions/DecisionModel.cs` |
| System One wire adapter (tolerant parsing, hard timeout, cancel-aware) | `Core/Decisions/SystemOneClient.cs` |
| Service: seam gating, TTL cache, durable events, stats, never throws | `Core/Decisions/DecisionService.cs` |
| Risk gate (escalate-only invariant) | `Core/Decisions/RiskGatePlugin.cs` |
| Auto-plan seam (calibration bands, abstain-to-heuristic) | `Tools/AutoPlanPlugin.cs` |
| Settings + composition | `Web/Services/HarnessBootstrapper.cs` |
| Settings UI (Capabilities tab) | `Web/Components/Pages/Settings.razor` |
| `GET /api/decisions` health + cost | `Web/UiHost.cs` |
| `blazorly decisions doctor` / `probe` | `Cli/DecisionsCommand.cs` |
| 42 tests | `tests/Blazorly.Harness.Tests/DecisionTests.cs` |

Supporting changes: `ApprovalService.CanAsk` (so an advisory escalation can't break headless
runs), and `AutoPlanPolicy` split into `EligibleBrief` / `FollowsApprovedPlan` / `ShouldEngage`
so a model can replace the *scoring* without touching the structural guards.

### Two real bugs the tests caught

1. **A user cancel was being swallowed as a timeout.** `TaskCanceledException` derives from
   `OperationCanceledException`, so the catch-all for transport errors ate cancellations too —
   an invisible wait inside the measured cancel path, which the interruption contract forbids.
   Now rethrown explicitly.
2. **`decisions probe` did not send what the live seam sends.** `ProbeAsync` used `StringContent`
   (explicit `Content-Length`) while `DecideAsync` used `PostAsJsonAsync`, which streams a
   `JsonContent` as *chunked*. Servers that only read `Content-Length` saw an empty body — so the
   probe reported success against an endpoint the real seam could not talk to. Both paths now
   share one serializer, and a test asserts the bytes are identical *and* that Content-Length is
   declared. This is why the probe is worth having.

### Verified end-to-end

Against a local fake endpoint returning `P(needs_plan)=0.97` for the trivial brief
*"write hello.txt containing hi"* (heuristic score ≈ 0):

- **System One on** → plan mode engaged, agent presented a plan and mutated nothing; the session
  log carries `decision/result {seam: auto-plan, impl: systemone, latencyMs: 17, stateChars: 37,
  degraded: false, answers: {needs_plan: 0.97}, ignorable: true}`.
- **System One off** → same brief wrote `hello.txt` and reported success. Byte-for-byte the
  pre-existing behaviour.

Full suite: 474 passed, 2 failed — both pre-existing Landlock confinement tests that require
Linux (this host is macOS); confirmed failing on the unmodified tree.

### Still open before trusting it on real work

1. **The wire format is a guess.** System One is waitlisted and the schema isn't public. Run
   `blazorly decisions probe` with the real key and reconcile `SystemOneClient.BuildRequest` /
   `TryReadAnswer` against the reply. Everything else is insulated from that.
2. **Redaction.** State currently includes raw tool arguments and briefs, which can contain
   secrets, and this is a third-party API. Nothing should be enabled on a real workspace until
   the redaction pass exists.
3. **No accuracy data yet.** The seam is built; the §7 offline replay over `~/.blazorly/sessions`
   that would tell you whether the model beats the heuristic is not.

