import assert from 'node:assert/strict';
import { after, before, describe, it } from 'node:test';

import { JellyfinClient } from '../src/client.js';
import * as ops from '../src/operations.js';
import { StubJellyfin } from './stub-server.js';

const CF_HEADERS = {
  'CF-Access-Client-Id': 'stub-client-id.access',
  'CF-Access-Client-Secret': 'stub-client-secret',
};

describe('login', () => {
  const stub = new StubJellyfin({ username: 'admin', password: 'secret' });
  let baseUrl = '';

  before(async () => {
    baseUrl = await stub.start();
  });
  after(async () => stub.stop());

  it('returns the user and reports administrator rights', async () => {
    const client = new JellyfinClient({ baseUrl });
    const result = await ops.login(client, 'admin', 'secret');
    assert.equal(result.userId, 'user-1');
    assert.equal(result.isAdministrator, true);
    assert.equal(result.serverName, 'Stub');
    assert.equal(client.token, 'stub-token');
  });

  it('fails on a wrong password', async () => {
    const client = new JellyfinClient({ baseUrl });
    await assert.rejects(() => ops.login(client, 'admin', 'wrong'), /HTTP 401/);
  });

  it('sends the MediaBrowser authorization header', async () => {
    const client = new JellyfinClient({ baseUrl });
    await ops.login(client, 'admin', 'secret');
    const auth = stub.requestsFor('/Users/AuthenticateByName').at(-1)?.headers['authorization'];
    assert.match(auth ?? '', /^MediaBrowser Client=/);
  });
});

describe('gateway headers', () => {
  // The stub refuses anything without the service token, exactly like Cloudflare Access does,
  // so a request that forgot the headers cannot pass this suite.
  const stub = new StubJellyfin({
    username: 'admin',
    password: 'secret',
    requiredHeaders: CF_HEADERS,
  });
  let baseUrl = '';

  before(async () => {
    baseUrl = await stub.start();
  });
  after(async () => stub.stop());

  it('reaches every endpoint when the headers are configured', async () => {
    const client = new JellyfinClient({ baseUrl, extraHeaders: CF_HEADERS });
    await ops.login(client, 'admin', 'secret');
    await ops.getConfiguration(client);
    await ops.status(client);
    await ops.readLogs(client);
    await ops.testPlayback(client);

    assert.ok(stub.requests.length >= 6, `expected several requests, saw ${stub.requests.length}`);
    for (const request of stub.requests) {
      assert.equal(
        request.headers['cf-access-client-id'],
        CF_HEADERS['CF-Access-Client-Id'],
        `missing CF-Access-Client-Id on ${request.method} ${request.path}`,
      );
      assert.equal(
        request.headers['cf-access-client-secret'],
        CF_HEADERS['CF-Access-Client-Secret'],
        `missing CF-Access-Client-Secret on ${request.method} ${request.path}`,
      );
    }
  });

  it('carries the headers on the video stream request too', async () => {
    const client = new JellyfinClient({ baseUrl, extraHeaders: CF_HEADERS });
    await ops.login(client, 'admin', 'secret');
    await ops.testPlayback(client);
    const stream = stub.requestsFor('/Videos/').at(-1);
    assert.ok(stream, 'no stream request was made');
    assert.equal(stream.headers['cf-access-client-id'], CF_HEADERS['CF-Access-Client-Id']);
    assert.match(stream.headers['range'] ?? '', /^bytes=0-/);
  });

  it('is refused without them', async () => {
    const client = new JellyfinClient({ baseUrl });
    await assert.rejects(() => ops.login(client, 'admin', 'secret'), /HTTP 403/);
  });
});

describe('configuration', () => {
  const stub = new StubJellyfin({ username: 'admin', password: 'secret' });
  let client: JellyfinClient;

  before(async () => {
    const baseUrl = await stub.start();
    client = new JellyfinClient({ baseUrl });
    await ops.login(client, 'admin', 'secret');
  });
  after(async () => stub.stop());

  it('saves every option and reads them back without the password', async () => {
    await ops.saveConfiguration(client, {
      ParentUrl: 'https://parent.example.com',
      Username: 'remote',
      Password: 'remote-password',
      CustomHeaders: [{ Name: 'CF-Access-Client-Id', Value: 'abc.access' }],
      PrefetchEpisodeCount: 6,
      MaxCacheSizeMb: 2048,
      DownloadWaitTimeoutSeconds: 600,
      SyncIntervalHours: 12,
    });
    const saved = await ops.getConfiguration(client);
    assert.equal(saved.ParentUrl, 'https://parent.example.com');
    assert.equal(saved.Username, 'remote');
    assert.equal(saved.Password, null, 'the password must never come back');
    assert.equal(saved.HasPassword, true);
    assert.equal(saved.PrefetchEpisodeCount, 6);
    assert.equal(saved.MaxCacheSizeMb, 2048);
    assert.equal(saved.DownloadWaitTimeoutSeconds, 600);
    assert.equal(saved.SyncIntervalHours, 12);
    assert.deepEqual(saved.CustomHeaders, [{ Name: 'CF-Access-Client-Id', Value: 'abc.access' }]);
  });
});

describe('testConnection', () => {
  const stub = new StubJellyfin({ username: 'admin', password: 'secret', requiredHeaders: CF_HEADERS });
  let client: JellyfinClient;

  before(async () => {
    const baseUrl = await stub.start();
    client = new JellyfinClient({ baseUrl, extraHeaders: CF_HEADERS });
    await ops.login(client, 'admin', 'secret');
  });
  after(async () => stub.stop());

  it('answers AccessDenied when the gateway headers are not in the settings', async () => {
    const result = await ops.testConnection(client, { ParentUrl: 'https://parent.example.com' });
    assert.equal(result.Status, 'AccessDenied');
    assert.equal(result.IsSuccess, false);
  });

  it('answers Success once they are', async () => {
    const result = await ops.testConnection(client, {
      ParentUrl: 'https://parent.example.com',
      CustomHeaders: [
        { Name: 'CF-Access-Client-Id', Value: CF_HEADERS['CF-Access-Client-Id'] },
        { Name: 'CF-Access-Client-Secret', Value: CF_HEADERS['CF-Access-Client-Secret'] },
      ],
    });
    assert.equal(result.Status, 'Success');
    assert.equal(result.IsSuccess, true);
  });
});

describe('readLogs', () => {
  const logText = [
    '[2026-09-23 10:00:00] [INF] [1] Main: starting',
    '[2026-09-23 10:00:01] [INF] [1] ChildServerManager: connecting to parent',
    '[2026-09-23 10:00:02] [ERR] [1] ParentServerClient: HTTP 403 from the gateway',
    '[2026-09-23 10:00:03] [INF] [1] Something unrelated',
  ].join('\n');
  const stub = new StubJellyfin({ username: 'admin', password: 'secret', logText });
  let client: JellyfinClient;

  before(async () => {
    const baseUrl = await stub.start();
    client = new JellyfinClient({ baseUrl });
    await ops.login(client, 'admin', 'secret');
  });
  after(async () => stub.stop());

  it('picks the most recently modified log file', async () => {
    const result = await ops.readLogs(client);
    assert.equal(result.file, 'new.log');
  });

  it('keeps child server and warning-or-worse lines by default', async () => {
    const result = await ops.readLogs(client);
    assert.equal(result.matchedLines, 2);
    assert.ok(result.lines.some((line) => line.includes('ChildServerManager')));
    assert.ok(result.lines.some((line) => line.includes('[ERR]')));
    assert.ok(!result.lines.some((line) => line.includes('Something unrelated')));
  });

  it('honours an explicit filter and a tail length', async () => {
    const result = await ops.readLogs(client, { filter: 'gateway', tailLines: 1 });
    assert.equal(result.lines.length, 1);
    assert.match(result.lines[0] ?? '', /403/);
  });
});

describe('getParentLibraries', () => {
  const stub = new StubJellyfin({
    username: 'remote',
    password: 'remote-password',
    requiredHeaders: CF_HEADERS,
    libraries: [
      { Id: 'lib-1', Name: 'Movies', CollectionType: 'movies' },
      { Id: 'lib-2', Name: 'Home Videos', CollectionType: 'homevideos' },
    ],
  });
  let baseUrl = '';

  before(async () => {
    baseUrl = await stub.start();
  });
  after(async () => stub.stop());

  it('signs in to the parent and lists its views through the gateway', async () => {
    const result = await ops.getParentLibraries(baseUrl, 'remote', 'remote-password', CF_HEADERS);
    assert.deepEqual(
      result.libraries.map((view) => view.name),
      ['Movies', 'Home Videos'],
    );
    assert.deepEqual(result.headersSent.sort(), ['CF-Access-Client-Id', 'CF-Access-Client-Secret']);
    const views = stub.requestsFor('/UserViews').at(-1);
    assert.equal(views?.headers['cf-access-client-id'], CF_HEADERS['CF-Access-Client-Id']);
  });

  it('fails clearly when the gateway headers are wrong', async () => {
    await assert.rejects(
      () => ops.getParentLibraries(baseUrl, 'remote', 'remote-password', { 'CF-Access-Client-Id': 'wrong' }),
      /HTTP 403/,
    );
  });
});

describe('testPlayback', () => {
  it('reports the bytes that actually arrived', async () => {
    const stub = new StubJellyfin({
      username: 'admin',
      password: 'secret',
      mediaBytes: Buffer.alloc(8192, 3),
    });
    const baseUrl = await stub.start();
    const client = new JellyfinClient({ baseUrl });
    await ops.login(client, 'admin', 'secret');

    const result = await ops.testPlayback(client);
    assert.equal(result.ok, true);
    assert.equal(result.itemName, 'Big Buck Bunny');
    assert.equal(result.mediaSourceId, 'ms-1');
    assert.equal(result.container, 'mp4');
    assert.equal(result.bytesRead, 8192);
    assert.equal(result.contentType, 'video/mp4');
    await stub.stop();
  });

  it('reports the refusal when the parent is unreachable and nothing is cached', async () => {
    const stub = new StubJellyfin({
      username: 'admin',
      password: 'secret',
      playbackErrorCode: 'ParentServerUnavailable',
    });
    const baseUrl = await stub.start();
    const client = new JellyfinClient({ baseUrl });
    await ops.login(client, 'admin', 'secret');

    const result = await ops.testPlayback(client);
    assert.equal(result.ok, false);
    assert.equal(result.playbackErrorCode, 'ParentServerUnavailable');
    assert.equal(result.bytesRead, 0);
    // It must not have tried to stream after a refusal.
    assert.equal(stub.requestsFor('/Videos/').length, 0);
    await stub.stop();
  });
});

describe('syncAndWait', () => {
  it('starts a sync and returns once the server reports a newer completion', async () => {
    const stub = new StubJellyfin({ username: 'admin', password: 'secret' });
    const baseUrl = await stub.start();
    const client = new JellyfinClient({ baseUrl });
    await ops.login(client, 'admin', 'secret');

    const result = await ops.syncAndWait(client, 10_000, 10);
    assert.equal(result.completed, true);
    assert.equal(result.status.MirroredItemCount, 17);
    assert.equal(stub.requestsFor('/ChildServer/Sync').length, 1);
    await stub.stop();
  });
});
