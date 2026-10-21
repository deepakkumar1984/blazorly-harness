# Blazorly Harness — desktop app

Electron shell that runs the Blazorly Harness engine locally and serves its web
UI in a native window — a desktop counterpart to `blazorly` in the browser.
Runs as **Blazorly Harness** in the Dock, the menu bar, and the tray.

```bash
cd desktop
npm install          # once
npm start            # launches the shell; starts or attaches to the engine
```

## How it finds an engine

1. **Attach** — if a healthy Blazorly engine already answers on the preferred
   port (default `5080`, or the last fallback port this shell used), the shell
   connects to it. Your browser session and the app share one engine.
2. **Spawn** — otherwise it launches `blazorly serve --no-open --port <port>`
   and stops it again when the app quits. A managed engine never outlives the
   app.

Binary lookup order: `--server-bin` / `BLAZORLY_BIN` → bundled copy inside the
packaged app (`Resources/engine/blazorly`) → the installer's well-known
locations (`~/.local/bin`, `~/.blazorly/app/current`) → a local
`src/Blazorly.Harness.Cli` build → `blazorly` on `PATH`. If nothing is found
the app shows an honest failure screen with the log path — it never pretends
the engine started.

## Local vs remote engines

**Local (default, zero configuration).** The shell attaches to a loopback
engine on `5080` if one is running, otherwise spawns and owns its own. Loopback
requests bypass the engine's token gate, so nothing needs configuring.

**Remote over SSH (recommended).** Tunnel first; the gate treats tunnel traffic
as local, so the token never leaves the server machine:

```bash
ssh -N -L 5080:localhost:5080 user@server     # then start the app normally
```

**Remote over the network.** Start the engine on the host with a pinned token,
then hand its startup URL (the one carrying `?token=…`) to the app:

```bash
# on the server
blazorly serve --host 0.0.0.0 --port 5080 --token <secret>

# on the client
cd desktop && npm start -- --attach "http://server-host:5080/?token=<secret>"
# or: BLAZORLY_DESKTOP_URL="http://server-host:5080/?token=<secret>" npm start
```

The shell verifies the engine with a Bearer probe, installs the `blazorly_auth`
session cookie itself (the token never appears in the URL bar or navigation
history), and then loads the UI. A wrong or missing token produces a specific
failure screen — "answered but requires its access token" — not a generic hang.
Access tokens are redacted from logs and excluded from diagnostics copies.

## Flags and environment

| Flag / env | Effect |
|---|---|
| `--server-bin <path>` / `BLAZORLY_BIN` | Engine binary to use (strict for the flag: a missing path fails loudly) |
| `--attach <url>` / `BLAZORLY_DESKTOP_URL` | Connect to a specific running engine instead of spawning; network binds take the startup URL, `?token=…` included |
| `--port <n>` | Preferred port (default `5080`; falls back to a free port, remembered for next launch) |
| `BLAZORLY_DESKTOP_CHROME=standard` | Use the plain OS title bar instead of the inset title strip |

## App behaviour

- **Window chrome** — macOS gets an inset title bar with traffic lights over a
  draggable 30 px strip; Windows/Linux get a hidden title bar with the native
  overlay. The strip is removed with `BLAZORLY_DESKTOP_CHROME=standard`.
- **Menus** — New Chat (`⌘N`), Restart Engine, Settings (`⌘,`), Check for
  Updates (runs `blazorly update` after confirmation), Show Logs, Copy
  Diagnostics.
- **Tray** — Open, New Chat, live engine status, Restart Engine, Quit.
- **Deep links** — `blazorly://` opens the app: `blazorly://settings`,
  `blazorly://session/<id>`, `blazorly://open`.
- **Full-screen apps** linger in the Dock with the engine running when the
  window is closed (macOS convention); Quit stops a managed engine.
- **Updates** — the shell itself follows the version in `package.json`;
  the engine updates through `blazorly update` (menu item).

## Icons

```bash
npm run icons   # favicon-style mark → icon.png/icon.icns + tray templates
```

Uses headless Chrome for SVG rendering plus `sips`/`iconutil` for the `.icns`.

## Packaging

```bash
bash scripts/package.sh mac                 # use blazorly from PATH in the bundle
bash scripts/package.sh mac --with-engine   # build + bundle the engine (~dotnet publish)
```

`--with-engine` stages the full engine directory (`resources/engine/`) exactly
the way `installer/make-dist.sh` does — launcher, run_code sidecar, and the
Web project's `wwwroot`. The release workflow does the same thing on macOS
runners, but stages the **osx-arm64 archive built by the dist job**, so the app
and the CLI release always carry identical engine bytes.

Artifacts land in `desktop/dist/` (`Blazorly Harness-<version>-arm64.dmg` +
zip), and CI also writes `SHA256SUMS-desktop`, which
[installer/install-desktop.sh](../installer/install-desktop.sh) verifies before
installing to `/Applications`.

Local builds skip signing discovery with `CSC_IDENTITY_AUTO_DISCOVERY=false`.

## Signing and notarization

The release workflow signs and notarizes automatically **once these exist**;
until then it builds an ad-hoc signed app (right-click → Open once).

One-time setup on your Mac (Team `8VKHSJ3FUC`):

1. Xcode → Settings → Accounts → your Apple ID → **Manage Certificates…** → `+`
   → **Developer ID Application**. (The `Apple Distribution` certificate is for
   the App Store and cannot sign direct downloads.)
2. Export it: Keychain Access → *My Certificates* → "Developer ID Application:
   …" → right-click → Export → `.p12` with a password.
   Base64 it: `base64 -i DeveloperID.p12 | pbcopy`.
3. Create an app-specific password at <https://account.apple.com> (Sign-in &
   Security → App-Specific Passwords).
4. Add repository secrets (Settings → Secrets and variables → Actions):

| Secret | Value |
|---|---|
| `MAC_CERT_P12` | base64 of the exported Developer ID Application `.p12` |
| `MAC_CERT_PASSWORD` | the `.p12` export password |
| `APPLE_ID` | your Apple ID email |
| `APPLE_APP_SPECIFIC_PASSWORD` | the app-specific password from step 3 |

`APPLE_TEAM_ID` is already set in the workflow. Re-run the release workflow
(or push the next tag) and the existing artifacts are replaced with signed,
notarized builds (`gh release upload --clobber`).

## Troubleshooting

- **The app shows the failure screen** — read the message and the log at
  `~/Library/Application Support/Blazorly Harness/logs/desktop-*.log`
  (Windows: `%APPDATA%\Blazorly Harness\logs`). Retry from the same screen.
- **Port 5080 busy with something else** — the shell picks a free port and
  remembers it; UI preferences are per origin, so the first fallback run starts
  with default appearance.
- **Engine logs** — the shell writes the engine's stdout/stderr (token redacted)
  into the same log file.

Architecture notes and how this shell relates to the desktop app it was
modelled on: [docs/architecture.md](docs/architecture.md).
