# Blazorly Harness

An agentic coding harness — give it a task and it reads, writes, runs commands, searches the web, and orchestrates sub-agents to get it done. Serves a web UI you use from any browser, or runs headless for automation and CI.

Site: [Blazorly Harness](https://harness.blazorly.com/) — features, install, and guides.

> **🔒 Permission presets** control what the agent can touch:
> `full-access` (default) runs tools directly on your host; `workspace-write` confines writes
> to the session folder; `read-only` denies all mutations. Switch mid-session with
> `/permission <preset>`.

---

## ✨ Features

- **Chat web UI** — streaming agent turns, live tool cards, tasks, context usage, and delegated agents. The resizable Terminal / Run panel sits below the chat composer
- **Personal appearance** — six theme presets or your device theme, interface and code fonts, custom accents, and optional CSS with a live preview. Open **Appearance** in the sidebar; preferences are saved in this browser and bundled fonts work offline
- **Workspace navigation** — one sidebar with Chats / Files tabs, workspace switching, rename, and workspace-scoped search. Folders are registered explicitly; the harness installation is never added as a default workspace
- **Workspace deletion** — confirmation shows the local folder and session count, then stops owned work and permanently deletes the folder, chats, child sessions, and attachments
- **Open chats and files** — one tab strip with individual close buttons and Close all. Chat drafts, attachments, and file edits survive tab switches; closing a chat tab preserves its saved history. Close all offers save or discard for unsaved files
- **Workspace editor** — text editing with Ctrl+S / Cmd+S, unsaved-change prompts, automatic file refresh, and protection against overwriting external edits. Supports UTF-8 files up to 512 KB; syntax highlighting and IntelliSense are still pending
- **Run controls** — start and stop commands detected from package.json, plain Node entry points, .NET, Python, PHP, Go, Rust, and Make, including nested projects. Processes remain tracked across browser refreshes while the host runs; Windows job objects keep child processes under the host's control. The bash tool, interactive terminal, and shell hooks currently expect `/bin/bash`; native Windows shell support is pending
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

**What you get:** a self-contained `blazorly` binary. Data lives in `~/.blazorly` (Windows: `%USERPROFILE%\.blazorly`), or the directory specified by `BLAZORLY_HOME`. No Docker, no Python, no .NET SDK.

---

## 🚀 Quick start

```bash
blazorly                            # starts the web UI at http://localhost:5080
```

Open the browser. A fresh installation has no provider, model, workspace, or session configured. In **Settings → Providers**, add your provider and key, then use **Discover** or enter model IDs. Settings are written to `<home>/settings.json` when you save. The page displays the actual path.

For example, to configure an OpenAI route explicitly, create `settings.json` with your model ID and key:

```json
{
  "provider": "openai",
  "model": "your-model-id",
  "apiKey": "your-api-key"
}
```

Works with DeepSeek, OpenAI, Anthropic, xAI, Ollama, LM Studio, and OpenAI-compatible endpoints (custom routes in Settings → Providers). Model pickers list only discovered or manually configured IDs. The built-in catalog supplies metadata for those IDs; it does not establish account access or server availability.

Removing a provider removes its route and clears the default selection if necessary. Deleting the harness home resets its saved configuration, workspace registrations, and session data; workspace folders stored elsewhere remain on disk. Browser appearance preferences are stored separately in the browser.

The [startup and behavior audit](audits/2026-09-25-startup-integrity.md) records the fixes and verification limits.

For a gateway that serves the Responses API, choose **OpenAI Responses** as its API type in **Settings → Providers**. Automatic OpenAI-compatible routes also use Responses for GPT-5 and GPT-6 models, including provider-prefixed ids such as `openai/gpt-6-astra`.

Personalize the UI in **Settings → Appearance**. Theme, font, and accent changes save automatically; custom CSS uses **Apply CSS** and can be disabled without losing your code. **Reset appearance** restores defaults. If a custom style hides the controls, open `/settings?tab=appearance&reset-appearance=1` on your running instance to reset them.

```
blazorly run "summarize this repo"      # headless task (current directory becomes the workspace)
blazorly --version                       # build stamp
```

---

## ⚙️ Essential configuration

| Setting | Default | What it does |
|---|---|---|
| `provider` / `model` | Unconfigured | Your explicitly selected LLM route |
| `apiKey` | — | API key, or that provider's own environment variable |
| `sandboxMode` | `full-access` | Tool sandbox: `full-access`, `workspace-write`, or `read-only` |
| `contextWindowTokens` | `262144` | Fallback compaction/context window when API/catalog metadata is absent |

Saved keys take precedence over the provider's own environment variable (for example `OPENAI_API_KEY` for OpenAI and `DEEPSEEK_API_KEY` for DeepSeek). Custom gateways use their saved key or explicitly named `apiKeyEnv`. An environment key authenticates an explicitly configured route; it does not add a provider automatically.

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

Browser appearance logic also has dependency-free JavaScript tests: `node --test tests/web/appearance.test.mjs` (Node.js 18+).

---

## 📄 License

MIT — see [LICENSE](LICENSE).
