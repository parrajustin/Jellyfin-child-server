# Jellyfin child server: progress

Last updated: 2026-09-23 (session 3, see "Session 3" below — two of four items land unverified).
Repository: https://github.com/parrajustin/Jellyfin-child-server, branch `main`.

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
| 8 | Golden image UI tests for every step | Done, pushed: eleven goldens generated in CI and compared by every later run | `7aa06c9`, see "Golden test images" |
| 9 | "Can't connect to parent server" when the parent is down | Done, pushed | `acf6d96` |
| 10 | `./release` building the Alpine container, all tests must pass first | Done, pushed and proven green in CI | `6eed657`, `8458fb2` |

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

### A second crash found by running it on Alpine

About half of the CI runs then died in the browser suite with "getaddrinfo ENOTFOUND child" or
"fetch failed" during the first library sync. The container had exited with code 139, a
segmentation fault, which takes its name out of Docker's resolver and makes a dead server look like
a network fault. The suite now records every container's exit code and its OOMKilled flag before
tearing the stack down, which is how this was pinned in one run instead of three.

The crash was always at the same point: the scan reaching `CollectionPosterVerifyPostScanTask`,
which refreshes a library with no poster, which asks the dynamic image provider to compose one out
of the mirrored items. That collage draws the library name with the platform's native font stack,
and on Alpine it takes the whole process down with no managed exception. It is intermittent because
the provider picks its source images at random. Publishing for `linux-musl-x64` and installing Noto
with a font cache did not change it; both stay in the image because they are right for an Alpine
build, but neither was the fix.

The fix is that a child server does not compose artwork at all. When a parent is configured, the
image processor reports that it cannot create collages, and every dynamic image provider leaves
that path alone. A library whose poster is not mirrored shows the web client's default tile.

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

All ten requested steps are done, pushed, and green in CI. What is left is worth doing but was not
asked for:

1. **Mirror the parent's library posters.** The child no longer composes them, so a library tile is
   the web client default unless the parent's own poster is copied into the library folder. Twenty
   or so lines in `ChildLibraryMirror`, plus refreshed goldens.
2. **Report the upstream bugs** to Jellyfin: the self-owning extra that hangs a scan, and the
   collage builder taking the process down on Alpine.
3. **Mirror the library types that are still skipped**: music, books and photos, plus collections
   and extras.
4. **A real device.** Nothing here has run on the low-disk machine this is meant for.

## Golden test images

- Location: `tests/e2e/specs/__screenshots__/<spec file name>/<screenshot name>.png`, from
  `snapshotPathTemplate: '{testDir}/__screenshots__/{testFileName}/{arg}{ext}'` in
  `tests/e2e/playwright.config.ts`, deliberately without a platform suffix.
- All eleven are committed and every CI run compares against them:
  - `03-playback-from-parent.spec.ts/player-osd.png`
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
- ~~The Alpine image uses Alpine's ffmpeg, not jellyfin-ffmpeg~~ — fixed in session 3: the image is
  Ubuntu with jellyfin-ffmpeg8 and the VA-API drivers. Not yet built anywhere.
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
- Library tiles on the child show the web client's default image, because the child no longer
  composes library posters and does not yet copy the parent's.
- SkiaSharp takes the process down on Alpine when it composes a collage. The child avoids that path,
  but any other feature that reaches it (a playlist or genre poster, for instance) would hit the
  same crash. A Debian base image would not have this problem, but the brief asked for Alpine.
- One upstream unit test (`BaseItemTests.PropagatePlayedState_WithoutReset_LeavesPositionUntouched`)
  flaked once and passed on rerun. Spec 05's settings page test also stalled once for twenty minutes
  on this machine with no server activity, and passed in three seconds on rerun; if either recurs in
  CI it needs a real investigation rather than a rerun.
- The sample media comes from file-examples.com, which blocks plain downloads; the clips are committed
  under `tests/e2e/media/samples/` with their hashes.

## Session 3 (2026-09-23)

Four items were asked for. Two are done and tested, one is written but unbuilt, one is blocked
outside this repository.

| # | Item | Status |
|---|---|---|
| 1 | Dockerfile using the optimized jellyfin-ffmpeg | Written, **never built** — no container runtime available |
| 2 | Headers always sent to the parent | Already true; one coverage hole closed, one documented |
| 3 | An API/MCP an agent can drive | Done, 25 tests pass |
| 4 | Live test against the real parent | **Blocked**: Cloudflare Access rejects the service token |

### 1. jellyfin-ffmpeg

The runtime stage was Alpine with `apk add ffmpeg`: no Jellyfin patches, no VA-API, QSV or NVENC,
so every transcode ran in software. It now follows `linuxserver/docker-jellyfin` — Ubuntu resolute,
the `repo.jellyfin.org` apt source, `jellyfin-ffmpeg8` at `/usr/lib/jellyfin-ffmpeg/ffmpeg` — while
still building this fork from source rather than installing the `jellyfin` package. Leaving Alpine
also drops the musl RID; the publish now picks the RID from `TARGETARCH`.

`./release` checks the built image actually ships a Jellyfin ffmpeg build and that
`JELLYFIN_FFMPEG` points at it, replacing the old "is it Alpine" check.

**This has not been built.** What was verified instead, against the live indexes: both base image
tags exist, `jellyfin-ffmpeg8` 8.1.2-5-resolute exists in the suite, the binary really does land at
`/usr/lib/jellyfin-ffmpeg/ffmpeg` (confirmed from the .deb), the signing key is ASCII armored so
`signed-by` works without gnupg, and every apt package named exists in resolute. That last check
caught one: `mesa-va-drivers` is only a virtual package on this suite, so the real provider
`libgl1-mesa-dri` is named instead.

Note for whoever builds it: the SkiaSharp crash that forced commit `8458fb2` was a musl problem.
On glibc the child could compose library posters again, so that workaround is now removable —
but it was left alone, because nothing here could test the revert.

### 3. The MCP server

`tools/jellyfin-mcp` — ten tools covering sign in, every parent option, connection test, logs,
save-and-connect, the parent's library list, sync, playback and status. No server-side changes were
needed, so there is no new C# to keep in step.

Two decisions worth keeping: the library list signs in to the *parent* and calls `/UserViews`, the
same request the web client makes when a user opens the dashboard, rather than reporting what the
child mirrored — that answers "what is actually on the parent", which is the question you have when
a library is missing. And playback is tested by reading bytes, because `PlaybackInfo` answers
happily for an item whose local file is still a placeholder.

The client refuses to follow redirects: a Jellyfin call has no reason to redirect, so a 3xx means a
gateway answered instead. For Cloudflare Access it decodes the `meta` JWT and reports
`service_token_status`, which separates "no token sent" from "the policy does not allow this token".

### 4. The live test, and why it did not happen

`jellyfin.parrajustin.com` answers 302 to `parrajustin.cloudflareaccess.com` **with or without**
the supplied service token, and Cloudflare's own signed verdict says `service_token_status: false`,
`auth_status: NONE`. The token is not being accepted, so nothing behind Access is reachable and no
part of the live test could run.

The usual cause is that the Access application has no **Service Auth** policy including this token;
a valid token on its own is not enough. It could also be rotated or expired. Either way the fix is
in Cloudflare Zero Trust, not in this repository.

What this did produce: the diagnostic above was written against this exact failure and verified
against the live server, so the next attempt gets told why rather than getting an HTML parse error.

### What is still unverified after this session

- The container image has never been built, so the apt install, the RID change and the ffmpeg
  checks in `./release` are all unproven.
- The e2e suite has not been run. The new sync-under-headers test type checks and Playwright lists
  it, nothing more.
- No live parent has ever been reached.

None of this is a claim that the work is wrong — it is a list of what nobody has watched run.

## Verification record

- **Verified in CI (Ubuntu runners, Docker), run 35682011833 on commit `8458fb2`, all three jobs
  green**:
  - the solution builds in Release and the whole upstream xunit suite passes, including the two new
    extras regressions and the 90 child server tests;
  - the browser suite runs against a real parent, gateway proxy and child in containers: 23 of 23
    tests pass, comparing against the eleven committed goldens;
  - `./release` runs all of that again and then builds the container image, tags it
    `12.1.0`, `8458fb2` and `latest`, and confirms the running image is Alpine.
- **Verified on this machine (Windows on ARM, no Docker)**: Debug build clean under Jellyfin's
  analyzers; 90 child server unit tests, 132 integration tests and the upstream implementation tests
  pass; a local two-server harness (parent, gate proxy, child, built from source with jellyfin-web
  v12.1 and jellyfin-ffmpeg 8.1.2) mirrors 17 items from an empty data directory and all 23 browser
  tests pass against it with screenshot comparison off.
- **The two crashes**: each was reproduced first, then fixed, then shown gone. The scan hang was
  reproduced on the child and on a plain parent with the same media, and a from-scratch scan now
  completes with the loop guard never firing. The Alpine segfault was pinned to exit code 139 from
  the container state the suite now records, and the runs since the fix are green.
- **Not verified**: any deployment on a real low-disk device; the image running anywhere other than
  the CI runner; behaviour against a parent library larger than the seventeen item fixture; and the
  gateway header path against a real Cloudflare Access tunnel rather than the test proxy.

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
