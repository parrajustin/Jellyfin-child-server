# Jellyfin child server: progress

Last updated: 2026-09-21 (session 1). Repository: https://github.com/parrajustin/Jellyfin-child-server, branch `main`.

## Goal

A Jellyfin server that runs on a device with little disk space. It mirrors the catalog of a
"parent" Jellyfin server, keeps only the video being watched (plus the episodes likely to
follow) on the local disk, fetches everything else from the parent on demand, and tells the
viewer plainly when the parent cannot be reached. Built from the Jellyfin server source at
tag v12.1 with no upstream git history, one conventional commit per step.

## Requested steps and status

| # | Step | Status | Where |
|---|------|--------|-------|
| 1 | Import Jellyfin v12.1 source into the new repo, no history | Done, pushed | commit `b74c2b8` |
| – | CI: build and run the upstream test suite on every push | Done, pushed | commit `a884165`, `.github/workflows/ci.yml` |
| 2 | Child signs in to a parent server | Done, pushed | commit `fb509c2` |
| 3 | Docker test with two servers; a user views child media fetched from the parent | Implemented, verified locally by API, not yet committed | working tree (see below) |
| 4 | Admin UI to enter parent user name/password with status messages, golden screenshot tests | Implemented, not yet run in a browser, not yet committed | working tree |
| 5 | Custom header support (Cloudflare Zero Trust) | Backend done in step 2 (headers are sent on every parent request and covered by unit tests); UI rows exist on the step 4 page; gate-based test and docs not written | – |
| 6 | Prefetch the next episodes (up to 4 away) while watching | Not started (download queue with priorities is in place) | – |
| 7 | Child shows all parent content: all folders, movies and TV shows | Movies and TV shows are mirrored in step 3; other library types not yet | – |
| 8 | Golden image UI tests for every step | Specs 03 and 04 exist; goldens not generated yet | – |
| 9 | "Can't connect to parent server" message when the parent is down | Server side done (error code + HTTP 503); web plugin and test not written | – |
| 10 | `./release` script building the Alpine container; all tests must pass | Dockerfile written; release script and gate not written | – |

## Actions taken so far

Step 1 and CI

- Downloaded the GitHub tarball of `jellyfin/jellyfin` at tag `v12.1` (commit `ee91c75`), imported it as a flat snapshot and dropped only the upstream `.github` folder (Jellyfin's own CI, renovate, issue templates), which would have run Jellyfin's workflows on this repo.
- Installed the .NET SDK 10.0.401 into the user profile on this machine (Jellyfin v12.1 targets net10.0). No Docker and no WSL exist here, so every container-based check runs in GitHub Actions.
- Added a CI workflow that builds the solution and runs the full upstream xunit suite on Ubuntu. First run: green (one upstream test, `BaseItemTests.PropagatePlayedState_WithoutReset_LeavesPositionUntouched`, flaked once on a duplicate run and passed on the other).

Step 2: parent sign in

- New assembly `src/Jellyfin.ChildServer` with `ParentServerClient` (Jellyfin `MediaBrowser` authorization header, custom headers, timeouts, clear error mapping) and `ChildServerManager` (settings in the `childserver` configuration store, stored access token reused and refreshed on rejection).
- New admin API `ChildServerController`: `GET/POST /ChildServer/Configuration`, `POST /ChildServer/TestConnection`, `POST /ChildServer/Connect`, `GET /ChildServer/Status`. Secrets never leave the server.
- 32 unit tests with stubbed HTTP and 6 integration tests through the real host.

Step 3: mirror and on-demand fetch (in the working tree)

- Library mirror (`ChildLibraryMirror`): reads the parent's views and items, writes zero byte placeholder videos with NFO sidecars and small poster/backdrop/thumb images under `<data>/childserver/library/<Library name>/...`, registers each parent library as a local library with all internet providers disabled, removes items that vanished on the parent, then runs the library scan. Scheduled task `ChildServerSync` (startup trigger plus a configurable interval) and `POST /ChildServer/Sync`.
- Mirror manifest (`<data>/childserver/mirror.json`) records for every placeholder the parent item and media source ids, size, container, duration, bitrate, media streams, and cache state.
- `MirroredMediaInfoProvider` applies the parent's probe data to placeholders; the upstream `ProbeProvider` skips mirrored paths, so empty files are never probed.
- Media cache (`ChildMediaCache`): single download worker with priorities, resumable downloads into `<file>.jfpart` then an atomic move over the placeholder, retries, least recently played eviction above `MaxCacheSizeMb`, parent reachability check cached for 5 seconds.
- Core hooks: `StreamingHelpers` prepares the file before any stream (static streams start immediately, transcodes wait for the complete file); `VideosController` serves a still-downloading file through a seekable stream that waits for bytes so HTTP range requests work; `MediaInfoHelper` answers `PlaybackErrorCode.ParentServerUnavailable` when the file is not cached and the parent is unreachable; `ExceptionMiddleware` maps `ParentServerUnavailableException` to HTTP 503 with `X-Application-Error-Code: ParentServerUnavailable`.
- New endpoints: `POST /ChildServer/Sync` (202), `GET /ChildServer/Items/{id}/Availability` (any signed in user), richer `GET /ChildServer/Status`.
- 79 unit tests in `tests/Jellyfin.ChildServer.Tests` (naming, NFO, manifest, progressive stream, download, resume, failure, eviction), 6 integration tests, Debug build clean under Jellyfin's analyzers.
- Local two-server run on this machine (parent and child from the Debug build, jellyfin-web v12.1 built locally, jellyfin-ffmpeg 8.1.2): the parent scanned the 13 fixture files and the child mirrored them. One earlier run hit two harness problems (a library scan race after adding two libraries back to back, fixed by an explicit refresh; stale server processes, fixed in the local harness). The rerun after those fixes was still in progress when this document was written; the API level playback check (bytes of the streamed episode compared with the parent's file) is what it verifies.
- Docker end-to-end harness under `tests/e2e/` (details below) plus an Alpine `Dockerfile` for the child image and a CI job `e2e` that builds the image with a layer cache and runs the suite. Not yet run in CI.

Step 4: admin page (in the working tree)

- `src/Jellyfin.ChildServer/Web/childserver-parent.html`, served by the server as a dashboard configuration page (`#/configurationpage?name=ChildServerParent`) and listed in the dashboard menu as "Parent server". Fields for URL, user name, password, extra headers, cache limits; buttons Test connection, Save (saves, signs in, shows a toast), Sync now; a status block with sync and cache facts.
- `DashboardController` learned to list and serve built-in pages next to plugin pages.
- Spec `tests/e2e/specs/04-parent-connection-page.spec.ts` with golden screenshots for the loaded page and the incorrect password, unreachable and success outcomes.

## Remaining steps

1. Finish step 3: confirm the local playback check, commit the server work, the harness and the Dockerfile (three conventional commits), push, and make the CI `e2e` job green. The first CI run will also produce the golden images (see below).
2. Step 4: run spec 04 against the local servers, fix anything, commit; generate its goldens in CI.
3. Step 5: header rows on the page are done; add the gate based test (the gate refuses requests without the configured headers), documentation for Cloudflare Access service tokens, and a golden screenshot.
4. Step 6: on `PlaybackStart` of a mirrored episode, queue the next episodes (default 4, configurable) with prefetch priority and protect them from eviction; test that playing S02E01 makes S02E02..S02E05 arrive and S02E06 stays a placeholder.
5. Step 7: mirror every library type the parent exposes (home videos, mixed content, folders, collections), keep folder structure for folder libraries, and compare counts against the parent in the suite.
6. Step 8: golden screenshots for every user facing surface added by the steps above (library views, item detail, player, admin pages).
7. Step 9: a small web plugin injected by the server (served config and index pages) that intercepts play requests for uncached items while the parent is down and shows "Can't connect to parent server"; golden test with the gate switched off.
8. Step 10: `./release` at the repo root that runs the unit suite and the Docker suite and only then builds and tags the Alpine image; a CI job that runs it.
9. Housekeeping: register the project in the project standard registry, write the feature docs.

## Golden test images

- Location: `tests/e2e/specs/__screenshots__/<spec file name>/<screenshot name>.png`. Playwright is configured with `snapshotPathTemplate: '{testDir}/__screenshots__/{testFileName}/{arg}{ext}'` in `tests/e2e/playwright.config.ts`, deliberately without the platform suffix.
- Expected files once generated:
  - `tests/e2e/specs/__screenshots__/03-playback-from-parent.spec.ts/player-osd.png`
  - `tests/e2e/specs/__screenshots__/03-playback-from-parent.spec.ts/home.png`
  - `tests/e2e/specs/__screenshots__/04-parent-connection-page.spec.ts/parent-page-loaded.png`
  - `tests/e2e/specs/__screenshots__/04-parent-connection-page.spec.ts/parent-page-invalid-password.png`
  - `tests/e2e/specs/__screenshots__/04-parent-connection-page.spec.ts/parent-page-unreachable.png`
  - `tests/e2e/specs/__screenshots__/04-parent-connection-page.spec.ts/parent-page-success.png`
- Current state: none are committed yet. Goldens are only valid when produced inside the Linux Playwright container (`mcr.microsoft.com/playwright:v1.63.0-noble`), because fonts and rendering differ per platform. To create or refresh them: run the CI workflow manually with the input `update_snapshots = true` (or locally with Docker: `bash tests/e2e/run.sh --update-snapshots`), download the `e2e-screenshots` artifact, copy it into `tests/e2e/specs/__screenshots__/` and commit. A developer without goldens can still run the flows with `E2E_IGNORE_SNAPSHOTS=1`.
- Comparison tolerance: `maxDiffPixelRatio 0.02`, animations disabled, caret hidden, viewport 1280x800, dark color scheme, locale `en-US`, timezone UTC. Time stamps on the admin page are masked; the video frame is hidden by CSS when capturing the player.

## Implementation summary

New code lives in three places:

- `MediaBrowser.Model/ChildServer/` and `MediaBrowser.Controller/ChildServer/`: settings, DTOs, enums and the interfaces (`IChildServerManager`, `IChildServerMediaCache`, `IChildServerLibrarySync`, `IChildServerWebPages`) that the rest of the server depends on.
- `src/Jellyfin.ChildServer/`: the implementation assembly, registered in `CoreAppHost` (services and reflection based part discovery for the configuration store, the scheduled task and the metadata provider).
  - `ParentServerClient`, `ChildServerManager`, `ParentSession`, `ChildServerPaths`
  - `Mirror/`: `ParentLibraryReader`, `MirrorPathBuilder`, `NfoWriter`, `MirrorManifest`, `MirrorEntry`, `ChildLibraryMirror`, `ChildLibrarySyncTask`
  - `Cache/`: `ChildMediaCache`, `DownloadJob`, `ChildCacheStream`
  - `Metadata/MirroredMediaInfoProvider`
  - `Web/ChildServerWebPages` and `Web/childserver-parent.html`
- Small hooks in upstream files: `Jellyfin.Api/Controllers/ChildServerController.cs` (new), `VideosController`, `DashboardController`, `Helpers/StreamingHelpers`, `Helpers/MediaInfoHelper`, `Middleware/ExceptionMiddleware`, `MediaBrowser.Providers/MediaInfo/ProbeProvider`, `MediaBrowser.Model/Dlna/PlaybackErrorCode`, `Jellyfin.Server/CoreAppHost`.

Tests: `tests/Jellyfin.ChildServer.Tests` (xunit v3, Moq, stubbed HTTP, temp folders), `tests/Jellyfin.Server.Integration.Tests/Controllers/ChildServerControllerTests.cs`, and the Docker suite in `tests/e2e/` (Playwright 1.63, TypeScript): `docker-compose.yml` (parent `jellyfin/jellyfin:12.1`, node `gate` proxy with a control API to cut the parent off or demand headers, `child` from the root `Dockerfile`, `e2e` runner), `run.sh` orchestrator, `lib/` API and UI helpers, `specs/`, and `media/` (four H.264 AVI clips from file-examples.com laid out as 3 movies and 10 episodes of "Supernatural").

Packaging: root `Dockerfile` builds jellyfin-web v12.1 in a node stage, publishes the server with the .NET 10 SDK, and runs on `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` with Alpine's ffmpeg, ICU and fonts.

## Architecture

```
 viewer's browser / apps
        |
        v
 +------------------------------ child server (this repo) ------------------------------+
 |  jellyfin-web  <-- served by the server; dashboard page "Parent server" (embedded)   |
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
 |  ChildMediaCache ------------------- one download at a time ------> <file>.jfpart    |
 |       manifest: <data>/childserver/mirror.json     files: <data>/childserver/library |
 +----------------------------------------|---------------------------------------------+
                                          | HTTPS/HTTP with MediaBrowser auth header
                                          | + custom headers (Cloudflare Access etc.)
                                          v
                              [gate proxy, tests only]
                                          v
                                   parent Jellyfin server
                    (/System/Info/Public, /Users/AuthenticateByName, /UserViews,
                     /Items, /Items/{id}/Images/*, /Videos/{id}/stream?static=true)
```

Data flow

1. Sign in: the admin saves URL, user name, password and headers. The child fetches public info, signs in as that user, stores the access token and the parent user id in `childserver.xml`.
2. Sync: for each parent library of a supported type, the child lists items, builds Jellyfin style paths (`Movies/Name (Year)/Name (Year).ext`, `TV Shows/Series (Year)/Season 02/Series S02E01.ext`), writes 0 byte placeholders and sidecars, updates the manifest, registers the library, and runs the scan. The scan creates normal Jellyfin items whose media info comes from the manifest.
3. Play: the client asks for playback info (refused only when the file is not cached and the parent is down). The stream request triggers the download; a direct play stream is served as bytes arrive, a transcode waits for the whole file. The finished file replaces the placeholder in place, so later plays are local.
4. Space: after each download the cache evicts the least recently played files above the limit by truncating them back to placeholders. The library keeps showing them; the next play fetches again.

Design choices

- Placeholders instead of `.strm` shortcuts so the parent's credentials never sit in library files, downloads can be cached and resumed, and Cloudflare style headers can be added to every request.
- The parent's own probe results are reused, so the child never runs ffprobe on mirrored files and offers clients accurate codec information.
- The browser test stack always routes the child through the gate proxy so tests can simulate a dead parent and header requirements without touching containers.

## Concerns

- Transcoded playback of a large uncached file waits for the complete download before ffmpeg starts (bounded by `DownloadWaitTimeoutSeconds`, default 15 minutes). Direct play does not wait. A later improvement could feed ffmpeg through the progressive stream.
- The parent password is stored in `childserver.xml` (like Jellyfin's own Live TV provider passwords) so the child can sign in again after a token is revoked. Treat the data folder as sensitive.
- The Alpine image uses Alpine's ffmpeg, not jellyfin-ffmpeg: no hardware acceleration and none of Jellyfin's ffmpeg patches. Fine for the tests; a real device may want a different ffmpeg.
- Golden screenshots can only be produced in CI (no Docker on this machine), so every UI change costs a CI cycle to refresh them. Playwright's bundled Chromium has no H.264 decoder; browser playback in the suite relies on the child transcoding to what the web client negotiates.
- One upstream unit test is flaky (see step 1 notes); a rerun passed. If it keeps failing it should be quarantined rather than hiding real failures.
- The web client shows the raw key `PlaybackError.ParentServerUnavailable` for the new error code until step 9 adds the injected plugin that renders the friendly message.
- Only movie and TV show libraries are mirrored so far; music, books, photos and mixed folders come with step 7. Collections and extras are not mirrored.
- The mirror keys everything on the parent's item ids and paths. Renames on the parent produce new placeholders and drop the old ones (cached bytes for renamed files are lost).
- Large parent libraries: the first sync downloads a poster per movie/series and a thumb per episode; that is many small requests. Images are fetched once and kept.
- The sample media comes from file-examples.com, which blocks plain downloads; the four clips are committed under `tests/e2e/media/samples/` (5 MB) with their hashes.

## Verification record

- Verified in CI: import builds and the upstream suite passes; step 2 unit and integration tests pass.
- Verified on this machine: Debug builds under Jellyfin's analyzers, 79 child server unit tests, 6 integration tests; a local parent scanned the fixture library and the child mirrored 13 items through the real APIs.
- Not verified yet: the Docker suite and the Alpine image build (first CI run pending), browser playback on the child, the admin page in a browser, any golden screenshot.

## How to run

```bash
# unit and integration tests
dotnet test Jellyfin.sln --configuration Release

# docker end-to-end suite (needs Docker); goldens must exist or set E2E_IGNORE_SNAPSHOTS=1
bash tests/e2e/run.sh

# regenerate goldens (Linux container only), then commit tests/e2e/specs/__screenshots__
bash tests/e2e/run.sh --update-snapshots

# build the child image
docker build -t jellyfin-child-server .
```
