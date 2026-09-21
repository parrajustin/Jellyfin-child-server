/**
 * 03 - playback from the parent.
 *
 * Preconditions (lib/global-setup.ts): the parent has the 13-file fixture library, the child
 * is connected to the parent through the gate and mirrors every item, the state file holds
 * the ids and tokens for both servers. Tests run serially and in this order: (c) leaves
 * S02E01 partly watched, which (d) clears before capturing the home page.
 */
import { expect, test } from '@playwright/test';
import { CLIENT_NAME, CREDENTIALS, DEVICE_IDS, env } from '../lib/env';
import { JellyfinApi, sha256, type ItemsResult } from '../lib/jellyfin';
import { readState, type E2EState, type EpisodeRef } from '../lib/state';
import {
  clickPlay,
  hideVideoFrame,
  login,
  openDetails,
  selectors,
  showPlayerOsd,
  stabilize,
  stopOsdKeepAlive,
  waitForHome,
  waitForPlayback,
} from '../lib/ui';

test.describe.configure({ mode: 'serial' });

const EPISODE_KEY = 'S02E01';

/** S02E01 is laid out from file_example_AVI_480_750kB.avi (tests/e2e/media/README.md). */
const S02E01_FIXTURE = {
  bytes: 742478,
  sha256: '7c1d478794e328ec1d2426b48c6115cb1c364fdfcaa65afcc8dd9cd4301121d2',
} as const;

const ITEM_QUERY = { Recursive: 'true', IncludeItemTypes: 'Movie,Episode', Fields: 'Path' } as const;

let state: E2EState;
let parent: JellyfinApi;
let child: JellyfinApi;

function episode(server: 'parent' | 'child'): EpisodeRef {
  const ref = state[server].episodes[EPISODE_KEY];
  if (!ref) {
    throw new Error(`${server} state has no episode ${EPISODE_KEY}; keys: ${Object.keys(state[server].episodes).join(', ')}`);
  }
  return ref;
}

const sortedNames = (result: ItemsResult): string[] => result.Items.map((item) => item.Name).sort();

test.beforeAll(() => {
  state = readState();
  parent = new JellyfinApi(env.parentUrl, CLIENT_NAME, DEVICE_IDS.parentAdmin).useAuth(
    state.parent.admin.token,
    state.parent.admin.userId,
  );
  child = new JellyfinApi(env.childUrl, CLIENT_NAME, DEVICE_IDS.childAdmin).useAuth(
    state.child.admin.token,
    state.child.admin.userId,
  );
});

test('child lists every parent movie and episode', async () => {
  const [childItems, parentItems] = await Promise.all([child.items(ITEM_QUERY), parent.items(ITEM_QUERY)]);

  expect(childItems.TotalRecordCount, 'child movie + episode count').toBe(13);
  expect(parentItems.TotalRecordCount, 'parent movie + episode count').toBe(13);
  expect(sortedNames(childItems), 'child names match parent names').toEqual(sortedNames(parentItems));
  expect(new Set(sortedNames(childItems))).toEqual(new Set(sortedNames(parentItems)));

  const series = await child.findItemByName('Supernatural', 'Series');
  expect(series.Type).toBe('Series');
  const seasons = await child.seasons(series.Id);
  expect(seasons.Items.map((season) => season.Name).sort(), 'Supernatural seasons on the child').toHaveLength(3);
  expect(seasons.TotalRecordCount).toBe(3);
});

test('playing an episode fetches it from the parent', async () => {
  const childEpisode = episode('child');
  const parentEpisode = episode('parent');

  const before = await child.childAvailability(childEpisode.id);
  expect(before.IsManaged, 'child manages the episode').toBe(true);
  expect(before.IsCached, 'episode is not cached before playback').toBe(false);

  const info = await child.playbackInfo(childEpisode.id);
  expect(info.ErrorCode ?? null, 'PlaybackInfo error code').toBeNull();
  expect(info.MediaSources.length, 'media sources').toBeGreaterThan(0);
  const source = info.MediaSources[0];
  expect(source.Container).toBe('avi');
  const videoStream = source.MediaStreams.find((stream) => stream.Type === 'Video');
  expect(videoStream, 'video stream').toBeTruthy();
  expect(videoStream?.Codec?.toLowerCase()).toBe('h264');
  expect(source.MediaStreams.some((stream) => stream.Type === 'Audio'), 'audio stream').toBe(true);

  const childBytes = await child.streamStatic(childEpisode.id, source.Id);

  const parentInfo = await parent.playbackInfo(parentEpisode.id);
  expect(parentInfo.MediaSources.length).toBeGreaterThan(0);
  const parentBytes = await parent.streamStatic(parentEpisode.id, parentInfo.MediaSources[0].Id);

  expect(parentBytes.byteLength, 'parent serves the fixture file').toBe(S02E01_FIXTURE.bytes);
  expect(sha256(parentBytes)).toBe(S02E01_FIXTURE.sha256);
  expect(childBytes.byteLength, 'child serves the same length').toBe(parentBytes.byteLength);
  expect(sha256(childBytes), 'child serves the same bytes').toBe(sha256(parentBytes));

  const after = await child.childAvailability(childEpisode.id);
  expect(after.IsCached, 'episode is cached after playback').toBe(true);
});

test('browser playback starts on the child', async ({ page }) => {
  const childEpisode = episode('child');

  await login(page, CREDENTIALS.childAdmin.username, CREDENTIALS.childAdmin.password);
  await openDetails(page, childEpisode.id, state.child.serverId);
  await clickPlay(page);
  await waitForPlayback(page, 90_000);

  expect(page.url()).toContain('#/video');

  await stabilize(page);
  // The frame is hidden with CSS rather than `mask`: the video element is full-viewport, so a
  // mask box over it would also cover the OSD controls being captured.
  await hideVideoFrame(page);
  await showPlayerOsd(page);
  try {
    await expect(page.locator(selectors.osdPage)).toHaveScreenshot('player-osd.png');
  } finally {
    await stopOsdKeepAlive(page);
  }
});

test('home shows the mirrored libraries', async ({ page }) => {
  // The previous test leaves S02E01 partly watched. Clear it so "Continue Watching" and
  // "Next Up" do not depend on how far playback got before the page closed.
  await child.markUnplayed(episode('child').id);

  await login(page, CREDENTIALS.childAdmin.username, CREDENTIALS.childAdmin.password);
  await waitForHome(page);

  const home = page.locator(selectors.homePage);
  await expect(home.getByText('Movies', { exact: true }).first()).toBeVisible({ timeout: 60_000 });
  await expect(home.getByText('TV Shows', { exact: true }).first()).toBeVisible({ timeout: 60_000 });

  await stabilize(page);
  await expect(page).toHaveScreenshot('home.png', { fullPage: false });
});
