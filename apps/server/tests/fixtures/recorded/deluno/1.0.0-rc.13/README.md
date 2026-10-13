# Deluno's recordings of Weir 1.0.0-rc.13

The real replies Weir gave Deluno, with the real requests Deluno sent to get them. Weir's contract tests
(`apps/server/tests/Weir.Contract.Tests/MediaManagers/Recorded/`, area `media_managers`) replay each recorded request against
a running Weir and hold its reply to the recorded one. Nothing here was written by hand: a test never builds a Deluno request or
a Weir reply from memory or from an issue.

## Where they come from

| | |
| --- | --- |
| Source | Deluno repository, `tests/fixtures/recorded/weir/1.0.0-rc.13/` (Deluno #1237) |
| Source commit | `28061d68dd34b9977310a0bbf561e253ffa5fed1` (the last Deluno commit to change that folder) |
| Copied | byte for byte; compare with the source folder before changing anything here |
| Captured | 11 October 2026, 01:59 to 02:02 UTC |
| Captured on | Deluno's golden VM (its clean virtual machine), by Deluno's recording script, `scripts/recorded-replies/Invoke-RecordReplies.ps1`, against a real Deluno and Weir 1.0.0-rc.13 |
| Weir version | `1.0.0-rc.13`. The folder is named for it, so a later recording sits beside it as `<version>/` |

A recording is one situation. Its fields are `app`, `version`, `state` (the file name), `recordedAt`, `note` (what was done
to reach it) and `exchanges`, each a `request` and the `reply` (`status`, `headers`, and `body` or `bodyText`). Secrets, machine
names and addresses were removed by Deluno's scrub step; `machine_name` reads `<scrubbed>`.

The paths in the recordings are the VM's (`C:\Downloads\Completed\Movies`, `C:\Weir\Ready\Movies`). The tests map them onto
folders of the test server and make each id their own (`Translation.cs`), and change nothing else in the recording.

## The files

Time order within a group is the order the recorded times give. A name is Deluno's name for the state it meant to reach; where the
reply shows something else, the test follows the reply (noted below).

| File | What it is |
| --- | --- |
| `capabilities.json` | `GET /intake/capabilities` with the secret: the five capabilities, `machine_name`, `product`, `version` |
| `capabilities-no-secret.json` | the same with no secret header: 401 |
| `capabilities-wrong-secret.json` | the same with a wrong secret: 401 |
| `library-folders.json` | `GET /intake/library-folders`: each workflow's folders, Deluno's id for its library, `folders_from_manager`, the minimum size |
| `handoff-accepted.json` | a hand-off of one file in the watched folder (`Recorded.Weir.A.mkv`): 200 |
| `status-completed.json` | the status of that hand-off once finished: `completed`, with the output path |
| `outcome-delivered.json` | Deluno reports `imported` with Weir's own output path: 200, `released` false |
| `outcome-already-recorded.json` | the same report again: the same 200 |
| `handoff-accepted-missing-file.json` | a hand-off for a file that is not in the watched folder: 200 |
| `missing-file-status-working.json` | the status of that hand-off a while later: `working` |
| `handoff-ignored-no-source.json` | a hand-off with no `sourcePath`: 200 `ignored` |
| `handoff-refused-wrong-secret.json` | a hand-off with a wrong secret: 401 |
| `handoff-refused-not-watched.json` | a hand-off for a file outside every watched folder: 400, naming the workflows and their folders |
| `no-record.json` | a status poll, then an outcome, for a hand-off id Weir never had: two 404s |
| `outcome-before-finished-200.json` | Deluno reports `imported` with the file's path in the watched folder (`Recorded.Weir.Q8.mkv`): 200, `released` true. Named for an outcome sent before Weir finished, but a 200 is what Weir answers once it has finished |
| `handoff-cancel-refused.json` | a cancel of that finished hand-off: 409 "is completed"; then its status, `completed` |
| `outcome-outcome-already-recorded.json` | Deluno then reports `not-imported`: 409, `code` `outcome_already_recorded`. Named for a cancelled hand-off, but the reply is the one for an import already recorded |
| `handoff-cancel-too-late.json` | a cancel of a hand-off: 204, no body. The reply is the one for a hand-off still waiting for its turn, which Weir cancels |
| `handoff-resend-accepted.json` | the same source sent again as a new hand-off (`Recorded.Weir.R.mkv`): 200 |
| `resend-status-completed.json`, `completed-without-output-path.json` | the status of the resent hand-off: `completed`, with no output path (rc.13) |
| `outcome-delivered-without-output-path.json` | Deluno reports `imported` with the file's path in the watched folder: 200, `released` true |

## Differences since rc.13 that the tests pin

A deliberate change is never made by editing a recording. The test names it as an `ExpectedDifference` with its issue number, and
the pin fails once Weir answers what was recorded again, so it gets removed.

| Recording | Field | Recorded (rc.13) | Now | Issue |
| --- | --- | --- | --- | --- |
| `resend-status-completed.json`, `completed-without-output-path.json` | `outputPath`, `outputFiles` | `null` | the output file, because the new send of a resent release is answered as completed with the output, never without one | #956 |

## Not recorded

Never invented. A test for any of these waits for a recording.

- `not_taken` failure reports (Weir #956, Deluno #1250): the golden VM has one Deluno connection to Weir, and Weir answers `not_taken`
  when a second connection of the same kind hands off a file whose pass belongs to the first. This is also why the "no disposition on
  a superseded send" change of #956 is not pinned against a recording: those are Weir's reports to Deluno, not replies.
- A hand-off of a release **folder**, and one with `sourceFiles`: every recorded hand-off is a single file in the watched root.
- A `not-imported` report Weir accepts (200), with any of its reasons, and an outcome while the hand-off is still waiting
  or working (409 `handoff_not_finished`, 409 `handoff_ended`).
- A status poll while queued, scheduled, failed, cancelled, passed through or rejected (`status-queued` could not be caught on the VM).
- A cancel while the hand-off is working (409).
- Deluno's own side of the talk, as Deluno answers Weir's reports: Deluno has not recorded it yet (its `MISSING.md` lists it).

Where Weir's tests cover one of these without a recording (for example `HandoffStatusTests`, `HandoffLifecycleTests` and
`HandoffFolderTests` in this area), they still use shapes from the issues, not from a recording.
