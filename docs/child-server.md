# Jellyfin child server

A Jellyfin server that mirrors a parent Jellyfin server and keeps only the media being
watched on its own disk. This document covers configuration; `progress.md` at the repository
root tracks the project itself.

Standing one up for the first time is a sequence rather than a set of settings — see
[launch_steps.md](../launch_steps.md) for deploy, first boot, connect, mirror, test playback and
point clients at it, plus the same flow driven through the MCP server.

## Connecting to the parent

Open the dashboard and pick **Parent server** in the menu (or go to
`/web/#/configurationpage?name=ChildServerParent`). Enter:

- **Parent server URL**: the address this device uses to reach the parent, including the
  port and any base path, for example `http://192.168.1.10:8096` or
  `https://media.example.com/jellyfin`.
- **User name** and **password**: an account on the parent server. The child signs in as
  that user, so it sees exactly the libraries that user can see. The password is stored so
  the child can sign in again when the parent revokes the access token.

**Test connection** tries the values in the form without saving them and reports one of:

| Outcome | Meaning |
|---|---|
| Connected | The parent answered and accepted the credentials; its name and version are shown. |
| Incorrect user name or password | The parent rejected the sign in. |
| Access denied | The parent, or a gateway in front of it, refused the request before sign in (HTTP 401 or 403). Usually missing gateway headers, see below. |
| Could not connect to the parent server | Nothing answered at the address, or it timed out. |
| That address is not a Jellyfin server | Something answered, but not with Jellyfin's API (for example a login page). |

**Save** stores the settings, signs in, and confirms with "Settings saved". **Sync now**
mirrors the parent's libraries immediately; a scheduled task (`Sync parent server library`)
also runs at startup and every *Sync interval* hours.

## Parent behind Cloudflare Access (Zero Trust)

When the parent is published through a Cloudflare tunnel with an Access policy, plain
requests are redirected to a login page. Create a **service token** in Zero Trust
(Access > Service Auth > Service Tokens) and allow it in the application's policy, then add
its two headers under **Extra request headers**:

| Header | Value |
|---|---|
| `CF-Access-Client-Id` | the service token's client id (ends with `.access`) |
| `CF-Access-Client-Secret` | the service token's client secret |

The child sends every configured header with every request to the parent: connection
tests, sign in, library reads, image and media downloads. Any gateway that authenticates by
header works the same way. Header names are validated (no spaces, colons or control
characters); values are stored as entered.

The end-to-end suite proves this with a proxy that refuses requests lacking the headers
(`tests/e2e/specs/05-custom-headers.spec.ts`).

## What is mirrored

Every video library the parent exposes: movies, TV shows, home videos, music videos, and
folder libraries with no collection type. Music, books and photos are not mirrored.

Movies and TV shows are laid out the way the Jellyfin scanner expects
(`Movies/Name (Year)/Name (Year).ext`, `TV Shows/Series/Season 02/Series S02E01.ext`).
Every other library keeps the parent's own folder structure, so a viewer browsing the child
sees the same folders in the same order. Nesting deeper than 24 levels is skipped and logged.
Two items with the same name in one folder are kept apart by appending a short id.

## When the parent is unreachable

Playing a video that is already on the device always works. Playing one that is not, while
the parent does not answer, is refused rather than left to fail in the player: the web client
shows

> **Can't connect to parent server**
> "<name>" is not stored on this device and the parent server cannot be reached. Try again
> when the parent server is back online.

The child does this by serving jellyfin-web with one extra script of its own
(`/web/childserver-plugin.js`, listed in the served `/web/config.json`), which asks the
availability endpoint before playback starts. jellyfin-web itself is not modified.

## Cache settings

- **Episodes to prefetch**: how many following episodes are downloaded while an episode
  plays (default 4).
- **Cache size limit (MB)**: above this, the least recently played files are removed first
  (they turn back into placeholders and are fetched again when played). 0 disables the limit.
- **Download wait timeout (seconds)**: how long a transcoded playback waits for a file that
  is not on the device yet. Direct play streams start while the file downloads.
- **Sync interval (hours)**: how often the parent library is mirrored.

## Where things live

- Settings: `<config>/childserver.xml` (contains the parent password and access token; keep
  the data folder private).
- Mirrored library: `<data>/childserver/library/<Parent library name>/...`, registered as
  local libraries with internet metadata providers disabled. Placeholders are empty files
  next to `.nfo` sidecars and small images; downloaded media replaces the placeholder in
  place.
- Mirror manifest: `<data>/childserver/mirror.json`.

## API

All endpoints need an administrator except the availability one.

| Method and path | Purpose |
|---|---|
| `GET /ChildServer/Configuration` | Settings without secrets |
| `POST /ChildServer/Configuration` | Save settings (empty password keeps the stored one) |
| `POST /ChildServer/TestConnection` | Try a URL, user name, password and headers without saving |
| `POST /ChildServer/Connect` | Sign in with the stored settings |
| `GET /ChildServer/Status` | Connection, sync and cache state |
| `POST /ChildServer/Sync` | Start a library sync in the background |
| `POST /ChildServer/Cache/Clear` | Turn every downloaded file back into a placeholder; answers the number cleared |
| `GET /ChildServer/Items/{id}/Availability` | Whether an item is cached, downloading, and whether the parent answers (any signed in user) |

When an item is not cached and the parent cannot be reached, `POST /Items/{id}/PlaybackInfo`
answers with `ErrorCode: "ParentServerUnavailable"` and stream requests answer HTTP 503
with the header `X-Application-Error-Code: ParentServerUnavailable`.

## Configuration by environment

A container has no dashboard on first boot, so the image seeds the parent configuration from the
environment before starting. Full list with comments in
[`docker/child-server.env.example`](../docker/child-server.env.example).

Setting `CHILD_PARENT_URL` makes the environment **authoritative**: `<config>/childserver.xml` is
rewritten from these values on every boot and dashboard edits are replaced. Leave every
`CHILD_PARENT_*` variable unset and the dashboard stays in charge. The access token is not written —
the server signs in again with the stored password, so the only cost of a restart is one sign in.

| Variable | Purpose |
|---|---|
| `CHILD_PARENT_URL` | Parent base URL. Setting it is what switches env control on. |
| `CHILD_PARENT_USERNAME` | Account on the parent the child signs in as |
| `CHILD_PARENT_PASSWORD` | Its password |
| `CF_ACCESS_CLIENT_ID` / `CF_ACCESS_CLIENT_SECRET` | Cloudflare Access service token |
| `CHILD_PARENT_HEADERS` | Any other gateway headers: `Name: value` pairs, `;` or newline separated |
| `CHILD_PREFETCH_EPISODE_COUNT` | Episodes fetched ahead (default 4) |
| `CHILD_MAX_CACHE_SIZE_MB` | Cache ceiling, 0 for none (default 5120) |
| `CHILD_DOWNLOAD_WAIT_TIMEOUT_SECONDS` | How long playback waits for a download (default 900) |
| `CHILD_SYNC_INTERVAL_HOURS` | Hours between mirrors (default 6) |
| `CHILD_LIBRARY_PATH` | Where the mirrored library lives |

Every secret has a `_FILE` form read from the named path, for Docker secrets and Kubernetes secret
mounts, and it wins over the inline value. Prefer it — an `--env-file` value shows up in
`docker inspect`, a mounted file does not. Secrets are never baked into the image and never logged;
the startup line reports only `password=set` / `headers=set`.

```bash
docker run -d --name child-server \
  --env-file child-server.env \
  -v /srv/child/config:/config -v /srv/child/cache:/cache \
  --device /dev/dri:/dev/dri -p 8096:8096 \
  xerofuzzion/jellyfin-child-server:latest-x86_64
```

## Deploying with compose

[`docker/docker-compose.yml`](../docker/docker-compose.yml) is a single-host deployment. Copy that
file and `child-server.env.example` to the server, then:

```bash
cp child-server.env.example child-server.env    # fill it in
docker compose up -d
docker compose logs -f
```

The two volumes are not interchangeable, and which disk each lands on is the main decision:

| Mount | Holds | Sizing |
|---|---|---|
| `/config` | database, mirrored library tree (placeholders and metadata), `childserver.xml` | Small and precious. Back it up. |
| `/cache` | the video bytes fetched from the parent, bounded by `CHILD_MAX_CACHE_SIZE_MB` | Large and disposable. Losing it costs a re-download. Put it on the biggest disk. |

Override paths, the published port and the image tag with `CHILD_SERVER_CONFIG_DIR`,
`CHILD_SERVER_CACHE_DIR`, `CHILD_SERVER_PORT` and `CHILD_SERVER_IMAGE` (use the `-aarch64` tag on
arm64). Hardware transcoding and a tmpfs for transcode scratch are commented blocks in the file —
device passthrough is left off by default because compose refuses to start when a named device does
not exist, rather than falling back to software.

## ffmpeg and hardware acceleration

The image ships **jellyfin-ffmpeg**, the patched ffmpeg Jellyfin builds itself, rather than
the distribution's ffmpeg. It is installed from `repo.jellyfin.org` at
`/usr/lib/jellyfin-ffmpeg/ffmpeg`, and `JELLYFIN_FFMPEG` points the server at it.

This is what makes hardware transcoding possible: the distribution build has none of
Jellyfin's patches and no VA-API, QSV or NVENC support, so every transcode fell back to
software. That matters more here than on a normal server, because a child server is meant to
run on a small machine.

The runtime stage follows `linuxserver/docker-jellyfin`: the same Ubuntu release, the same
apt source, the same acceleration packages. It does **not** install the `jellyfin` package —
the server in this image is this fork, built from source — so only ffmpeg and the runtime
libraries come from the repository.

| Build argument | Default | Purpose |
|---|---|---|
| `JELLYFIN_FFMPEG_PACKAGE` | `jellyfin-ffmpeg8` | Set to `jellyfin-ffmpeg7` for the older build |
| `UBUNTU_SUITE` | `resolute` | Ubuntu release shared by the .NET images and the Jellyfin repository |

To use a GPU, pass the device through and give the container access:

```bash
# Intel QSV / VA-API, or AMD
docker run --device /dev/dri:/dev/dri ... jellyfin-child-server

# NVIDIA (NVIDIA_DRIVER_CAPABILITIES and NVIDIA_VISIBLE_DEVICES are already set in the image)
docker run --gpus all ... jellyfin-child-server
```

Then enable the matching hardware acceleration in the dashboard under Playback. `./release`
refuses to tag an image whose ffmpeg is not a Jellyfin build, so this cannot regress
silently.

## Publishing the image

`./release.sh` builds and pushes `xerofuzzion/jellyfin-child-server:v<N>-<arch>` plus
`latest-<arch>`, where `<N>` comes from `version.json` and is bumped only after every push
succeeds. It runs the fast gates first (dotnet, MCP, entrypoint) but not the Docker e2e suite.

```bash
./release.sh                 # x86_64 only
./release.sh --arm64         # also aarch64
./release.sh --dry-run       # build locally, push nothing
```

aarch64 is cheap here, unlike the other stacks in this monorepo: the web and server stages are
pinned to `$BUILDPLATFORM` and a framework dependent .NET publish cross compiles from naming the
RID, so only the final stage is emulated and all it does is run apt. That stage still needs binfmt
registered (`docker run --privileged tonistiigi/binfmt --install arm64`).

## Building a release locally

`./release` is the local gate — it does not push. It
builds the solution, runs the unit and integration suites, runs the browser suite against a
real parent, gateway and child in Docker, and only then builds and tags the image from the
repository `Dockerfile`, whose runtime stage is Ubuntu with jellyfin-ffmpeg. Any failing step stops it and
nothing is tagged. `./release --help` lists the options.
