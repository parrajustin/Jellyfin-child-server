#!/usr/bin/env node
/**
 * Gate: the only path from the child server to the parent server in the e2e stack.
 *
 * A streaming reverse proxy (GATE_PORT, default 8096 -> GATE_UPSTREAM, default
 * http://parent:8096) plus a control API (GATE_CONTROL_PORT, default 8097) that lets the
 * tests simulate a dead parent and require Cloudflare-Access style headers.
 *
 * Control API (JSON):
 *   GET  /state            -> { down, requiredHeaders, requestCount, upstream }
 *   POST /down             -> stop the proxy listener and destroy open sockets (ECONNREFUSED for clients)
 *   POST /up               -> listen again
 *   POST /require-headers  -> body { "Header-Name": "exact value", ... }; {} clears. Requests missing
 *                             a header or carrying a different value get 403 "access denied by gate"
 *                             without touching the upstream.
 *   GET  /requests         -> last 200 proxied requests: [{ method, path, headers, status }] where
 *                             headers holds only the required header names present (lowercased)
 *   POST /reset            -> clears the request log and counter
 *
 * Node built-ins only. One log line per proxied request on stdout.
 */
import http from 'node:http';
import https from 'node:https';
import { URL } from 'node:url';

const PORT = toPort(process.env.GATE_PORT, 8096);
const CONTROL_PORT = toPort(process.env.GATE_CONTROL_PORT, 8097);
const BIND = process.env.GATE_BIND || '0.0.0.0';
const UPSTREAM = new URL(process.env.GATE_UPSTREAM || 'http://parent:8096');
const LOG_LIMIT = 200;
const MAX_CONTROL_BODY = 64 * 1024;

const HOP_BY_HOP = new Set([
  'connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'proxy-connection',
  'te',
  'trailer',
  'trailers',
  'transfer-encoding',
  'upgrade',
]);

const transport = UPSTREAM.protocol === 'https:' ? https : http;
const upstreamPort = UPSTREAM.port ? Number(UPSTREAM.port) : UPSTREAM.protocol === 'https:' ? 443 : 80;
const agent = new transport.Agent({ keepAlive: true, maxSockets: 256 });

/** @type {{ down: boolean, requiredHeaders: Record<string, string>, requestCount: number }} */
const state = { down: false, requiredHeaders: {}, requestCount: 0 };
/** @type {Array<{ method: string, path: string, headers: Record<string, string>, status: number }>} */
const requestLog = [];
/** @type {Set<import('node:net').Socket>} */
const proxySockets = new Set();
/** Serializes down/up transitions. */
let transition = Promise.resolve();

function toPort(value, fallback) {
  const n = Number(value);
  return Number.isInteger(n) && n > 0 && n < 65536 ? n : fallback;
}

function log(line) {
  process.stdout.write(`${new Date().toISOString()} ${line}\n`);
}

function record(entry) {
  requestLog.push(entry);
  if (requestLog.length > LOG_LIMIT) {
    requestLog.splice(0, requestLog.length - LOG_LIMIT);
  }
}

function headerText(value) {
  return Array.isArray(value) ? value.join(', ') : value;
}

/** Only the required header names that are present, lowercased. */
function pickRequiredHeaders(headers) {
  const picked = {};
  for (const name of Object.keys(state.requiredHeaders)) {
    if (headers[name] !== undefined) {
      picked[name] = headerText(headers[name]);
    }
  }
  return picked;
}

/** Name of the first required header that is missing or different, or null. */
function findMissingHeader(headers) {
  for (const [name, expected] of Object.entries(state.requiredHeaders)) {
    const actual = headers[name];
    if (actual === undefined || headerText(actual) !== expected) {
      return name;
    }
  }
  return null;
}

function copyHeaders(source) {
  const copy = {};
  for (const [name, value] of Object.entries(source)) {
    if (value !== undefined && !HOP_BY_HOP.has(name)) {
      copy[name] = value;
    }
  }
  return copy;
}

function sendText(res, status, text) {
  if (res.headersSent || res.destroyed) {
    res.destroy();
    return;
  }
  res.writeHead(status, {
    'content-type': 'text/plain; charset=utf-8',
    'content-length': Buffer.byteLength(text),
    'cache-control': 'no-store',
  });
  res.end(text);
}

function sendJson(res, status, value) {
  const text = JSON.stringify(value);
  if (res.headersSent || res.destroyed) {
    res.destroy();
    return;
  }
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(text),
    'cache-control': 'no-store',
  });
  res.end(text);
}

// ---------------------------------------------------------------------------
// Proxy
// ---------------------------------------------------------------------------

function handleProxyRequest(req, res) {
  const startedAt = process.hrtime.bigint();
  const method = req.method || 'GET';
  const path = req.url || '/';
  state.requestCount += 1;
  const entry = { method, path, headers: pickRequiredHeaders(req.headers), status: 0 };
  record(entry);

  let logged = false;
  const finish = (status, note) => {
    if (logged) {
      return;
    }
    logged = true;
    entry.status = status;
    const ms = Number(process.hrtime.bigint() - startedAt) / 1e6;
    log(`proxy ${method} ${path} -> ${status}${note ? ` (${note})` : ''} ${ms.toFixed(1)}ms`);
  };

  const denied = findMissingHeader(req.headers);
  if (denied) {
    req.resume();
    sendText(res, 403, 'access denied by gate');
    finish(403, `required header "${denied}" missing or different`);
    return;
  }

  const headers = copyHeaders(req.headers);
  headers.host = UPSTREAM.host;

  const upstreamReq = transport.request(
    {
      agent,
      protocol: UPSTREAM.protocol,
      hostname: UPSTREAM.hostname,
      port: upstreamPort,
      method,
      path,
      headers,
    },
    (upstreamRes) => {
      const status = upstreamRes.statusCode || 502;
      if (res.destroyed) {
        upstreamRes.resume();
        finish(status, 'client went away');
        return;
      }
      res.once('finish', () => finish(status));
      upstreamRes.once('error', (err) => {
        finish(status, `upstream body error: ${err.message}`);
        res.destroy(err);
      });
      // Status, headers (minus hop-by-hop) and body are streamed through; Range requests and
      // 206 responses pass untouched.
      res.writeHead(status, copyHeaders(upstreamRes.headers));
      upstreamRes.pipe(res);
    },
  );

  upstreamReq.once('error', (err) => {
    const reason = err.code || err.message;
    if (res.headersSent) {
      finish(entry.status || 502, `upstream error after headers: ${reason}`);
      res.destroy(err);
      return;
    }
    sendText(res, 502, `gate: upstream ${UPSTREAM.host} error: ${reason}`);
    finish(502, `upstream error: ${reason}`);
  });
  req.once('error', (err) => {
    upstreamReq.destroy(err);
    finish(499, `client request error: ${err.message}`);
  });
  res.once('close', () => {
    if (!res.writableFinished) {
      upstreamReq.destroy();
      finish(499, 'client closed');
    }
  });
  req.pipe(upstreamReq);
}

const proxy = http.createServer(handleProxyRequest);
proxy.keepAliveTimeout = 5_000;
proxy.on('connection', (socket) => {
  proxySockets.add(socket);
  socket.once('close', () => proxySockets.delete(socket));
});
proxy.on('clientError', (err, socket) => {
  if (socket.writable && !socket.destroyed) {
    socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n');
  } else {
    socket.destroy();
  }
});

function listenProxy() {
  return new Promise((resolve, reject) => {
    const onError = (err) => reject(err);
    proxy.once('error', onError);
    proxy.listen(PORT, BIND, () => {
      proxy.off('error', onError);
      resolve();
    });
  });
}

function closeProxy() {
  return new Promise((resolve) => {
    if (!proxy.listening) {
      for (const socket of proxySockets) {
        socket.destroy();
      }
      resolve();
      return;
    }
    proxy.close(() => resolve());
    for (const socket of proxySockets) {
      socket.destroy();
    }
  });
}

async function goDown() {
  if (state.down) {
    return;
  }
  state.down = true;
  await closeProxy();
  log(`control: proxy DOWN (${BIND}:${PORT} closed, open sockets destroyed)`);
}

async function goUp() {
  if (!state.down) {
    return;
  }
  await listenProxy();
  state.down = false;
  log(`control: proxy UP (${BIND}:${PORT})`);
}

function enqueue(step) {
  const run = transition.then(step, step);
  transition = run.catch(() => undefined);
  return run;
}

// ---------------------------------------------------------------------------
// Control API
// ---------------------------------------------------------------------------

function snapshot() {
  return {
    down: state.down,
    requiredHeaders: { ...state.requiredHeaders },
    requestCount: state.requestCount,
    upstream: UPSTREAM.origin,
  };
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_CONTROL_BODY) {
        const err = new Error(`body larger than ${MAX_CONTROL_BODY} bytes`);
        err.status = 413;
        req.destroy(err);
        reject(err);
        return;
      }
      chunks.push(chunk);
    });
    req.once('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.once('error', reject);
  });
}

function parseHeaderMap(text) {
  const trimmed = text.trim();
  const value = trimmed === '' ? {} : JSON.parse(trimmed);
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('body must be a JSON object of header name -> value');
  }
  const headers = {};
  for (const [name, raw] of Object.entries(value)) {
    const key = name.trim().toLowerCase();
    if (!/^[!#$%&'*+.^_`|~0-9a-z-]+$/.test(key)) {
      throw new Error(`invalid header name "${name}"`);
    }
    if (typeof raw !== 'string' && typeof raw !== 'number' && typeof raw !== 'boolean') {
      throw new Error(`value of header "${name}" must be a string`);
    }
    headers[key] = String(raw);
  }
  return headers;
}

async function handleControlRequest(req, res) {
  const method = req.method || 'GET';
  const url = new URL(req.url || '/', 'http://gate.control');
  const route = `${method} ${url.pathname.replace(/\/+$/, '') || '/'}`;

  switch (route) {
    case 'GET /':
    case 'GET /health':
    case 'GET /state':
      req.resume();
      sendJson(res, 200, snapshot());
      return;
    case 'GET /requests':
      req.resume();
      sendJson(
        res,
        200,
        requestLog.map((entry) => ({
          method: entry.method,
          path: entry.path,
          headers: { ...entry.headers },
          status: entry.status,
        })),
      );
      return;
    case 'POST /down':
      await readBody(req);
      await enqueue(goDown);
      sendJson(res, 200, snapshot());
      return;
    case 'POST /up':
      await readBody(req);
      await enqueue(goUp);
      sendJson(res, 200, snapshot());
      return;
    case 'POST /require-headers': {
      const text = await readBody(req);
      let headers;
      try {
        headers = parseHeaderMap(text);
      } catch (err) {
        sendJson(res, 400, { error: err.message });
        return;
      }
      state.requiredHeaders = headers;
      const names = Object.keys(headers);
      log(`control: require headers ${names.length ? names.join(', ') : '(none)'}`);
      sendJson(res, 200, snapshot());
      return;
    }
    case 'POST /reset':
      await readBody(req);
      requestLog.length = 0;
      state.requestCount = 0;
      log('control: request log reset');
      sendJson(res, 200, snapshot());
      return;
    default:
      req.resume();
      sendJson(res, 404, { error: `unknown route ${route}` });
  }
}

const control = http.createServer((req, res) => {
  handleControlRequest(req, res).catch((err) => {
    log(`control ${req.method} ${req.url} failed: ${(err && err.stack) || err}`);
    sendJson(res, err && err.status ? err.status : 500, { error: String((err && err.message) || err) });
  });
});

function listenControl() {
  return new Promise((resolve, reject) => {
    const onError = (err) => reject(err);
    control.once('error', onError);
    control.listen(CONTROL_PORT, BIND, () => {
      control.off('error', onError);
      resolve();
    });
  });
}

// ---------------------------------------------------------------------------
// Process lifecycle
// ---------------------------------------------------------------------------

process.on('unhandledRejection', (reason) => {
  log(`unhandled rejection: ${(reason && reason.stack) || reason}`);
});
process.on('uncaughtException', (err) => {
  log(`uncaught exception: ${(err && err.stack) || err}`);
});

let shuttingDown = false;
function shutdown(signal) {
  if (shuttingDown) {
    return;
  }
  shuttingDown = true;
  log(`${signal} received, shutting down`);
  const timer = setTimeout(() => process.exit(0), 2_000);
  timer.unref();
  control.close(() => undefined);
  closeProxy().then(() => {
    agent.destroy();
    process.exit(0);
  });
}
process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT', () => shutdown('SIGINT'));

await listenProxy();
await listenControl();
log(`gate listening: proxy ${BIND}:${PORT} -> ${UPSTREAM.origin}, control ${BIND}:${CONTROL_PORT}`);
