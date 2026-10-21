// Small persisted shell preferences (userData/desktop-config.json).
//
// We remember the port the engine last ran on: the Blazorly UI stores
// appearance and drafts per browser origin, so reusing the same fallback port
// keeps that state across launches even when the default port is taken.

import fs from 'node:fs';
import path from 'node:path';

const FILE = 'desktop-config.json';

export function loadDesktopConfig(dir, logger) {
  const file = path.join(dir, FILE);
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (error) {
    if (error.code !== 'ENOENT') logger.warning('config.load-failed', { error });
    return {};
  }
}

export function saveDesktopConfig(dir, config, logger) {
  const file = path.join(dir, FILE);
  try {
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(file, JSON.stringify(config, null, 2) + '\n');
  } catch (error) {
    logger.warning('config.save-failed', { error });
  }
}
