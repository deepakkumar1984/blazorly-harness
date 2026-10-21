// Structured, redacting logger.
//
// The reference desktop app separates "safe" fields from "sensitive" ones in
// every log line; we keep the same discipline with a simpler shape: sensitive
// substrings (access tokens, API keys) are redacted before anything is written
// to disk. Logs land in <userData>/logs/desktop-YYYY-MM-DD.log and on stderr
// when running from a terminal.

import fs from 'node:fs';
import path from 'node:path';

const REDACTIONS = [
  // Access tokens the harness prints in its startup URL (?token=...).
  /([?&]token=)[^&\s"']+/gi,
  // Provider API keys that could slip into command output.
  /(sk-[A-Za-z0-9_-]{8})[A-Za-z0-9_-]+/g,
  /(tsk-[A-Za-z0-9_-]{8})[A-Za-z0-9_-]+/g,
];

export function redact(text) {
  let out = String(text ?? '');
  out = out.replace(REDACTIONS[0], '$1[redacted]');
  out = out.replace(REDACTIONS[1], '$1…');
  out = out.replace(REDACTIONS[2], '$1…');
  return out;
}

export class Logger {
  #stream = null;
  #file = null;

  constructor({ dir, alsoStderr = true } = {}) {
    this.alsoStderr = alsoStderr;
    try {
      fs.mkdirSync(dir, { recursive: true });
      const day = new Date().toISOString().slice(0, 10);
      this.#file = path.join(dir, `desktop-${day}.log`);
      this.#stream = fs.createWriteStream(this.#file, { flags: 'a' });
    } catch {
      // Logging must never take the app down; fall back to stderr only.
      this.#stream = null;
    }
  }

  get filePath() {
    return this.#file;
  }

  #write(level, event, fields) {
    const entry = { ts: new Date().toISOString(), level, event, ...fields };
    const line = redact(JSON.stringify(entry));
    if (this.#stream) this.#stream.write(line + '\n');
    if (this.alsoStderr && level !== 'debug') process.stderr.write(line + '\n');
  }

  info(event, fields = {}) {
    this.#write('info', event, fields);
  }

  debug(event, fields = {}) {
    this.#write('debug', event, fields);
  }

  warning(event, fields = {}) {
    this.#write('warning', event, fields);
  }

  error(event, fields = {}) {
    const { error, ...rest } = fields;
    this.#write('error', event, {
      ...rest,
      error: error instanceof Error ? `${error.name}: ${error.message}` : error,
    });
  }
}
