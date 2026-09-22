# Jellyfin child server: progress

Last updated: 2026-09-21 (session 2). Repository: https://github.com/parrajustin/Jellyfin-child-server, branch `main`.

## Goal

A Jellyfin server that runs on a device with little disk space. It mirrors the catalog of a
"parent" Jellyfin server, keeps only the video being watched (plus the episodes likely to
follow) on the local disk, fetches everything else from the parent on demand, and tells the
viewer plainly when the parent cannot be reached. Built from the Jellyfin server source at
tag v12.1 with no upstream git history, one conventional commit per step.

## Requested steps and status

| # | Step | Status | Where |
|---|------|--------|-------|
| 1 | Import Jellyfin v12.1 source into the new repo, no history | Done, pushed | `b74c2b8` |
| – | CI: build and run the upstream test suite on every push | Done, pushed | `a884165`, `.github/workflows/ci.yml` |
| 2 | Child signs in to a parent server | Done, pushed | `fb509c2` |
| 3 | Docker test with two servers; a user views child media fetched from the parent | Done, pushed | `382e5ab` server, `505bada` suite, Dockerfile and CI job |
| 4 | Admin UI for the parent user name/password with status messages | Done, pushed | `b6e321b`, `0c8a035`, `521489d` |
| 5 | Custom header support (Cloudflare Zero Trust and the like) | Done, pushed | `d81c4b8` |
| 6 | Prefetch the next episodes (up to 4 away) while watching | Done, pushed | `1d68993` |
| 7 | Child shows all parent content: all folders, movies and TV shows | Done, pushed | `3571f2a`, and the upstream scan fix `ffd3597` |
| 8 | Golden image UI tests for every step | Specs cover every step; one golden file is committed, the rest are being generated in CI | see "Golden test images" |
| 9 | "Can't connect to parent server" when the parent is down | Done, pushed | `acf6d96` |
| 10 | `./release` building the Alpine container, all tests must pass first | Done, pushed | `6eed657` |

## Actions taken

### Step 1 and CI

- Imported the GitHub tarball of `jellyfin/jellyfin` at tag `v12.1` (upstream commit `ee91c75`) as a
  flat snapshot with no upstream history, dropping only the upstream `.github` folder so Jellyfin's
  own workflows do not run here.
- Installed .NET SDK 10.0.401 into the user profile (Jellyfin v12.1 targets `net10.0`). This machine
  has no Docker and no WSL, so container work happens in GitHub Actions and everything else runs
  against a local two-server harness.
- CI builds the solution and runs the full upstream xunit suite on Ubuntu, plus an `e2e` job that
  builds the child image with a layer cache and runs the browser suite, plus a `release` job behind a
  manual input.

### Step 2: parent sign in

- `src/Jellyfin.ChildServer` with `ParentServerClient` (Jellyfin `MediaBrowser` authorization header,
  custom headers, timeouts, mapped failures) and `ChildServerManager` (settings in the `childserver`
  configuration store; the access token is reused and renewed when the parent rejects it).
- Admin API `ChildServerController`: `GET/POST /ChildServer/Configuration`,
  `POST /ChildServer/TestConnection`, `POST /ChildServer/Connect`, `GET /ChildServer/Status`. The
  password never leaves the server.

### Step 3: mirror and on-demand fetch

- `ChildLibraryMirror` reads the parent's views and items and writes zero byte placeholder videos with
  NFO sidecars and poster/backdrop/thumb images under `<data>/childserver/library/<Library>/...`,
  registers each parent library locally with every internet provider disabled, drops items that
  vanished upstream, then runs the library scan. Scheduled task `ChildServerSync` (startup plus
  interval) and `POST /ChildServer/Sync`.
- `<data>/childserver/mirror.json` records, per placeholder, the parent item and media source ids,
  size, container, duration, bitrate, media streams and cache state.
- `MirroredMediaInfoProvider` applies the parent's probe data; the upstream `ProbeProvider` skips
  mirrored paths so empty files are never handed to ffprobe.
- `ChildMediaCache`: one download worker with priorities, resumable downloads into `<file>.jfpart`
  then an atomic move over the placeholder, retries, least recently played eviction above
  `MaxCacheSizeMb`, parent reachability cached for 5 seconds.
- Core hooks: `StreamingHelpers` prepares the file before any stream; `VideosController` serves a
  still-downloading file through a seekable stream that waits for bytes, so range requests work;
  `MediaInfoHelper` answers `PlaybackErrorCode.ParentServerUnavailable`; `ExceptionMiddleware` maps
  `ParentServerUnavailableException` to HTTP 503 with `X-Application-Error-Code`.

### Step 4: admin page

- `src/Jellyfin.ChildServer/Pages/childserver-parent.html`, served as a dashboard configuration page
  (`#/configurationpage?name=ChildServerParent`) and listed in the dashboard menu as "Parent server".
  URL, user name, password, extra headers, cache limits; Test connection, Save, Sync now; a status
  block with sync and cache facts.
- `DashboardController` lists and serves built-in pages next to plugin pages.
- Spec 04 covers the loaded page and the incorrect password, unreachable and success outcomes.

### Step 5: gateway headers

- Header rows on the settings page, sent on every parent request (connection test, sign in, listing,
  images, downloads). Spec 05 puts the gate proxy in "headers required" mode and checks that a
  connection without them is refused and that the saved headers reach every request.
- `docs/child-server.md` documents Cloudflare Access service tokens.

### Step 6: prefetch

- `ChildPrefetchConsumer` listens for `PlaybackStart`; `PrefetchPlanner` picks the following episodes
  in the same series, default four, configurable. They download at prefetch priority behind anything
  the viewer is waiting for. `POST /ChildServer/Cache/Clear` empties the cache for tests.
- Spec 06 clears the cache, reports playback of S02E03 and checks that S02E04 to S02E07 arrive and
  nothing else does.

### Step 7: every video library, folders included

- The mirror covered movies and TV shows only. It now mirrors every video library the parent exposes:
  movies, TV shows, home videos, music videos and folder libraries with no collection type.
- Movies and TV shows keep the naming-based layout the scanner expects. Every other library is walked
  as a tree: `ParentLibraryReader.GetChildrenAsync` lists one folder at a time and the child recreates
  the same folder names, so the viewer sees the parent's own structure. The walk stops at 24 levels;
  names that collide inside one folder get the first eight characters of the parent item id appended.
- The fixture gained a "Home Videos" library with nested folders (`2018/Beach Trip`, `2018`, `Clips`).
- Spec 07 compares libraries and the whole folder tree with the parent and browses the folders in the
  web client.

### An upstream scan hang found by step 7

Adding the home videos library made the child's library scan stop at 88% forever, with no error. A
managed thread dump showed one thread spinning in `LibraryManager.GetCollectionFolders`, walking an
item's parent/owner chain. The cause is upstream, not in the child server: a video that sits directly
in a folder named after an extra type (`clips`, `extras`, `trailers` and the other directory-name
rules) is resolved as a library item in its own right, and `FindExtras` then matched it against the
rule for its own folder. The item became its own extra, `RefreshExtras` set its owner to itself and
cleared its parent, and the walk never reached a library root. A plain parent server with the same
media reproduced it, which ruled out the child server code.

Fixed in two places: `FindExtras` skips a candidate whose path or id is the owner's own, and
`GetCollectionFolders` stops after 128 levels and logs the item it loops on instead of spinning. Two
regression tests were added to the upstream `FindExtrasTests`.

### Step 9: "Can't connect to parent server"

- The child injects a small script into the web client it serves, without forking jellyfin-web:
  `ChildServerWebClientMiddleware` serves `/web/childserver-plugin.js`, adds the script tag to
  `/web/index.html` ahead of the application bundle, and lists the plugin in `/web/config.json`.
  Everything else still comes from the static files on disk.
- The script registers a pre-play interceptor. Before playback it asks
  `/ChildServer/Items/{id}/Availability`; when the item is managed by the child, is not on this device
  and the parent does not answer, it shows the message and stops playback. When availability cannot be
  determined it stays out of the way and lets the server decide.
- Spec 09 checks the served config, index page and script, the API error code, the dialog the viewer
  sees, that only one dialog appears, that it can be dismissed, and that playback works again once the
  parent is back.

### Step 10: the release script

`./release` runs, in this order: build in Release, the unit and integration suites, the Docker browser
suite, then the container image from the repository `Dockerfile` whose runtime stage is
`mcr.microsoft.com/dotnet/aspnet:10.0-alpine`, then a check that the built image really is Alpine.
Any failing step stops the run and nothing is tagged. Tags default to the assembly version, the short
commit and `latest`; `--tag` overrides them, `--push` pushes them, `--skip-e2e` leaves out the Docker
suite. Missing prerequisites are reported before any work starts. A `release` CI job runs it on
demand through the workflow's `release` input.

## Remaining steps

1. **Goldens (step 8)**: the CI run with `update_snapshots = true` produces every screenshot; download
   the `e2e-screenshots` artifact and commit the PNGs listed below. Until then only `player-osd.png`
   is committed and the other screenshot assertions are skipped.
2. **A green CI `e2e` run** with the goldens in place, and one `release` run to prove the Alpine image
   builds from these commits.
3. **Housekeeping**: register the project in the project standard registry and write the feature docs.

## Golden test images

- Location: `tests/e2e/specs/__screenshots__/<spec file name>/<screenshot name>.png`, from
  `snapshotPathTemplate: '{testDir}/__screenshots__/{testFileName}/{arg}{ext}'` in
  `tests/e2e/playwright.config.ts`, deliberately without a platform suffix.
- Committed today:
  - `tests/e2e/specs/__screenshots__/03-playback-from-parent.spec.ts/player-osd.png`
- Expected once generated in CI:
  - `03-playback-from-parent.spec.ts/home.png`
  - `04-parent-connection-page.spec.ts/parent-page-loaded.png`
  - `04-parent-connection-page.spec.ts/parent-page-invalid-password.png`
  - `04-parent-connection-page.spec.ts/parent-page-unreachable.png`
  - `04-parent-connection-page.spec.ts/parent-page-success.png`
  - `05-custom-headers.spec.ts/parent-page-headers.png`
  - `05-custom-headers.spec.ts/parent-page-access-denied.png`
  - `07-all-libraries-and-folders.spec.ts/home-videos-library.png`
  - `07-all-libraries-and-folders.spec.ts/home-videos-folder.png`
  - `09-parent-unreachable.spec.ts/parent-down-dialog.png`
- Goldens are only valid when produced inside the Linux Playwright container
  (`mcr.microsoft.com/playwright:v1.63.0-noble`): fonts and rendering differ per platform. To create
  or refresh them, run the CI workflow manually with `update_snapshots = true` (or locally with
  Docker, `bash tests/e2e/run.sh --update-snapshots`), download the `e2e-screenshots` artifact, copy
  it into `tests/e2e/specs/__screenshots__/` and commit. Without goldens, run the flows with
  `E2E_IGNORE_SNAPSHOTS=1`.
- Comparison tolerance: `maxDiffPixelRatio 0.02`, animations disabled, caret hidden, viewport
  1280x800 (1280x1800 in specs 04 and 05), dark colour scheme, locale `en-US`, timezone UTC. Time
  stamps on the admin page are masked, the "Recently added" row on home is masked, and the video
  frame is hidden by CSS when capturing the player.

## Implementation summary

- `MediaBrowser.Model/ChildServer/` and `MediaBrowser.Controller/ChildServer/`: settings, DTOs, enums
  and the interfaces the rest of the server depends on (`IChildServerManager`,
  `IChildServerMediaCache`, `IChildServerLibrarySync`, `IChildServerWebPages`).
- `src/Jellyfin.ChildServer/`: the implementation assembly, registered in `CoreAppHost`.
  - `ParentServerClient`, `ChildServerManager`, `ParentSession`, `ChildServerPaths`
  - `Mirror/`: `ParentLibraryReader`, `MirrorPathBuilder`, `NfoWriter`, `MirrorManifest`,
    `MirrorEntry`, `ChildLibraryMirror`, `ChildLibrarySyncTask`
  - `Cache/`: `ChildMediaCache`, `DownloadJob`, `ChildCacheStream`, `PrefetchPlanner`,
    `ChildPrefetchConsumer`
  - `Metadata/MirroredMediaInfoProvider`
  - `Pages/`: `ChildServerWebPages`, `ChildServerWebClient`, `childserver-parent.html`,
    `childserver-plugin.js`
- Hooks in upstream files: `Jellyfin.Api/Controllers/ChildServerController.cs` (new),
  `Jellyfin.Api/Middleware/ChildServerWebClientMiddleware.cs` (new), `VideosController`,
  `DashboardController`, `Helpers/StreamingHelpers`, `Helpers/MediaInfoHelper`,
  `Middleware/ExceptionMiddleware`, `MediaBrowser.Providers/MediaInfo/ProbeProvider`,
  `MediaBrowser.Model/Dlna/PlaybackErrorCode`, `Jellyfin.Server/Startup`,
  `Jellyfin.Server/CoreAppHost`, and the extras fix in
  `Emby.Server.Implementations/Library/LibraryManager`.
- Tests: `tests/Jellyfin.ChildServer.Tests` (90 xunit tests, stubbed HTTP, temp folders),
  `tests/Jellyfin.Server.Integration.Tests` (child server controller and dashboard pages),
  `tests/Jellyfin.Server.Implementations.Tests` (the extras regressions), and the browser suite in
  `tests/e2e/` (Playwright 1.63, TypeScript, 23 tests) with `docker-compose.yml`
  (parent `jellyfin/jellyfin:12.1`, node `gate` proxy with a control API, `child` from the root
  `Dockerfile`, `e2e` runner), `run.sh`, `lib/`, `specs/` and `media/`.
- Packaging: the root `Dockerfile` builds jellyfin-web v12.1 in a node stage, publishes the server
  with the .NET 10 SDK and runs on `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` with Alpine's ffmpeg,
  ICU and fonts. `./release` is the gate in front of it.

## Architecture

```
 viewer's browser / apps
        |
        v
 +------------------------------ child server (this repo) ------------------------------+
 |  jellyfin-web, served by the server                                                  |
 |    + dashboard page "Parent server"                                                  |
 |    + injected plugin script: pre-play interceptor -> "Can't connect to parent"       |
 |                                                                                      |
 |  Jellyfin API  ---> ChildServerController (/ChildServer/*)                           |
 |     |                                                                                |
 |     |  PlaybackInfo / stream / HLS                                                   |
 |     v                                                                                |
 |  MediaInfoHelper ---- uncached + parent down --> ErrorCode ParentServerUnavailable   |
 |  StreamingHelpers --> IChildServerMediaCache.PrepareForStreaming                     |
 |  VideosController --> progressive stream while the file downloads                    |
 |                                                                                      |
 |  ChildLibraryMirror (task + API) ---- placeholders + NFO + images --> local library   |
 |  MirroredMediaInfoProvider ---------- parent probe data ----------> library scan     |
 |  ChildPrefetchConsumer -------------- PlaybackStart --------------> next episodes    |
 |  ChildMediaCache ------------------- one download at a time ------> <file>.jfpart    |
 |       manifest: <data>/childserver/mirror.json     files: <data>/childserver/library |
 +----------------------------------------|---------------------------------------------+
                                          | HTTP(S) with the MediaBrowser auth header
                                          | + custom headers (Cloudflare Access etc.)
                                          v
                              [gate proxy, tests only]
                                          v
                                   parent Jellyfin server
```

Data flow

1. **Sign in**: the admin saves URL, user name, password and headers. The child fetches public info,
   signs in as that user and stores the access token and parent user id in `childserver.xml`.
2. **Sync**: for every video library the child lists items (flat for movies and TV, a folder walk for
   everything else), writes 0 byte placeholders and sidecars, updates the manifest, registers the
   library and runs the scan. The scan creates normal Jellyfin items whose media info comes from the
   manifest.
3. **Play**: the client asks for playback info, refused only when the file is not cached and the
   parent is down; the web client then shows the plain message. The stream request triggers the
   download; direct play is served as bytes arrive, a transcode waits for the whole file. The
   finished file replaces the placeholder in place.
4. **Prefetch**: `PlaybackStart` of an episode queues the following ones, default four.
5. **Space**: after each download the cache truncates the least recently played files back to
   placeholders above the limit. The library keeps showing them; the next play fetches again.

Design choices

- Placeholders instead of `.strm` shortcuts, so parent credentials never sit in library files,
  downloads can be cached and resumed, and gateway headers can be added to every request.
- The parent's probe results are reused, so the child never runs ffprobe on mirrored files.
- The web client is extended by injection, not by forking jellyfin-web.
- The browser suite always routes the child through the gate proxy, so a dead parent and header
  requirements can be simulated without touching containers.

## Concerns

- Transcoded playback of a large uncached file waits for the whole download before ffmpeg starts
  (bounded by `DownloadWaitTimeoutSeconds`, default 15 minutes). Direct play does not wait.
- The parent password is stored in `childserver.xml`, like Jellyfin's own Live TV provider passwords,
  so the child can sign in again after a token is revoked. Treat the data folder as sensitive.
- The Alpine image uses Alpine's ffmpeg, not jellyfin-ffmpeg: no hardware acceleration and none of
  Jellyfin's ffmpeg patches. Fine for the tests; a real device may want a different ffmpeg.
- Goldens can only be produced in CI (no Docker on this machine), so every UI change costs a CI cycle.
  Playwright's bundled Chromium has no H.264 decoder, so browser playback in the suite relies on the
  child transcoding to what the web client negotiates.
- The mirror keys everything on parent item ids and paths. A rename on the parent produces a new
  placeholder and drops the old one, losing the cached bytes for that file.
- The first sync of a large library fetches one image per item: many small requests, once.
- Music, books and photo libraries are still not mirrored; neither are collections and extras.
- The injected plugin is loaded from the served `index.html`, which is served with `no-cache`, but a
  client that ignores that keeps the old script until its own cache expires.
- `stabilize()` in the browser suite freezes CSS animations for screenshots, and jellyfin-web only
  removes a dialog when its exit animation ends, so a spec that dismisses a dialog after a screenshot
  has to call `resumeAnimations()` first. This cost an afternoon once.
- Library paths given to Jellyfin on Windows must use backslashes. A path like `C:/Users/...` is
  accepted by the API and then scanned as empty, which looks like a broken fixture.
- One upstream unit test (`BaseItemTests.PropagatePlayedState_WithoutReset_LeavesPositionUntouched`)
  flaked once and passed on rerun. Spec 05's settings page test also stalled once for twenty minutes
  on this machine with no server activity, and passed in three seconds on rerun; if either recurs in
  CI it needs a real investigation rather than a rerun.
- The sample media comes from file-examples.com, which blocks plain downloads; the clips are committed
  under `tests/e2e/media/samples/` with their hashes.

## Verification record

- **Verified in CI (Linux, Docker)**: the v12.1 import builds and the upstream suite passes; steps 2
  to 6 unit and integration tests pass; one full `e2e` run of the Docker suite completed and produced
  the first screenshots. A run of every suite against the commits for steps 7, 9 and 10 is in flight
  at the time of writing.
- **Verified on this machine (Windows, no Docker)**: Debug build clean under Jellyfin's analyzers;
  90 child server unit tests, 132 integration tests and the upstream implementation tests pass; the
  local two-server harness (parent, gate proxy, child, all from the Debug build with jellyfin-web
  v12.1 and jellyfin-ffmpeg 8.1.2) mirrors 17 items from an empty data directory, and all 23 browser
  tests pass against it with screenshot comparison off.
- **The extras fix**: the hang was reproduced on the child and on a plain parent with the same media,
  then a from-scratch parent scan and child mirror were confirmed to complete with the loop guard
  never firing.
- **Not verified**: the Alpine image build since these changes (no Docker here; the `release` CI job
  exists to prove it), the Docker suite since step 6, every golden screenshot except the player, and
  any real deployment on a low-disk device.

## How to run

```bash
# unit and integration tests
dotnet test Jellyfin.sln --configuration Release

# docker end-to-end suite (needs Docker); goldens must exist or set E2E_IGNORE_SNAPSHOTS=1
bash tests/e2e/run.sh

# regenerate goldens (Linux container only), then commit tests/e2e/specs/__screenshots__
bash tests/e2e/run.sh --update-snapshots

# everything, then the Alpine image
./release
```
