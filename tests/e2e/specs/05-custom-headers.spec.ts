/**
 * 05 - custom request headers, as needed for a parent behind Cloudflare Access.
 *
 * The gate is told to require two service token headers on every request. The child must
 * send the configured headers with every call to the parent: connection tests, sign in,
 * media downloads and the listing calls of a library sync. The last test checks the header
 * rows on the settings page.
 * Preconditions: lib/global-setup.ts connected the child to the parent through the gate.
 */
import { expect, test, type Locator, type Page } from '@playwright/test';
import { CLIENT_NAME, CREDENTIALS, DEVICE_IDS, env } from '../lib/env';
import { GateControl } from '../lib/gate';
import { JellyfinApi, type ParentRequestHeader } from '../lib/jellyfin';
import { readState, type E2EState } from '../lib/state';
import { login, stabilize } from '../lib/ui';

test.describe.configure({ mode: 'serial' });
test.use({ viewport: { width: 1280, height: 1800 } });

const PAGE_ROUTE = `${env.childUrl}/web/#/configurationpage?name=ChildServerParent`;
const OUTCOME = /^(Success|InvalidCredentials|AccessDenied|ConnectionFailed|InvalidResponse|NotConfigured|RequestFailed)$/;

/** What the gate demands (lowercased names, as it reports them) and what the child is configured with. */
const REQUIRED = {
  'cf-access-client-id': 'e2e-service-token.access',
  'cf-access-client-secret': 'e2e-service-token-secret-value',
} as const;
const HEADERS: ParentRequestHeader[] = [
  { Name: 'CF-Access-Client-Id', Value: REQUIRED['cf-access-client-id'] },
  { Name: 'CF-Access-Client-Secret', Value: REQUIRED['cf-access-client-secret'] },
];

let state: E2EState;
let child: JellyfinApi;
let gate: GateControl;

/** Body for POST /ChildServer/TestConnection. */
const credentials = () => ({
  Url: env.parentUrlForChild,
  Username: CREDENTIALS.childToParent.username,
  Password: CREDENTIALS.childToParent.password,
});

/** Body for POST /ChildServer/Configuration. */
const settings = () => ({
  ParentUrl: env.parentUrlForChild,
  Username: CREDENTIALS.childToParent.username,
  Password: CREDENTIALS.childToParent.password,
});

test.beforeAll(async () => {
  state = readState();
  child = new JellyfinApi(env.childUrl, CLIENT_NAME, DEVICE_IDS.childAdmin).useAuth(
    state.child.admin.token,
    state.child.admin.userId,
  );
  gate = new GateControl(env.gateControlUrl);
  await gate.resetAll();
});

test.afterAll(async () => {
  // Leave the stack as global setup made it: no header requirement, no headers configured.
  await gate.requireHeaders({});
  await gate.reset();
  await child.saveChildConfig({ ...settings(), CustomHeaders: [] });
  const result = await child.childConnect();
  expect(result.Status).toBe('Success');
});

test('a gateway that demands headers rejects a connection without them', async () => {
  await gate.requireHeaders(REQUIRED);

  const result = await child.childTestConnection({ ...credentials(), CustomHeaders: [] });

  expect(result.Status).toBe('AccessDenied');
  expect(result.IsSuccess).toBe(false);
  expect(result.Message).toContain('403');
});

test('the configured headers are sent with every request of a connection test', async () => {
  await gate.reset();

  const result = await child.childTestConnection({ ...credentials(), CustomHeaders: HEADERS });

  expect(result.Status).toBe('Success');
  const requests = await gate.requests();
  expect(requests.length).toBeGreaterThanOrEqual(2);
  expect(requests.map((r) => r.path)).toEqual(expect.arrayContaining(['/System/Info/Public', '/Users/AuthenticateByName']));
  for (const request of requests) {
    expect(request.headers['cf-access-client-id']).toBe(REQUIRED['cf-access-client-id']);
    expect(request.headers['cf-access-client-secret']).toBe(REQUIRED['cf-access-client-secret']);
    expect(request.status).toBeLessThan(400);
  }
});

test('saved headers carry over to sign in and media downloads', async () => {
  await child.saveChildConfig({ ...settings(), CustomHeaders: HEADERS });
  const saved = await child.childConfig();
  expect(saved.CustomHeaders.map((h) => h.Name)).toEqual(['CF-Access-Client-Id', 'CF-Access-Client-Secret']);

  const connect = await child.childConnect();
  expect(connect.Status).toBe('Success');

  // Fetch a movie that no earlier spec touched: its download must go through the gate with the headers.
  const movie = Object.values(state.child.movies)[0];
  const before = await child.childAvailability(movie.id);
  expect(before.IsManaged).toBe(true);
  expect(before.IsCached).toBe(false);

  await gate.reset();
  const info = await child.playbackInfo(movie.id);
  const mediaSourceId = info.MediaSources[0].Id;
  const bytes = await child.streamStatic(movie.id, mediaSourceId);
  expect(bytes.length).toBe(before.ExpectedBytes);

  const after = await child.childAvailability(movie.id);
  expect(after.IsCached).toBe(true);

  const download = (await gate.requests()).find((r) => r.path.includes('/stream'));
  expect(download).toBeDefined();
  expect(download?.headers['cf-access-client-id']).toBe(REQUIRED['cf-access-client-id']);
  expect(download?.headers['cf-access-client-secret']).toBe(REQUIRED['cf-access-client-secret']);
  expect(download?.status).toBe(200);
});

test('a library sync sends the headers on every listing request', async () => {
  // The mirror in global setup ran before any header requirement existed, so the listing paths had
  // never been seen with one in force: only the two sign in calls and the media download were.
  // Syncing again here puts /UserViews and the item queries through the gate under the requirement.
  await gate.reset();
  await child.waitForChildSync();

  const requests = await gate.requests();
  const views = requests.filter((r) => r.path.startsWith('/UserViews'));
  const itemQueries = requests.filter((r) => /^\/Items\?/.test(r.path));

  expect(views.length, 'a sync must list the parent views').toBeGreaterThan(0);
  expect(itemQueries.length, 'a sync must query the parent items').toBeGreaterThan(0);

  for (const request of [...views, ...itemQueries]) {
    expect(request.headers['cf-access-client-id'], `on ${request.method} ${request.path}`).toBe(
      REQUIRED['cf-access-client-id'],
    );
    expect(request.headers['cf-access-client-secret'], `on ${request.method} ${request.path}`).toBe(
      REQUIRED['cf-access-client-secret'],
    );
    expect(request.status, `on ${request.method} ${request.path}`).toBeLessThan(400);
  }

  // Not covered here: the image fetch (`/Items/{id}/Images/...`). The mirror skips an image whose
  // sidecar is already on disk (ChildLibraryMirror.FetchImageAsync), so a re-sync never requests
  // one, and nothing in the suite can delete the sidecars — Cache/Clear only truncates the video
  // files. Covering it needs a mirror from an empty data directory with the requirement already in
  // force, which means requiring the headers in global setup rather than in this spec.
});

test('the settings page shows the header rows and the gateway outcome', async ({ page }) => {
  await login(page, CREDENTIALS.childAdmin.username, CREDENTIALS.childAdmin.password);
  const root = await openSettingsPage(page);

  const names = root.locator('.csHeaderRow .csHeaderName');
  await expect(names).toHaveCount(2);
  await expect(names.nth(0)).toHaveValue('CF-Access-Client-Id');
  await expect(names.nth(1)).toHaveValue('CF-Access-Client-Secret');
  await expect(root.locator('#emptyHeaders')).toBeHidden();

  await root.locator('#btnTestConnection').click();
  const box = root.locator('#connectionStatus');
  await expect(box).toHaveAttribute('data-status', OUTCOME, { timeout: 60_000 });
  await expect(box).toHaveAttribute('data-status', 'Success');
  await expect(root).toHaveScreenshot('parent-page-headers.png', { mask: [root.locator('.childServerTime')] });

  // Without the rows the gateway refuses the child.
  await root.locator('.csRemoveHeader').first().click();
  await root.locator('.csRemoveHeader').first().click();
  await expect(names).toHaveCount(0);
  await expect(root.locator('#emptyHeaders')).toBeVisible();
  await root.locator('#btnTestConnection').click();
  await expect(box).toHaveAttribute('data-status', OUTCOME, { timeout: 60_000 });
  await expect(box).toHaveAttribute('data-status', 'AccessDenied');
  await expect(box.locator('#connectionStatusTitle')).toHaveText('Access denied');
  await expect(root).toHaveScreenshot('parent-page-access-denied.png', { mask: [root.locator('.childServerTime')] });
});

async function openSettingsPage(page: Page): Promise<Locator> {
  await page.goto(PAGE_ROUTE, { waitUntil: 'domcontentloaded' });
  const root = page.locator('#childServerParentPage').last();
  await root.waitFor({ state: 'visible', timeout: 60_000 });
  await expect(root.locator('#txtParentUrl')).toHaveValue(/.+/, { timeout: 30_000 });
  await expect(root.locator('#factConfigured .csBadge')).toBeVisible({ timeout: 30_000 });
  await stabilize(page);
  return root;
}
