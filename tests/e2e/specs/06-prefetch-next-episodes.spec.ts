/**
 * 06 - prefetching the episodes that follow the one being watched.
 *
 * The cache is cleared first so the outcome does not depend on earlier specs. Reporting the
 * start of playback of S02E03 must make the child download S02E04 to S02E07 (four episodes
 * away) and nothing else: not the earlier episodes, and not S03E01.
 */
import { expect, test } from '@playwright/test';
import { CLIENT_NAME, DEVICE_IDS, env } from '../lib/env';
import { JellyfinApi } from '../lib/jellyfin';
import { readState, type E2EState } from '../lib/state';

test.describe.configure({ mode: 'serial' });

const WATCHED = 'S02E03';
const EXPECTED_PREFETCH = ['S02E04', 'S02E05', 'S02E06', 'S02E07'];
const MUST_STAY_PLACEHOLDERS = ['S01E01', 'S01E02', 'S02E01', 'S02E02', 'S03E01'];

let state: E2EState;
let child: JellyfinApi;

test.beforeAll(() => {
  state = readState();
  child = new JellyfinApi(env.childUrl, CLIENT_NAME, DEVICE_IDS.childAdmin).useAuth(
    state.child.admin.token,
    state.child.admin.userId,
  );
});

function episodeId(key: string): string {
  const ref = state.child.episodes[key];
  if (!ref) {
    throw new Error(`child state has no episode ${key}`);
  }
  return ref.id;
}

test('clearing the cache turns every cached file back into a placeholder', async () => {
  const before = await child.childStatus();
  const status = await child.postForStatus('/ChildServer/Cache/Clear');
  expect(status).toBe(200);

  const after = await child.childStatus();
  expect(after.CachedItemCount).toBe(0);
  expect(after.CachedBytes).toBe(0);
  expect(after.MirroredItemCount).toBe(before.MirroredItemCount);
  for (const key of [WATCHED, ...EXPECTED_PREFETCH, ...MUST_STAY_PLACEHOLDERS]) {
    const availability = await child.childAvailability(episodeId(key));
    expect(availability.IsManaged, key).toBe(true);
    expect(availability.IsCached, key).toBe(false);
  }
});

test('starting an episode downloads the next four and only those', async () => {
  const info = await child.playbackInfo(episodeId(WATCHED));
  expect(info.ErrorCode ?? null).toBeNull();

  // What every client sends when playback starts; the server reacts to it like to a browser play.
  const status = await child.postForStatus('/Sessions/Playing', {
    ItemId: episodeId(WATCHED),
    MediaSourceId: info.MediaSources[0].Id,
    PlaySessionId: info.PlaySessionId,
    CanSeek: true,
    IsPaused: false,
    IsMuted: false,
    PositionTicks: 0,
    PlayMethod: 'DirectPlay',
  });
  expect(status).toBe(204);

  await expect
    .poll(
      async () => {
        const cached: string[] = [];
        for (const key of EXPECTED_PREFETCH) {
          const availability = await child.childAvailability(episodeId(key));
          if (availability.IsCached) {
            cached.push(key);
          }
        }
        return cached;
      },
      { timeout: 180_000, intervals: [2_000] },
    )
    .toEqual(EXPECTED_PREFETCH);

  for (const key of EXPECTED_PREFETCH) {
    const availability = await child.childAvailability(episodeId(key));
    expect(availability.DownloadedBytes, key).toBe(availability.ExpectedBytes);
  }

  for (const key of MUST_STAY_PLACEHOLDERS) {
    const availability = await child.childAvailability(episodeId(key));
    expect(availability.IsCached, key).toBe(false);
    expect(availability.IsDownloading, key).toBe(false);
  }

  const after = await child.childStatus();
  expect(after.ActiveDownloads).toBe(0);
  expect(after.CachedItemCount).toBeGreaterThanOrEqual(EXPECTED_PREFETCH.length);
});
