// Deep links and file-open events (blazorly://).
//
// macOS delivers open-url/open-file before the app is ready; Windows/Linux
// deliver the URL in argv. Following the reference app's pattern, events are
// queued until a consumer attaches, then replayed — nothing is dropped.

import { app } from 'electron';
import { DEEP_LINK_SCHEME } from './paths.mjs';

export class DeepLinkQueue {
  #urls = [];
  #files = [];
  #handler = null;

  pushUrl(url) {
    if (typeof url === 'string' && url.startsWith(`${DEEP_LINK_SCHEME}:`)) this.#deliver(url);
  }

  pushFile(filePath) {
    if (typeof filePath === 'string' && filePath) this.#deliver(filePath);
  }

  /** @param {(target: string) => void} handler */
  setHandler(handler) {
    this.#handler = handler;
    const pending = [...this.#urls.splice(0), ...this.#files.splice(0)];
    for (const item of pending) this.#deliver(item);
  }

  #deliver(item) {
    if (this.#handler) this.#handler(item);
    else if (item.startsWith(`${DEEP_LINK_SCHEME}:`)) this.#urls.push(item);
    else this.#files.push(item);
  }
}

/** Register the protocol client and process-level event sources. */
export function registerDeepLinks({ queue, logger, isPackaged }) {
  if (process.defaultApp && !isPackaged && process.argv.length >= 2) {
    app.setAsDefaultProtocolClient(DEEP_LINK_SCHEME, process.execPath, [process.argv[1]]);
  } else {
    app.setAsDefaultProtocolClient(DEEP_LINK_SCHEME);
  }

  app.on('open-url', (event, url) => {
    event.preventDefault();
    logger.info('deeplink.open-url', { scheme: url.split('://')[0] });
    queue.pushUrl(url);
  });
  app.on('open-file', (event, filePath) => {
    event.preventDefault();
    logger.info('deeplink.open-file');
    queue.pushFile(filePath);
  });
}

/** Extract a blazorly:// URL from a second-instance argv, if present. */
export function deepLinkFromArgv(argv) {
  return argv.find((arg) => typeof arg === 'string' && arg.startsWith(`${DEEP_LINK_SCHEME}://`)) ?? null;
}
