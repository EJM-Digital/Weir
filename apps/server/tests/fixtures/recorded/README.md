# Recordings of other apps

What a real Sonarr, a real Radarr and a real Deluno said, kept as the tests found them. A test replays a recording; it never writes
a request or a reply by hand. Where a case has no recording, the test says so and the app's folder lists it (`MISSING.md`).

| Folder | What it holds | Where it came from |
| --- | --- | --- |
| `deluno/` | Weir's replies to Deluno | Deluno's own recorder (Weir #953) |
| `deluno-side/` | Deluno's side of its talk with Weir | Deluno's recorder, copied byte for byte (`deluno-side/README.md`, Weir #971) |
| `sonarr/`, `radarr/` | Weir's manager client code talking to a real Sonarr and a real Radarr, and the webhooks they post | `apps/server/tools/Weir.ArrRecorder`, run by `.github/workflows/record-arr.yml` (Weir #1013, #1014) |

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
- Weekly, `record-arr.yml` records again against the pinned images and the newest releases and fails when the replies differ from these
  once times, ids, sizes and the progress of a command are set aside (`node scripts/compare-arr-recordings.mjs`).
