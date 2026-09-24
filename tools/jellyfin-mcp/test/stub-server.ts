/**
 * A stub Jellyfin server.
 *
 * It answers the handful of endpoints the MCP tools use and, crucially, records the headers of
 * every request it received, so a test can assert that gateway headers reached each one.
 */

import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import { AddressInfo } from 'node:net';

export interface RecordedRequest {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: string;
}

export interface StubOptions {
  /** Header names that must be present, or the stub answers 403 like a gateway would. */
  requiredHeaders?: Record<string, string>;
  /** Credentials the stub accepts. */
  username?: string;
  password?: string;
  isAdministrator?: boolean;
  /** Bytes served for a video stream. */
  mediaBytes?: Buffer;
  /** When set, PlaybackInfo answers with this ErrorCode instead of a media source. */
  playbackErrorCode?: string;
  logText?: string;
  libraries?: Array<{ Id: string; Name: string; CollectionType?: string }>;
  items?: Array<{ Id: string; Name: string; Type?: string }>;
}

export class StubJellyfin {
  readonly requests: RecordedRequest[] = [];
  private server: Server | null = null;
  private settings: Record<string, unknown> = {
    ParentUrl: null,
    Username: null,
    Password: null,
    HasPassword: false,
    CustomHeaders: [],
    PrefetchEpisodeCount: 4,
    MaxCacheSizeMb: 5120,
    DownloadWaitTimeoutSeconds: 900,
    SyncIntervalHours: 6,
  };
  private syncCompletedUtc: string | null = null;

  constructor(readonly options: StubOptions = {}) {}

  async start(): Promise<string> {
    this.server = createServer((req, res) => {
      void this.handle(req, res);
    });
    await new Promise<void>((resolve) => this.server!.listen(0, '127.0.0.1', resolve));
    const address = this.server.address() as AddressInfo;
    return `http://127.0.0.1:${address.port}`;
  }

  async stop(): Promise<void> {
    if (this.server) {
      await new Promise<void>((resolve, reject) =>
        this.server!.close((error) => (error ? reject(error) : resolve())),
      );
      this.server = null;
    }
  }

  /** Every recorded request that reached a path containing `fragment`. */
  requestsFor(fragment: string): RecordedRequest[] {
    return this.requests.filter((request) => request.path.includes(fragment));
  }

  private async readBody(req: IncomingMessage): Promise<string> {
    const chunks: Buffer[] = [];
    for await (const chunk of req) {
      chunks.push(chunk as Buffer);
    }
    return Buffer.concat(chunks).toString('utf8');
  }

  private json(res: ServerResponse, status: number, payload: unknown): void {
    const text = JSON.stringify(payload);
    res.writeHead(status, { 'Content-Type': 'application/json' });
    res.end(text);
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const body = await this.readBody(req);
    const path = req.url ?? '';
    const headers: Record<string, string> = {};
    for (const [name, value] of Object.entries(req.headers)) {
      headers[name.toLowerCase()] = Array.isArray(value) ? value.join(', ') : (value ?? '');
    }
    this.requests.push({ method: req.method ?? 'GET', path, headers, body });

    // The gateway check comes first, exactly like a real one: nothing downstream is reached.
    for (const [name, value] of Object.entries(this.options.requiredHeaders ?? {})) {
      if (headers[name.toLowerCase()] !== value) {
        res.writeHead(403, { 'Content-Type': 'text/plain' });
        res.end('access denied by stub gateway');
        return;
      }
    }

    if (path.startsWith('/System/Info/Public')) {
      this.json(res, 200, { ServerName: 'Stub', Version: '12.1.0', Id: 'stub-id' });
      return;
    }

    if (path.startsWith('/Users/AuthenticateByName')) {
      const parsed = JSON.parse(body || '{}') as { Username?: string; Pw?: string };
      const expectedUser = this.options.username ?? 'admin';
      const expectedPass = this.options.password ?? 'password';
      if (parsed.Username !== expectedUser || parsed.Pw !== expectedPass) {
        res.writeHead(401);
        res.end();
        return;
      }
      this.json(res, 200, {
        AccessToken: 'stub-token',
        ServerId: 'stub-id',
        User: {
          Id: 'user-1',
          Name: expectedUser,
          Policy: { IsAdministrator: this.options.isAdministrator !== false },
        },
      });
      return;
    }

    if (path.startsWith('/ChildServer/Configuration')) {
      if (req.method === 'POST') {
        const parsed = JSON.parse(body || '{}') as Record<string, unknown>;
        for (const [key, value] of Object.entries(parsed)) {
          if (key === 'Password') {
            if (typeof value === 'string' && value.length > 0) {
              this.settings.HasPassword = true;
            }
            continue;
          }
          this.settings[key] = value;
        }
        res.writeHead(204);
        res.end();
        return;
      }
      this.json(res, 200, { ...this.settings, Password: null });
      return;
    }

    if (path.startsWith('/ChildServer/TestConnection')) {
      const parsed = JSON.parse(body || '{}') as { CustomHeaders?: Array<{ Name: string; Value: string }> };
      const supplied = parsed.CustomHeaders ?? (this.settings.CustomHeaders as Array<{ Name: string; Value: string }>);
      const needed = this.options.requiredHeaders ?? {};
      const satisfied = Object.entries(needed).every(([name, value]) =>
        supplied?.some((header) => header.Name.toLowerCase() === name.toLowerCase() && header.Value === value),
      );
      if (!satisfied) {
        this.json(res, 200, {
          Status: 'AccessDenied',
          Message: 'The parent server, or a proxy in front of it, denied access (HTTP 403).',
          IsSuccess: false,
        });
        return;
      }
      this.json(res, 200, {
        Status: 'Success',
        Message: 'Connected.',
        ServerName: 'Parent',
        ServerVersion: '12.1.0',
        UserId: 'parent-user',
        IsSuccess: true,
      });
      return;
    }

    if (path.startsWith('/ChildServer/Connect')) {
      this.json(res, 200, {
        Status: 'Success',
        Message: 'Signed in.',
        ServerName: 'Parent',
        UserId: 'parent-user',
        IsSuccess: true,
      });
      return;
    }

    if (path.startsWith('/ChildServer/Sync')) {
      this.syncCompletedUtc = new Date(Date.now() + 1000).toISOString();
      res.writeHead(202);
      res.end();
      return;
    }

    if (path.startsWith('/ChildServer/Status')) {
      this.json(res, 200, {
        IsConfigured: Boolean(this.settings.ParentUrl),
        IsAuthenticated: true,
        ParentServerName: 'Parent',
        SyncState: 'Idle',
        LastSyncCompletedUtc: this.syncCompletedUtc,
        LastSyncError: null,
        MirroredItemCount: 17,
        CachedItemCount: 1,
        CachedBytes: 1024,
        ActiveDownloads: 0,
      });
      return;
    }

    if (path.startsWith('/System/Logs/Log')) {
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      res.end(this.options.logText ?? '');
      return;
    }

    if (path.startsWith('/System/Logs')) {
      this.json(res, 200, [
        { Name: 'old.log', Size: 10, DateCreated: '2020-01-01T00:00:00Z', DateModified: '2020-01-01T00:00:00Z' },
        { Name: 'new.log', Size: 20, DateCreated: '2026-01-01T00:00:00Z', DateModified: '2026-01-01T00:00:00Z' },
      ]);
      return;
    }

    if (path.startsWith('/UserViews')) {
      this.json(res, 200, {
        Items: this.options.libraries ?? [
          { Id: 'lib-1', Name: 'Movies', CollectionType: 'movies' },
          { Id: 'lib-2', Name: 'Shows', CollectionType: 'tvshows' },
        ],
        TotalRecordCount: 2,
      });
      return;
    }

    if (path.startsWith('/Items/') && path.includes('/PlaybackInfo')) {
      if (this.options.playbackErrorCode) {
        this.json(res, 200, { ErrorCode: this.options.playbackErrorCode, MediaSources: [] });
        return;
      }
      this.json(res, 200, {
        PlaySessionId: 'session-1',
        MediaSources: [{ Id: 'ms-1', Container: 'mp4', Size: 1024, SupportsDirectPlay: true }],
      });
      return;
    }

    if (path.startsWith('/Items')) {
      this.json(res, 200, {
        Items: this.options.items ?? [{ Id: 'item-1', Name: 'Big Buck Bunny', Type: 'Movie' }],
        TotalRecordCount: 1,
      });
      return;
    }

    if (path.startsWith('/Videos/')) {
      const media = this.options.mediaBytes ?? Buffer.alloc(4096, 7);
      res.writeHead(206, {
        'Content-Type': 'video/mp4',
        'Content-Range': `bytes 0-${media.length - 1}/${media.length}`,
      });
      res.end(media);
      return;
    }

    res.writeHead(404);
    res.end();
  }
}
