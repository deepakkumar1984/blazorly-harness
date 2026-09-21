# Blazorly Harness

An agentic coding harness — give it a task and it reads, writes, runs commands, searches the web, and orchestrates sub-agents to get it done. Serves a web UI you use from any browser, or runs headless for automation and CI.

Site: [blazorly.dev](https://blazorly.dev) — features, install, and guides.

> **🔒 Permission presets** control what the agent can touch:
> `full-access` (default) runs tools directly on your host; `workspace-write` confines writes
> to the session folder; `read-only` denies all mutations. Switch mid-session with
> `/permission <preset>`.

---

## ✨ Features

- **Chat-first web UI** — streaming agent turns, live tool cards, terminal drawer, task list, context meter, and a delegations panel tracking every sub-agent in flight
- **Multi-agent orchestration** — spawn sub-agents, build teams, fan out parallel swarms with review gates; children run inside the parent chat with live progress
- **Full workspace toolset** — bash, file read/write/edit, grep/glob, web search & fetch, LSP diagnostics, tmux awareness, session search, run_code, and any tool your MCP servers expose
- **Durable sessions** — every turn, chat, and tool call is saved; sessions survive app restarts, browser closes, and cold-resume of sub-agents days later
- **Decision model (System One / JEV-1)** — optional fast classifier for tool selection, auto-plan gating, and risk awareness; pure-code path carries the easy cases, one small model call handles the ambiguous tail
- **Single binary, six platforms** — self-contained install (no SDK, no Docker) for Linux, macOS, and Windows on both x64 and arm64

---

## 📦 Install

Pick your platform — a single command, no prerequisites:

### Linux & macOS

```bash
curl -fsSL https://raw.githubusercontent.com/deepakkumar1984/blazorly-harness/main/installer/install.sh | bash
```

### Windows

```powershell
powershell -c "irm https://raw.githubusercontent.com/deepakkumar1984/blazorly-harness/main/installer/install.ps1 | iex"
```

That puts `blazorly` on your PATH. Run it again to upgrade to the latest release — it verifies the checksum and atomically swaps the binary.

**What you get:** a self-contained `blazorly` binary. Data lives in `~/.blazorly` (Windows: `%LOCALAPPDATA%\blazorly`). No Docker, no Python, no .NET SDK.

---

## 🚀 Quick start

```bash
blazorly                            # starts the web UI at http://localhost:5080
```

Open the browser. The first run creates a settings file at `~/.blazorly/settings.json`. Set your provider and key on the Settings page — it discovers models live from each provider's API — or drop this into `settings.json`:

```json
{
  "provider": "deepseek",
  "model": "deepseek-v4-flash",
  "apiKey": "sk-…"
}
```

Works with DeepSeek, OpenAI, Anthropic, xAI, Ollama, LM Studio, and any OpenAI-compatible endpoint (custom routes in Settings → Routes).

```
blazorly run "summarize this repo"      # headless task (current directory becomes the workspace)
blazorly --version                       # build stamp
```

---

## ⚙️ Essential configuration

| Setting | Default | What it does |
|---|---|---|
| `provider` / `model` | `deepseek` / `deepseek-v4-flash` | Your LLM route |
| `apiKey` | — | API key (or `DEEPSEEK_API_KEY` / `OPENAI_API_KEY` env var) |
| `sandboxMode` | `full-access` | Tool sandbox: `full-access`, `workspace-write`, or `read-only` |
| `contextWindowTokens` | `65536` | Compaction and context-meter window |

API keys resolve per-request — a provider's key is never sent to another route. Environment variables take precedence if the settings field is empty.

Everything else (feature toggles, retry policy, MCP servers, System One, custom providers) is on the Settings page and in `settings.json`. Enable features by name and disable plugins you don't need:

```json
{
  "enableTeams": true,
  "disabledPlugins": ["terminals", "lsp"]
}
```

---

## 🎯 Skills

Skills are reusable instruction packs the model loads when your task matches. Drop a `SKILL.md` folder into any of these — the harness scans **all three**:

| Location | Scope |
|---|---|
| `~/.blazorly/skills/` | global, harness-native (wins name collisions) |
| `~/.agents/skills/` | the shared convention other agent tools read — one collection serves them all |
| `<workspace>/.blazorly/skills/` | this project only |

Each skill is a folder with a `SKILL.md`: `name` + `description` frontmatter, markdown body with the instructions. Descriptions ride in the system prompt every turn; when your brief matches one, the model calls the `skill` tool itself and follows the loaded instructions — you never invoke anything manually.

## 🤖 Multi-agent & delegation

Blazorly runs sub-agents — each with its own session, provider, and workspace — behind these surfaces:

- **`swarm`** — planner shards your objective, workers run in parallel, a reviewer verifies the actual workspace; failed tasks re-dispatch with review notes
- **`review`** — an independent agent forked from your conversation checks the files, runs tests, and returns a structured verdict (`pass` | `fail` | `concerns`)
- **Teams** — `spawn_teammate`, `send_message`, shared task board; batch sends to run teammates in parallel
- **Background sub-agents** — fire and poll with `subagent_list`; settled children cold-resume later

**Delegation runs inside one chat.** Sub-agent sessions don't clutter your sidebar. The **Agents panel** (beside the task list) shows live status per worker — pulsing while running, a checkmark when done, each row links to the child session for full inspection. A delegated child can never hang waiting for you: its `ask_user_question` answers itself.

The system prompt reserves delegation for genuinely large or parallelizable work — a file edit or quick lookup stays inline with the lead.

---

## 🧠 Decision model (System One / JEV-1)

Optional fast classifier for things a small model handles better than a full LLM call:

| Seam | What it asks | Effect |
|---|---|---|
| `auto-plan` | *Does this brief need a plan before anything changes?* | Engages plan mode before the first model call |
| `risk-gate` | *Could this call destroy data or leak secrets?* | Parks calls for your approval |
| `tool-gate` | *Which tools does the upcoming work need?* | Prunes per-request tool schemas to a shortlist |

The **tool gate** is the hybrid pattern: core + recently-used + lexical relevance stay with zero extra calls; the ambiguous tail gets one System One `Choice` (cached per brief). Any failure returns the full tool list — byte-identical to pre-gate behaviour. Stats at `GET /api/decisions`.

```jsonc
{
  "enableSystemOne": true,
  "systemOneApiKey": "tsk-…",
  "systemOneSeams": ["tool-gate"],
  "enableToolGate": true
}
```

---

## 🔧 Build from source

Requires .NET SDK 10.0. Linux needs `cc`/`gcc` for the Landlock sandbox helper (auto-compiled on first use); macOS and Windows build and run fine without it — the confining presets just can't jail `bash`/`run_code` there.

```bash
git clone https://github.com/deepakkumar1984/blazorly-harness.git
cd blazorly-harness
dotnet build Blazorly.Harness.slnx
dotnet test

dotnet run --project src/Blazorly.Harness.Web          # UI at http://localhost:5080
dotnet run --project src/Blazorly.Harness.Cli -- run "hello"
```

---

## 📄 License

MIT — see [LICENSE](LICENSE).