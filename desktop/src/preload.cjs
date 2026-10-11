// Renderer bridge (sandboxed preload).
//
// One namespaced surface is exposed to pages we control (splash, error) and,
// harmlessly, to the engine UI itself: window.blazorlyDesktop. The reference
// desktop app follows the same pattern (a single contextBridge object with a
// namespaced IPC surface).

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('blazorlyDesktop', {
  isDesktop: true,
  platform: process.platform,
  getInfo: () => ipcRenderer.invoke('desktop:info'),
  getServerStatus: () => ipcRenderer.invoke('desktop:server-status'),
  onServerStatus: (callback) => {
    const listener = (_event, status) => callback(status);
    ipcRenderer.on('desktop:server-status-changed', listener);
    return () => ipcRenderer.removeListener('desktop:server-status-changed', listener);
  },
  restartServer: () => ipcRenderer.invoke('desktop:restart-server'),
  openLogs: () => ipcRenderer.invoke('desktop:open-logs'),
  copyDiagnostics: () => ipcRenderer.invoke('desktop:copy-diagnostics'),
  openExternal: (url) => ipcRenderer.invoke('desktop:open-external', url),
  quit: () => ipcRenderer.send('desktop:quit'),
});
