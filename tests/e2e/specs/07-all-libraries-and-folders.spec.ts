/**
 * 07 - every video library of the parent shows up on the child, folders included.
 *
 * The parent has a "Home Videos" library with nested folders. The child must expose the same
 * library with the same folder tree and the same videos, next to the movie and show libraries,
 * and the web client must browse it. Preconditions: lib/global-setup.ts synced the child.
 */
import { expect, test } from '@playwright/test';
import { CLIENT_NAME, CREDENTIALS, DEVICE_IDS, env } from '../lib/env';
import { JellyfinApi, type ItemsResult } from '../lib/jellyfin';
import { readState, type E2EState } from '../lib/state';
import { login, resumeAnimations, stabilize, waitForHome } from '../lib/ui';

test.describe.configure({ mode: 'serial' });

const HOME_VIDEOS = 'Home Videos';
const EXPECTED_TREE = [
  '2018',
  '2018/Beach Trip',
  '2018/Beach Trip/Arrival',
  '2018/Beach Trip/Sunset',
  '2018/Birthday',
  'Clips',
  'Clips/Garden',
];

interface View {
  Id: string;
  Name: string;
  CollectionType?: string | null;
}

let state: E2EState;
let parent: JellyfinApi;
let child: JellyfinApi;

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

async function views(api: JellyfinApi): Promise<View[]> {
  const result = await api.get<{ Items: View[] }>(`/UserViews?userId=${encodeURIComponent(api.userId ?? '')}`);
  return result.Items;
}

async function view(api: JellyfinApi, name: string): Promise<View> {
  const found = (await views(api)).find((v) => v.Name === name);
  if (!found) {
    throw new Error(`${api.baseUrl} has no library named ${name}`);
  }
  return found;
}

/** Relative paths of every folder and video below a folder, in the parent's own naming. */
async function walk(api: JellyfinApi, parentId: string, prefix: string = ''): Promise<string[]> {
  const result = await api.items({ ParentId: parentId, SortBy: 'SortName', SortOrder: 'Ascending' });
  const paths: string[] = [];
  for (const item of result.Items) {
    const path = prefix ? `${prefix}/${item.Name}` : item.Name;
    paths.push(path);
    if (item.IsFolder) {
      paths.push(...(await walk(api, item.Id, path)));
    }
  }
  return paths;
}

test('the child exposes the same video libraries as the parent', async () => {
  const parentNames = (await views(parent)).filter((v) => v.CollectionType !== 'music').map((v) => v.Name).sort();
  const childNames = (await views(child)).map((v) => v.Name).sort();

  expect(childNames).toEqual(parentNames);
  expect(childNames).toEqual(expect.arrayContaining(['Movies', 'TV Shows', HOME_VIDEOS]));

  const childHomeVideos = await view(child, HOME_VIDEOS);
  expect(childHomeVideos.CollectionType).toBe('homevideos');
});

test('the home videos library keeps the parent folder structure', async () => {
  const parentTree = await walk(parent, (await view(parent, HOME_VIDEOS)).Id);
  const childTree = await walk(child, (await view(child, HOME_VIDEOS)).Id);

  expect(childTree.sort()).toEqual(parentTree.sort());
  expect(childTree).toEqual([...EXPECTED_TREE].sort());

  const childVideos: ItemsResult = await child.items({
    ParentId: (await view(child, HOME_VIDEOS)).Id,
    Recursive: 'true',
    IncludeItemTypes: 'Video',
    Fields: 'Path',
  });
  expect(childVideos.TotalRecordCount).toBe(4);
  for (const video of childVideos.Items) {
    const availability = await child.childAvailability(video.Id);
    expect(availability.IsManaged, video.Name).toBe(true);
  }
});

test('the web client browses the mirrored folders', async ({ page }) => {
  const library = await view(child, HOME_VIDEOS);
  const folders = await child.items({ ParentId: library.Id, SortBy: 'SortName', SortOrder: 'Ascending' });
  const year = folders.Items.find((item) => item.Name === '2018');
  expect(year).toBeDefined();

  await login(page, CREDENTIALS.childAdmin.username, CREDENTIALS.childAdmin.password);
  await waitForHome(page);
  await page.locator(`#indexPage .card[data-id="${library.Id}"] .cardImageContainer`).first().click();

  const cards = page.locator('.itemsContainer .card');
  await expect(cards.filter({ hasText: '2018' }).first()).toBeVisible({ timeout: 60_000 });
  await expect(cards.filter({ hasText: 'Clips' }).first()).toBeVisible();
  await stabilize(page);
  await expect(page).toHaveScreenshot('home-videos-library.png', { fullPage: false });

  // The card's overlay link sits above the image, so that anchor is what a viewer actually clicks.
  await resumeAnimations(page);
  await page.locator(`.itemsContainer .card[data-id="${year!.Id}"] a[href*="parentId="]`).first().click();
  await expect(cards.filter({ hasText: 'Beach Trip' }).first()).toBeVisible({ timeout: 60_000 });
  await expect(cards.filter({ hasText: 'Birthday' }).first()).toBeVisible();
  await stabilize(page);
  await expect(page).toHaveScreenshot('home-videos-folder.png', { fullPage: false });
});
