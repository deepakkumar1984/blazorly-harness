// Application menu — English for now. The reference app localizes its native
// menu from JSON files (native-menu-locales/*.json); when the desktop shell
// grows user-facing releases, this builder is the single place to add that.

import { Menu, app, shell } from 'electron';

/**
 * @param {object} opts
 * @param {object} opts.actions
 * @param {() => void} opts.actions.newChat
 * @param {() => void} opts.actions.openSettings
 * @param {() => void} opts.actions.restartServer
 * @param {() => void} opts.actions.checkForUpdates
 * @param {() => void} opts.actions.openLogs
 * @param {() => void} opts.actions.copyDiagnostics
 * @param {boolean} opts.isDev
 */
export function installAppMenu({ actions, isDev }) {
  const isMac = process.platform === 'darwin';
  const external = (url) => () => shell.openExternal(url);

  const template = [
    ...(isMac
      ? [
          {
            label: app.name,
            submenu: [
              { role: 'about' },
              { label: 'Check for Updates…', click: actions.checkForUpdates },
              { type: 'separator' },
              { label: 'Settings…', accelerator: 'CmdOrCtrl+,', click: actions.openSettings },
              { type: 'separator' },
              { role: 'services' },
              { type: 'separator' },
              { role: 'hide' },
              { role: 'hideOthers' },
              { role: 'unhide' },
              { type: 'separator' },
              { role: 'quit' },
            ],
          },
        ]
      : []),
    {
      label: 'File',
      submenu: [
        { label: 'New Chat', accelerator: 'CmdOrCtrl+N', click: actions.newChat },
        { type: 'separator' },
        { label: 'Restart Engine', click: actions.restartServer },
        ...(isMac
          ? [{ role: 'close' }]
          : [
              { type: 'separator' },
              { label: 'Settings…', accelerator: 'Ctrl+,', click: actions.openSettings },
              { type: 'separator' },
              { role: 'quit' },
            ]),
      ],
    },
    {
      label: 'Edit',
      submenu: [
        { role: 'undo' },
        { role: 'redo' },
        { type: 'separator' },
        { role: 'cut' },
        { role: 'copy' },
        { role: 'paste' },
        { role: 'pasteAndMatchStyle' },
        { role: 'delete' },
        { role: 'selectAll' },
      ],
    },
    {
      label: 'View',
      submenu: [
        { role: 'reload' },
        { role: 'forceReload' },
        ...(isDev ? [{ role: 'toggleDevTools' }] : []),
        { type: 'separator' },
        { role: 'resetZoom' },
        { role: 'zoomIn' },
        { role: 'zoomOut' },
        { type: 'separator' },
        { role: 'togglefullscreen' },
      ],
    },
    ...(isMac ? [{ role: 'windowMenu' }] : []),
    {
      role: 'help',
      submenu: [
        { label: 'Documentation', click: external('https://harness.blazorly.com/') },
        { label: 'GitHub Repository', click: external('https://github.com/deepakkumar1984/blazorly-harness') },
        { type: 'separator' },
        { label: 'Show Logs', click: actions.openLogs },
        { label: 'Copy Diagnostics', click: actions.copyDiagnostics },
      ],
    },
  ];

  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}
