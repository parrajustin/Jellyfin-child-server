/**
 * A small Jellyfin HTTP client.
 *
 * Two things it does that a generic fetch wrapper would not:
 *
 *  - it builds the `MediaBrowser` authorization header Jellyfin expects, and
 *  - it carries a set of extra headers on *every* request, which is how a server published
 *    behind Cloudflare Access (or any similar gateway) is reached. The same client is therefore
 *    used both for the child server and, when the agent wants to look at the parent directly,
 *    for the parent.
 */

import type { LogFile } from './types.js';

export interface ClientOptions {
  baseUrl: string;
  /** Sent with every request. Use for CF-Access-Client-Id / CF-Access-Client-Secret and friends. */
  extraHeaders?: Record<string, string>;
  clientName?: string;
  deviceName?: string;
  deviceId?: string;
  version?: string;
  timeoutMs?: number;
}

export class JellyfinError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body: string,
    readonly url: string,
  ) {
    super(message);
    this.name = 'JellyfinError';
  }
}

/** Trims a body so a failure message stays readable in a tool result. */
function summarise(body: string, limit = 600): string {
  const collapsed = body.replace(/\s+/g, ' ').trim();
  return collapsed.length > limit ? `${collapsed.slice(0, limit)}…` : collapsed;
}

export class JellyfinClient {
  readonly baseUrl: string;
  readonly extraHeaders: Record<string, string>;
  private readonly clientName: string;
  private readonly deviceName: string;
  private readonly deviceId: string;
  private readonly version: string;
  private readonly timeoutMs: number;

  token: string | null = null;
  userId: string | null = null;

  constructor(options: ClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/+$/, '');
    this.extraHeaders = { ...(options.extraHeaders ?? {}) };
    this.clientName = options.clientName ?? 'Jellyfin Child Server MCP';
    this.deviceName = options.deviceName ?? 'mcp';
    this.deviceId = options.deviceId ?? 'jellyfin-child-server-mcp';
    this.version = options.version ?? '1.0.0';
    this.timeoutMs = options.timeoutMs ?? 30_000;
  }

  /** `MediaBrowser Client="…", Device="…", DeviceId="…", Version="…"[, Token="…"]` */
  authHeader(): string {
    const parts = [
      `Client="${encodeURIComponent(this.clientName)}"`,
      `Device="${encodeURIComponent(this.deviceName)}"`,
      `DeviceId="${encodeURIComponent(this.deviceId)}"`,
      `Version="${encodeURIComponent(this.version)}"`,
    ];
    if (this.token) {
      parts.push(`Token="${encodeURIComponent(this.token)}"`);
    }
    return `MediaBrowser ${parts.join(', ')}`;
  }

  url(path: string): string {
    if (/^https?:\/\//i.test(path)) {
      return path;
    }
    return `${this.baseUrl}${path.startsWith('/') ? '' : '/'}${path}`;
  }

  useAuth(token: string, userId: string): this {
    this.token = token;
    this.userId = userId;
    return this;
  }

  /**
   * The single choke point for outbound requests. Every call goes through here, so the extra
   * gateway headers cannot be dropped by a new call site that forgets them.
   */
  async raw(path: string, init: RequestInit = {}): Promise<Response> {
    const headers = new Headers(init.headers);
    headers.set('Authorization', this.authHeader());
    for (const [name, value] of Object.entries(this.extraHeaders)) {
      headers.set(name, value);
    }
    const signal = init.signal ?? AbortSignal.timeout(this.timeoutMs);
    return fetch(this.url(path), { ...init, headers, signal });
  }

  private async readJson<T>(method: string, path: string, response: Response): Promise<T> {
    const text = await response.text();
    if (!response.ok) {
      throw new JellyfinError(
        `${method} ${this.url(path)} -> HTTP ${response.status}${text ? `: ${summarise(text)}` : ''}`,
        response.status,
        text,
        this.url(path),
      );
    }
    if (!text) {
      return undefined as T;
    }
    try {
      return JSON.parse(text) as T;
    } catch {
      throw new JellyfinError(
        `${method} ${this.url(path)} answered ${response.status} with a body that is not JSON: ${summarise(text, 200)}`,
        response.status,
        text,
        this.url(path),
      );
    }
  }

  async get<T>(path: string): Promise<T> {
    const response = await this.raw(path, { method: 'GET', headers: { Accept: 'application/json' } });
    return this.readJson<T>('GET', path, response);
  }

  async getText(path: string): Promise<string> {
    const response = await this.raw(path, { method: 'GET' });
    const text = await response.text();
    if (!response.ok) {
      throw new JellyfinError(
        `GET ${this.url(path)} -> HTTP ${response.status}`,
        response.status,
        text,
        this.url(path),
      );
    }
    return text;
  }

  async post<T>(path: string, body?: unknown): Promise<T> {
    const response = await this.raw(path, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify(body ?? {}),
    });
    return this.readJson<T>('POST', path, response);
  }

  /** For endpoints whose status code is the answer (204 save, 202 sync). */
  async postForStatus(path: string, body?: unknown): Promise<number> {
    const response = await this.raw(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body ?? {}),
    });
    await response.arrayBuffer();
    return response.status;
  }

  // ----- the handful of stock Jellyfin endpoints the tools need -----

  async publicSystemInfo<T>(): Promise<T> {
    return this.get<T>('/System/Info/Public');
  }

  async logFiles(): Promise<LogFile[]> {
    return this.get<LogFile[]>('/System/Logs');
  }

  async logFile(name: string): Promise<string> {
    return this.getText(`/System/Logs/Log?name=${encodeURIComponent(name)}`);
  }
}
