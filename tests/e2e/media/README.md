# Sample media fixtures

Four short H.264 AVI clips from
https://file-examples.com/index.php/sample-video-files/sample-avi-files-download/
(fetched 2026-09-21; the site serves them through a redirect page, so they are
committed here instead of being downloaded during tests).

| File | Resolution | Bytes | SHA-256 |
|---|---|---|---|
| `file_example_AVI_480_750kB.avi` | 480x270 | 742478 | `7c1d478794e328ec1d2426b48c6115cb1c364fdfcaa65afcc8dd9cd4301121d2` |
| `file_example_AVI_640_800kB.avi` | 640x360 | 829208 | `60f6a6743ff96850fba153c5b09123bb6f86bc687a73ed53850a3a1fefda9f6e` |
| `file_example_AVI_1280_1_5MG.avi` | 1280x720 | 1480958 | `50a48a3e5ac93a164c62179ca85d6891eba738807ab29a266122586459f80c10` |
| `file_example_AVI_1920_2_3MG.avi` | 1920x1080 | 2279794 | `abfd4c73130792c851663170844fe0168e3d4c3a222b11f33815cfa775b7178a` |

`build-library.mjs <outDir>` lays the clips out as a Jellyfin media library
(movies plus a TV show with two seasons) for the parent server used in the
end-to-end tests. Adjacent episodes use different clips so a fetched file can be
told apart from its neighbours by size and hash.
