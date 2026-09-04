# Blazorly Harness

An agentic coding harness: a complete agent runtime (agent loop, tool pipeline, sandboxing, session persistence) with four front-ends — a Blazor web UI, a headless CLI, and JSON-RPC / ACP stdio servers for editors and automation.

Configure an LLM provider to start working sessions: set `provider`, `model`, and an API key in `settings.json` (or pick them in the web Settings page, which discovers models live from each provider's API). The provider dropdown groups routes into US companies, Chinese companies, local & self-hosted, and other — plus any custom OpenAI-compatible routes you add.

## Solution layout

| Project | What it is |
|---|---|
| `src/Blazorly.Harness.Kernel` | Event bus, harness context, plugin host, scoped layers |
| `src/Blazorly.Harness.Llm` | LLM routing: provider adapters (Anthropic, OpenAI-compatible), streaming, token estimation, model discovery |
| `src/Blazorly.Harness.Core` | Agent loop, sessions, compaction, subagents, jobs, credentials, MCP client, schedules, telemetry, and other core services |
| `src/Blazorly.Harness.Tools` | Built-in tools (bash, fs, web, LSP, terminals, code mode, …) and the Landlock sandbox (confines bash and run_code writes to the session workspace) |
| `src/Blazorly.Harness.Persistence` | Session persistence: JSONL and SQLite backends |
| `src/Blazorly.Harness.Sdk` | Client SDK over the automation protocol |
| `src/Blazorly.Harness.Web` | Blazor Server UI + REST/WebSocket API |
| `src/Blazorly.Harness.Cli` | `blazorly` CLI launcher (run, sessions, eval, serve-stdio, serve-acp) |
| `tests/Blazorly.Harness.Tests` | xUnit test suite |

## Prerequisites

- **.NET SDK 10.0** (all projects target `net10.0`) — `dotnet --version` to check.
- **Linux** for full bash sandboxing: the harness compiles a small Landlock helper (Linux kernel 5.13+) using `cc`/`gcc` on first use. Without it, mutating commands fail closed rather than run unsandboxed. Builds and everything else work on any platform .NET 10 supports.
- `python3` is only needed for the test suite's fake LSP fixture.

## Build and test

```bash
dotnet build Blazorly.Harness.slnx
dotnet test
```

## Run the web UI

```bash
dotnet run --project src/Blazorly.Harness.Web
```

With the default `http` launch profile this serves **http://localhost:5080** (the `https` profile uses `https://localhost:7295`). The harness composition boots before the server accepts requests; persisted sessions reattach automatically on startup.

## Install a release (Windows / macOS / Linux)

No SDK, no build — self-contained binaries from [GitHub Releases](https://github.com/deepakkumar1984/blazorly-harness/releases):

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/deepakkumar1984/blazorly-harness/main/installer/install.sh | sh

# Windows
powershell -c "irm https://raw.githubusercontent.com/deepakkumar1984/blazorly-harness/main/installer/install.ps1 | iex"
```

Both install the latest release for your platform (linux-x64/arm64, osx-x64/arm64, win-x64/arm64), verify the published checksum, install to `~/.blazorly/app/current` (Windows: `%LOCALAPPDATA%\blazorly\app\current`), and put `blazorly` on your PATH. Re-running the command upgrades in place. Set `BLAZORLY_INSTALL_BASE` to a local `dist/` folder to test unpublished builds; `BLAZORLY_SKIP_VERIFY=1` skips the checksum gate.

The installed `blazorly` is the whole product:

```text
blazorly                # the UI at http://localhost:5080 (--port N, --no-open)
blazorly run "job"      # headless task          blazorly sessions   # list sessions
blazorly eval ...       # task benchmarks        blazorly serve-acp  # ACP for editors
blazorly --version      # build stamp
```

Notes: data always lives in `~/.blazorly` regardless of binary location; on macOS/Windows there is no Landlock sandbox, so bash/run_code fail closed until you switch a session's permission preset to `danger-full-access` (`/permission`). To cut a release: `git tag v0.1.0 && git push --tags` — the release workflow builds all six platforms and publishes the GitHub Release the installers read.

From the UI you can create/rename/fork/archive sessions, chat with the agent, watch tool calls stream in, manage workspaces, edit credentials, and configure providers on the **Settings** page — which is just a friendly editor for `settings.json` (see below).

**@file references** — type `@` in the composer to autocomplete files under the session workspace (keyboard: arrows/Tab/Enter). Referenced files are attached to the outgoing message: text bodies travel as content blocks (256 KB cap per file, binary and oversized files degrade to notices), and images (`png`/`jpg`/`gif`/`webp`, ≤ 8 MB) go through the attachment store so vision models see them directly. Directories and misses produce an explicit note instead of silent failure.

**Runtime context** — every turn's context snapshot carries the current wall-clock time (minute precision, the `time` plugin) and, when a tmux server is running, a listing of its sessions/panes/commands (the `tmux` plugin — fail-soft, cached 30 s). Both are mount plugins, disable via `disabledPlugins: ["time", "tmux"]`.

**Terminal** — the session header's Terminal button opens a drawer with a persistent shell in the session workspace (command history with ↑, Ctrl+C, clear, kill shell). The shell is scoped to the session's agent, so the model can also inspect it with `terminal_read`/`terminal_list`. Piped stdio, not a PTY: interactive TUIs (vim, top) are not supported. The shell survives drawer close/reopen and page reloads; it dies with the app process.

Agents run server-side: closing the browser tab does not stop turns — reopening the session shows the live status and re-attaches the stream.

### REST / WebSocket API

The same surface is available over HTTP for scripting:

| Endpoint | Purpose |
|---|---|
| `GET /api/session.list` · `POST /api/session.create` | List / create sessions |
| `POST /api/session.prompt` · `/api/session.cancel` | Send a prompt / cancel a turn (`mode`: `queue`) |
| `GET /api/session.history?id=…` · `POST /api/session.fork` | Event history / fork at a sequence |
| `GET /api/session.projection?sessionId=…&name=…` | Cached log folds (`stats`, `turns`) — same numbers as the web stats dock |
| `GET /api/session.export?id=…` | Session ZIP (`session.jsonl` + `transcript.md`) |
| `POST /api/session.rename` · `/api/session.archive` · `/api/session.command` | Session management and slash commands |
| `GET /api/session.files?sessionId=…&q=…` | @-mention autocomplete candidates under the session cwd |
| `GET /api/events?sessionId=…` (WebSocket) | Live session event stream |
| `GET /api/workspace.list` · `POST /api/workspace.add` · `/api/workspace.remove` | Workspace registry |
| `GET /api/llm.providers` · `POST /api/llm.discover` | Provider/model catalog and live model discovery |
| `GET /api/credentials.describe` · `POST /api/credentials.set` · `/api/credentials.unset` | Credential store |
| `GET /api/telemetry` · `GET /api/jobs.list` · `GET /api/host.browse` | Local usage stats, background jobs, directory browser |

## Run the CLI

```bash
dotnet run --project src/Blazorly.Harness.Cli -- <command>
```

**Headless one-shot task** (the invoking directory becomes the workspace on first use):

```bash
dotnet run --project src/Blazorly.Harness.Cli -- run "summarize this repo's structure"

# Common flags
--workspace <path>    # workspace root (default: current directory)
--provider <name>     # deepseek | openai | anthropic | openai-compatible | a configured custom route
--model <id>          # model id for the run
--resume <sessionId>  # continue a persisted session
--timeout <seconds>   # cancel the run after N seconds
--json                # one JSON envelope instead of the live stream
--quiet               # suppress streamed output
```

**Other commands:**

```bash
... -- sessions                      # list persisted sessions (newest last)
... -- serve-stdio                   # JSON-RPC automation protocol on stdin/stdout
... -- serve-acp                     # Agent Client Protocol for editors
                                     #   flags: --workspace, --permission auto|ask
```

Exit codes for `run`: `0` completed · `2` turn error/blocked · `3` aborted (e.g. timeout) · `1` harness failure.

**Task benchmark** (each `eval/tasks/<id>/task.json` runs headless in an isolated workspace,
then shell checks score it):

```bash
... -- eval --tasks eval/tasks --out eval/results-manual
```

Each task sets a prompt, optional workspace `setup` (files + shell commands), and `checks`
(shell commands, exit 0 passes). Every task gets a fresh harness home seeded with your
provider keys, so eval sessions never pollute `~/.blazorly`. Results land as per-task JSON
plus `results.json`/`summary.md`; exit `0` means all tasks passed. **Never commit a results
directory: the seeded home inside it contains copied provider keys** (matching `eval/results-*`
entries in `.gitignore`).

**Interruption tasks** — scored assertions about the interruption contract, not just task
outcomes. `expectFinish` declares how the run may end (`completed`, `max-tokens`, `aborted`,
`interrupted`, `error`, `blocked`; absent means completed) and `interrupt` injects one:

```json
{
  "expectFinish": "aborted",
  "interrupt": { "cancelAfterMs": 1200 }
}
```

- `cancelAfterMs` — a user-style stop lands mid-turn; the turn must end `aborted` (user cause)
  with every pending tool closed durably in the log.
- `killAfterMs` — the headless process is SIGKILLed once its first tool call is durable
  (plus this delay). With `resumePrompt`, a second process must reload the log (torn tail
  discarded, interrupted turn repaired) and complete the session.

Checks receive `BLAZORLY_SESSION_ID` and `BLAZORLY_SESSION_LOG` so they can assert on the
durable log directly (see `eval/tasks/interrupt-*`). These tasks pin `provider: "scripted"`
and run against a fake OpenAI-compatible server (`scripts/fake_openai.py` or the C#
`FakeOpenAiServer` in tests); a `--timeout` CLI override replaces every task's timeout.

**Benchmarks** — the interruption-first measurement suite (cancel-propagation latency,
replay/projection cost vs. session size, FTS5 backfill throughput). Every benchmark is also
a correctness assertion on the contract it measures:

```bash
dotnet test --filter Category=Benchmark
```

Results print to the console and land in `benchmarks/results-<timestamp>/`
(`results.json` + `summary.md`, gitignored — numbers are per-machine). Cancel latency is
measured per phase: mid-tool (process-tree kill dominates), mid-stream over the in-process
adapter (the true `aborted` path with partial-message commit), and mid-SSE over a real
HTTP adapter (surfaces as `error`/ABORTED — the documented asymmetry). Replay cost uses
synthetic 1K–100K-event logs plus the largest real session under `~/.blazorly`.

**Paper draft + crash study** — `paper/paper.md` states the interruption contract and
carries the measurements; `paper/ux-study-protocol.md` is the restart-crash participant
protocol. The automated pilot behind §4.5 is
`python3 scripts/study-restart-ux.py [--trials N] [--seed S]` (SIGKILLs the web app
mid-turn, restarts, verifies explanation + resumability; writes `study/results-*`,
gitignored).

## Configuration

### Harness home

All state lives in one directory, `~/.blazorly` by default. Override it with the `BLAZORLY_HOME` environment variable (useful for isolated/portable homes and CI):

```bash
export BLAZORLY_HOME=/path/to/home   # optional
```

The directory is created on first boot and holds:

| Path | Purpose |
|---|---|
| `settings.json` | Runtime settings (provider, model, sandbox, feature toggles) — edited by the Settings page |
| `sessions/` or `sessions.db` | Session logs — JSONL by default, SQLite if `persistence` is `sqlite` |
| `credentials.json` | Credential store (`credentials.set`/`unset` API or the UI) |
| `mcp.json` | MCP servers to bridge in as tools (see below) |
| `hooks.json` | Hook rules (loaded when hooks are enabled) |
| `attachments/`, `spills/`, `telemetry.json`, `bin/` | Attachments, context spills, local-only usage stats, compiled Landlock helper |

### Provider and API keys

Set `provider`, `model`, and optionally `apiKey`/`baseUrl` in `settings.json` (or pick them in the web Settings page):

```json
{
  "provider": "deepseek",
  "model": "deepseek-v4-flash",
  "baseUrl": "https://api.deepseek.com",
  "sandboxMode": "workspace-write",
  "persistence": "jsonl"
}
```

Built-in providers: `deepseek` (the default), `openai`, `anthropic`, `openai-compatible` (any OpenAI-compatible endpoint), plus local routes (`ollama`, …) and other hosted providers. Extra custom OpenAI-compatible routes can be added under `customProviders` in settings or from the Settings page (with live model discovery via `POST /api/llm.discover`).

API keys resolve per request, in this order — and one provider's key is never sent to another provider's route:

1. `apiKey` from `settings.json`
2. The provider's environment variable: `DEEPSEEK_API_KEY`, `OPENAI_API_KEY`, or `ANTHROPIC_API_KEY`
3. For OpenAI-compatible routes only: `DEEPSEEK_API_KEY`, then `OPENAI_API_KEY` as a legacy fallback

```bash
export DEEPSEEK_API_KEY=sk-...    # or OPENAI_API_KEY / ANTHROPIC_API_KEY
dotnet run --project src/Blazorly.Harness.Web
```

### Settings reference (key fields)

| Setting | Default | Meaning |
|---|---|---|
| `provider` / `model` | `deepseek` / `deepseek-v4-flash` | Default LLM route and model |
| `baseUrl` | `https://api.deepseek.com` | Endpoint for the main route |
| `apiKey` | — | Stored key (env vars take precedence only if this is empty) |
| `sandboxMode` | `workspace-write` | Tool sandbox: `read-only` or `workspace-write` |
| `persistence` | `jsonl` | `jsonl` or `sqlite` session storage |
| `workspaceRoot` | current directory | Default workspace added on first boot |
| `contextWindowTokens` | `65536` | Used by compaction and the token meter |
| `compactionThreshold` / `compactionPrunerChars` | `0.72` / `4000` | When compaction kicks in and how it prunes |
| `enable*` flags | mostly `true` | Toggle plugins: terminals, LSP, web, skills, goals, plan mode, auto plan mode, code mode, workflows, teams, MCP, schedule, hooks, auto-titles, spill, ask-user, session query, project instructions |
| `autoPlanThreshold` | `55` | Complexity score (0–100) at which auto-plan engages a fresh turn's brief (settings file only) |
| `telemetryEnabled` | `true` | Local-only usage aggregates; nothing leaves the machine |
| `enableE2b`, `e2bApiKey`, `e2bTemplate`, `e2bBaseUrl` | off | Remote E2B sandbox execution (key resolves from settings, else the `E2B_API_KEY` env var) |
| `webSearchBackend` | `duckduckgo` | `web_search` backend: `duckduckgo` (keyless), `tavily`, or `brave` (change applies after restart) |
| `tavilyApiKey` / `braveApiKey` | — | Search API keys (or `TAVILY_API_KEY` / `BRAVE_API_KEY` env vars); without a key the backend falls back to DuckDuckGo |
| `pluginDirs` | `<home>/plugins` | Third-party plugin directories (each `*.dll` with `IHarnessPlugin` impls loads at boot) |
| `disabledPlugins` | `[]` | Plugin names to skip at boot (built-in, capability, or third-party) |

Changes made in the Settings page persist to `settings.json` and re-apply live (provider routes are rebuilt without a restart).

### MCP servers

Add stdio MCP servers to `~/.blazorly/mcp.json`; their tools appear as `mcp__<server>__<tool>`:

```json
{
  "servers": [
    { "name": "my-server", "command": "npx", "args": ["-y", "some-mcp-server"], "env": { "KEY": "value" } }
  ]
}
```

### Project instructions

The agent picks up instruction files — `AGENTS.md`, `CLAUDE.md`, and their `.local.md` overlays — from the harness home, the workspace root, and any directory it touches with read/write/edit. Commit an `AGENTS.md` to a repo to steer behavior there.

### Auto plan mode

Plan mode (manual: `/plan`) restricts a session to read-only work until the model presents a plan via `exit_plan_mode` and you approve it in a modal. **Auto plan mode** extends this: at the start of each fresh user turn, a deterministic complexity scorer (length, sequencing words, scope verbs, multi-entity targets, `@file` references, numbered steps; questions are capped) rates the brief, and scores at or above `autoPlanThreshold` engage plan mode *before* the first model call. The header shows a `📋 plan · auto` chip, the mutation guard blocks writes, and the system prompt tells the model why planning was engaged.

Guard rails: steers mid-turn never flip the mode; subagent and goal-driven turns are exempt; a brief that follows a plan you just approved runs without re-engaging (each approved plan covers its follow-up arc); `/plan` stays authoritative in both directions. Headless runs without an interactive reviewer fail closed — the model presents the plan as its final message instead of mutating. Disable with the Settings toggle, `{"disable":["auto-plan"]}` in `patches.json`, or `enableAutoPlan: false`.

### Plugins

Everything mounts as plugins: core services join the same topological boot as capability
plugins (`PluginHost.ApplyAllAsync` orders by `Inject` keys, failing fast on deadlocks and
duplicate names). Third-party plugins are plain C# classes deriving from `HarnessPlugin`
(or `AsyncHarnessPlugin`) with public parameterless constructors — drop the compiled `*.dll`
into `~/.blazorly/plugins/` (or set `pluginDirs`) and they join the boot, injecting any
service key (`tools`, `sessions`, `systemPrompt`, …). Updating a plugin takes an app restart;
a name colliding with a built-in fails the boot with `DUPLICATE_PLUGIN`.

Machine-specific overrides without touching the managed `settings.json` go in
`~/.blazorly/patches.json` (absent file = no-op; bad entries warn and are skipped):

```json
{
  "set": { "contextWindowTokens": 100000, "enableTeams": true },
  "disable": ["terminals"]
}
```

`set` keys are top-level settings fields (case-insensitive); `disable` maps plugin names to
their `Enable*` flags (`web` → `enableWeb`).

## License

TBD — all rights reserved by the authors.
