// IPC handlers.
//
// Sender validation: only pages we ship (file:// splash/error) and the engine
// UI on the loopback origin may call these channels — mirroring the reference
// app's sender-validation observations.

import { clipboard, ipcMain, shell } from 'electron';

export function registerIpc({ logger, server, getWindow, actions }) {
  const isTrustedSender = (event) => {
    const url = event.senderFrame?.url ?? '';
    if (url.startsWith('file://')) return true;
    try {
      const origin = server.baseUrl ? new URL(server.baseUrl).origin : null;
      return origin !== null && new URL(url).origin === origin;
    } catch {
      return false;
    }
  };

  const handle = (channel, fn) => {
    ipcMain.handle(channel, (event, ...args) => {
      if (!isTrustedSender(event)) {
        logger.warning('ipc.rejected-untrusted-sender', {
          channel,
          sender: event.senderFrame?.url?.split('?')[0] ?? 'unknown',
        });
        throw new Error('untrusted sender');
      }
      return fn(...args);
    });
  };

  handle('desktop:info', () => ({
    name: 'Blazorly Harness',
    version: process.env.npm_package_version ?? null,
    appVersion: null,
    platform: process.platform,
    arch: process.arch,
    logPath: logger.filePath,
    server: server.diagnostics(),
  }));
  handle('desktop:server-status', () => server.diagnostics());
  handle('desktop:restart-server', async () => {
    await actions.restartServer();
    return server.diagnostics();
  });
  handle('desktop:open-logs', () => {
    if (logger.filePath) shell.showItemInFolder(logger.filePath);
    return true;
  });
  handle('desktop:copy-diagnostics', () => {
    clipboard.writeText(JSON.stringify(server.diagnostics(), null, 2));
    return true;
  });
  handle('desktop:open-external', (url) => {
    try {
      const parsed = new URL(String(url));
      if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
        shell.openExternal(String(url));
        return true;
      }
    } catch {
      // fall through
    }
    logger.warning('ipc.blocked-external-url');
    return false;
  });

  ipcMain.on('desktop:quit', (event) => {
    if (!isTrustedSender(event)) return;
    actions.quit();
  });

  // Keep splash/error pages up to date with server status transitions.
  server.on('status-changed', (status) => {
    getWindow()?.webContents.send('desktop:server-status-changed', status);
  });
}
