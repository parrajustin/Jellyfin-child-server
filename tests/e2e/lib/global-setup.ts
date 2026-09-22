/**
 * Playwright global setup. Brings the parent, the gate and the child to the known fixture
 * state and writes the state file the specs read. Idempotent: running it again against
 * servers that are already set up only re-checks and rewrites the state file.
 */
import { CLIENT_NAME, CREDENTIALS, DEVICE_IDS, SERVER_NAMES, env, type Credentials } from './env';
import { GateControl } from './gate';
import { JellyfinApi, type AuthResult, type CollectionType } from './jellyfin';
import {
  episodeKey,
  movieKey,
  seasonKey,
  writeState,
  type E2EState,
  type EpisodeRef,
  type MovieRef,
  type ServerState,
} from './state';

/** The parent runs on Linux in Docker and on Windows for a local run; a mixed separator breaks its scan. */
const mediaSeparator = env.parentMediaRoot.includes('\\') ? '\\' : '/';

/** What tests/e2e/media/build-library.mjs lays out (see tests/e2e/media/README.md). */
export const FIXTURE = {
  items: 13,
  movies: 3,
  episodes: 10,
  seasons: 3,
  series: 'Supernatural',
  libraries: [
    { name: 'Movies', collectionType: 'movies' as CollectionType, path: `${env.parentMediaRoot}${mediaSeparator}Movies` },
    { name: 'TV Shows', collectionType: 'tvshows' as CollectionType, path: `${env.parentMediaRoot}${mediaSeparator}TV Shows` },
    { name: 'Home Videos', collectionType: 'homevideos' as CollectionType, path: `${env.parentMediaRoot}${mediaSeparator}Home Videos` },
  ],
  /** Plain videos in the home videos library (not counted in `items`, which covers movies and episodes). */
  homeVideos: 4,
} as const;

const HEALTH_TIMEOUT_MS = 180_000;
const SCAN_TIMEOUT_MS = 5 * 60_000;
const ITEM_QUERY = { Recursive: 'true', IncludeItemTypes: 'Movie,Episode', Fields: 'Path' } as const;

function log(message: string): void {
  console.log(`[e2e setup] ${message}`);
}

export default async function globalSetup(): Promise<void> {
  const startedAt = Date.now();
  const parent = new JellyfinApi(env.parentUrl, CLIENT_NAME, DEVICE_IDS.parentAdmin);
  const child = new JellyfinApi(env.childUrl, CLIENT_NAME, DEVICE_IDS.childAdmin);
  const gate = new GateControl(env.gateControlUrl);

  log(
    `parent ${env.parentUrl} | gate ${env.gateUrl} (control ${env.gateControlUrl}) | child ${env.childUrl} | ` +
      `child reaches the parent through ${env.parentUrlForChild}`,
  );
  await Promise.all([
    parent.waitForHealthy(HEALTH_TIMEOUT_MS).then(() => log('parent is healthy')),
    gate.waitForReady(HEALTH_TIMEOUT_MS).then(() => log('gate control is ready')),
    child.waitForHealthy(HEALTH_TIMEOUT_MS).then(() => log('child is healthy')),
  ]);

  // Baseline for every run: proxy up, no required headers, empty request log.
  await gate.resetAll();

  // ----- startup wizards -----
  const parentWizard = await parent.completeWizard(
    CREDENTIALS.parentAdmin.username,
    CREDENTIALS.parentAdmin.password,
    SERVER_NAMES.parent,
  );
  log(`parent startup wizard ${parentWizard ? 'completed' : 'was already completed'}`);
  const childWizard = await child.completeWizard(
    CREDENTIALS.childAdmin.username,
    CREDENTIALS.childAdmin.password,
    SERVER_NAMES.child,
  );
  log(`child startup wizard ${childWizard ? 'completed' : 'was already completed'}`);

  // ----- parent: libraries and scan -----
  const parentAuth = await parent.login(CREDENTIALS.parentAdmin.username, CREDENTIALS.parentAdmin.password);
  const existing = await parent.virtualFolders();
  let added = false;
  for (const library of FIXTURE.libraries) {
    if (existing.some((folder) => folder.Name === library.name)) {
      log(`parent library "${library.name}" already exists`);
      continue;
    }
    await parent.addLibrary(library.name, library.collectionType, [library.path]);
    added = true;
    log(`parent library "${library.name}" added (${library.path})`);
  }
  const parentCount = (await parent.items(ITEM_QUERY)).TotalRecordCount;
  if (added || parentCount !== FIXTURE.items) {
    await parent.refreshLibrary();
    log(`parent library scan requested (${parentCount} of ${FIXTURE.items} items before)`);
  }
  await parent.waitForItems({}, FIXTURE.items, SCAN_TIMEOUT_MS);
  log(`parent has ${FIXTURE.items} movies and episodes`);

  // The gate must front the parent: it is the only path the child is allowed to use.
  const viaGate = new JellyfinApi(env.gateUrl, CLIENT_NAME, DEVICE_IDS.parentAdmin);
  const [parentInfo, gateInfo] = await Promise.all([parent.publicInfo(), viaGate.publicInfo()]);
  if (gateInfo.Id !== parentInfo.Id) {
    throw new Error(
      `the gate at ${env.gateUrl} does not front the parent: server id ${gateInfo.Id} instead of ${parentInfo.Id}`,
    );
  }
  log(`gate forwards to parent "${parentInfo.ServerName}" ${parentInfo.Version} (${parentInfo.Id})`);

  // ----- child: parent link and mirror -----
  const childAuth = await child.login(CREDENTIALS.childAdmin.username, CREDENTIALS.childAdmin.password);
  await child.saveChildConfig({
    ParentUrl: env.parentUrlForChild,
    Username: CREDENTIALS.childToParent.username,
    Password: CREDENTIALS.childToParent.password,
    PrefetchEpisodeCount: 4,
    MaxCacheSizeMb: 512,
    DownloadWaitTimeoutSeconds: 300,
    SyncIntervalHours: 6,
    CustomHeaders: [],
  });
  const connection = await child.childConnect();
  if (connection.Status !== 'Success' || !connection.IsSuccess) {
    throw new Error(
      `child could not sign in to the parent through the gate (${env.parentUrlForChild}): ` +
        `${connection.Status}: ${connection.Message}`,
    );
  }
  log(
    `child connected to parent "${connection.ServerName ?? '?'}" ${connection.ServerVersion ?? '?'} ` +
      `as user ${connection.UserId ?? '?'}`,
  );

  const childCount = (await child.items(ITEM_QUERY)).TotalRecordCount;
  if (childCount !== FIXTURE.items) {
    log(`child has ${childCount} of ${FIXTURE.items} items; running a sync`);
    const synced = await child.waitForChildSync(SCAN_TIMEOUT_MS);
    log(`child sync completed at ${synced.LastSyncCompletedUtc} (${synced.MirroredItemCount} mirrored items)`);
  } else {
    log('child already mirrors every item; sync skipped');
  }
  await child.waitForItems({}, FIXTURE.items, SCAN_TIMEOUT_MS);
  log(`child lists ${FIXTURE.items} movies and episodes`);

  // ----- state file -----
  const state: E2EState = {
    version: 1,
    createdAt: new Date().toISOString(),
    parentUrlForChild: env.parentUrlForChild,
    gate: { url: env.gateUrl, controlUrl: env.gateControlUrl },
    parent: await describeServer(parent, env.parentUrl, CREDENTIALS.parentAdmin, DEVICE_IDS.parentAdmin, parentAuth),
    child: await describeServer(child, env.childUrl, CREDENTIALS.childAdmin, DEVICE_IDS.childAdmin, childAuth),
  };
  writeState(state);
  log(`state written to ${env.stateFile} (${((Date.now() - startedAt) / 1000).toFixed(1)} s)`);
}

async function describeServer(
  api: JellyfinApi,
  url: string,
  admin: Credentials,
  deviceId: string,
  auth: AuthResult,
): Promise<ServerState> {
  const info = await api.publicInfo();
  const series = await api.findItemByName(FIXTURE.series, 'Series');
  const seasons = await api.seasons(series.Id);
  const episodes = await api.episodes(series.Id);
  const movies = await api.items({ Recursive: 'true', IncludeItemTypes: 'Movie', Fields: 'Path' });

  if (seasons.Items.length !== FIXTURE.seasons) {
    const names = seasons.Items.map((season) => season.Name).join(', ') || 'none';
    throw new Error(`${url}: "${FIXTURE.series}" has ${seasons.Items.length} seasons (${names}), expected ${FIXTURE.seasons}`);
  }
  const seasonIds: Record<string, string> = {};
  for (const season of seasons.Items) {
    if (season.IndexNumber !== null && season.IndexNumber !== undefined) {
      seasonIds[seasonKey(season.IndexNumber)] = season.Id;
    }
  }

  const episodeMap: Record<string, EpisodeRef> = {};
  for (const episode of episodes.Items) {
    if (episode.ParentIndexNumber === null || episode.ParentIndexNumber === undefined) {
      throw new Error(`${url}: episode "${episode.Name}" (${episode.Id}) has no season number`);
    }
    if (episode.IndexNumber === null || episode.IndexNumber === undefined) {
      throw new Error(`${url}: episode "${episode.Name}" (${episode.Id}) has no episode number`);
    }
    const key = episodeKey(episode.ParentIndexNumber, episode.IndexNumber);
    if (episodeMap[key]) {
      throw new Error(`${url}: two episodes resolve to ${key}: ${episodeMap[key].id} and ${episode.Id}`);
    }
    episodeMap[key] = {
      id: episode.Id,
      name: episode.Name,
      key,
      season: episode.ParentIndexNumber,
      episode: episode.IndexNumber,
      seasonId: episode.SeasonId ?? undefined,
      path: episode.Path ?? undefined,
    };
  }
  if (Object.keys(episodeMap).length !== FIXTURE.episodes) {
    throw new Error(
      `${url}: found ${Object.keys(episodeMap).length} episodes (${Object.keys(episodeMap).sort().join(', ')}), ` +
        `expected ${FIXTURE.episodes}`,
    );
  }

  const movieMap: Record<string, MovieRef> = {};
  for (const movie of movies.Items) {
    let key = movieKey(movie);
    if (movieMap[key]) {
      key = `${key}#${movie.Id}`;
    }
    movieMap[key] = {
      id: movie.Id,
      name: movie.Name,
      key,
      path: movie.Path ?? undefined,
      year: movie.ProductionYear ?? undefined,
    };
  }
  if (Object.keys(movieMap).length !== FIXTURE.movies) {
    throw new Error(`${url}: found ${Object.keys(movieMap).length} movies, expected ${FIXTURE.movies}`);
  }

  return {
    url,
    serverId: info.Id,
    serverName: info.ServerName,
    version: info.Version,
    admin: { username: admin.username, password: admin.password, userId: auth.userId, token: auth.token, deviceId },
    itemCount: FIXTURE.items,
    supernatural: { seriesId: series.Id, seasonCount: seasons.Items.length, seasonIds },
    episodes: episodeMap,
    movies: movieMap,
  };
}
