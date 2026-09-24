/**
 * Drives the MCP server the way a real agent would: over the protocol, through a linked pair of
 * in-memory transports. This is what proves the tool schemas and the handler wiring are right,
 * which the operations tests cannot see.
 */

import assert from 'node:assert/strict';
import { after, before, describe, it } from 'node:test';

import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';

import { createServer } from '../src/index.js';
import { clearClient } from '../src/session.js';
import { StubJellyfin } from './stub-server.js';

const CF_HEADERS = {
  'CF-Access-Client-Id': 'stub-client-id.access',
  'CF-Access-Client-Secret': 'stub-client-secret',
};

/** Tool results are JSON in a text block; this unwraps them. */
function parse(result: unknown): { payload: any; isError: boolean } {
  const typed = result as { content: Array<{ type: string; text: string }>; isError?: boolean };
  const first = typed.content[0];
  assert.ok(first, 'the tool returned no content');
  assert.equal(first.type, 'text');
  return { payload: JSON.parse(first.text), isError: typed.isError === true };
}

describe('MCP server', () => {
  const stub = new StubJellyfin({
    username: 'admin',
    password: 'secret',
    requiredHeaders: CF_HEADERS,
    logText: '[2026-09-23 10:00:02] [ERR] [1] ParentServerClient: HTTP 403 from the gateway',
  });
  let baseUrl = '';
  let client: Client;

  before(async () => {
    baseUrl = await stub.start();
    clearClient();
    const server = createServer();
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    client = new Client({ name: 'test-agent', version: '1.0.0' });
    await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);
  });

  after(async () => {
    await client.close();
    await stub.stop();
  });

  it('advertises every tool the brief asks for', async () => {
    const { tools } = await client.listTools();
    const names = tools.map((tool) => tool.name).sort();
    assert.deepEqual(names, [
      'jellyfin_connect_parent',
      'jellyfin_get_logs',
      'jellyfin_get_parent_config',
      'jellyfin_get_parent_libraries',
      'jellyfin_set_parent_config',
      'jellyfin_status',
      'jellyfin_sync',
      'jellyfin_test_parent_connection',
      'jellyfin_test_playback',
      'jellyfin_login',
    ].sort());
    for (const tool of tools) {
      assert.ok(tool.description && tool.description.length > 20, `${tool.name} needs a description`);
    }
  });

  it('refuses work before a sign in, with an actionable message', async () => {
    const { payload, isError } = parse(await client.callTool({ name: 'jellyfin_status', arguments: {} }));
    assert.equal(isError, true);
    assert.match(payload.error, /jellyfin_login/);
  });

  it('runs the whole flow: login, configure, test, connect, sync, play', async () => {
    const login = parse(
      await client.callTool({
        name: 'jellyfin_login',
        arguments: { baseUrl, username: 'admin', password: 'secret', headers: CF_HEADERS },
      }),
    );
    assert.equal(login.isError, false);
    assert.equal(login.payload.isAdministrator, true);

    const configured = parse(
      await client.callTool({
        name: 'jellyfin_set_parent_config',
        arguments: {
          parentUrl: 'https://parent.example.com',
          username: 'remote',
          password: 'remote-password',
          headers: CF_HEADERS,
          prefetchEpisodeCount: 4,
          maxCacheSizeMb: 4096,
        },
      }),
    );
    assert.equal(configured.isError, false);
    assert.equal(configured.payload.saved, true);
    assert.equal(configured.payload.settings.MaxCacheSizeMb, 4096);
    assert.equal(configured.payload.settings.Password, null);

    const tested = parse(
      await client.callTool({
        name: 'jellyfin_test_parent_connection',
        arguments: { parentUrl: 'https://parent.example.com', headers: CF_HEADERS },
      }),
    );
    assert.equal(tested.payload.Status, 'Success');

    const connected = parse(await client.callTool({ name: 'jellyfin_connect_parent', arguments: {} }));
    assert.equal(connected.payload.connected, true);

    const synced = parse(await client.callTool({ name: 'jellyfin_sync', arguments: { timeoutMs: 10_000 } }));
    assert.equal(synced.payload.completed, true);

    const played = parse(await client.callTool({ name: 'jellyfin_test_playback', arguments: {} }));
    assert.equal(played.payload.ok, true);
    assert.ok(played.payload.bytesRead > 0);
  });

  it('explains an AccessDenied result instead of just reporting it', async () => {
    const result = parse(
      await client.callTool({
        name: 'jellyfin_test_parent_connection',
        arguments: { parentUrl: 'https://parent.example.com', headers: {} },
      }),
    );
    assert.equal(result.payload.Status, 'AccessDenied');
    assert.match(result.payload.hint, /header/i);
  });

  it('returns the parent libraries through the gateway', async () => {
    const result = parse(
      await client.callTool({
        name: 'jellyfin_get_parent_libraries',
        arguments: { parentUrl: baseUrl, username: 'admin', password: 'secret', headers: CF_HEADERS },
      }),
    );
    assert.equal(result.isError, false);
    assert.deepEqual(
      result.payload.libraries.map((view: { name: string }) => view.name),
      ['Movies', 'Shows'],
    );
  });

  it('filters the logs to what matters after a failure', async () => {
    const result = parse(await client.callTool({ name: 'jellyfin_get_logs', arguments: { tailLines: 10 } }));
    assert.equal(result.isError, false);
    assert.equal(result.payload.file, 'new.log');
    assert.ok(result.payload.lines.some((line: string) => line.includes('403')));
  });
});
