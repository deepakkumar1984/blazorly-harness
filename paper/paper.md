# Interruption as a First-Class State in a Local Agent Harness

*Draft — measurements from the blazorly-harness artifact tree (arm64 Linux VM, .NET 10,
September 2026). All numbers are reproducible: `dotnet test --filter Category=Benchmark`,
`dotnet run --project src/Blazorly.Harness.Cli -- eval --tasks eval/tasks` against a fake
OpenAI-compatible server, and `python3 scripts/study-restart-ux.py`.*

## Abstract

Interactive coding agents run long, multi-step turns whose work is valuable while it is
in flight: partial reasoning, half-finished tool calls, queued follow-ups. Most harness
engineering treats interruption as an error path — something that happens to a turn
rather than a state of it. This paper describes the design of a local agent harness in
which interruption is a first-class, durable state, and reports measurements of what that
costs and what it buys. The harness keeps an append-only event log as the single source
of truth; every turn ends durably with an explicit reason; cancelled tool calls close
with durable results; partial assistant output is committed rather than discarded; and a
process killed mid-turn leaves a log that reloads with in-memory repair and resumes.
On this design, a user stop becomes durable in 0.3–227 ms depending on phase (median
0.3 ms mid-stream, 148 ms inside a process-tree kill), a 132,010-event production session
cold-replays in 1.29 s, its full-text search index rebuilds in 42 ms, and in an 8-trial
crash pilot every killed session explained itself on reload and resumed to completion.
We also quantify an asymmetry we argue is a latent spec bug in OpenAI-compatible
clients: cancelling mid-SSE-read surfaces as a terminal stream *error* rather than an
*abort*, conflating user intent with provider failure.

## 1. Introduction

An agent turn is not a transaction. It is a stream of durably-logged facts — user
messages, assistant chunks, tool calls and results — interleaved with live processes
(shells, file edits, HTTP streams). Users interrupt constantly: they hit Stop when a
model goes the wrong way, their editor kills the agent process, their laptop sleeps.
Three properties decide whether that experience is tolerable:

1. **Nothing is lost that does not need to be.** Partial output the user saw should stay
   in the transcript; work that completed (a file written, a command run) stays done.
2. **The log never lies.** Whatever the interruption, the durable record is consistent
   enough to reload, and a follow-up message continues from a truthful state.
3. **Interruption is fast.** The gap between a stop and a settled, usable session is
   small enough that the user's next action is never blocked.

We call the union of these the **interruption contract**. This paper describes a harness
built around it and measures it. The contributions are (a) the contract itself, stated as
checkable invariants; (b) deterministic, scored evaluation tasks that assert the contract
under user cancel, watchdog timeout, and process kill; (c) latency/throughput
measurements of interruption and recovery at realistic log sizes, including a
132K-event production session; and (d) a crash-recovery UX pilot.

## 2. System overview

The harness is a .NET 10 application with a Blazor Server UI, a CLI (headless runs,
JSON-RPC and ACP servers for editors), and a kernel/plugin core. Components mount
through a plugin host whose ordering derives from declared service dependencies; the
same mechanism loads third-party assemblies. Beyond the kernel, the parts that matter
here are:

- **The session log.** One JSON-lines file per session (`session.jsonl`), append-only,
  never rewritten. Line 1 is a header; every following line is an event
  (`turn/start`, `step/start`, `assistant/chunk`, `tool/call`, `tool/result`,
  `turn/end`, …) with a contiguous sequence number. A torn final line — a process
  killed mid-append — is discarded on load.
- **The surface projection.** Model-visible history is *derived* from the log: an
  ordered list of `user/message`, `assistant/message`, and `tool/result` events.
  Compaction rewrites the surface by *replacing* ranges; the log keeps everything.
- **The driver.** Each turn claims inbox messages, assembles prompts, streams LLM
  chunks into durable events, dispatches tools, and writes `turn/end` with an explicit
  reason from a closed set: `completed | aborted(cause) | interrupted | error | blocked
  | max-tokens`. `aborted` carries a cause (`user | parent | disposed`); `interrupted`
  is reserved for "the process died before this turn ended."
- **Cancellation plumbing.** One `CancellationTokenSource` per agent phase, cancelled
  by `Agent.Cancel`; the token flows into the LLM stream, tool dispatch, and every tool
  body. Tools observe it; `bash` kills its process tree and reports a structured
  `aborted: true` output.

## 3. The interruption contract

Stated as invariants the implementation and tests enforce:

- **I1 — every turn ends, exactly once, with an explicit reason.** Live: the driver's
  `finally` appends `turn/end`. After a crash: reload-time repair synthesizes the
  missing `step/end` and `turn/end {interrupted}` *in memory* — the on-disk log keeps
  the honest record that the turn never ended.
- **I2 — pending tool calls are closed durably.** A cancelled tool either returns a
  structured abort (`bash`: process tree killed, `aborted: true`) or an error result
  (`ABORTED`); both are committed as `tool/result` events. A killed process leaves
  dangling calls on disk, and repair synthesizes `TOOL_OUTCOME_UNKNOWN` results so the
  next request to the model is balanced.
- **I3 — partial assistant work is preserved.** If a cancelled stream produced
  non-whitespace text or reasoning, an `assistant/message` is committed with
  `interrupted: true` before the abort propagates. The user keeps what they read; the
  model sees it as finished-but-interrupted on the next turn.
- **I4 — queued intent survives.** The inbox is durable (`agent/inbox/spliced`
  events); a cancel splices out queued items with an explicit `canceled` outcome
  unless the caller asks to keep them.
- **I5 — the log reloads and resumes.** Open → repair → derive → send: a follow-up
  message on a killed session is an ordinary turn whose history includes the repaired
  tail. Forks refuse boundaries inside an open (pre-repair) turn.
- **I6 — exit codes tell the truth.** Headless runs map finish reasons to a process
  contract: `0` completed/max-tokens, `2` error/blocked, `3` aborted/interrupted.

The contract is not free-form prose: each invariant is asserted by unit tests,
integration tests, and scored eval tasks (below).

## 4. Evaluation

### 4.1 Scored interruption evals

`eval/tasks/interrupt-*` are benchmark tasks with an expected finish and an injected
interruption, run against a fake OpenAI-compatible SSE server whose scripted flow makes
a 2.5 s `bash` window deterministic:

| Task | Injection | Expected | Checks (shell, over the durable log) |
|---|---|---|---|
| `interrupt-cancel` | user stop at 1.2 s (mid-tool) | `aborted` (exit 3) | `turn/end {aborted, user}`; every `tool/call` has a durable result; every complete line parses |
| `interrupt-timeout` | watchdog at 2 s (mid-tool) | `aborted` (exit 3) | same contract via the timeout path; turn numbering monotonic |
| `interrupt-restart` | SIGKILL after first durable `tool/call`, then resume | `completed` (exit 0) after resume | all lines parse (torn tail tolerated); turns strictly increasing; final `turn/end` completed; killed turn open on disk (`starts == ends + 1`) |

Checks receive `BLAZORLY_SESSION_LOG`/`BLAZORLY_SESSION_ID` so they assert on the log
itself. All three pass via the real CLI in a shared eval home (1.8–2.2 s wall each).
Notably, `interrupt-restart` passes only because of I2's repair half and I5; and it
initially failed in shared-home runs, exposing (and leading to fixes for) a UTF-8 BOM
glued to the first log line and a kill anchor that could satisfy itself on a sibling
task's log — the evals police the contract, not just the happy path.

### 4.2 Cancel-propagation latency

Measured per phase (in-process scripted adapter and real HTTP adapter; each measurement
also asserts the invariant it rides on):

| Phase | Median | p95 | Durable outcome |
|---|---|---|---|
| Mid-stream, in-process adapter (raw OCE path) | 0.3 ms | 6.8 ms | `aborted`; partial message committed `interrupted` (I3) |
| Mid-SSE, HTTP adapter | 1.6 ms | 26 ms | `error`/ABORTED (see 4.4) |
| Mid-tool (bash process-tree kill) | 148 ms | 227 ms | `aborted`; tool closed with structured abort (I2) |

The mid-tool number is dominated by `kill(process tree)` and the child's teardown; the
harness adds sub-millisecond overhead on top. All three are well under any
human-perceptible threshold for "the stop worked."

### 4.3 Replay, projection, and search cost vs. log size

Synthetic replay-valid logs (1K–100K events, alternating tool/no-tool turns) plus the
largest real session (132,010 events, 24 MB, production use incl. compaction):

| Log | Cold replay (OpenAsync) | DeriveMessages | stats fold cold → warm | FTS5 backfill | FTS5 query |
|---|---|---|---|---|---|
| 1,008 ev | 32 ms | 4.0 ms (180 msg) | 3.7 → 0.02 ms | 10.9 ms | 1.7 ms |
| 5,012 ev | 77 ms | 31 ms (895 msg) | 4.3 → 0.02 ms | 49 ms | 0.3 ms |
| 20,005 ev | 319 ms | 67 ms (3,572 msg) | 12.2 → 0.10 ms | 207 ms | 0.3 ms |
| 100,001 ev | 1,182 ms | 181 ms (17,857 msg) | 38.2 → 0.39 ms | 591 ms | 0.3 ms |
| **real 132,010 ev** | **1,286 ms** | **31 ms (212 msg)** | 21.8 → 0.28 ms | 42 ms | 0.2 ms |

Replay is linear at ~105K events/s; nothing about the interruption design makes log
growth pathological. The real session's derived history is 212 messages *from 132K
events* — compaction keeps the model-facing surface small while the log stays complete,
which is the whole point of deriving rather than truncating. Warm folds (count-keyed
cache) are effectively free. FTS backfill throughput is 92K–169K events/s on
text-heavy synthetic logs; the real log (mostly non-text events) indexes at 3.1M
events/s, i.e. 42 ms for the entire session.

### 4.4 The abort asymmetry

Cancelling *during* an SSE read cannot be expressed by OpenAI-compatible clients as an
abort: the HTTP layer converts the cancellation into a terminal stream error, so the
turn ends `error {code: ABORTED}` rather than `aborted {user}`. The distinction is not
cosmetic — finish reasons drive UX ("you pressed Stop" vs. "the provider failed"),
retry policy (error codes are retry-candidate classes; user aborts are not), and
analytics. Our harness documents and survives the asymmetry (both outcomes satisfy I1;
latency is unaffected), but we argue client libraries should let a caller-cancelled
read propagate as a distinct signal, and the eval suite pins the current behavior so a
future fix cannot silently change it.

### 4.5 Restart-crash UX pilot

`scripts/study-restart-ux.py` crashes the web app with SIGKILL mid-turn (alternating
unpaced/mid-SSE-paced streams, kill delay randomized per phase), restarts it, and
measures what a returning user faces (8 trials, seed 42):

- **Explanation:** 8/8 — the reloaded transcript states the app restarted while the
  turn was running (I1's repair half made visible).
- **Stuck UI:** 0/8 — no orphaned "typing…" indicators after reload.
- **Resumability:** 8/8 — a single follow-up message completed the session.
- **Resume latency:** median 0.56 s (repair already supplies the tool outcome; the
  resumed turn is summary-only). When the crash preceded the first tool execution, the
  resumed turn re-ran the full scripted flow: 3.6 s.
- **Crash-to-serving downtime:** median 4.6 s (process restart and boot, dominated by
  .NET startup and session reattach; not an interruption-contract cost).

The pilot is automated; `paper/ux-study-protocol.md` is the human-participant protocol
reusing the same script and measures.

## 5. Related work

The harness lineage is `dsh`, whose spine-and-plugin composition and durable session
concepts this system extends; the interruption contract and its measurements are new
here. Commercial CLI agents expose stop buttons and resume-after-crash to varying
degrees, but their interruption semantics are undocumented and their logs are not
available for external assertion, which is precisely what the scored-eval approach in
4.1 requires. The torn-tail/repair discipline mirrors classic write-ahead-log practice
(database crash recovery) applied to conversational state; we are not aware of prior
work *measuring* cancel-propagation latency per turn phase or crash-repair UX for
interactive agents.

## 6. Limitations

Measurements are from one arm64 Linux VM; absolute numbers will shift on other hardware
(the methodology is committed and one command to re-run). Interruption evals and the UX
pilot use a deterministic fake provider — real providers add their own stream
lifetimes, which the mid-tool measurements intentionally sidestep. The sandbox
confinement (Landlock) is Linux-only; other platforms fail closed rather than degrade.
PTY terminals are out of scope by design. The repair path is in-memory by choice — the
disk log never back-fills synthetic events — so external readers must apply the same
repair rules to see closed turns.

## 7. Conclusion

Treating interruption as a durable first-class state is cheap: sub-millisecond to
~150 ms to make a stop durable depending on phase, linear replay that keeps a 132K-event
session usable in 1.3 s, a search index that rebuilds faster than a keystroke, and
crash recovery that explains itself and resumes in under a second of harness time. The
cost is discipline — a closed set of turn reasons, durable closure of every tool call,
commit-don't-discard for partial output, and repair-on-reload — enforced not by
convention but by eval tasks that fail when the contract breaks. We offer the contract,
the tasks, and the numbers as a template other harnesses can adopt and be held to.
