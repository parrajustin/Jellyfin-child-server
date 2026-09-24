/**
 * The MCP tool surface.
 *
 * The tools follow the order an agent needs them in: sign in, describe the parent, try it, read
 * the logs when it did not work, save it, look at the libraries, then prove a video plays.
 */

import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';

import { JellyfinClient, JellyfinError } from './client.js';
import * as ops from './operations.js';
import { currentClient, headersToRecord, recordToHeaders, setClient } from './session.js';
import type { ChildServerSettingsInput } from './types.js';

type ToolResult = {
  content: Array<{ type: 'text'; text: string }>;
  isError?: boolean;
};

function ok(payload: unknown): ToolResult {
  return { content: [{ type: 'text', text: JSON.stringify(payload, null, 2) }] };
}

function fail(error: unknown): ToolResult {
  const detail =
    error instanceof JellyfinError
      ? { error: error.message, httpStatus: error.status, url: error.url }
      : { error: error instanceof Error ? error.message : String(error) };
  return { content: [{ type: 'text', text: JSON.stringify(detail, null, 2) }], isError: true };
}

/** Every handler funnels through here so a thrown error becomes a readable tool error. */
async function run(handler: () => Promise<unknown>): Promise<ToolResult> {
  try {
    return ok(await handler());
  } catch (error) {
    return fail(error);
  }
}

const headersArg = z
  .record(z.string())
  .optional()
  .describe(
    'Extra headers sent with every request to the parent, as {name: value}. Use this for a ' +
      'parent behind Cloudflare Access, e.g. {"CF-Access-Client-Id": "...", "CF-Access-Client-Secret": "..."}.',
  );

export function registerTools(server: McpServer): void {
  // -------------------------------------------------------------------------
  // 1. Sign in
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_login',
    {
      title: 'Sign in to the child server',
      description:
        'Signs in to the Jellyfin child server as an administrator and keeps the session for the ' +
        'other tools. Call this first. The reply says whether the account is an administrator; ' +
        'the configuration tools need one.',
      inputSchema: {
        baseUrl: z.string().describe('Child server base URL, e.g. http://localhost:8096'),
        username: z.string(),
        password: z.string(),
        headers: z
          .record(z.string())
          .optional()
          .describe('Extra headers for reaching the child server itself, if it sits behind a gateway.'),
      },
    },
    async ({ baseUrl, username, password, headers }) =>
      run(async () => {
        const client = new JellyfinClient({ baseUrl, extraHeaders: headers ?? {} });
        const result = await ops.login(client, username, password);
        setClient(client);
        if (!result.isAdministrator) {
          return {
            ...result,
            warning:
              'This account is not an administrator. The parent configuration tools will answer 403.',
          };
        }
        return result;
      }),
  );

  // -------------------------------------------------------------------------
  // 2. Read the current parent settings
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_get_parent_config',
    {
      title: 'Read the parent server settings',
      description:
        'Returns the stored parent server settings. The password is never returned; HasPassword ' +
        'says whether one is stored.',
      inputSchema: {},
    },
    async () => run(async () => ops.getConfiguration(currentClient())),
  );

  // -------------------------------------------------------------------------
  // 3. Set every parent option
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_set_parent_config',
    {
      title: 'Set the parent server settings',
      description:
        'Saves the parent server settings. Every option is optional; anything left out keeps its ' +
        'stored value, and an omitted password keeps the stored one. This only saves — use ' +
        'jellyfin_test_parent_connection first if you want to check the settings before storing them.',
      inputSchema: {
        parentUrl: z.string().optional().describe('Parent server base URL, e.g. https://jellyfin.example.com'),
        username: z.string().optional().describe('Account on the parent the child signs in as'),
        password: z.string().optional().describe('Omit to keep the stored password'),
        headers: headersArg,
        prefetchEpisodeCount: z
          .number()
          .int()
          .min(0)
          .optional()
          .describe('How many following episodes to fetch while one plays (default 4)'),
        maxCacheSizeMb: z.number().int().min(0).optional().describe('Cache ceiling in MB (default 5120)'),
        downloadWaitTimeoutSeconds: z
          .number()
          .int()
          .min(0)
          .optional()
          .describe('How long a transcode waits for a download (default 900)'),
        syncIntervalHours: z.number().int().min(0).optional().describe('Hours between syncs (default 6)'),
      },
    },
    async (args) =>
      run(async () => {
        const client = currentClient();
        const settings = buildSettings(args);
        await ops.saveConfiguration(client, settings);
        return { saved: true, settings: await ops.getConfiguration(client) };
      }),
  );

  // -------------------------------------------------------------------------
  // 4. Test the connection
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_test_parent_connection',
    {
      title: 'Test the parent server connection',
      description:
        'Tries a parent URL, account and headers without saving anything. Answers a Status of ' +
        'Success, InvalidCredentials, AccessDenied, ConnectionFailed or InvalidResponse, with a ' +
        'message. AccessDenied almost always means the gateway headers are missing or wrong. ' +
        'With no arguments it tests the stored settings.',
      inputSchema: {
        parentUrl: z.string().optional(),
        username: z.string().optional(),
        password: z.string().optional(),
        headers: headersArg,
      },
    },
    async (args) =>
      run(async () => {
        const client = currentClient();
        const settings = buildSettings(args);
        const result = await ops.testConnection(client, settings);
        return {
          ...result,
          hint:
            result.Status === 'AccessDenied'
              ? 'The gateway refused the request before Jellyfin saw it. Check the header names and values, then call jellyfin_get_logs.'
              : result.Status === 'ConnectionFailed'
                ? 'Nothing answered at that URL. Check the host and the scheme, then call jellyfin_get_logs.'
                : undefined,
        };
      }),
  );

  // -------------------------------------------------------------------------
  // 5. Logs
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_get_logs',
    {
      title: 'Read the child server logs',
      description:
        'Returns the tail of the newest server log, filtered. With no filter it keeps child server ' +
        'lines and anything at Warning or above, which is what you want after a failed connection test.',
      inputSchema: {
        filter: z.string().optional().describe('Only lines containing this (case insensitive)'),
        tailLines: z.number().int().min(1).max(2000).optional().describe('How many lines (default 100)'),
        name: z.string().optional().describe('A specific log file; defaults to the newest'),
      },
    },
    async ({ filter, tailLines, name }) =>
      run(async () => ops.readLogs(currentClient(), { filter, tailLines, name })),
  );

  // -------------------------------------------------------------------------
  // 6. Save and connect
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_connect_parent',
    {
      title: 'Save the connection and sign in to the parent',
      description:
        'Signs in to the parent with the stored settings and keeps the access token, which is what ' +
        '"saving the connection" means. Pass settings to save them first, in one step. Optionally ' +
        'runs a library sync afterwards and waits for it.',
      inputSchema: {
        parentUrl: z.string().optional(),
        username: z.string().optional(),
        password: z.string().optional(),
        headers: headersArg,
        sync: z.boolean().optional().describe('Run a library sync after connecting and wait for it'),
        syncTimeoutMs: z.number().int().min(1000).optional().describe('Sync wait timeout (default 300000)'),
      },
    },
    async ({ sync, syncTimeoutMs, ...rest }) =>
      run(async () => {
        const client = currentClient();
        const settings = buildSettings(rest);
        if (Object.keys(settings).length > 0) {
          await ops.saveConfiguration(client, settings);
        }
        const connection = await ops.connect(client);
        if (!connection.IsSuccess) {
          return {
            connected: false,
            connection,
            hint: 'Call jellyfin_get_logs for the reason the parent refused.',
          };
        }
        if (!sync) {
          return { connected: true, connection, status: await ops.status(client) };
        }
        const synced = await ops.syncAndWait(client, syncTimeoutMs ?? 300_000);
        return { connected: true, connection, sync: synced };
      }),
  );

  // -------------------------------------------------------------------------
  // 7. Libraries on the parent
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_get_parent_libraries',
    {
      title: "List the parent's libraries",
      description:
        'Lists the libraries the parent exposes, by signing in to the parent and calling ' +
        "/UserViews — the same request the web client makes when a user opens the dashboard. " +
        'This asks the parent directly rather than reporting what the child mirrored, so it ' +
        'answers "what is actually there". With no arguments it uses the stored parent settings, ' +
        'which requires a password argument because the child never hands the stored one back.',
      inputSchema: {
        parentUrl: z.string().optional().describe('Defaults to the stored parent URL'),
        username: z.string().optional().describe('Defaults to the stored parent user name'),
        password: z.string().describe('Required: the stored password is never readable'),
        headers: headersArg,
        compareWithChild: z
          .boolean()
          .optional()
          .describe("Also list the child's own libraries and report which parent ones are missing"),
      },
    },
    async ({ parentUrl, username, password, headers, compareWithChild }) =>
      run(async () => {
        const client = currentClient();
        const stored = await ops.getConfiguration(client);
        const url = parentUrl ?? stored.ParentUrl;
        const user = username ?? stored.Username;
        if (!url) {
          throw new Error('No parent URL given and none is stored. Call jellyfin_set_parent_config first.');
        }
        if (!user) {
          throw new Error('No parent user name given and none is stored.');
        }
        const extra = headers ?? headersToRecord(stored.CustomHeaders);
        const parent = await ops.getParentLibraries(url, user, password, extra);
        if (!compareWithChild) {
          return parent;
        }
        const child = await ops.getChildLibraries(client);
        const childNames = new Set(child.map((view) => view.name));
        return {
          ...parent,
          childLibraries: child,
          missingOnChild: parent.libraries.filter((view) => !childNames.has(view.name)).map((v) => v.name),
        };
      }),
  );

  // -------------------------------------------------------------------------
  // 8. Sync
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_sync',
    {
      title: 'Mirror the parent libraries',
      description:
        'Starts a library sync on the child and waits for it to finish, then reports how many items ' +
        'are mirrored. Run this after connecting, before testing playback.',
      inputSchema: {
        timeoutMs: z.number().int().min(1000).optional().describe('How long to wait (default 300000)'),
      },
    },
    async ({ timeoutMs }) => run(async () => ops.syncAndWait(currentClient(), timeoutMs ?? 300_000)),
  );

  // -------------------------------------------------------------------------
  // 9. Prove a video plays
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_test_playback',
    {
      title: 'Test that a video starts',
      description:
        'Asks the child for playback info and then pulls the first bytes of the stream, which is ' +
        'what forces the fetch from the parent. Reading bytes is the real test: PlaybackInfo alone ' +
        'answers happily for an item whose local file is still a placeholder. With no item id it ' +
        'picks the first movie or episode.',
      inputSchema: {
        itemId: z.string().optional().describe('Defaults to the first movie or episode on the child'),
        bytes: z
          .number()
          .int()
          .min(1024)
          .max(16 * 1024 * 1024)
          .optional()
          .describe('How many bytes to pull (default 262144)'),
      },
    },
    async ({ itemId, bytes }) => run(async () => ops.testPlayback(currentClient(), itemId, bytes ?? 256 * 1024)),
  );

  // -------------------------------------------------------------------------
  // 10. Status
  // -------------------------------------------------------------------------
  server.registerTool(
    'jellyfin_status',
    {
      title: 'Child server status',
      description:
        'Connection, sync and cache state: whether the parent is configured and signed in, how many ' +
        'items are mirrored and cached, and the last connection or sync error.',
      inputSchema: {},
    },
    async () => run(async () => ops.status(currentClient())),
  );
}

interface SettingsArgs {
  parentUrl?: string;
  username?: string;
  password?: string;
  headers?: Record<string, string>;
  prefetchEpisodeCount?: number;
  maxCacheSizeMb?: number;
  downloadWaitTimeoutSeconds?: number;
  syncIntervalHours?: number;
}

/** Only sends the fields the caller actually set, so the rest keep their stored values. */
function buildSettings(args: SettingsArgs): ChildServerSettingsInput {
  const settings: ChildServerSettingsInput = {};
  if (args.parentUrl !== undefined) {
    settings.ParentUrl = args.parentUrl;
  }
  if (args.username !== undefined) {
    settings.Username = args.username;
  }
  if (args.password !== undefined) {
    settings.Password = args.password;
  }
  if (args.headers !== undefined) {
    settings.CustomHeaders = recordToHeaders(args.headers);
  }
  if (args.prefetchEpisodeCount !== undefined) {
    settings.PrefetchEpisodeCount = args.prefetchEpisodeCount;
  }
  if (args.maxCacheSizeMb !== undefined) {
    settings.MaxCacheSizeMb = args.maxCacheSizeMb;
  }
  if (args.downloadWaitTimeoutSeconds !== undefined) {
    settings.DownloadWaitTimeoutSeconds = args.downloadWaitTimeoutSeconds;
  }
  if (args.syncIntervalHours !== undefined) {
    settings.SyncIntervalHours = args.syncIntervalHours;
  }
  return settings;
}
