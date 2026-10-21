// Resolution of the blazorly engine binary and per-platform defaults.

import fs from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const PRODUCT_NAME = 'Blazorly Harness';
export const DEEP_LINK_SCHEME = 'blazorly';
export const DEFAULT_PORT = 5080;

const here = path.dirname(fileURLToPath(import.meta.url));
export const APP_ROOT = path.resolve(here, '..', '..');

function executableName() {
  return process.platform === 'win32' ? 'blazorly.exe' : 'blazorly';
}

function searchPath(name) {
  const exts = process.platform === 'win32'
    ? (process.env.PATHEXT ?? '.EXE').split(';').map((e) => e.toLowerCase())
    : [''];
  for (const dir of (process.env.PATH ?? '').split(path.delimiter)) {
    if (!dir) continue;
    for (const ext of exts) {
      const candidate = path.join(dir, name + ext);
      if (fs.existsSync(candidate)) return candidate;
    }
  }
  return null;
}

/**
 * Locate the engine binary, in priority order:
 *   1. explicit override (--server-bin / BLAZORLY_BIN)
 *   2. bundled copy shipped inside the packaged app (Resources/engine/blazorly,
 *      or a bare Resources/blazorly)
 *   3. the installer's well-known locations (GUI launches do not inherit the
 *      shell PATH, so `~/.local/bin` must be probed explicitly)
 *   4. a sibling dev build from this repository
 *   5. `blazorly` on PATH
 */
export function findServerBinary({ override = null } = {}) {
  // An explicit override is authoritative: if it does not exist we fail rather
  // than silently falling back to a different binary than the caller asked for.
  if (override) {
    try {
      if (fs.statSync(override).isFile()) return { source: 'override', file: override };
    } catch {
      // fall through to null
    }
    return null;
  }
  const candidates = [];
  if (process.env.BLAZORLY_BIN) {
    candidates.push({ source: 'BLAZORLY_BIN', file: process.env.BLAZORLY_BIN });
  }
  if (process.resourcesPath) {
    candidates.push(
      { source: 'bundled-engine', file: path.join(process.resourcesPath, 'engine', executableName()) },
      { source: 'bundled', file: path.join(process.resourcesPath, executableName()) },
    );
  }
  const home = process.env.HOME || homedir();
  if (home) {
    candidates.push(
      { source: 'home-local-bin', file: path.join(home, '.local', 'bin', executableName()) },
      { source: 'home-app-current', file: path.join(home, '.blazorly', 'app', 'current', executableName()) },
    );
  }
  candidates.push({
    source: 'dev-build',
    file: path.join(APP_ROOT, '..', 'src', 'Blazorly.Harness.Cli', 'bin', 'Debug', 'net10.0', executableName()),
  });

  for (const candidate of candidates) {
    try {
      if (candidate.file && fs.statSync(candidate.file).isFile()) return candidate;
    } catch {
      // keep looking
    }
  }

  const onPath = searchPath(executableName());
  if (onPath) return { source: 'path', file: onPath };
  return null;
}

/** URL scheme route mapping for `blazorly://<route>` deep links. */
export function deepLinkTarget(url) {
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== `${DEEP_LINK_SCHEME}:`) return null;
    // blazorly://settings, blazorly://session/<id> — host carries the first
    // segment because URL parsers put it there for custom schemes.
    const first = (parsed.hostname || '').toLowerCase();
    const rest = parsed.pathname.replace(/^\/+/, '');
    if (first === 'session') {
      const id = rest.split('/')[0];
      if (/^[A-Za-z0-9._-]{1,128}$/.test(id)) return `/session/${id}`;
      return '/';
    }
    if (first === 'settings') return '/settings';
    return '/';
  } catch {
    return null;
  }
}
