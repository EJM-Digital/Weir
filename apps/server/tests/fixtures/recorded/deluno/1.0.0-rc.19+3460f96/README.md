# Deluno's recordings of Weir 1.0.0-rc.19

The real replies Weir gave Deluno, with the real requests Deluno sent to get them, made in the same run as `../../deluno-side/1.0.0-rc.30.34/`.
Weir's contract tests (`apps/server/tests/Weir.Contract.Tests/MediaManagers/Recorded/`, area `media_managers`) replay each recorded request
against a running Weir and hold its reply to the recorded one. Nothing here was written by hand. `../1.0.0-rc.13/` holds the first set; a test
reads a state from here when this folder holds it, and from there for a state only that set recorded (`Recording.Load`).

## Where they come from

| | |
| --- | --- |
| Source | Deluno repository, `artifacts/deluno-side-recordings/1.0.0-rc.30.34-weir-1.0.0-rc.19/weir-side/1.0.0-rc.19+3460f96/` |
| Copied | byte for byte: the 62 recordings, without the `logs/` folder Deluno kept beside them |
| Captured | 22 October 2026 |
| Captured on | Deluno's golden VM, against a real Deluno 1.0.0-rc.30.34 and Weir 1.0.0-rc.19 (build 3460f96) |
| Weir version | `1.0.0-rc.19+3460f96`, the version Weir reported about itself; the folder is named for it |
| Scrubbed | a secret reads `<scrubbed>`; `machine_name` reads `<scrubbed>` |
| Weir build | `../weir-builds-recorded.json` names the build (`1.0.0-rc.19`) for `node scripts/check-recorded-freshness.mjs` |

A recording is `{app, version, state, recordedAt, note, exchanges[{request{method, path, headers, body}, reply{status, headers, body}}]}`; `state`
is the file name and `note` says what was done to reach it.

## What is in it

- The reads and refusals of the first set, recorded again: `capabilities*`, `library-folders`, `handoff-accepted`, `handoff-accepted-missing-file`,
  `handoff-ignored-no-source`, `handoff-refused-wrong-secret`, `handoff-refused-not-watched`, `status-completed`, `missing-file-status-working`,
  `no-record`, `completed-with-output-path`, `outcome-delivered`, `outcome-already-recorded`.
- A release sent again: `handoff-resend-accepted`, `resend-status-*`, `resend-takeover-*`, `completed-after-resend`, `outcome-superseded` and
  `outcome-superseded-newer`. The first send is closed as `cancelled` with a message that the release was sent again; the second is completed with its output.
- Hand-offs by kind, with a status at each step: `handoff-release-folder-accepted`, `handoff-source-files-accepted`, `release-folder-status-*`,
  `source-files-status-*`, `completed-season-pack-status-*`, `failed-partial-season-pack-corrupt-status-*`, `failed-rejected-status-*`
  (the audio-only file under the reject policy, queued, working, then `passed-through` when Deluno could not replace it), `output-collision-*-status`.
- Cancels and outcomes Weir refuses or accepts: `handoff-cancelled` (204), `handoff-cancel-working` and `handoff-cancel-too-late` (409),
  `outcome-handoff-not-finished*`, `outcome-handoff-ended*`, `outcome-imported-with-reason`, `outcome-not-imported-*`, `database-busy-*`,
  `internal-error-outcome`, `status-queued`, `status-working`, `status-failed`, `status-cancelled`.

## What the tests replay

`RecordedHandoffTests`, `RecordedStatusAndCancelTests`, `RecordedHandoffCancelTests`, `RecordedOutcomeTests`, `RecordedReadTests`,
`RecordedLibraryFoldersTests` and `RecordedWebhookSecretTests` replay the states they name. A state whose scenario needs a made-up hand-off
(a database that is busy, an internal error) is not replayed until a test can make that situation without writing the reply by hand.

## What changed from the first set

| State | rc.13 | rc.19 |
| --- | --- | --- |
| `handoff-cancel-too-late` | a 204: the hand-off was still waiting | a 409: the hand-off was already completed (`handoff-cancelled` is the 204) |
| `resend-status-completed` | `outputPath` null | the output file (Weir #956) |
