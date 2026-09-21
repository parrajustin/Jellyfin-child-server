# End-to-end tests (Docker)

Black-box tests of the child server against a real parent Jellyfin server, run with
Playwright inside Docker. Everything in this folder is self-contained: no other part of the
repository is touched by the tests, and the only host requirements are Docker (with the
`docker compose` plugin) and Node 24 for the one script that lays out the fixture library.

## The stack

| Service  | Image                                    | Role                                                                                   |
|----------|------------------------------------------|----------------------------------------------------------------------------------------|
| `parent` | `jellyfin/jellyfin:12.1`                 | The upstream server. Serves the fixture library from `.work/parent-media` (read-only).  |
| `gate`   | `node:24-alpine` running `gate/server.mjs` | Reverse proxy in front of the parent and the **only** route the child may use.        |
| `child`  | `jellyfin-child-server:e2e` (repo `Dockerfile`) | The system under test. Its parent URL is `http://gate:8096`.                    |
| `e2e`    | `mcr.microsoft.com/playwright:v1.63.0-noble` (`tests/e2e/Dockerfile`) | The Playwright runner, profile `test`.     |

The child talks to the parent only through the gate, so tests can take the parent "down"
(the proxy listener closes and open sockets are destroyed: the child sees `ECONNREFUSED`,
exactly like a dead parent) and can require Cloudflare-Access style headers on every request
(`403 access denied by gate` otherwise). See the header of `gate/server.mjs` for the control
API and `lib/gate.ts` for the client.

### Port map

| Host port | Container       | Purpose                                             |
|-----------|-----------------|-----------------------------------------------------|
| 18096     | `parent:8096`   | Parent server (direct, not through the gate)        |
| 18097     | `gate:8096`     | Gate proxy (forwards to the parent)                 |
| 18098     | `gate:8097`     | Gate control API (`/state`, `/down`, `/up`, ...)    |
| 18196     | `child:8096`    | Child server                                        |

Inside the compose network the `e2e` service uses the service names (`CHILD_URL=http://child:8096`
and so on); the defaults in `lib/env.ts` are the host ports above, so a developer can also run
`npx playwright test` from this folder against a stack started with `docker compose up -d parent gate child`.

## Running locally

```bash
bash tests/e2e/run.sh                     # whole suite
bash tests/e2e/run.sh -g "browser playback"   # one test (any playwright args pass through)
```

`run.sh` does, in order:

1. `node media/build-library.mjs .work/parent-media` lays out the 13 fixture files
   (3 movies, 10 Supernatural episodes; sizes and hashes in `media/README.md`).
2. `docker compose build child e2e` (only `e2e` when `E2E_SKIP_CHILD_BUILD=1`).
3. `docker compose up -d parent gate child`.
4. `docker compose run --rm e2e npx playwright test "$@"`.
5. Copies `docker compose logs` of `child`, `parent` and `gate` into `test-results/*.log`.
6. `docker compose down -v --remove-orphans`, then exits with the Playwright exit code.

Results land in `test-results/` (HTML report in `test-results/html`, JUnit in
`test-results/junit.xml`, traces and failure screenshots in `test-results/artifacts`).
On Linux hosts the files written by the container are owned by root.

Global setup (`lib/global-setup.ts`) waits for all three services, completes both startup
wizards, creates the parent libraries with every remote metadata provider disabled, waits
for the 13 items, configures the child (`ParentUrl = http://gate:8096`, parent admin
credentials, prefetch 4, cache 512 MB), connects it, runs a sync, waits for the child to
list the same 13 items and writes `.work/state.json` (tokens, user ids, series, season,
episode and movie ids on both servers). It is idempotent: against servers that are already
set up it only re-checks and rewrites the state file.

Credentials (test-only): parent admin `parentadmin` / `Parent!Pass123`, child admin
`childadmin` / `Child!Pass123`; the child signs in to the parent as the parent admin.

## Golden screenshots

Screenshot goldens live in `specs/__screenshots__/<spec file>/<name>.png` with no platform
or project suffix, and **they are only ever produced inside the Linux e2e container**
(same image, same browser build, same fonts). Never run `--update-snapshots` on a host
Playwright install and never commit a golden that came from one.

To create or update goldens:

```bash
bash tests/e2e/run.sh --update-snapshots
git add tests/e2e/specs/__screenshots__
git commit
```

or trigger the `CI` workflow manually with `update_snapshots = true` and download the
`e2e-screenshots` artifact. When a golden is missing, Playwright writes the actual image to
the goldens folder and fails that test ("A snapshot doesn't exist ... writing actual"); the
written files are still uploaded in `e2e-screenshots`, so a first CI run can bootstrap them.

Comparison settings (`playwright.config.ts`): `maxDiffPixelRatio 0.02`, animations
disabled, caret hidden, CSS scale, viewport 1280x800, dark scheme, `en-US`, UTC. The
player capture hides the video frame and the wall-clock "Ends at" text with CSS and keeps
the OSD visible with pointer moves (jellyfin-web hides it 3 s after the last input).

A developer without goldens can still run the flows with `E2E_IGNORE_SNAPSHOTS=1`
(`ignoreSnapshots` in the config); every non-screenshot assertion still runs.

## CI

`.github/workflows/ci.yml` runs the `e2e` job on every push to `main`, every pull request
and on manual dispatch, independently of the unit tests:

1. `docker/build-push-action` builds the child image from the repository `Dockerfile` as
   `jellyfin-child-server:e2e` (`load: true`, GitHub Actions layer cache).
2. `bash tests/e2e/run.sh` with `E2E_SKIP_CHILD_BUILD=1`, plus `--update-snapshots` when
   the workflow was dispatched with `update_snapshots = true`.
3. Uploads `tests/e2e/test-results` as `e2e-results` and `tests/e2e/specs/__screenshots__`
   as `e2e-screenshots` (both `if: always()`).

## Environment variables

| Variable                | Default (host run)               | Meaning                                                         |
|-------------------------|----------------------------------|-----------------------------------------------------------------|
| `CHILD_URL`             | `http://localhost:18196`         | Child server as seen by the tests                               |
| `PARENT_URL`            | `http://localhost:18096`         | Parent server as seen by the tests (direct)                     |
| `GATE_URL`              | `http://localhost:18097`         | Gate proxy as seen by the tests                                 |
| `GATE_CONTROL_URL`      | `http://localhost:18098`         | Gate control API                                                |
| `PARENT_URL_FOR_CHILD`  | `http://gate:8096`               | Parent URL configured on the child (inside the compose network) |
| `STATE_FILE`            | `tests/e2e/.work/state.json`     | Written by global setup, read by the specs                      |
| `E2E_IGNORE_SNAPSHOTS`  | `0`                              | `1` skips screenshot assertions                                 |
| `E2E_SKIP_CHILD_BUILD`  | `0`                              | `1` makes `run.sh` skip building the child image                |

Gate container variables: `GATE_UPSTREAM` (default `http://parent:8096`), `GATE_PORT`
(8096), `GATE_CONTROL_PORT` (8097), `GATE_BIND` (0.0.0.0).

## Layout

```
tests/e2e/
  package.json, package-lock.json   @playwright/test 1.63.0, typescript 5.x, @types/node 24.x
  playwright.config.ts              serial, one chromium project, goldens without platform suffix
  tsconfig.json
  Dockerfile                        e2e runner image
  docker-compose.yml                parent, gate, child, e2e (profile "test")
  run.sh                            the whole flow, used locally and by CI
  lib/env.ts                        URLs, credentials, device ids
  lib/jellyfin.ts                   typed fetch client: wizard, login, libraries, playback, /ChildServer/*
  lib/gate.ts                       gate control client
  lib/state.ts                      state file shape and readers
  lib/global-setup.ts               brings both servers to the fixture state
  lib/ui.ts                         jellyfin-web 12.1 selectors and page helpers
  specs/03-playback-from-parent.spec.ts
  specs/__screenshots__/            committed goldens (Linux container only)
  gate/server.mjs                   proxy + control API, node built-ins only
  media/                            fixture clips and build-library.mjs
```

## Child server API contract used by the tests

Admin endpoints need the admin token; enums are strings; JSON is PascalCase.

- `GET /ChildServer/Configuration` -> `{ParentUrl, Username, Password (always null), HasPassword, CustomHeaders:[{Name,Value}], PrefetchEpisodeCount, MaxCacheSizeMb, DownloadWaitTimeoutSeconds, SyncIntervalHours}`; `POST` same shape -> 204 (400 on invalid).
- `POST /ChildServer/TestConnection` `{Url, Username, Password, CustomHeaders}` -> 200 `{Status: Success|InvalidCredentials|AccessDenied|ConnectionFailed|InvalidResponse|NotConfigured, Message, ServerName, ServerVersion, ServerId, UserId, IsSuccess}`.
- `POST /ChildServer/Connect` -> same result shape (stored settings, persists the token).
- `GET /ChildServer/Status` -> `{IsConfigured, IsAuthenticated, ParentServerName, ParentServerVersion, ParentUserId, LastConnectionStatus, LastConnectionMessage, LastConnectionAttemptUtc, SyncState: Idle|Running, LastSyncStartedUtc, LastSyncCompletedUtc, LastSyncError, MirroredItemCount, CachedItemCount, CachedBytes, ActiveDownloads}`.
- `POST /ChildServer/Sync` -> 202 (background sync).
- `GET /ChildServer/Items/{itemId}/Availability` (any signed-in user) -> `{IsManaged, IsCached, IsDownloading, DownloadedBytes, ExpectedBytes, ParentReachable}`.

`waitForChildSync` in `lib/jellyfin.ts` treats a sync as done when `SyncState` is back to
`Idle` and `LastSyncCompletedUtc` changed to a later server timestamp than before the
trigger; it fails as soon as `LastSyncError` is set for that sync. Only server timestamps
are compared with each other, never with the test runner's clock.

## Notes and known limits

- Jellyfin's file name cleaning strips `480p`/`720p`/`1080p`, so the three movies are all
  named "Sample Movie" (2018) on both servers. The state file keys movies by file base name
  (`Sample Movie 480p (2018)`), episodes by `S02E01` style keys.
- The bundled Playwright Chromium has no H.264/AAC decoders, so the browser playback test
  relies on the child transcoding for the browser (HLS or WebM, whatever jellyfin-web
  negotiates). If that ever proves unworkable, switch the project to `channel: 'chrome'` and
  add `npx playwright install chrome` to `tests/e2e/Dockerfile`.
- "playing an episode fetches it from the parent" asserts the episode is **not** cached
  before the first stream; it only passes against a fresh child container (which is what
  `run.sh` and CI give you).
- The e2e container needs `ipc: host` (set in the compose file) for Chromium's shared memory.
