# Recordings of other apps

What a real Sonarr, a real Radarr, a real Deluno and the five download clients said, kept as the tests found them. A test replays a recording; it never writes
a request or a reply by hand. Where a case has no recording, the test says so and the app's folder lists it (`MISSING.md`).

| Folder | What it holds | Where it came from |
| --- | --- | --- |
| `deluno/` | Weir's replies to Deluno | Deluno's own recorder (Weir #953) |
| `deluno-side/` | Deluno's side of its talk with Weir | Deluno's recorder, copied byte for byte (`deluno-side/README.md`, Weir #971) |
| `sonarr/`, `radarr/` | Weir's manager client code talking to a real Sonarr and a real Radarr, and the webhooks they post | `apps/server/tools/Weir.ArrRecorder`, run by `.github/workflows/record-arr.yml` (Weir #1013, #1014) |
| `metadata-gateway/` | The public metadata service's answers to the read-only lookups Weir makes for posters and original languages (`metadata-gateway/README.md`) | read-only requests to the public service, 20 October 2026 (Weir #1142) |
| `qbittorrent/`, `transmission/`, `deluge/`, `sabnzbd/`, `nzbget/` | Weir's download-client code talking to a real qBittorrent (4.x and 5.x), Transmission, Deluge, SABnzbd and NZBGet | `apps/server/tools/Weir.ClientRecorder`, run by `.github/workflows/record-clients.yml` (Weir #1015) |

## Sonarr and Radarr

`<app>/<version>/<state>.json`, in the format of Deluno's recordings: `{app, version, state, recordedAt, note, exchanges[{request{method, path, headers, body}, reply{status, headers, body | bodyText}}]}`. `<version>/` is the version the app reported
about itself.

| | Sonarr | Radarr |
| --- | --- | --- |
| Image | `lscr.io/linuxserver/sonarr`, pinned by digest in `apps/server/tools/Weir.ArrRecorder/compose.yaml` | `lscr.io/linuxserver/radarr`, pinned by digest in the same file |
| Recorded | 17 October 2026, on a GitHub-hosted Linux runner, with a Transmission download client | the same run |
| Set up with | a root folder, Breaking Bad, a download client, a webhook back to the recorder, a remote path mapping, three finished downloads | a root folder, Big Buck Bunny, a download client, a webhook back to the recorder, a remote path mapping, one finished download |

How a recording is made, and what it holds, is in the recorder's `README.md`. What each state is for, and which test replays it:

| State | Replayed by |
| --- | --- |
| `connection-test`, `connection-test-wrong-key` | `ArrConnectionAndQueueRepliesTests` |
| `describe`, `describe-no-root-folder`, `describe-wrong-key` | `ArrConnectionAndQueueRepliesTests`, and the contract fake manager's root folders (`ArrShapes`) |
| `queue-empty`, `queue-with-download`, `queue-after-remove`, `queue-remove-and-blocklist`, `queue-remove-unknown-item` | `ArrConnectionAndQueueRepliesTests`, and the contract fake manager's queue (`ArrShapes`) |
| `downloaded-scan`, `downloaded-scan-with-mapping`, `downloaded-scan-for-download`, `downloaded-scan-refuses-a-number`, `downloaded-scan-next-file`, `command-status-finished` | `ArrScanAndSetupRepliesTests`, `ArrDownloadedScanBusyRepliesTests`, `DownloadedScanRetryTests`, `DownloadedScanRecordedRepliesTests` |
| `setup-check` | `ArrScanAndSetupRepliesTests` |
| `library-listing`, `rescan`, `redownload-risk`, `redownload`, `redownload-unknown-title`, `redownload-file-gone` | `ArrLibraryAndRedownloadRepliesTests`, and the "Download again" tests in `Weir.Api.Tests` (`ArrRedownloadReplies`) |
| `webhook-*` | `ArrWebhookReplayTests` (posted to a running Weir), `ArrWebhookRecordedTests` (how Weir reads them) |

## Checks

- `node scripts/check-recorded-manifest.mjs` fails when a recording differs from its line in `MANIFEST.sha256`. A recording is never
  edited: run the recorder again, copy its output over the folder, then `node scripts/check-recorded-manifest.mjs --write`.
- `node scripts/check-recorded-replays.mjs` fails when a recorded state is named by no test.
- `node scripts/check-recorded-freshness.mjs` fails when the newest Weir build named in `weir-builds-recorded.json` is more than one release behind the repository. A new folder under `deluno/` or `deluno-side/` is listed there with the Weir build it was recorded against, or the check fails.
- When a recorder compose file changes, `record-arr.yml` and `record-clients.yml` record again against the pinned images (by hand, also the newest releases) and fail when the replies differ from these
  once times, ids, sizes and the progress of a command are set aside (`node scripts/compare-arr-recordings.mjs`).

## The download clients

Same format. Each client is the published Docker image, pinned by digest in `apps/server/tools/Weir.ClientRecorder/compose.yaml` (Transmission's in `apps/server/tools/Weir.ArrRecorder/compose.yaml`), recorded on a GitHub-hosted Linux runner on 17 October 2026. A secret, and the value of a session cookie, session id or sign-in token, is `<scrubbed>`; the replay tests sign in with `<scrubbed>` so the request they send is the recorded one.

| Client | Version | Recorded states | Replayed by |
| --- | --- | --- | --- |
| `qbittorrent/` | 4.6.7, 5.1.4 and 5.2.4 | `test`, `test-wrong-password`, `folders` | `QBittorrentRepliesTests` (each version) |
| `transmission/` | 4.0.6 and 4.1.3 | `test-open`, `test`, `test-wrong-password`, `test-nothing-saved`, `folders` | `TransmissionRepliesTests` (each version) |
| `deluge/` | 2.2.0 | `test`, `test-wrong-password`, `folders-stock`, `folders-move-completed-off`, `folders` | `DelugeRepliesTests`, `DelugeRulesTests`, `SessionCookieTests` |
| `sabnzbd/` | 5.1.3 | `test`, `test-wrong-key`, `folders`, `folders-wrong-key` | `SabnzbdRepliesTests` |
| `nzbget/` | 26.3 | `test`, `test-wrong-password`, `folders`, `folders-wrong-password` | `NzbgetRepliesTests` |

All five are also served by the contract suite's fake download client (`FakeDownloadClient`), which answers a running Weir with these replies and decides only whether the secret is the expected one (`DownloadClientRecordedRepliesTests`). What the recorder could not make a client do is in each client's `MISSING.md`.
