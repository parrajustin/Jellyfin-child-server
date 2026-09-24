/**
 * The flows behind the tools. Kept free of MCP types so they can be unit tested against a stub
 * Jellyfin server without going through the protocol.
 */

import { JellyfinClient, JellyfinError } from './client.js';
import { headersToRecord } from './session.js';
import type {
  AuthenticationResult,
  BaseItem,
  ChildServerSettings,
  ChildServerSettingsInput,
  ChildServerStatus,
  ItemsResult,
  ParentConnectionResult,
  PlaybackInfoResponse,
} from './types.js';

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

export interface LoginResult {
  userId: string;
  userName: string;
  isAdministrator: boolean;
  serverName?: string;
  serverVersion?: string;
}

/**
 * Signs in to the child server as an administrator.
 *
 * The public system info call first is deliberate: if a gateway is in the way, it fails here with
 * a 401/403 rather than inside the login, which makes the difference between "wrong password" and
 * "the gateway never let us through" obvious in the tool result.
 */
export async function login(
  client: JellyfinClient,
  username: string,
  password: string,
): Promise<LoginResult> {
  const info = await client.publicSystemInfo<{ ServerName?: string; Version?: string }>();
  const result = await client.post<AuthenticationResult>('/Users/AuthenticateByName', {
    Username: username,
    Pw: password,
  });
  if (!result?.AccessToken || !result.User?.Id) {
    throw new Error(`Sign in as "${username}" on ${client.baseUrl} returned no access token.`);
  }
  client.useAuth(result.AccessToken, result.User.Id);
  const isAdministrator = result.User.Policy?.IsAdministrator === true;
  return {
    userId: result.User.Id,
    userName: result.User.Name ?? username,
    isAdministrator,
    serverName: info?.ServerName,
    serverVersion: info?.Version,
  };
}

export async function getConfiguration(client: JellyfinClient): Promise<ChildServerSettings> {
  return client.get<ChildServerSettings>('/ChildServer/Configuration');
}

/** POST /ChildServer/Configuration answers 204, or 400 when a header name or URL is rejected. */
export async function saveConfiguration(
  client: JellyfinClient,
  settings: ChildServerSettingsInput,
): Promise<void> {
  const status = await client.postForStatus('/ChildServer/Configuration', settings);
  if (status !== 204) {
    throw new Error(
      `POST ${client.url('/ChildServer/Configuration')} -> HTTP ${status}, expected 204. ` +
        'A 400 usually means an invalid parent URL or a header name with a space or colon in it.',
    );
  }
}

export async function testConnection(
  client: JellyfinClient,
  settings: ChildServerSettingsInput,
): Promise<ParentConnectionResult> {
  return client.post<ParentConnectionResult>('/ChildServer/TestConnection', settings);
}

export async function connect(client: JellyfinClient): Promise<ParentConnectionResult> {
  return client.post<ParentConnectionResult>('/ChildServer/Connect');
}

export async function status(client: JellyfinClient): Promise<ChildServerStatus> {
  return client.get<ChildServerStatus>('/ChildServer/Status');
}

// ---------------------------------------------------------------------------
// Logs
// ---------------------------------------------------------------------------

export interface LogQuery {
  /** Only lines containing this (case insensitive). Defaults to child server lines. */
  filter?: string;
  /** How many matching lines from the end of the file. */
  tailLines?: number;
  /** Which log file; defaults to the most recently modified one. */
  name?: string;
}

export interface LogResult {
  file: string;
  matchedLines: number;
  totalLines: number;
  lines: string[];
}

/**
 * Reads the newest server log and returns the tail of the lines that matter.
 *
 * Jellyfin only serves whole log files and they get large, so the filtering happens here. The
 * default filter is the child server's own namespaces plus anything logged at Warning or above,
 * which is what an agent debugging a failed connection actually wants.
 */
export async function readLogs(client: JellyfinClient, query: LogQuery = {}): Promise<LogResult> {
  const files = await client.logFiles();
  if (files.length === 0) {
    return { file: '', matchedLines: 0, totalLines: 0, lines: [] };
  }
  const chosen =
    (query.name ? files.find((file) => file.Name === query.name) : undefined) ??
    [...files].sort((a, b) => Date.parse(b.DateModified) - Date.parse(a.DateModified))[0];
  if (!chosen) {
    throw new Error(`No log file named "${query.name}". Available: ${files.map((f) => f.Name).join(', ')}`);
  }
  const text = await client.logFile(chosen.Name);
  const allLines = text.split(/\r?\n/);
  const needle = query.filter?.toLowerCase();
  const matches = allLines.filter((line) => {
    if (!line.trim()) {
      return false;
    }
    if (needle) {
      return line.toLowerCase().includes(needle);
    }
    return /childserver|child server|parentserver|\[WRN\]|\[ERR\]|\[FTL\]/i.test(line);
  });
  const tail = Math.max(1, query.tailLines ?? 100);
  return {
    file: chosen.Name,
    matchedLines: matches.length,
    totalLines: allLines.length,
    lines: matches.slice(-tail),
  };
}

// ---------------------------------------------------------------------------
// Libraries
// ---------------------------------------------------------------------------

export interface LibraryView {
  id: string;
  name: string;
  collectionType?: string;
}

export interface ParentLibrariesResult {
  parentUrl: string;
  headersSent: string[];
  userId: string;
  libraries: LibraryView[];
}

/**
 * Lists the parent's libraries by calling the parent the way the web client does: sign in, then
 * GET /UserViews. This goes to the parent directly rather than asking the child what it mirrored,
 * so it answers "what does the parent actually expose to this account", which is the question you
 * have when a library is missing from the child.
 */
export async function getParentLibraries(
  parentUrl: string,
  username: string,
  password: string,
  extraHeaders: Record<string, string>,
): Promise<ParentLibrariesResult> {
  const parent = new JellyfinClient({ baseUrl: parentUrl, extraHeaders });
  await login(parent, username, password);
  const views = await parent.get<ItemsResult>(`/UserViews?userId=${encodeURIComponent(parent.userId ?? '')}`);
  return {
    parentUrl: parent.baseUrl,
    headersSent: Object.keys(extraHeaders),
    userId: parent.userId ?? '',
    libraries: views.Items.map((item) => ({
      id: item.Id,
      name: item.Name,
      collectionType: item.CollectionType,
    })),
  };
}

/** The libraries the child itself is serving, for comparison with the parent's. */
export async function getChildLibraries(client: JellyfinClient): Promise<LibraryView[]> {
  const views = await client.get<ItemsResult>(
    `/UserViews?userId=${encodeURIComponent(client.userId ?? '')}`,
  );
  return views.Items.map((item) => ({
    id: item.Id,
    name: item.Name,
    collectionType: item.CollectionType,
  }));
}

// ---------------------------------------------------------------------------
// Sync
// ---------------------------------------------------------------------------

export interface SyncResult {
  completed: boolean;
  status: ChildServerStatus;
  waitedMs: number;
}

/**
 * Starts a sync and waits for it to finish. Completion is a return to Idle with a
 * LastSyncCompletedUtc newer than the one read before the trigger; only the server's own
 * timestamps are compared with each other, never against this machine's clock.
 */
export async function syncAndWait(
  client: JellyfinClient,
  timeoutMs = 300_000,
  pollMs = 2_000,
): Promise<SyncResult> {
  const before = await status(client);
  const previousCompleted = before.LastSyncCompletedUtc
    ? Date.parse(before.LastSyncCompletedUtc)
    : Number.NEGATIVE_INFINITY;
  const code = await client.postForStatus('/ChildServer/Sync');
  // 409 means a sync is already running; that is the one we wait for.
  if (code !== 202 && code !== 409) {
    throw new Error(`POST ${client.url('/ChildServer/Sync')} -> HTTP ${code}, expected 202.`);
  }
  const startedAt = Date.now();
  const deadline = startedAt + timeoutMs;
  for (;;) {
    const current = await status(client);
    if (current.LastSyncError) {
      throw new Error(`The sync failed: ${current.LastSyncError}`);
    }
    const completed = current.LastSyncCompletedUtc ? Date.parse(current.LastSyncCompletedUtc) : Number.NEGATIVE_INFINITY;
    if (current.SyncState === 'Idle' && completed > previousCompleted) {
      return { completed: true, status: current, waitedMs: Date.now() - startedAt };
    }
    if (Date.now() >= deadline) {
      return { completed: false, status: current, waitedMs: Date.now() - startedAt };
    }
    await sleep(pollMs);
  }
}

// ---------------------------------------------------------------------------
// Playback
// ---------------------------------------------------------------------------

export interface PlaybackTestResult {
  ok: boolean;
  itemId: string;
  itemName: string;
  itemType?: string;
  mediaSourceId?: string;
  container?: string;
  bytesRead: number;
  contentType?: string;
  httpStatus?: number;
  /** Set when the child refused because the parent is unreachable and nothing is cached. */
  playbackErrorCode?: string;
  message: string;
}

/** Picks the first playable video when the caller did not name one. */
export async function findPlayableItem(client: JellyfinClient): Promise<BaseItem> {
  const result = await client.get<ItemsResult>(
    '/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Limit=1&Fields=Path&SortBy=SortName',
  );
  const first = result.Items[0];
  if (!first) {
    throw new Error('The child server has no movies or episodes to play. Run a sync first.');
  }
  return first;
}

/**
 * Proves a video actually starts: asks for playback info, then pulls the first bytes of the
 * stream. Reading bytes is the part that matters — PlaybackInfo alone answers happily for an
 * item whose file is still a placeholder, and the fetch from the parent only happens once the
 * stream is requested.
 */
export async function testPlayback(
  client: JellyfinClient,
  itemId?: string,
  bytesToRead = 256 * 1024,
): Promise<PlaybackTestResult> {
  const item = itemId
    ? await client.get<BaseItem>(`/Items/${encodeURIComponent(itemId)}?userId=${encodeURIComponent(client.userId ?? '')}`)
    : await findPlayableItem(client);

  let info: PlaybackInfoResponse;
  try {
    info = await client.post<PlaybackInfoResponse>(`/Items/${encodeURIComponent(item.Id)}/PlaybackInfo`, {});
  } catch (error) {
    if (error instanceof JellyfinError && error.status === 503) {
      return {
        ok: false,
        itemId: item.Id,
        itemName: item.Name,
        itemType: item.Type,
        bytesRead: 0,
        httpStatus: 503,
        playbackErrorCode: 'ParentServerUnavailable',
        message: 'The child refused playback: the file is not cached and the parent cannot be reached.',
      };
    }
    throw error;
  }

  if (info.ErrorCode) {
    return {
      ok: false,
      itemId: item.Id,
      itemName: item.Name,
      itemType: item.Type,
      bytesRead: 0,
      playbackErrorCode: info.ErrorCode,
      message: `PlaybackInfo refused playback with ErrorCode "${info.ErrorCode}".`,
    };
  }

  const source = info.MediaSources?.[0];
  if (!source?.Id) {
    return {
      ok: false,
      itemId: item.Id,
      itemName: item.Name,
      itemType: item.Type,
      bytesRead: 0,
      message: 'PlaybackInfo returned no media source, so there is nothing to stream.',
    };
  }

  const query = new URLSearchParams({ static: 'true', mediaSourceId: source.Id });
  const response = await client.raw(`/Videos/${encodeURIComponent(item.Id)}/stream?${query.toString()}`, {
    method: 'GET',
    headers: { Range: `bytes=0-${bytesToRead - 1}` },
  });

  if (!response.ok && response.status !== 206) {
    const errorCode = response.headers.get('X-Application-Error-Code') ?? undefined;
    await response.arrayBuffer();
    return {
      ok: false,
      itemId: item.Id,
      itemName: item.Name,
      itemType: item.Type,
      mediaSourceId: source.Id,
      container: source.Container,
      bytesRead: 0,
      httpStatus: response.status,
      playbackErrorCode: errorCode,
      message:
        `The stream request answered HTTP ${response.status}` +
        (errorCode ? ` with X-Application-Error-Code: ${errorCode}` : '') +
        '.',
    };
  }

  const bytes = new Uint8Array(await response.arrayBuffer());
  const ok = bytes.byteLength > 0;
  return {
    ok,
    itemId: item.Id,
    itemName: item.Name,
    itemType: item.Type,
    mediaSourceId: source.Id,
    container: source.Container,
    bytesRead: bytes.byteLength,
    contentType: response.headers.get('Content-Type') ?? undefined,
    httpStatus: response.status,
    message: ok
      ? `Playback started: ${bytes.byteLength} bytes of "${item.Name}" arrived from the child.`
      : `The stream answered HTTP ${response.status} but sent no bytes.`,
  };
}
