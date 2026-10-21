// Window factory.
//
// The primary window mirrors the reference desktop app's chrome choices:
// macOS gets an inset title bar with positioned traffic lights and a draggable
// strip above the page; Windows/Linux get a hidden title bar with a native
// overlay. Because the Blazorly UI was designed for a browser viewport, the
// inset strip is simulated by injecting a small top padding plus a drag region
// (see INSET_CSS). Set BLAZORLY_DESKTOP_CHROME=standard to fall back to the
// plain OS title bar.

import { BrowserWindow, shell } from 'electron';

const INSET = process.env.BLAZORLY_DESKTOP_CHROME !== 'standard';
const STRIP_HEIGHT = 30;

const INSET_CSS = `
  .app-shell { padding-top: ${STRIP_HEIGHT}px; }
  body::before {
    content: '';
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    height: ${STRIP_HEIGHT}px;
    -webkit-app-region: drag;
    z-index: 2147483647;
  }
`;

export function chromeFor(platform) {
  if (!INSET) return { titleBarStyle: 'default' };
  if (platform === 'darwin') {
    return {
      titleBarStyle: 'hiddenInset',
      trafficLightPosition: { x: 14, y: 14 },
      acceptFirstMouse: true,
    };
  }
  return {
    titleBarStyle: 'hidden',
    titleBarOverlay: { color: '#0f0d16', symbolColor: '#c9c6d4', height: STRIP_HEIGHT },
  };
}

/**
 * @param {object} opts
 * @param {string} opts.preloadPath
 * @param {string} opts.iconPath       PNG used on Windows/Linux task switchers
 * @param {import('./logger.mjs').Logger} opts.logger
 * @param {() => string|null} opts.allowedOrigin  current engine origin, or null
 */
export function createMainWindow({ preloadPath, iconPath, logger, allowedOrigin }) {
  const win = new BrowserWindow({
    width: 1280,
    height: 840,
    minWidth: 880,
    minHeight: 560,
    show: false,
    backgroundColor: '#0f0d16',
    title: 'Blazorly Harness',
    icon: iconPath,
    ...chromeFor(process.platform),
    webPreferences: {
      preload: preloadPath,
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webviewTag: false,
      spellcheck: true,
    },
  });

  win.once('ready-to-show', () => win.show());

  const isAllowedNavigation = (url) => {
    const origin = allowedOrigin();
    if (!origin) return false;
    try {
      return new URL(url).origin === origin;
    } catch {
      return false;
    }
  };

  win.webContents.on('will-navigate', (event, url) => {
    if (isAllowedNavigation(url)) return;
    event.preventDefault();
    openExternal(url, logger);
  });
  win.webContents.setWindowOpenHandler(({ url }) => {
    openExternal(url, logger);
    return { action: 'deny' };
  });
  win.webContents.on('did-fail-load', (event, code, description, url, isMainFrame) => {
    if (!isMainFrame || code === -3 /* ERR_ABORTED */) return;
    logger.error('window.did-fail-load', { code, description, url });
  });
  win.webContents.on('render-process-gone', (event, details) => {
    logger.error('window.render-process-gone', { reason: details.reason, exitCode: details.exitCode });
  });

  return win;
}

/** Apply the simulated title strip once the app UI has loaded. */
export async function applyInsetCss(webContents, logger) {
  if (!INSET) return;
  try {
    await webContents.insertCSS(INSET_CSS);
  } catch (error) {
    logger.warning('window.inset-css-failed', { error });
  }
}

export function openExternal(url, logger) {
  try {
    const parsed = new URL(url);
    if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
      shell.openExternal(url);
      return;
    }
  } catch {
    // fall through
  }
  logger.warning('shell.blocked-external-url', { protocol: safeProtocol(url) });
}

function safeProtocol(url) {
  try {
    return new URL(url).protocol;
  } catch {
    return 'invalid';
  }
}
