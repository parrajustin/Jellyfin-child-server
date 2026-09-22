/**
 * Small typed Jellyfin API client for the end-to-end tests. fetch only, no dependencies.
 * Jellyfin JSON is PascalCase and enums are serialized as strings.
 *
 * The child server endpoints (/ChildServer/...) follow the contract in the README; the
 * server side is written against the same contract.
 */
import { createHash } from 'node:crypto';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

export const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

export function describeError(error: unknown): string {
  if (error instanceof Error) {
    const cause = (error as Error & { cause?: unknown }).cause;
    const causeText =
      cause instanceof Error ? ` (cause: ${cause.message})` : cause !== undefined ? ` (cause: ${String(cause)})` : '';
    return `${error.name}: ${error.message}${causeText}`;
  }
  return String(error);
}

export function sha256(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

export class JellyfinHttpError extends Error {
  constructor(
    readonly method: string,
    readonly url: string,
    readonly status: number,
    readonly body: string,
    detail?: string,
  ) {
    super(
      `${method} ${url} -> HTTP ${status}${detail ? ` (${detail})` : ''}${body ? `: ${body.slice(0, 500)}` : ''}`,
    );
    this.name = 'JellyfinHttpError';
  }
}

// ---------------------------------------------------------------------------
// Jellyfin DTOs (the subset the tests read)
// ---------------------------------------------------------------------------

export interface PublicSystemInfo {
  Id: string;
  ServerName: string;
  Version: string;
  ProductName?: string;
  OperatingSystem?: string;
  LocalAddress?: string;
  StartupWizardCompleted: boolean;
}

export interface StartupConfiguration {
  ServerName?: string | null;
  UICulture?: string | null;
  MetadataCountryCode?: string | null;
  PreferredMetadataLanguage?: string | null;
}

export interface AuthenticationResult {
  User: { Id: string; Name: string; ServerId?: string; Policy?: { IsAdministrator?: boolean } };
  AccessToken: string;
  ServerId?: string;
}

export interface AuthResult {
  userId: string;
  token: string;
}

export interface BaseItem {
  Id: string;
  Name: string;
  Type: string;
  ServerId?: string;
  Path?: string | null;
  IndexNumber?: number | null;
  ParentIndexNumber?: number | null;
  ProductionYear?: number | null;
  SeriesId?: string | null;
  SeriesName?: string | null;
  SeasonId?: string | null;
  SeasonName?: string | null;
  ParentId?: string | null;
  MediaType?: string | null;
  IsFolder?: boolean;
  ChildCount?: number | null;
  RunTimeTicks?: number | null;
  UserData?: { Played?: boolean; PlaybackPositionTicks?: number; PlayCount?: number } | null;
}

export interface ItemsResult<T extends BaseItem = BaseItem> {
  Items: T[];
  TotalRecordCount: number;
  StartIndex?: number;
}

export interface VirtualFolder {
  Name: string;
  Locations: string[];
  CollectionType?: string | null;
  ItemId?: string;
  LibraryOptions?: unknown;
}

export type MediaStreamType = 'Audio' | 'Video' | 'Subtitle' | 'EmbeddedImage' | 'Data' | 'Lyric';

export interface MediaStream {
  Type: MediaStreamType;
  Codec?: string | null;
  Index: number;
  IsDefault?: boolean;
  Width?: number | null;
  Height?: number | null;
  Channels?: number | null;
  Language?: string | null;
}

export interface MediaSource {
  Id: string;
  Path?: string | null;
  Protocol?: string;
  Container?: string | null;
  Size?: number | null;
  Name?: string | null;
  RunTimeTicks?: number | null;
  SupportsDirectPlay?: boolean;
  SupportsDirectStream?: boolean;
  SupportsTranscoding?: boolean;
  TranscodingUrl?: string | null;
  MediaStreams: MediaStream[];
}

export interface PlaybackInfoResponse {
  MediaSources: MediaSource[];
  PlaySessionId?: string | null;
  ErrorCode?: string | null;
}

export type CollectionType = 'movies' | 'tvshows' | 'homevideos';

// ---------------------------------------------------------------------------
// Child server contract
// ---------------------------------------------------------------------------

export interface ParentRequestHeader {
  Name: string;
  Value: string;
}

/** GET /ChildServer/Configuration. Password is never returned. */
export interface ChildServerSettings {
  ParentUrl: string | null;
  Username: string | null;
  Password: string | null;
  HasPassword: boolean;
  CustomHeaders: ParentRequestHeader[];
  PrefetchEpisodeCount: number;
  MaxCacheSizeMb: number;
  DownloadWaitTimeoutSeconds: number;
  SyncIntervalHours: number;
}

/** POST /ChildServer/Configuration. An empty password keeps the stored one. */
export interface ChildServerSettingsInput {
  ParentUrl: string;
  Username: string;
  Password?: string | null;
  CustomHeaders?: ParentRequestHeader[];
  PrefetchEpisodeCount?: number;
  MaxCacheSizeMb?: number;
  DownloadWaitTimeoutSeconds?: number;
  SyncIntervalHours?: number;
}

export type ParentConnectionStatus =
  | 'Success'
  | 'InvalidCredentials'
  | 'AccessDenied'
  | 'ConnectionFailed'
  | 'InvalidResponse'
  | 'NotConfigured';

export interface ParentConnectionResult {
  Status: ParentConnectionStatus;
  Message: string;
  ServerName?: string | null;
  ServerVersion?: string | null;
  ServerId?: string | null;
  UserId?: string | null;
  IsSuccess: boolean;
}

/** POST /ChildServer/TestConnection body. */
export interface TestConnectionRequest {
  Url: string;
  Username: string;
  Password?: string | null;
  CustomHeaders?: ParentRequestHeader[];
}

export type SyncState = 'Idle' | 'Running';

export interface ChildServerStatus {
  IsConfigured: boolean;
  IsAuthenticated: boolean;
  ParentServerName?: string | null;
  ParentServerVersion?: string | null;
  ParentUserId?: string | null;
  LastConnectionStatus?: ParentConnectionStatus | null;
  LastConnectionMessage?: string | null;
  LastConnectionAttemptUtc?: string | null;
  SyncState: SyncState;
  LastSyncStartedUtc?: string | null;
  LastSyncCompletedUtc?: string | null;
  LastSyncError?: string | null;
  MirroredItemCount: number;
  CachedItemCount: number;
  CachedBytes: number;
  ActiveDownloads: number;
}

/** GET /ChildServer/Items/{itemId}/Availability (any signed-in user). */
export interface ItemAvailability {
  IsManaged: boolean;
  IsCached: boolean;
  IsDownloading: boolean;
  DownloadedBytes: number;
  ExpectedBytes: number;
  ParentReachable: boolean;
}

// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------

const ITEM_TYPES_QUERY = { Recursive: 'true', IncludeItemTypes: 'Movie,Episode', Fields: 'Path' } as const;

export class JellyfinApi {
  readonly baseUrl: string;
  token: string | null = null;
  userId: string | null = null;

  constructor(
    baseUrl: string,
    readonly clientName: string,
    readonly deviceId: string,
    readonly version: string = '1.0.0',
  ) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
  }

  /** Restores a session saved in the state file. */
  useAuth(token: string, userId: string): this {
    this.token = token;
    this.userId = userId;
    return this;
  }

  /** `MediaBrowser Client="...", Device="e2e", DeviceId="...", Version="1.0.0"[, Token="..."]` */
  authHeader(): string {
    const parts = [
      `Client="${encodeURIComponent(this.clientName)}"`,
      'Device="e2e"',
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

  // ----- transport -----

  async raw(path: string, init: RequestInit = {}): Promise<Response> {
    const headers = new Headers(init.headers);
    headers.set('Authorization', this.authHeader());
    return fetch(this.url(path), { ...init, headers });
  }

  async get<T>(path: string): Promise<T> {
    const response = await this.raw(path, { method: 'GET', headers: { Accept: 'application/json' } });
    return this.readJson<T>('GET', path, response);
  }

  async post<T>(path: string, body?: unknown): Promise<T> {
    const response = await this.raw(path, this.jsonInit('POST', body));
    return this.readJson<T>('POST', path, response);
  }

  /** POST and return only the status code (the body is drained and discarded). */
  async postForStatus(path: string, body?: unknown): Promise<number> {
    const response = await this.raw(path, this.jsonInit('POST', body));
    await response.arrayBuffer().catch(() => undefined);
    return response.status;
  }

  /** POST and throw (with the response body) unless the status is the expected one. */
  async postExpect(path: string, expectedStatus: number, body?: unknown): Promise<void> {
    const response = await this.raw(path, this.jsonInit('POST', body));
    const text = await response.text().catch(() => '');
    if (response.status !== expectedStatus) {
      throw new JellyfinHttpError('POST', this.url(path), response.status, text, `expected ${expectedStatus}`);
    }
  }

  async deleteForStatus(path: string): Promise<number> {
    const response = await this.raw(path, { method: 'DELETE', headers: { Accept: 'application/json' } });
    await response.arrayBuffer().catch(() => undefined);
    return response.status;
  }

  async getBytes(path: string): Promise<Uint8Array> {
    const response = await this.raw(path, { method: 'GET', headers: { Accept: '*/*' } });
    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new JellyfinHttpError('GET', this.url(path), response.status, text);
    }
    return new Uint8Array(await response.arrayBuffer());
  }

  private jsonInit(method: string, body: unknown): RequestInit {
    const headers: Record<string, string> = { Accept: 'application/json' };
    let payload: string | undefined;
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      payload = JSON.stringify(body);
    }
    return { method, headers, body: payload };
  }

  private async readJson<T>(method: string, path: string, response: Response): Promise<T> {
    const text = await response.text().catch(() => '');
    if (!response.ok) {
      throw new JellyfinHttpError(method, this.url(path), response.status, text);
    }
    if (response.status === 204 || text.length === 0) {
      return undefined as T;
    }
    try {
      return JSON.parse(text) as T;
    } catch {
      throw new Error(`${method} ${this.url(path)} returned a non-JSON body: ${text.slice(0, 200)}`);
    }
  }

  // ----- server lifecycle -----

  /** Polls GET /health until it answers 200 (body "Healthy"). */
  async waitForHealthy(timeoutMs: number = 120_000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    let last = 'no attempt yet';
    let readyStreak = 0;
    for (;;) {
      try {
        const response = await fetch(`${this.baseUrl}/health`, { signal: AbortSignal.timeout(5_000) });
        const text = (await response.text()).trim();
        if (response.status === 200) {
          // /health turns green before the API does: until startup finishes, API routes answer
          // with an HTML "server is starting" page, and a fresh server can drop connections for a
          // moment while it finishes starting. Require JSON from the public info route twice in a row.
          const info = await fetch(`${this.baseUrl}/System/Info/Public`, { signal: AbortSignal.timeout(5_000) });
          const contentType = info.headers.get('content-type') ?? '';
          if (info.status === 200 && contentType.includes('json')) {
            readyStreak++;
            if (readyStreak >= 2) {
              return;
            }
            last = 'API answered once; confirming';
          } else {
            readyStreak = 0;
            last = `API not ready: HTTP ${info.status} ${contentType}`;
          }
        } else {
          readyStreak = 0;
          last = `HTTP ${response.status} ${text.slice(0, 80)}`;
        }
      } catch (error) {
        readyStreak = 0;
        last = describeError(error);
      }
      if (Date.now() >= deadline) {
        break;
      }
      await sleep(1_000);
    }
    throw new Error(`${this.baseUrl}/health was not healthy within ${timeoutMs} ms (last: ${last})`);
  }

  async publicInfo(): Promise<PublicSystemInfo> {
    return this.get<PublicSystemInfo>('/System/Info/Public');
  }

  /**
   * Runs the startup wizard exactly like jellyfin-web does. Returns false when the wizard
   * was already completed (idempotent).
   */
  async completeWizard(username: string, password: string, serverName: string = 'E2E Server'): Promise<boolean> {
    const info = await this.publicInfo();
    if (info.StartupWizardCompleted === true) {
      return false;
    }

    const first = await this.get<StartupConfiguration>('/Startup/Configuration');
    await this.postExpect('/Startup/Configuration', 204, { ...first, ServerName: serverName, UICulture: 'en-US' });

    // Reading the first user creates it; without this read the update below answers 404.
    await this.get<{ Name?: string }>('/Startup/User');
    const userStatus = await this.postForStatus('/Startup/User', { Name: username, Password: password });
    // 403: the first user already has a password from an earlier, interrupted run.
    if (userStatus !== 204 && userStatus !== 403) {
      throw new Error(`POST ${this.url('/Startup/User')} -> HTTP ${userStatus}, expected 204`);
    }

    const second = await this.get<StartupConfiguration>('/Startup/Configuration');
    await this.postExpect('/Startup/Configuration', 204, {
      ...second,
      PreferredMetadataLanguage: 'en',
      MetadataCountryCode: 'US',
    });

    await this.postExpect('/Startup/RemoteAccess', 204, { EnableRemoteAccess: true });
    await this.postExpect('/Startup/Complete', 204);
    return true;
  }

  async login(username: string, password: string): Promise<AuthResult> {
    const result = await this.post<AuthenticationResult>('/Users/AuthenticateByName', {
      Username: username,
      Pw: password,
    });
    if (!result?.AccessToken || !result.User?.Id) {
      throw new Error(`login as ${username} on ${this.baseUrl} returned no access token`);
    }
    this.token = result.AccessToken;
    this.userId = result.User.Id;
    return { userId: result.User.Id, token: result.AccessToken };
  }

  // ----- library -----

  async virtualFolders(): Promise<VirtualFolder[]> {
    return this.get<VirtualFolder[]>('/Library/VirtualFolders');
  }

  /** Adds a library with every remote metadata and image provider disabled. Expects 204. */
  async addLibrary(name: string, collectionType: CollectionType, paths: string[]): Promise<void> {
    const query = new URLSearchParams({ name, collectionType, refreshLibrary: 'true' });
    for (const p of paths) {
      query.append('paths', p);
    }
    const noProviders = (type: string) => ({
      Type: type,
      MetadataFetchers: [] as string[],
      MetadataFetcherOrder: [] as string[],
      ImageFetchers: [] as string[],
      ImageFetcherOrder: [] as string[],
    });
    const body = {
      LibraryOptions: {
        EnableInternetProviders: false,
        SaveLocalMetadata: false,
        EnableRealtimeMonitor: false,
        EnableChapterImageExtraction: false,
        ExtractChapterImagesDuringLibraryScan: false,
        EnableTrickplayImageExtraction: false,
        ExtractTrickplayImagesDuringLibraryScan: false,
        EnableLUFSScan: false,
        PathInfos: paths.map((p) => ({ Path: p })),
        TypeOptions: ['Movie', 'Series', 'Season', 'Episode'].map(noProviders),
      },
    };
    await this.postExpect(`/Library/VirtualFolders?${query.toString()}`, 204, body);
  }

  /** POST /Library/Refresh queues a full scan and returns 204 immediately. */
  async refreshLibrary(): Promise<void> {
    await this.postExpect('/Library/Refresh', 204);
  }

  async items<T extends BaseItem = BaseItem>(query: Record<string, string>): Promise<ItemsResult<T>> {
    const params = new URLSearchParams(query);
    return this.get<ItemsResult<T>>(`/Items?${params.toString()}`);
  }

  /**
   * Polls GET /Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path (plus `query`)
   * until TotalRecordCount === expectedCount. Throws with the last count on timeout.
   */
  async waitForItems(query: Record<string, string>, expectedCount: number, timeoutMs: number): Promise<ItemsResult> {
    const fullQuery: Record<string, string> = { ...ITEM_TYPES_QUERY, ...query };
    const deadline = Date.now() + timeoutMs;
    let lastCount = -1;
    let lastError = '';
    for (;;) {
      try {
        const result = await this.items(fullQuery);
        lastCount = result.TotalRecordCount;
        if (lastCount === expectedCount) {
          return result;
        }
      } catch (error) {
        lastError = describeError(error);
      }
      if (Date.now() >= deadline) {
        break;
      }
      await sleep(2_000);
    }
    throw new Error(
      `${this.baseUrl}: expected ${expectedCount} items for ${JSON.stringify(fullQuery)} within ${timeoutMs} ms, ` +
        `last count was ${lastCount}${lastError ? ` (last error: ${lastError})` : ''}`,
    );
  }

  /** Searches by name and returns the item whose Name matches exactly. */
  async findItemByName(name: string, type: string): Promise<BaseItem> {
    const result = await this.items({ Recursive: 'true', IncludeItemTypes: type, SearchTerm: name, Fields: 'Path' });
    const exact = result.Items.filter((item) => item.Name === name);
    if (exact.length === 0) {
      const seen = result.Items.map((item) => item.Name).join(', ') || 'nothing';
      throw new Error(`no ${type} named "${name}" on ${this.baseUrl} (search returned: ${seen})`);
    }
    return exact[0];
  }

  private userQuery(prefix: '?' | '&'): string {
    return this.userId ? `${prefix}userId=${encodeURIComponent(this.userId)}` : '';
  }

  async seasons(seriesId: string): Promise<ItemsResult> {
    return this.get<ItemsResult>(`/Shows/${encodeURIComponent(seriesId)}/Seasons${this.userQuery('?')}`);
  }

  async episodes(seriesId: string): Promise<ItemsResult> {
    return this.get<ItemsResult>(`/Shows/${encodeURIComponent(seriesId)}/Episodes?Fields=Path${this.userQuery('&')}`);
  }

  // ----- playback -----

  /** POST /Items/{id}/PlaybackInfo with an empty JSON body (the signed-in user is used). */
  async playbackInfo(itemId: string): Promise<PlaybackInfoResponse> {
    return this.post<PlaybackInfoResponse>(`/Items/${encodeURIComponent(itemId)}/PlaybackInfo`, {});
  }

  /** GET /Videos/{id}/stream?static=true&mediaSourceId=... as bytes. */
  async streamStatic(itemId: string, mediaSourceId: string): Promise<Uint8Array> {
    const query = new URLSearchParams({ static: 'true', mediaSourceId });
    return this.getBytes(`/Videos/${encodeURIComponent(itemId)}/stream?${query.toString()}`);
  }

  /** Clears played state and resume position for the signed-in user. */
  async markUnplayed(itemId: string): Promise<void> {
    const status = await this.deleteForStatus(`/UserPlayedItems/${encodeURIComponent(itemId)}${this.userQuery('?')}`);
    if (status !== 200 && status !== 204) {
      throw new Error(`DELETE ${this.url(`/UserPlayedItems/${itemId}`)} -> HTTP ${status}`);
    }
  }

  // ----- child server -----

  async childConfig(): Promise<ChildServerSettings> {
    return this.get<ChildServerSettings>('/ChildServer/Configuration');
  }

  /** POST /ChildServer/Configuration, expects 204 (400 on invalid input). */
  async saveChildConfig(settings: ChildServerSettingsInput): Promise<void> {
    await this.postExpect('/ChildServer/Configuration', 204, settings);
  }

  async childTestConnection(body: TestConnectionRequest): Promise<ParentConnectionResult> {
    return this.post<ParentConnectionResult>('/ChildServer/TestConnection', body);
  }

  /** Signs in to the configured parent with the stored settings and persists the token. */
  async childConnect(): Promise<ParentConnectionResult> {
    return this.post<ParentConnectionResult>('/ChildServer/Connect');
  }

  async childStatus(): Promise<ChildServerStatus> {
    return this.get<ChildServerStatus>('/ChildServer/Status');
  }

  /** POST /ChildServer/Sync starts a background sync; returns the status code (202 expected). */
  async childSync(): Promise<number> {
    return this.postForStatus('/ChildServer/Sync');
  }

  async childAvailability(itemId: string): Promise<ItemAvailability> {
    return this.get<ItemAvailability>(`/ChildServer/Items/${encodeURIComponent(itemId)}/Availability`);
  }

  /**
   * Triggers a sync and waits until it has completed: SyncState back to Idle with a
   * LastSyncCompletedUtc newer than the one seen before the trigger. Fails on LastSyncError.
   * Only server timestamps are compared with each other (no cross-clock comparison).
   */
  async waitForChildSync(timeoutMs: number = 300_000): Promise<ChildServerStatus> {
    const before = await this.childStatus();
    const status = await this.childSync();
    // 409 would mean "a sync is already running"; that sync is the one we wait for.
    if (status !== 202 && status !== 409) {
      throw new Error(`POST ${this.url('/ChildServer/Sync')} -> HTTP ${status}, expected 202`);
    }
    const deadline = Date.now() + timeoutMs;
    const previousCompletedMs = before.LastSyncCompletedUtc ? Date.parse(before.LastSyncCompletedUtc) : Number.NEGATIVE_INFINITY;
    let last = before;
    let lastNetworkError: string | null = null;
    while (Date.now() < deadline) {
      await sleep(1_000);
      try {
        last = await this.childStatus();
        lastNetworkError = null;
      } catch (error) {
        // Polling failures are not sync failures. A child that is mirroring a library drops
        // connections, and Docker's embedded resolver has failed to answer for ten seconds at a
        // time in CI ("getaddrinfo ENOTFOUND child") while the container was up and working.
        // Keep asking until the deadline; the message below reports the last failure.
        lastNetworkError = error instanceof Error ? error.message : String(error);
        continue;
      }
      const startedChanged = !!last.LastSyncStartedUtc && last.LastSyncStartedUtc !== before.LastSyncStartedUtc;
      const completedChanged = !!last.LastSyncCompletedUtc && last.LastSyncCompletedUtc !== before.LastSyncCompletedUtc;
      if (last.SyncState !== 'Idle') {
        continue;
      }
      if (last.LastSyncError && (startedChanged || completedChanged)) {
        throw new Error(`child sync failed on ${this.baseUrl}: ${last.LastSyncError}`);
      }
      if (completedChanged) {
        const completedMs = Date.parse(last.LastSyncCompletedUtc as string);
        if (Number.isNaN(completedMs) || completedMs > previousCompletedMs) {
          return last;
        }
      }
    }
    throw new Error(
      `child sync on ${this.baseUrl} did not complete within ${timeoutMs} ms ` +
        `(state ${last.SyncState}, started ${last.LastSyncStartedUtc ?? 'never'}, ` +
        `completed ${last.LastSyncCompletedUtc ?? 'never'}, error ${last.LastSyncError ?? 'none'}` +
        `${lastNetworkError ? `, last poll failed with: ${lastNetworkError}` : ''})`,
    );
  }
}
