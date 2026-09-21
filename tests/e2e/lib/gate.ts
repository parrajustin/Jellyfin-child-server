/**
 * Client for the gate control API (tests/e2e/gate/server.mjs). The gate is the only path
 * from the child to the parent; tests use it to simulate an unreachable parent and to
 * require Cloudflare-Access style headers.
 */
import { describeError, sleep } from './jellyfin';

export interface GateState {
  down: boolean;
  /** lowercased header name -> exact value required on every proxied request */
  requiredHeaders: Record<string, string>;
  requestCount: number;
  upstream?: string;
}

export interface GateRequestEntry {
  method: string;
  path: string;
  /** Only the required header names that were present, lowercased. */
  headers: Record<string, string>;
  status: number;
}

export class GateControl {
  readonly controlUrl: string;

  constructor(controlUrl: string) {
    this.controlUrl = controlUrl.replace(/\/+$/, '');
  }

  private async call<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
    const init: RequestInit = { method, headers: { Accept: 'application/json' } };
    if (body !== undefined) {
      init.headers = { ...(init.headers as Record<string, string>), 'Content-Type': 'application/json' };
      init.body = JSON.stringify(body);
    }
    const response = await fetch(`${this.controlUrl}${path}`, init);
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`gate control ${method} ${path} -> HTTP ${response.status}: ${text.slice(0, 300)}`);
    }
    return JSON.parse(text) as T;
  }

  state(): Promise<GateState> {
    return this.call<GateState>('GET', '/state');
  }

  /** The last 200 proxied requests. */
  requests(): Promise<GateRequestEntry[]> {
    return this.call<GateRequestEntry[]>('GET', '/requests');
  }

  /** Stops the proxy listener and drops open sockets: the child sees ECONNREFUSED. */
  down(): Promise<GateState> {
    return this.call<GateState>('POST', '/down');
  }

  up(): Promise<GateState> {
    return this.call<GateState>('POST', '/up');
  }

  /** Every proxied request must carry each header with the exact value; `{}` clears. */
  requireHeaders(headers: Record<string, string>): Promise<GateState> {
    return this.call<GateState>('POST', '/require-headers', headers);
  }

  /** Clears the request log and counter. */
  reset(): Promise<GateState> {
    return this.call<GateState>('POST', '/reset');
  }

  async waitForReady(timeoutMs: number = 120_000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    let last = 'no attempt yet';
    for (;;) {
      try {
        const response = await fetch(`${this.controlUrl}/state`, { signal: AbortSignal.timeout(5_000) });
        if (response.ok) {
          return;
        }
        last = `HTTP ${response.status}`;
      } catch (error) {
        last = describeError(error);
      }
      if (Date.now() >= deadline) {
        break;
      }
      await sleep(1_000);
    }
    throw new Error(`gate control ${this.controlUrl} not ready within ${timeoutMs} ms (last: ${last})`);
  }

  /** Baseline for a run: proxy up, nothing required, empty log. */
  async resetAll(): Promise<void> {
    await this.up();
    await this.requireHeaders({});
    await this.reset();
  }
}
