// Blazorly Harness desktop shell — Electron main process.
//
// Architecture follows the reference desktop app studied for this build (see
// docs/architecture.md): identity and deep-link registration during early
// bootstrap, a single primary window that shows a branded splash while the
// engine starts, hardened webPreferences, structured safe/sensitive logging,
// and an honest failure screen when the engine cannot start.

import { app, clipboard, dialog, powerSaveBlocker, session, shell } from 'electron';
import { execFile } from 'node:child_process';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { Logger } from './lib/logger.mjs';
import {
  APP_ROOT,
  DEFAULT_PORT,
  PRODUCT_NAME,
  deepLinkTarget,
  findServerBinary,
} from './lib/paths.mjs';
import { ServerManager } from './lib/server.mjs';
import { applyInsetCss, createMainWindow } from './lib/windows.mjs';
import { installAppMenu } from './lib/menu.mjs';
import { StatusTray } from './lib/tray.mjs';
import { DeepLinkQueue, deepLinkFromArgv, registerDeepLinks } from './lib/deep-links.mjs';
import { registerIpc } from './lib/ipc.mjs';
import { loadDesktopConfig, saveDesktopConfig } from './lib/config.mjs';

const isDev = !app.isPackaged;
const assetsDir = path.join(APP_ROOT, 'assets');
const preloadPath = path.join(APP_ROOT, 'src', 'preload.cjs');
const splashUrl = pathToFileURL(path.join(assetsDir, 'splash.html')).toString();
const errorUrl = pathToFileURL(path.join(assetsDir, 'error.html')).toString();

// ---------------------------------------------------------------------------
// Early bootstrap — everything here must run before `ready`.
// ---------------------------------------------------------------------------

app.setName(PRODUCT_NAME);
app.setAppUserModelId('com.blazorly.harness');

const cliArgs = parseArgs(process.argv.slice(1));
const logger = new Logger({ dir: path.join(app.getPath('userData'), 'logs') });
const links = new DeepLinkQueue();

let isQuitting = false;
let mainWindow = null;
let tray = null;
let server = null;
let powerBlockerId = null;

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  logger.info('app.second-launch-exit');
  app.quit();
} else {
  registerDeepLinks({ queue: links, logger, isPackaged: app.isPackaged });

  app.on('second-instance', (_event, argv) => {
    const link = deepLinkFromArgv(argv);
    if (link) links.pushUrl(link);
    showWindow();
  });

  app.on('activate', () => showWindow());

  app.on('window-all-closed', () => {
    // macOS keeps the app (and engine) alive in the Dock; other platforms quit.
    if (process.platform !== 'darwin') app.quit();
  });

  app.on('before-quit', (event) => {
    if (isQuitting) return;
    isQuitting = true;
    logger.info('app.before-quit', { platform: process.platform });
    event.preventDefault();
    shutdown().finally(() => app.quit());
  });

  // Last-resort cleanup for hard exits (e.g. terminal Ctrl+C while developing).
  app.on('will-quit', () => {
    logger.info('app.will-quit');
    server?.killSync();
  });

  main().catch((error) => {
    logger.error('app.main-failed', { error });
    dialog.showErrorBox('Blazorly Harness', `The desktop shell failed to start.\n\n${error?.message ?? error}`);
    app.exit(1);
  });
}

// ---------------------------------------------------------------------------
// Main flow
// ---------------------------------------------------------------------------

async function main() {
  await app.whenReady();
  logger.info('app.ready', {
    version: app.getVersion(),
    electron: process.versions.electron,
    chrome: process.versions.chrome,
    chrome_mode: process.env.BLAZORLY_DESKTOP_CHROME ?? 'inset',
  });

  const binary = findServerBinary({ override: cliArgs.serverBin });
  logger.info('server.binary-resolved', {
    binary: binary?.file ?? null,
    source: binary?.source ?? null,
  });

  const userData = app.getPath('userData');
  const desktopConfig = loadDesktopConfig(userData, logger);
  const preferredPorts = cliArgs.port
    ? [cliArgs.port]
    : [DEFAULT_PORT, desktopConfig.lastPort].filter((p) => Number.isInteger(p));

  server = new ServerManager({
    logger,
    binary,
    preferredPorts,
    cwd: userData,
    attachUrl: cliArgs.attach ?? process.env.BLAZORLY_DESKTOP_URL ?? null,
  });
  server.on('status-changed', (status) => {
    if (status.status === 'running' && status.port) {
      saveDesktopConfig(userData, { ...desktopConfig, lastPort: status.port }, logger);
    }
  });
  server.on('crashed', (diagnostics) => onServerCrashed(diagnostics));

  const actions = {
    newChat: () => {
      showWindow();
      navigate('/');
    },
    openSettings: () => {
      showWindow();
      navigate('/settings');
    },
    restartServer: () => restartServer(),
    checkForUpdates: () => checkForUpdates(),
    openLogs: () => {
      if (logger.filePath) shell.showItemInFolder(logger.filePath);
    },
    copyDiagnostics: () => clipboard.writeText(JSON.stringify(server.diagnostics(), null, 2)),
    quit: () => app.quit(),
  };
  installAppMenu({ actions, isDev });
  registerIpc({ logger, server, getWindow: windowFor, actions });

  tray = new StatusTray({
    iconPath: path.join(assetsDir, 'trayTemplate.png'),
    logger,
    actions: {
      showWindow,
      newChat: actions.newChat,
      restartServer: actions.restartServer,
      quit: actions.quit,
    },
  });
  server.on('status-changed', (status) => {
    tray?.updateStatus(status);
    updatePowerBlocker(status);
  });

  mainWindow = createMainWindow({
    preloadPath,
    iconPath: path.join(assetsDir, 'icon.png'),
    logger,
    allowedOrigin: () => (server.baseUrl ? new URL(server.baseUrl).origin : null),
  });
  mainWindow.on('close', (event) => {
    if (!isQuitting && process.platform === 'darwin') {
      event.preventDefault();
      mainWindow.hide();
    }
  });
  mainWindow.webContents.on('did-finish-load', () => {
    const url = mainWindow.webContents.getURL();
    if (server.baseUrl && url.startsWith(server.baseUrl)) applyInsetCss(mainWindow.webContents, logger);
  });

  // Deep links: focus the window and route within the engine UI.
  links.setHandler((target) => {
    if (!app.isReady()) return;
    const route = deepLinkTarget(target) ?? '/';
    showWindow();
    navigate(route);
  });

  await boot();
}

async function boot() {
  const win = windowFor();
  win?.loadURL(splashUrl);
  const diagnostics = await server.attachOrSpawn();
  if (diagnostics.status === 'failed') {
    logger.error('boot.engine-failed', { stage: diagnostics.failure?.stage });
    win?.loadURL(errorUrl);
    return;
  }
  await applyAttachAuth();
  logger.info('boot.engine-ready', { baseUrl: diagnostics.baseUrl, attachMode: diagnostics.attachMode });
  win?.loadURL(diagnostics.baseUrl + '/');
}

async function restartServer() {
  const win = windowFor();
  logger.info('server.restart-requested');
  win?.loadURL(splashUrl);
  const diagnostics = await server.restart();
  if (diagnostics.status === 'failed') {
    logger.error('server.restart-failed', { stage: diagnostics.failure?.stage });
    win?.loadURL(errorUrl);
    return diagnostics;
  }
  await applyAttachAuth();
  logger.info('server.restart-complete', { baseUrl: diagnostics.baseUrl });
  win?.loadURL(diagnostics.baseUrl + '/');
  return diagnostics;
}

/**
 * Token-gated engines (network binds) accept the access token once and then
 * trust the `blazorly_auth` cookie. Set that cookie on the window's session
 * before the first load so nothing token-shaped ends up in the URL bar or in
 * navigation history. The token itself is never written to logs or diagnostics.
 */
async function applyAttachAuth() {
  const token = server?.authToken;
  const base = server?.baseUrl;
  if (!token || !base) return;
  try {
    await session.defaultSession.cookies.set({
      url: `${base}/`,
      name: 'blazorly_auth',
      value: token,
      httpOnly: true,
      sameSite: 'lax',
    });
    logger.info('server.attach-cookie-set', { baseUrl: base });
  } catch (error) {
    logger.error('server.attach-cookie-failed', { error });
  }
}

function onServerCrashed(diagnostics) {
  const win = windowFor();
  win?.loadURL(errorUrl);
  win?.show();
  dialog.showMessageBox({
    type: 'error',
    title: 'Blazorly Harness',
    message: 'The engine stopped unexpectedly.',
    detail: diagnostics.failure?.message ?? 'No further details were captured.',
    buttons: ['Show Details', 'Quit'],
    defaultId: 0,
  }).then((result) => {
    if (result.response === 1) app.quit();
  });
}

async function checkForUpdates() {
  if (server.attachMode) {
    dialog.showMessageBox({
      type: 'info',
      title: 'Blazorly Harness',
      message: 'You are connected to an engine you started outside the app.',
      detail: 'Update that engine from your terminal with `blazorly update`.',
    });
    return;
  }
  const binary = findServerBinary({ override: cliArgs.serverBin });
  if (!binary) {
    dialog.showMessageBox({
      type: 'warning',
      title: 'Blazorly Harness',
      message: 'No engine binary to update.',
    });
    return;
  }
  const confirm = await dialog.showMessageBox({
    type: 'question',
    title: 'Blazorly Harness',
    message: 'Check for updates now?',
    detail: `Runs \`blazorly update\` (checksum-verified) for:\n${binary.file}\n\nThe engine restarts afterwards.`,
    buttons: ['Update', 'Cancel'],
    defaultId: 0,
    cancelId: 1,
  });
  if (confirm.response !== 0) return;

  const result = await new Promise((resolve) => {
    execFile(binary.file, ['update'], { timeout: 5 * 60 * 1000 }, (error, stdout, stderr) =>
      resolve({ error, output: `${stdout ?? ''}${stderr ?? ''}`.trim() }),
    );
  });
  if (result.error) {
    logger.error('update.failed', { error: result.error });
    await dialog.showMessageBox({
      type: 'error',
      title: 'Blazorly Harness',
      message: 'Update failed.',
      detail: result.output || result.error.message,
    });
    return;
  }
  logger.info('update.completed');
  await dialog.showMessageBox({
    type: 'info',
    title: 'Blazorly Harness',
    message: 'Update completed.',
    detail: result.output || 'The engine is up to date.',
  });
  await restartServer();
}

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

function windowFor() {
  return mainWindow && !mainWindow.isDestroyed() ? mainWindow : null;
}

function showWindow() {
  const win = windowFor();
  if (!win) return;
  if (win.isMinimized()) win.restore();
  win.show();
  win.focus();
}

function navigate(route) {
  const win = windowFor();
  if (!win || !server?.baseUrl) return;
  win.loadURL(new URL(route, server.baseUrl).toString());
}

function updatePowerBlocker(status) {
  const shouldBlock = status.status === 'running' || status.status === 'attached';
  if (shouldBlock && powerBlockerId === null) {
    powerBlockerId = powerSaveBlocker.start('prevent-app-suspension');
  } else if (!shouldBlock && powerBlockerId !== null) {
    if (powerSaveBlocker.isStarted(powerBlockerId)) powerSaveBlocker.stop(powerBlockerId);
    powerBlockerId = null;
  }
}

async function shutdown() {
  logger.info('app.shutdown');
  // Defensive watchdog: if a Chromium-level teardown ever stalls, the app must
  // still exit rather than linger in the Dock without its engine.
  const watchdog = setTimeout(() => {
    logger.warning('app.shutdown-watchdog-fired');
    app.exit(0);
  }, 8_000);
  watchdog.unref();
  // The engine UI installs a beforeunload guard for unsaved edits; destroying
  // the window skips it so quitting the app is never blocked by the renderer.
  const win = windowFor();
  if (win) {
    win.destroy();
    logger.info('app.shutdown-window-destroyed');
  }
  tray?.destroy();
  tray = null;
  try {
    await server?.stop();
  } catch (error) {
    logger.error('app.shutdown-failed', { error });
  }
  clearTimeout(watchdog);
  logger.info('app.shutdown-complete');
}

function parseArgs(argv) {
  const parsed = { attach: null, serverBin: null, port: null };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg.startsWith('--attach=')) parsed.attach = arg.slice('--attach='.length);
    else if (arg === '--attach' && argv[i + 1]) parsed.attach = argv[++i];
    else if (arg.startsWith('--server-bin=')) parsed.serverBin = arg.slice('--server-bin='.length);
    else if (arg === '--server-bin' && argv[i + 1]) parsed.serverBin = argv[++i];
    else if (arg.startsWith('--port=')) parsed.port = Number(arg.slice('--port='.length)) || null;
    else if (arg === '--port' && argv[i + 1]) parsed.port = Number(argv[++i]) || null;
  }
  return parsed;
}
