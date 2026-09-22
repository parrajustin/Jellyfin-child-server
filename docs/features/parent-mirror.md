# Parent mirror

## Status

| Field | Value |
|---|---|
| Status | active |
| Owner | Luis Trujano |
| Reuse Decision | n/a (fork of Jellyfin server v12.1) |

## Purpose and business rules

See `docs/child-server.md` for the full feature description (mirroring, on-play download,
prefetch, eviction, parent-unreachable handling). This file exists for the pitfalls this
standard requires per feature; it is not a duplicate of `docs/child-server.md`.

## Failure modes and pitfalls

1. **2026-09-21** — Upstream Jellyfin v12.1 bug (not child-server code), found and fixed this
   session. A video sitting directly in a folder named after an extra type (`clips`, `extras`,
   `trailers`, and the other directory-name rules in `NamingOptions`) is resolved as a library
   item in its own right, then `FindExtras` matches it against the extra-type rule for its own
   folder, so `RefreshExtras` sets its owner to itself and clears its parent.
   `LibraryManager.GetCollectionFolders` then walks that self-referencing owner/parent chain
   forever, and a library scan hangs at 88% with no error logged. Reproduced on a plain,
   unmodified parent Jellyfin server too, so this is an upstream defect, not something
   introduced by the child-server changes. Fixed by skipping a candidate whose path or id
   equals the owner's own id, plus a 128-level bound on the walk that logs the looping item
   if the bound is hit. Diagnosed with a thread dump (`dotnet-stack report`), see
   `LESSONS.md` L-184; the self-referencing rows were confirmed with a direct SQLite query
   over `jellyfin.db`, see L-185. Fix: commit ffd3597.
