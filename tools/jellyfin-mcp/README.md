# Jellyfin child server MCP server

An MCP server that lets an agent set up and exercise a Jellyfin child server: sign in as an
administrator, point it at a parent, test the connection, read the logs when the test fails, save
the connection, list the parent's libraries and prove a video actually plays.

It talks to the servers over their HTTP APIs only. There is no child server code in here, and
nothing needs to be added to the server for it to work.

## Build

```bash
cd tools/jellyfin-mcp
npm install
npm run build      # -> dist/index.js
npm test           # 23 tests against a stub Jellyfin server
```

## Wiring it up

```json
{
  "mcpServers": {
    "jellyfin-child-server": {
      "command": "node",
      "args": ["/path/to/Jellyfin-child-server/tools/jellyfin-mcp/dist/index.js"]
    }
  }
}
```

The session lives in the process: `jellyfin_login` once, then the other tools reuse it. Nothing is
written to disk, and no credentials are stored.

## Tools

| Tool | What it does |
|---|---|
| `jellyfin_login` | Signs in to the **child** server. Warns if the account is not an administrator. |
| `jellyfin_get_parent_config` | Reads the stored parent settings. The password is never returned. |
| `jellyfin_set_parent_config` | Sets any of URL, user name, password, gateway headers, prefetch count, cache ceiling, download timeout, sync interval. Omitted fields keep their stored values. |
| `jellyfin_test_parent_connection` | Tries a parent without saving. Adds a hint explaining `AccessDenied` and `ConnectionFailed`. |
| `jellyfin_get_logs` | Tail of the newest log, filtered to child server lines and anything at Warning or above. |
| `jellyfin_connect_parent` | Saves the settings and signs in to the parent, keeping the token. Optionally syncs and waits. |
| `jellyfin_get_parent_libraries` | Signs in to the **parent** and calls `/UserViews` — the request the web client makes when a user opens the dashboard. `compareWithChild` also reports which libraries have not reached the child. |
| `jellyfin_sync` | Mirrors the parent libraries and waits for the scan. |
| `jellyfin_test_playback` | Asks for playback info, then pulls the first bytes of the stream. |
| `jellyfin_status` | Connection, sync and cache state. |

## A parent behind Cloudflare Access

Pass the service token as headers; they are sent with every request:

```json
{
  "parentUrl": "https://jellyfin.example.com",
  "username": "remote",
  "password": "…",
  "headers": {
    "CF-Access-Client-Id": "….access",
    "CF-Access-Client-Secret": "…"
  }
}
```

The service token must be covered by a **Service Auth** policy on the Access application. A valid
token on its own is not enough — without that policy Cloudflare answers 302 to the login page and
the tools report `AccessDenied`. Cloudflare's own verdict is visible in the `meta` JWT of the
redirect as `"service_token_status": false`.

## Why playback is tested by reading bytes

`PlaybackInfo` answers happily for an item whose local file is still a zero byte placeholder — the
fetch from the parent only starts when the stream is requested. So `jellyfin_test_playback` issues a
ranged `GET /Videos/{id}/stream` and reports how many bytes arrived. A refusal comes back as
`ParentServerUnavailable` with no bytes, which is the child saying the file is not cached and the
parent is unreachable.

## Tests

`test/stub-server.ts` is a stub Jellyfin that records the headers of every request it receives. The
gateway suite configures it to refuse anything without the service token, so a code path that
forgets to send the headers fails the suite rather than passing quietly — including the video
stream request, which is the one that used to be easy to miss.

`test/server.test.ts` drives the tools over the real MCP protocol through a linked pair of
in-memory transports, so the tool schemas and handler wiring are covered too.
