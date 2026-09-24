# Launch steps

Getting a Jellyfin child server running against a real parent, start to finish: deploy, create the
admin account, verify the parent connection, mirror the libraries, prove a video plays, and point
clients at it. Then the same thing driven by an agent through the MCP server.

Reference material rather than steps lives in [docs/child-server.md](docs/child-server.md); this
file is the order to do things in.

## What you need

- A server with Docker and the compose plugin.
- A parent Jellyfin server, and an account on it. The account does **not** need to be an
  administrator — the child only reads.
- If the parent is behind Cloudflare Access, a service token **covered by a Service Auth policy**
  on that application. A valid token alone is not enough; see [Troubleshooting](#troubleshooting).
- Two directories on disk. They are not interchangeable:

  | Mount | Holds | Sizing |
  |---|---|---|
  | `/config` | database, the mirrored library tree, `childserver.xml` | Small and precious. Back it up. |
  | `/cache` | the video bytes fetched from the parent, capped by `CHILD_MAX_CACHE_SIZE_MB` | Large and disposable. Put it on the biggest disk. |

---

## 1. Deploy

Copy two files to the server: [`docker/docker-compose.yml`](docker/docker-compose.yml) and
[`docker/child-server.env.example`](docker/child-server.env.example).

```bash
cp child-server.env.example child-server.env
```

Fill in `child-server.env`. The minimum is the parent URL and an account:

```ini
CHILD_PARENT_URL=https://jellyfin.example.com
CHILD_PARENT_USERNAME=remote
CHILD_PARENT_PASSWORD=...

# Only if the parent is behind Cloudflare Access:
CF_ACCESS_CLIENT_ID=0123456789abcdef0123456789abcdef.access
CF_ACCESS_CLIENT_SECRET=...

# Worth setting deliberately — this is the whole point of a child server:
CHILD_MAX_CACHE_SIZE_MB=20480
```

Prefer the `_FILE` form for secrets if you have Docker or Kubernetes secrets available
(`CHILD_PARENT_PASSWORD_FILE=/run/secrets/...`): an `--env-file` value is visible in
`docker inspect`, a mounted file is not.

On arm64, set `CHILD_SERVER_IMAGE` to the `-aarch64` tag.

```bash
docker compose up -d
docker compose logs -f
```

The first line to look for confirms the environment was read:

```
[child-server] wrote /config/config/childserver.xml from the environment (parent=https://... user=remote password=set headers=set)
```

If it instead says `no CHILD_PARENT_URL set`, the env file was not picked up — check you are running
compose from the directory holding `child-server.env`.

Wait for the container to report healthy:

```bash
docker compose ps
```

---

## 2. First boot: create the child's own admin account

**The environment configures the parent, not the child's own login.** A fresh Jellyfin still runs
its startup wizard, and you need an admin account on the child before anything else works.

Open `http://<server>:8096` and complete the wizard: language, **create the administrator user**,
skip adding media libraries (the child creates them itself when it mirrors), remote access, done.

Do not add any libraries by hand. The mirror creates and owns them.

If you would rather not click, the same thing over the API:

```bash
CHILD=http://localhost:8096
curl -sS "$CHILD/Startup/Configuration" -o /tmp/startup.json
curl -sS -X POST "$CHILD/Startup/User" -H 'Content-Type: application/json' \
  -d '{"Name":"admin","Password":"CHANGE-ME"}' -o /dev/null -w '%{http_code}\n'
curl -sS -X POST "$CHILD/Startup/RemoteAccess" -H 'Content-Type: application/json' \
  -d '{"EnableRemoteAccess":true}' -o /dev/null -w '%{http_code}\n'
curl -sS -X POST "$CHILD/Startup/Complete" -o /dev/null -w '%{http_code}\n'
```

Each should answer `204`. Restart afterwards so the wizard state is picked up cleanly:
`docker compose restart`.

---

## 3. Verify the parent connection

Sign in to the child as the admin you just made, then go to **Dashboard → Parent server**
(`/web/#/configurationpage?name=ChildServerParent`).

The URL, user name and headers should already be filled in from the environment, and the password
field will be blank with a note that one is stored — the server never hands a password back.

Click **Test connection**. You want `Success`. Anything else, see
[Troubleshooting](#troubleshooting) and then read the logs.

Then click **Save** — that signs in to the parent and keeps the access token, which is what
"saving the connection" means.

The same over the API, using an admin API key (Dashboard → API keys):

```bash
KEY=<api key>
curl -sS -X POST "$CHILD/ChildServer/Connect" -H "Authorization: MediaBrowser Token=\"$KEY\"" | jq
curl -sS "$CHILD/ChildServer/Status"  -H "Authorization: MediaBrowser Token=\"$KEY\"" | jq
```

---

## 4. Mirror the libraries

**Dashboard → Parent server → Sync now**, or:

```bash
curl -sS -X POST "$CHILD/ChildServer/Sync" -H "Authorization: MediaBrowser Token=\"$KEY\"" -w '%{http_code}\n'
```

`202` means it started. Watch it finish:

```bash
watch -n5 "curl -sS $CHILD/ChildServer/Status -H 'Authorization: MediaBrowser Token=\"$KEY\"' \
  | jq '{SyncState, MirroredItemCount, LastSyncError}'"
```

The first sync of a large parent takes a while — it lists every library and fetches one image per
item. `SyncState` returns to `Idle` and `MirroredItemCount` stops climbing when it is done. A
non-null `LastSyncError` is the thing to read.

What gets mirrored: every **video** library, folder structure included. Music, books, photos,
collections and extras are not mirrored, so a `boxsets` library on the parent will not appear.

---

## 5. Prove a video plays

Browse to a movie in the web client and press play. The first play of an uncached item fetches it
from the parent, so expect a pause before it starts; the next play is instant.

To check it without a browser, the strongest signal is bytes actually arriving:

```bash
ITEM=$(curl -sS "$CHILD/Items?Recursive=true&IncludeItemTypes=Movie&Limit=1" \
  -H "Authorization: MediaBrowser Token=\"$KEY\"" | jq -r '.Items[0].Id')

curl -sS -X POST "$CHILD/Items/$ITEM/PlaybackInfo" -H "Authorization: MediaBrowser Token=\"$KEY\"" \
  -H 'Content-Type: application/json' -d '{}' | jq '{ErrorCode, MediaSources: [.MediaSources[].Id]}'

MS=$(curl -sS -X POST "$CHILD/Items/$ITEM/PlaybackInfo" -H "Authorization: MediaBrowser Token=\"$KEY\"" \
  -H 'Content-Type: application/json' -d '{}' | jq -r '.MediaSources[0].Id')

curl -sS -r 0-262143 -o /dev/null -w 'HTTP %{http_code}, %{size_download} bytes\n' \
  "$CHILD/Videos/$ITEM/stream?static=true&mediaSourceId=$MS" \
  -H "Authorization: MediaBrowser Token=\"$KEY\""
```

`HTTP 206` with a non-zero byte count is a pass. `PlaybackInfo` answering happily is **not** enough
on its own — it succeeds for an item whose local file is still a zero byte placeholder, because the
fetch from the parent only starts when the stream is requested.

`ErrorCode: "ParentServerUnavailable"`, or a stream answering `503` with
`X-Application-Error-Code: ParentServerUnavailable`, means the file is not cached and the parent
cannot be reached.

---

## 6. Connect clients

The child is an ordinary Jellyfin server to anything downstream. Nothing knows it is a child.

**Its own web UI** — `http://<server>:8096/web`. This is the same jellyfin-web the parent serves.

**Phone, TV and desktop apps** — add a server, address `http://<server>:8096` (or your HTTPS address
if you put a reverse proxy in front), sign in with an account on the *child*. Create additional
non-admin users under Dashboard → Users for other people; they do not need parent credentials.

**Behind a reverse proxy** — terminate TLS in front and proxy to `8096`. Playback of an uncached
item can pause for a long time while the file is fetched, so raise proxy read timeouts well above
the default. `CHILD_DOWNLOAD_WAIT_TIMEOUT_SECONDS` (default 900) is the server's own ceiling for a
transcode waiting on a download; direct play streams as bytes arrive and does not wait.

Two behaviours worth expecting so they do not look like faults:

- Library tiles use the web client's default image — the child does not compose library posters.
- An item evicted from the cache is still listed and still plays; it is fetched again.

---

## Driving it with the MCP server

[`tools/jellyfin-mcp`](tools/jellyfin-mcp) lets an agent do everything above over its API. It runs
wherever the agent runs, not on the server, and talks to the child over HTTP.

### Build it

```bash
cd tools/jellyfin-mcp
npm install
npm run build          # -> dist/index.js
npm test               # 25 tests, optional but fast
```

### Wire it up

`.mcp.json` in your project, or `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "jellyfin-child-server": {
      "command": "node",
      "args": ["/abs/path/to/Jellyfin-child-server/tools/jellyfin-mcp/dist/index.js"]
    }
  }
}
```

The path must be absolute. Restart the client; ten `jellyfin_*` tools should appear.

### The sequence

Sign in first — every other tool needs the session, and they will tell you so if you skip it.

| Step | Tool | Notes |
|---|---|---|
| 1 | `jellyfin_login` | `baseUrl`, `username`, `password` of the **child**. Warns if the account is not an administrator. |
| 2 | `jellyfin_set_parent_config` | Any of parent URL, user name, password, `headers`, prefetch count, cache ceiling, timeouts. Omitted fields keep their stored values. |
| 3 | `jellyfin_test_parent_connection` | Tries without saving. Adds a hint explaining `AccessDenied` and `ConnectionFailed`. |
| 4 | `jellyfin_get_logs` | When step 3 failed. Defaults to child-server lines plus anything at Warning or above. |
| 5 | `jellyfin_connect_parent` | Saves and signs in. Pass `sync: true` to mirror and wait in one call. |
| 6 | `jellyfin_get_parent_libraries` | Asks the **parent** via `/UserViews` — the request the dashboard makes. `compareWithChild: true` reports what has not reached the child. Needs `password`, since the stored one is never readable. |
| 7 | `jellyfin_sync` | Mirror and wait. |
| 8 | `jellyfin_test_playback` | Pulls real bytes, not just `PlaybackInfo`. |
| 9 | `jellyfin_status` | Connection, sync and cache state at any point. |

Cloudflare Access headers go in as a map and are sent on every request:

```json
{
  "parentUrl": "https://jellyfin.example.com",
  "username": "remote",
  "password": "...",
  "headers": {
    "CF-Access-Client-Id": "....access",
    "CF-Access-Client-Secret": "..."
  }
}
```

The session lives in the MCP process and nothing is written to disk, so credentials are gone when
it exits.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `AccessDenied` from the connection test | A gateway refused the request before Jellyfin saw it. Check header names and values. |
| A redirect to `*.cloudflareaccess.com`, or `service_token_status=false` | The Access application has no **Service Auth** policy including this token, or it was rotated. A valid token on its own is not enough. Fix it in Zero Trust, not here. |
| `ConnectionFailed` | Nothing answered at that URL. Check host and scheme. |
| `InvalidCredentials` | The parent rejected the user name or password. |
| `That address is not a Jellyfin server` | Something answered but not with Jellyfin's API — usually a login page. |
| Startup log says `no CHILD_PARENT_URL set` | The env file was not read. Run compose from the directory holding it. |
| Parent page shows settings you did not enter | Expected. `CHILD_PARENT_URL` makes the environment authoritative and `childserver.xml` is rewritten every boot; UI edits are replaced. Unset the `CHILD_PARENT_*` variables to hand control back to the dashboard. |
| A library is on the parent but not the child | Only video libraries are mirrored. Music, books, photos, collections and extras are not. |
| Playback fails but the item is listed, and the parent is up | See below. |

### Direct play failing for whole libraries on the parent

The child fetches media with one request: `GET /Videos/{id}/stream?static=true`. If the parent
answers `500` to that, the child cannot cache the item — the mirror, the posters and browsing all
still work, so it looks like a child server fault when it is not.

This is worth knowing because it has been seen on a real parent, and it sorted cleanly by storage
mount: every item under two of the parent's mounts failed, every item under the others succeeded,
independent of codec and container. The files were readable — a transcode of the same item returned
a playlist happily — so only the static file-serving path was affected. FUSE and network mounts are
the usual suspects.

To tell this apart from a child server problem, ask the parent directly with the same credentials
and headers:

```bash
curl -sS -r 0-8191 -o /dev/null -w 'parent direct play: HTTP %{http_code}\n' \
  "$PARENT/Videos/$PARENT_ITEM/stream?static=true&mediaSourceId=$PARENT_MS" \
  -H "Authorization: MediaBrowser Token=\"$PARENT_TOKEN\"" \
  -H "CF-Access-Client-Id: ..." -H "CF-Access-Client-Secret: ..."
```

`500` there is a problem on the parent. Any Jellyfin client doing direct play against those
libraries hits the same wall and silently falls back to transcoding, which is why it can go
unnoticed.

---

## Not yet proven

Honest about what nobody has watched run end to end. The images are built and published for both
architectures, but that proves they assemble, not that they work:

- **No container started from this image has run yet.** In particular the entrypoint writes
  `childserver.xml` by hand, and that file has been validated against the property names in the C#
  model but never round tripped through the server that reads it. If step 3 shows an empty Parent
  server page despite the startup log reporting it wrote the file, that is the thing to suspect —
  say so, and configure it through the dashboard meanwhile.
- No deployment on a real low-disk device.
- Behaviour against a parent library much larger than the test fixture. A real parent with ~12,000
  items was reachable and listed correctly, but no full mirror of one has been completed.
- The Cloudflare Access path is verified against a live tunnel for sign in and listing, but not
  through a child server end to end.
