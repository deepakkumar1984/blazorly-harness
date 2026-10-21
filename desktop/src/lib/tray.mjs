// Status tray, modelled on the reference app's tray (open window, new chat,
// live status line, quit). The tray is best-effort: on Linux desktops without
// an indicator service it may not appear, and the app must keep working.

import { Menu, Tray, nativeImage } from 'electron';

export class StatusTray {
  #tray = null;
  #actions;
  #logger;
  #status = { status: 'stopped', port: null, attachMode: false };

  constructor({ iconPath, actions, logger }) {
    this.#actions = actions;
    this.#logger = logger;
    try {
      const image = nativeImage.createFromPath(iconPath);
      if (image.isEmpty()) throw new Error(`empty tray image at ${iconPath}`);
      image.setTemplateImage(true);
      this.#tray = new Tray(image);
      this.#tray.on('click', () => this.#actions.showWindow());
      this.rebuild();
    } catch (error) {
      this.#logger.warning('tray.unavailable', { error });
    }
  }

  updateStatus(status) {
    this.#status = status;
    this.rebuild();
  }

  rebuild() {
    if (!this.#tray) return;
    const statusText = statusLabel(this.#status);
    this.#tray.setToolTip(`Blazorly Harness — ${statusText}`);
    this.#tray.setContextMenu(
      Menu.buildFromTemplate([
        { label: 'Open Blazorly Harness', click: () => this.#actions.showWindow() },
        { label: 'New Chat', click: () => this.#actions.newChat() },
        { type: 'separator' },
        { label: statusText, enabled: false },
        { label: 'Restart Engine', click: () => this.#actions.restartServer() },
        { type: 'separator' },
        { label: 'Quit Blazorly Harness', click: () => this.#actions.quit() },
      ]),
    );
  }

  destroy() {
    this.#tray?.destroy();
    this.#tray = null;
  }
}

function statusLabel(status) {
  switch (status.status) {
    case 'running':
      return `Engine running on port ${status.port}`;
    case 'attached':
      return status.baseUrl
        ? `Connected to ${status.baseUrl.replace(/^https?:\/\//, '')}`
        : 'Connected to existing engine';
    case 'starting':
      return 'Engine starting…';
    case 'failed':
      return 'Engine failed — open the app for details';
    default:
      return 'Engine stopped';
  }
}
