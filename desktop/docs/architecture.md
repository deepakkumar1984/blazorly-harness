# Desktop shell architecture

The desktop app is a thin, honest shell: the .NET engine does the work, the
shell supervises it and presents its UI natively. This document records the
structure, the security posture, and the reference study that shaped it.

## Process model

```
Blazorly Harness.app (Electron main)
├── splash.html            branded startup screen while the engine warms up
├── engine child process   `blazorly serve --no-open --port <n>` (managed)  ──┐
├── BrowserWindow          loads http://127.0.0.1:<n>/ (engine UI)            │
│     └── preload.cjs      contextBridge: window.blazorlyDesktop              │
├── StatusTray             status line + quick actions                        │
└── logs/desktop-*.log     structured, token-redacted                     ────┘
```

Modules under `src/lib/`:

| Module | Responsibility |
|---|---|
| `server.mjs` | attach-or-spawn, readiness probe (`/api/session.list`), output capture, SIGINT-then-SIGKILL shutdown, process-group kill |
| `windows.mjs` | window factory, platform chrome, navigation guards, external-link routing |
| `menu.mjs` / `tray.mjs` | native menu and tray with live engine status |
| `deep-links.mjs` | `blazorly://` registration; queues pre-ready `open-url`/`open-file` events and replays them |
| `ipc.mjs` | renderer bridge handlers with sender validation |
| `logger.mjs` / `config.mjs` | redacting JSON-lines logs; persisted shell preferences (sticky fallback port) |

Startup sequence: identity + protocol registration (before `ready`) → splash in
the main window → attach-or-spawn → probe until the engine answers → load the
engine UI → apply the inset title strip. Any failure lands on the error screen
with the failure stage, message, and log tail — never a silent fallback.

## Security posture

- `contextIsolation: true`, `nodeIntegration: false`, `sandbox: true`,
  `webviewTag: false` for the window that loads the engine UI.
- One `contextBridge` surface (`window.blazorlyDesktop`); IPC handlers validate
  the sender (our `file://` pages, or the engine's loopback origin).
- `setWindowOpenHandler` denies popups and routes http(s) to the system browser;
  `will-navigate` allows only same-origin navigation.
- Logs redact access tokens (`?token=…`) and `sk-`/`tsk-` key material.
- No telemetry; nothing leaves the machine except the engine's own provider
  calls, which are configured in the harness settings.

## Reference study

The shell mirrors patterns from the Codex desktop app (OpenAI's Electron app,
statically analysed for this project — see the REA evidence bundle in the
workspace that produced this app). The mapping:

| Reference pattern | Here |
|---|---|
| Early bootstrap: identity, single-instance lock, protocol registration, deep-link queue before `ready` | `main.mjs` + `deep-links.mjs` |
| Startup splash with brand shimmer inside the main window | `assets/splash.html` |
| Window modes with inset title bars, positioned traffic lights, vibrancy | `windows.mjs` (`inset` strip, traffic lights at 14/14) |
| One namespaced `codex_desktop:*` IPC surface via contextBridge | `window.blazorlyDesktop` bridge, `desktop:*` channels |
| Hardened webPreferences (contextIsolation, sandbox, no node integration) | same, incl. sender validation |
| Tray with open/new-chat/status | `tray.mjs` |
| Structured `safe`/`sensitive` logging with redaction | `logger.mjs` |
| `powerSaveBlocker` while work is live | held while the engine runs |
| Deep links (`codex://`, open-file/open-url queueing) | `blazorly://`, same queueing |
| Sparkle-style update flow | `Check for Updates…` → `blazorly update` (checksum-verified by the engine) |
| Native menu localization from JSON locales | not adopted; menu is English-only (single-builder refactor point, see `menu.mjs`) |

Deliberately not copied: the reference app embeds its own SQLite, node-pty
terminal, LSP client, and app-server RPC — the Blazorly engine already provides
the equivalent surface over HTTP/WebSocket, so the shell stays a supervisor.

## Verified behaviour

Verified live on macOS (Electron 44):

- attach to an engine already running on 5080 (shared with the browser UI);
- spawn + readiness in ~2 s, own port, engine killed on quit (no orphans);
- graceful quit from the bridge/menu within ~30 ms, watchdog backstop at 8 s;
- honest failure screen for a missing engine binary and for failed restarts;
- inset title strip and drag region over the real UI.
- remote attach over the network: Bearer probe, `blazorly_auth` cookie set on
  the window session before first load (token never in the URL), verified
  against a token-gated engine on the LAN IP; missing token produces the
  specific "requires its access token" failure;
- deep links on the packaged `.app` (`open blazorly://settings` focuses and
  routes the running app); AppleScript/⌘Q quit path.

Not yet verified: Windows/Linux chrome, NSIS/AppImage/deb installers,
notarization, auto-update of the shell itself.
