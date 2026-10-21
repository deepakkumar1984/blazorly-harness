// Engine lifecycle: attach to a running Blazorly server or spawn our own,
// wait until it answers, and keep its output for diagnostics.
//
// Behavioural contract:
//   - Never claim success unless /api/session.list answers with JSON.
//   - A server that dies during startup is a failure with its output attached.
//   - Attached servers are the user's process: we detach, we never kill them.

import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import net from 'node:net';
import { redact } from './logger.mjs';

const READY_TIMEOUT_MS = 90_000;
const PROBE_TIMEOUT_MS = 1_500;
const SHUTDOWN_GRACE_MS = 5_000;
const LOG_TAIL_LINES = 200;

async function probe(baseUrl, token = null) {
  try {
    const headers = token ? { authorization: `Bearer ${token}` } : undefined;
    const res = await fetch(`${baseUrl}/api/session.list`, {
      headers,
      signal: AbortSignal.timeout(PROBE_TIMEOUT_MS),
    });
    if (res.status === 401) return { state: 'unauthorized' };
    if (!res.ok) return { state: 'unexpected-response', status: res.status };
    const body = await res.json();
    return Array.isArray(body?.sessions) ? { state: 'ok' } : { state: 'unexpected-response', status: res.status };
  } catch (error) {
    return { state: 'unreachable', error };
  }
}

/**
 * Split an attach URL into the origin used for navigation and the optional
 * access token. The engine prints startup URLs of the form
 * `http://host:port/?token=…`; the gate also accepts a `blazorly_auth` cookie
 * or a Bearer header, which is what this shell uses after parsing.
 */
export function parseAttachUrl(url) {
  try {
    const parsed = new URL(url);
    const token = parsed.searchParams.get('token');
    parsed.search = '';
    parsed.hash = '';
    const base = parsed.toString().replace(/\/+$/, '');
    return { base, token };
  } catch {
    return null;
  }
}

function getFreePort() {
  return new Promise((resolve, reject) => {
    const srv = net.createServer();
    srv.unref();
    srv.on('error', reject);
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address();
      srv.close(() => resolve(port));
    });
  });
}

export class ServerManager extends EventEmitter {
  #logTail = [];

  /**
   * @param {object} opts
   * @param {import('./logger.mjs').Logger} opts.logger
   * @param {{source: string, file: string}|null} opts.binary
   * @param {number[]} opts.preferredPorts  probed for an existing engine, then
   *                                        used for spawn in order of preference
   * @param {string} opts.cwd
   * @param {string|null} [opts.attachUrl]  explicit engine URL; spawn is disabled
   */
  constructor({ logger, binary, preferredPorts, cwd, attachUrl = null }) {
    super();
    this.logger = logger;
    this.binary = binary;
    this.preferredPorts = [...new Set(preferredPorts)].filter((p) => Number.isInteger(p) && p > 0);
    this.cwd = cwd;
    this.attach = attachUrl ? parseAttachUrl(attachUrl) : null;
    /** Access token presented to a remote engine; applied as a session cookie. */
    this.authToken = null;
    this.child = null;
    this.status = 'stopped'; // stopped | starting | running | attached | failed
    this.port = null;
    this.baseUrl = null;
    this.attachMode = false;
    this.failure = null; // { stage, message }
  }

  /** Diagnostics snapshot for the error page / tray / support. */
  diagnostics() {
    return {
      status: this.status,
      port: this.port,
      baseUrl: this.baseUrl,
      attachMode: this.attachMode,
      pid: this.child?.pid ?? null,
      binary: this.binary?.file ?? null,
      binarySource: this.binary?.source ?? null,
      failure: this.failure,
      logTail: [...this.#logTail],
    };
  }

  #setStatus(status, extra = {}) {
    this.status = status;
    this.emit('status', this.diagnostics());
    this.emit('status-changed', { ...this.diagnostics(), ...extra });
  }

  #pushLog(line) {
    const clean = redact(line).trimEnd();
    if (!clean) return;
    this.#logTail.push(clean);
    if (this.#logTail.length > LOG_TAIL_LINES) this.#logTail.shift();
    this.emit('log', clean);
  }

  /**
   * Try `preferredPort` first: if a healthy Blazorly server already answers
   * there we attach to it (the user may be mid-session from the browser).
   * Otherwise we spawn our own engine on the preferred or a free port.
   */
  async attachOrSpawn() {
    if (this.attach) {
      const { base, token } = this.attach;
      const result = await probe(base, token);
      if (result.state === 'ok') {
        this.attachMode = true;
        this.baseUrl = base;
        this.port = Number(new URL(base).port) || null;
        this.authToken = token;
        this.logger.info('server.attached-explicit', { baseUrl: base });
        this.#setStatus('attached');
        return this.diagnostics();
      }
      this.failure = { stage: 'attach', message: attachFailureMessage(base, result) };
      this.logger.warning('server.attach-failed', { baseUrl: base, state: result.state, status: result.status });
      this.#setStatus('failed');
      return this.diagnostics();
    }

    for (const candidate of this.preferredPorts) {
      const preferred = `http://127.0.0.1:${candidate}`;
      if ((await probe(preferred)).state === 'ok') {
        this.attachMode = true;
        this.port = candidate;
        this.baseUrl = preferred;
        this.logger.info('server.attached-existing', { baseUrl: preferred });
        this.#setStatus('attached');
        return this.diagnostics();
      }
    }

    if (!this.binary) {
      this.failure = {
        stage: 'locate',
        message:
          'Could not find the blazorly engine. Install it (see the project README), ' +
          'or set BLAZORLY_BIN to the binary path.',
      };
      this.#setStatus('failed');
      return this.diagnostics();
    }

    let port = null;
    for (const candidate of this.preferredPorts) {
      if (await this.#portAvailable(candidate)) {
        port = candidate;
        break;
      }
    }
    port ??= await getFreePort();
    return this.#spawn(port);
  }

  async #portAvailable(port) {
    return new Promise((resolve) => {
      const srv = net.createServer();
      srv.once('error', () => resolve(false));
      srv.once('listening', () => srv.close(() => resolve(true)));
      srv.listen(port, '127.0.0.1');
    });
  }

  async #spawn(port) {
    this.#setStatus('starting');
    this.failure = null;
    const args = ['serve', '--no-open', '--port', String(port)];
    this.logger.info('server.spawn', {
      binary: this.binary.file,
      source: this.binary.source,
      args,
    });

    const child = spawn(this.binary.file, args, {
      cwd: this.cwd,
      env: { ...process.env },
      stdio: ['ignore', 'pipe', 'pipe'],
      // New process group on POSIX so shutdown can take the whole tree down.
      detached: process.platform !== 'win32',
      windowsHide: true,
    });
    this.child = child;
    this.port = port;
    this.baseUrl = `http://127.0.0.1:${port}`;

    const onData = (buf) => {
      for (const line of buf.toString('utf8').split(/\r?\n/)) this.#pushLog(line);
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);

    const exited = new Promise((resolve) => {
      child.once('exit', (code, signal) => resolve({ code, signal }));
      child.once('error', (error) => resolve({ error }));
    });

    const deadline = Date.now() + READY_TIMEOUT_MS;
    while (Date.now() < deadline) {
      if ((await probe(this.baseUrl)).state === 'ok') {
        this.logger.info('server.ready', { baseUrl: this.baseUrl, pid: child.pid });
        this.#setStatus('running');
        this.#watchExit(child, exited);
        return this.diagnostics();
      }
      const done = await Promise.race([
        exited.then((r) => r),
        new Promise((r) => setTimeout(() => r(null), 400)),
      ]);
      if (done) {
        if (this.child !== child) return this.diagnostics(); // stopped during startup
        const message = done.error
          ? `The engine failed to start: ${done.error.message}`
          : `The engine exited during startup (code ${done.code ?? 'n/a'}${done.signal ? `, signal ${done.signal}` : ''}).`;
        this.failure = { stage: 'startup', message };
        this.logger.error('server.failed-during-startup', { message });
        this.#setStatus('failed');
        return this.diagnostics();
      }
    }

    this.failure = {
      stage: 'timeout',
      message: `The engine did not answer on ${this.baseUrl} within ${READY_TIMEOUT_MS / 1000}s.`,
    };
    this.logger.error('server.ready-timeout', { baseUrl: this.baseUrl });
    this.#setStatus('failed');
    return this.diagnostics();
  }

  async #watchExit(child, exitedPromise) {
    const done = await exitedPromise;
    // Expected shutdown/replace: this child is no longer the current one.
    if (this.child !== child) return;
    const message = done.error
      ? `The engine process failed: ${done.error.message}`
      : `The engine stopped unexpectedly (code ${done.code ?? 'n/a'}${done.signal ? `, signal ${done.signal}` : ''}).`;
    this.failure = { stage: 'crash', message };
    this.child = null;
    this.logger.error('server.exited-unexpectedly', { message });
    this.#setStatus('failed');
    this.emit('crashed', this.diagnostics());
  }

  /** Stop a managed child; detach from an attached server without touching it. */
  async stop() {
    if (!this.child) {
      this.#setStatus('stopped');
      return;
    }
    const child = this.child;
    this.child = null;
    this.#setStatus('stopped');
    this.logger.info('server.stop', { pid: child.pid });
    await new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.#kill(child, 'SIGKILL');
        resolve();
      }, SHUTDOWN_GRACE_MS);
      child.once('exit', () => {
        clearTimeout(timer);
        resolve();
      });
      this.#kill(child, 'SIGINT');
    });
  }

  #kill(child, signal) {
    if (!child?.pid) return;
    try {
      if (process.platform === 'win32') {
        spawn('taskkill', ['/pid', String(child.pid), '/T', '/F'], { windowsHide: true });
      } else {
        process.kill(-child.pid, signal); // whole process group
      }
    } catch {
      try {
        child.kill(signal);
      } catch {
        // already gone
      }
    }
  }

  /** Best-effort synchronous kill for hard exits (e.g. terminal Ctrl+C in dev). */
  killSync() {
    if (this.child) {
      try {
        if (process.platform === 'win32') {
          spawn('taskkill', ['/pid', String(this.child.pid), '/T', '/F'], {
            windowsHide: true,
            stdio: 'ignore',
          });
        } else {
          process.kill(-this.child.pid, 'SIGTERM');
        }
      } catch {
        // best effort only
      }
    }
  }

  async restart() {
    const wasAttached = this.attachMode;
    await this.stop();
    this.attachMode = false;
    this.failure = null;
    if (wasAttached) this.logger.info('server.restart-after-attached');
    return this.attachOrSpawn();
  }
}

/**
 * Distinguish "nothing there" from "there is an engine but it wants its token":
 * a token-gated engine answers 401 to the health probe, and the fix is to pass
 * the startup URL (which carries ?token=…) as --attach.
 */
function attachFailureMessage(base, result) {
  if (result.state === 'unauthorized') {
    return (
      `The engine at ${base} answered but requires its access token. ` +
      `Attach with the URL printed when the engine started, e.g. --attach "${base}/?token=…".`
    );
  }
  if (result.state === 'unexpected-response') {
    return `Something answered at ${base} (HTTP ${result.status}) but it is not a Blazorly engine.`;
  }
  return `Could not reach a Blazorly engine at ${base}. Start it, or run the app without --attach to launch one.`;
}
