/**
 * 09 - the parent server is down and the requested video is not on this device.
 *
 * The gate stops listening, which is what a dead parent looks like to the child. The API
 * refuses playback with the ParentServerUnavailable error code, and the web client, through
 * the plugin the child injects into it, shows "Can't connect to parent server" instead of a
 * broken player. Preconditions: lib/global-setup.ts synced the child through the gate.
 */
import { expect, test } from '@playwright/test';
import { CLIENT_NAME, CREDENTIALS, DEVICE_IDS, env } from '../lib/env';
import { GateControl } from '../lib/gate';
import { JellyfinApi } from '../lib/jellyfin';
import { readState, type E2EState } from '../lib/state';
import { clickPlay, login, openDetails, resumeAnimations, selectors, stabilize } from '../lib/ui';

test.describe.configure({ mode: 'serial' });

const EPISODE = 'S02E02';
const TITLE = "Can't connect to parent server";

let state: E2EState;
let child: JellyfinApi;
let gate: GateControl;

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
  await gate.up();
  await gate.reset();
});

function episodeId(): string {
  const ref = state.child.episodes[EPISODE];
  if (!ref) {
    throw new Error(`child state has no episode ${EPISODE}`);
  }
  return ref.id;
}

test('the served web client loads the child server plugin', async () => {
  const config = await child.get<{ plugins: string[] }>('/web/config.json');
  expect(config.plugins).toContain('ChildServerPlugin');

  const index = await (await child.raw('/web/index.html')).text();
  const scriptIndex = index.indexOf('childserver-plugin.js');
  const bundleIndex = index.indexOf('main.jellyfin');
  expect(scriptIndex).toBeGreaterThan(0);
  expect(scriptIndex).toBeLessThan(bundleIndex);

  const script = await child.raw('/web/childserver-plugin.js');
  expect(script.status).toBe(200);
  expect(script.headers.get('content-type')).toContain('javascript');
  expect(await script.text()).toContain('preplayintercept');
});

test('playback of an uncached item is refused while the parent is unreachable', async () => {
  expect(await child.postForStatus('/ChildServer/Cache/Clear')).toBe(200);
  await gate.down();

  // The reachability answer is cached for a few seconds.
  await expect
    .poll(async () => (await child.childAvailability(episodeId())).ParentReachable, { timeout: 30_000, intervals: [1_000] })
    .toBe(false);
  const availability = await child.childAvailability(episodeId());
  expect(availability.IsManaged).toBe(true);
  expect(availability.IsCached).toBe(false);

  const info = await child.playbackInfo(episodeId());
  expect(info.ErrorCode).toBe('ParentServerUnavailable');
  expect(info.MediaSources).toHaveLength(0);
});

test('the web client explains that the parent server cannot be reached', async ({ page }) => {
  await login(page, CREDENTIALS.childAdmin.username, CREDENTIALS.childAdmin.password);
  await openDetails(page, episodeId(), state.child.serverId);
  await clickPlay(page);

  const dialogs = page.locator(selectors.dialog);
  const dialog = dialogs.first();
  await expect(dialog).toBeVisible({ timeout: 60_000 });
  await expect(dialog.locator(selectors.dialogTitle)).toHaveText(TITLE);
  await expect(dialog.locator(selectors.dialogText)).toContainText('not stored on this device');
  await expect(page.locator(selectors.video)).toHaveCount(0);

  // One refusal, one message: a second play attempt must not stack another dialog on the first.
  await expect(dialogs).toHaveCount(1);

  await stabilize(page);
  await expect(page).toHaveScreenshot('parent-down-dialog.png', { fullPage: false });

  // The viewer can get rid of it. jellyfin-web removes a dialog when its exit animation ends, so
  // the frozen animations the screenshot needed have to be released first.
  await resumeAnimations(page);
  await dialog.locator(selectors.dialogButton).first().click();
  await expect(dialogs).toHaveCount(0);
});

test('playback works again once the parent is back', async () => {
  await gate.up();
  await expect
    .poll(async () => (await child.childAvailability(episodeId())).ParentReachable, { timeout: 30_000, intervals: [1_000] })
    .toBe(true);

  const info = await child.playbackInfo(episodeId());
  expect(info.ErrorCode ?? null).toBeNull();
  expect(info.MediaSources.length).toBeGreaterThan(0);
});
