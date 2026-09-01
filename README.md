# Blazorly Harness

An agentic coding harness: a complete agent runtime (agent loop, tool pipeline, sandboxing, session persistence) with four front-ends — a Blazor web UI, a headless CLI, and JSON-RPC / ACP stdio servers for editors and automation.

Works out of the box with zero configuration: a keyless `replay` provider runs a scripted demo agent over the real pipeline, so you can boot the web app and start a session immediately. Point it at a real LLM provider when you're ready.

## Solution layout

| Project | What it is |
|---|---|
| `src/Blazorly.Harness.Kernel` | Event bus, harness context, plugin host, scoped layers |
| `src/Blazorly.Harness.Llm` | LLM routing: provider adapters (Anthropic, OpenAI-compatible, replay), streaming, token estimation, model discovery |
| `src/Blazorly.Harness.Core` | Agent loop, sessions, compaction, subagents, jobs, credentials, MCP client, schedules, telemetry, and other core services |
| `src/Blazorly.Harness.Tools` | Built-in tools (bash, fs, web, LSP, terminals, code mode, …) and the Landlock sandbox |
| `src/Blazorly.Harness.Persistence` | Session persistence: JSONL and SQLite backends |
| `src/Blazorly.Harness.Sdk` | Client SDK over the automation protocol |
| `src/Blazorly.Harness.Web` | Blazor Server UI + REST/WebSocket API |
| `src/Blazorly.Harness.Cli` | `blazorly` CLI launcher (run, sessions, serve-stdio, serve-acp) |
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

From the UI you can create/rename/fork/archive sessions, chat with the agent, watch tool calls stream in, manage workspaces, edit credentials, and configure providers on the **Settings** page — which is just a friendly editor for `settings.json` (see below).

### REST / WebSocket API

The same surface is available over HTTP for scripting:

| Endpoint | Purpose |
|---|---|
| `GET /api/session.list` · `POST /api/session.create` | List / create sessions |
| `POST /api/session.prompt` · `/api/session.cancel` | Send a prompt / cancel a turn (`mode`: `queue`) |
| `GET /api/session.history?id=…` · `POST /api/session.fork` | Event history / fork at a sequence |
| `POST /api/session.rename` · `/api/session.archive` · `/api/session.command` | Session management and slash commands |
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
--provider <name>     # replay | deepseek | openai | anthropic | openai-compatible | a configured custom route
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
                                     #   flags: --workspace, --chunk-delay <ms>, --permission auto|ask
```

Exit codes for `run`: `0` completed · `2` turn error/blocked · `3` aborted (e.g. timeout) · `1` harness failure.

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

Built-in providers: `replay` (keyless scripted demo — the default), `deepseek`, `openai`, `anthropic`, `openai-compatible` (any OpenAI-compatible endpoint). Extra custom OpenAI-compatible routes can be added under `customProviders` in settings or from the Settings page (with live model discovery via `POST /api/llm.discover`).

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
| `provider` / `model` | `replay` / `demo` | Default LLM route and model |
| `baseUrl` | `https://api.deepseek.com` | Endpoint for the main route |
| `apiKey` | — | Stored key (env vars take precedence only if this is empty) |
| `sandboxMode` | `workspace-write` | Tool sandbox: `read-only` or `workspace-write` |
| `persistence` | `jsonl` | `jsonl` or `sqlite` session storage |
| `workspaceRoot` | current directory | Default workspace added on first boot |
| `contextWindowTokens` | `65536` | Used by compaction and the token meter |
| `compactionThreshold` / `compactionPrunerChars` | `0.72` / `4000` | When compaction kicks in and how it prunes |
| `enable*` flags | mostly `true` | Toggle plugins: terminals, LSP, web, skills, goals, plan mode, code mode, workflows, teams, MCP, schedule, hooks, auto-titles, spill, ask-user, session query, project instructions |
| `telemetryEnabled` | `true` | Local-only usage aggregates; nothing leaves the machine |
| `enableE2b`, `e2bApiKey`, `e2bTemplate`, `e2bBaseUrl` | off | Remote E2B sandbox execution (key resolves from settings, else the `E2B_API_KEY` env var) |

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

## License

TBD — all rights reserved by the authors.
