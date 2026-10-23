# Deluno's recordings of its own side of the talk with Weir

What Deluno sends Weir, what Weir sends Deluno, and Deluno's own replies to the calls Weir makes of it. Deluno recorded them
(Deluno #1239 and #1265) and checksum-pins them with `MANIFEST.sha256`; Weir copies them and replays them (Weir #971). The
recordings of Weir's replies, made by Deluno the other way round, are in `../deluno/` (Weir #953).

## Where they come from

| | |
| --- | --- |
| Source | Deluno repository, `tests/fixtures/recorded/deluno/` |
| Source commit | `73905126bbf27db1be75b7bdefcb983805fe01bb` (Deluno `main`). It carries Deluno #1267, which re-made five recordings so that a body-less 404 means only that the route is missing, and the reports Weir 1.0.0-rc.18 sent Deluno on the golden VM; every file here is byte-identical to that commit (git blob ids match) |
| Copied | byte for byte, with Deluno's `MANIFEST.sha256`; 93 recordings. `1.0.0-rc.30.34/` came later, from Deluno's recording run of 22 October 2026 (`artifacts/deluno-side-recordings/1.0.0-rc.30.34-weir-1.0.0-rc.19` in Deluno's repository), with that run's manifest lines added. `1.0.0-rc.30.32/events-superseded` is gone: the same report was recorded again, behind a real hand-off, as `1.0.0-rc.30.34/events-failed-superseded` |
| Never edited here | `node scripts/check-recorded-manifest.mjs` fails (CI, `repo-checks`) when a file differs from its line in the manifest, and names the file |
| Weir build | `../weir-builds-recorded.json` names the Weir build each folder below was recorded against (rc.30.30: 1.0.0-rc.13, rc.30.32: 1.0.0-rc.18, rc.30.34: 1.0.0-rc.19, rc.30.35: 1.0.0-rc.20). `node scripts/check-recorded-freshness.mjs` (CI, `repo-checks`, and the release's `validate` job) fails when the newest is more than one release behind the repository (Weir #979) |
| Copy again | `git -C <Deluno> archive <commit> tests/fixtures/recorded/deluno` and put the `1.0.0-rc.*` folders and `MANIFEST.sha256` here, then change the commit above |

| Version folder | What it holds | How it was recorded |
| --- | --- | --- |
| `1.0.0-rc.30.30/` | Deluno's requests to Weir (`handoff-*`, `outcome-*`, `status-ask-queued`, `cancel-queued`, `capabilities-ask`, `library-folders-ask`) with Weir's replies, and Weir's reports to Deluno (`events-*`) with Deluno's replies | 13 October 2026, a real Deluno rc.30.30 and a real Weir 1.0.0-rc.13 on Deluno's golden VM, with a recording proxy in front of each |
| `1.0.0-rc.30.32/` | Deluno's own replies (`reply-*`) to the calls Weir makes of it or a person makes with a key: the hand-off list, where downloads land, import the original, send the outcome again, and the refusals of a key | 14 October 2026, from Deluno's server code through its in-process test host, not from the VM |
| `1.0.0-rc.30.32/` | Weir's reports to Deluno (`events-*`) with Deluno's answers: the send that was replaced, the second send of a release, the two name collisions, a season pack and a partly cleaned one, a rejected release, and a report Deluno answered 503, 401 or 403 and Weir did not send again | 15 October 2026, a real Weir 1.0.0-rc.18 on Deluno's golden VM behind a recording proxy; the 503, 401 and 403 are the proxy's own answers and the request was not passed on |
| `1.0.0-rc.30.34/` | Deluno's requests to Weir (`handoff-*`, `outcome-*`, `status-ask-queued`, `status-ask-queued-source-missing`, `cancel-queued`, `capabilities-ask`, `library-folders-ask`) with Weir's replies, and Weir's reports to Deluno (`events-*`) with Deluno's answers, from one run. New since rc.18: a release sent again while Weir worked the first send (`handoff-resend-during`), the first send reported failed with `failureClass` superseded and answered 202 (`events-failed-superseded`), a send of a file Weir had already cleaned (`events-completed-already-cleaned`), an outcome for hand-offs still queued (`outcome-not-imported-removed-handoff-ended`), a queued hand-off whose download was deleted (`status-ask-queued-source-missing`), and an audio-only file under the reject policy that Deluno could not replace, handed back and reported finished (`events-completed-reject-policy-`: the recorder cut the name short, and the file's own `state` says the same, so it is named as recorded). Not recorded again, so the older copies stay: `events-too-late` (rc.30.30) and the `reply-*` set (rc.30.32) | 22 October 2026, a real Deluno 1.0.0-rc.30.34 and a real Weir 1.0.0-rc.19 (build 3460f96) on Deluno's golden VM behind a recording proxy. A report for a hand-off the recorder made straight to Weir (the name collisions, the season packs, `events-failed-under-minimum`, `events-failed-unreadable`, `events-failed-rejected`, `events-completed-after-takeover`, `events-completed-reject-policy-`) is answered 409 "could not match" by the real Deluno, which has no row for it; the 503, 401 and 403 are the proxy's own answers. Weir's own replies in the same run are in `../deluno/1.0.0-rc.19+3460f96/` |
| `1.0.0-rc.30.35/` | `real-*`: the whole conversation about one hand-off, in order, and the exchanges of connecting the two applications, as a recording proxy between a real Deluno and a real Weir saw them | 23 October 2026, run 39965707775 of `record-deluno.yml` against the newest Deluno release, 1.0.0-rc.30.35 (`ghcr.io/ejm-digital/deluno@sha256:2f6b82dd...`), with Weir built from commit c61f0b5a (1.0.0-rc.20). It replaced `1.0.0-rc.30.33/` (recorded 18 October 2026 the same way), which held the same nine states; only ids and times differed between them, and it is gone. In Compose on a GitHub-hosted runner by the `with-deluno` scenario shard (`scripts/scenarios-ci/with-deluno`, `.github/workflows/record-deluno.yml`): Weir's image built from that commit, Deluno's published image pinned by digest, a real qBittorrent, nothing of Deluno's source. Written by the shard and copied here unchanged; they are not from Deluno's repository |

A recording is `{app, version, state, recordedAt, note, exchanges[{request{method, path, headers, body}, reply{status, headers, body}}]}`.
A secret is scrubbed (`<scrubbed>`). In the rc.30.32 recordings what differs from run to run is a placeholder: `<id>`, `<utc>`,
`<data-root>`, `<handoff:x>`, `<library:x>` and `<connection:x>`. A placeholder stands for any value of its type; the tests
(`DelunoPlaceholders`) give it a real one.

## What replays them

`apps/server/tests/Weir.Contract.Tests/MediaManagers/Recorded/` (area `media_managers`), against a running Weir. No test builds a Deluno
request or a Weir reply by hand. Where a recording lacks a hand-off a status poll, a cancel or a report names, the test makes it from
the recorded single-file hand-off with only its id and release changed (`DelunoSide.HandoffLike`).

| Recordings | Test class | What is held |
| --- | --- | --- |
| `capabilities-ask`, `library-folders-ask`, `handoff-single-file`, `handoff-release-folder`, `handoff-usenet-folder`, `handoff-resend`, `handoff-resend-during`, `status-ask-queued`, `status-ask-queued-source-missing`, `cancel-queued`, `outcome-imported`, `outcome-not-imported-removed`, `outcome-not-imported-removed-handoff-ended`, `outcome-imported-for-replaced-send` | `DelunoRequestsTests`, `RecordedLibraryFoldersTests` | Deluno's request is sent as recorded (its Idempotency-Key too); Weir's reply is the recorded one, member by member; and Weir's state afterwards is what the call implies: the output where the Deluno table below puts it, the queued hand-off cancelled and never worked, the file's record of what Deluno did |
| `events-completed`, `events-completed-resent`, `events-failed-under-minimum`, `events-failed-unreadable`, `events-failed-source-gone`, `events-completed-already-cleaned`, `events-too-late` | `DelunoReportsTests` | A fake Deluno records what Weir POSTs for each ending: the same members, in the same order, of the same types, with the exact words (`status`, `failureClass` `source_gone`, `disposition`, `sourceRemoved`, the ids and the output paths). The fake answers with Deluno's recorded reply and Weir must read it: a 202 is delivered and no longer owed; a 409 `dismissed` is Deluno having stopped waiting, said so in plain words, and not retried |
| `reply-download-destinations-libraries`, `reply-download-destinations-empty`, `reply-key-without-imports-scope`, `reply-key-with-imports-scope-only`, `reply-no-sign-in` | `DelunoRepliesTests`, `DelunoReportsTests` | A fake Deluno serves each reply to Weir: the folders it names (and the path mappings of the processor connection, and a null one) are read and judged; a 200 with no library, a 404 `library_not_found` (and a 404 with no body, which still means the route is missing), a 401 and both 403 shapes each get a sentence, no crash, and no retry |
| `events-completed-answered-503-not-repeated` | `DelunoReportsTests`, `RecordedReportRetryTests` | Deluno answers a finished hand-off's report 503, as recorded: Weir's report is the recorded one and stays owed with a note that it is waiting (`DelunoReportsTests`, a running server). `RecordedReportRetryTests` serves the recorded 503 and then the recorded 202 to the production heartbeat on a fake clock: the same report goes again at the first heartbeat a minute later and is then no longer owed. The recording shows rc.18 never sent it again, and the test fails on the code before the fix. The rest of the schedule (doubling to an hour, `Retry-After`, a week) is held by `OwedReportRetryTests` |
| `events-answered-401-not-repeated`, `events-answered-403-not-repeated` | `DelunoReportsTests`, `RecordedReportRetryTests` | Deluno answers the report 401 or 403 as recorded: Weir stops owing it, says it could not tell Deluno, and sends it once, however long the fake clock then runs |
| `events-failed-superseded`, `events-completed-after-takeover` | `DelunoReportsTests` | A release sent twice while Weir holds the first: the first send is closed as cancelled and reported failed with `failureClass` superseded (Deluno answers 202), the second finished with the output, with the members and exact words recorded; a finished report for a hand-off Deluno has no row for is read as recorded |
| `events-completed-reject-policy-` | `DelunoReportsTests` | An audio-only file under the reject policy, chosen while Deluno could replace a rejected release and sent after it no longer said it could: handed back unchanged, read as `passed-through`, and reported finished |
| `events-completed-collision-replace-first`, `-second`, `events-completed-collision-keep-both-first`, `-second` | `DelunoReportsTests` | One file sent twice under each collision policy: Weir had already cleaned this exact file (same path, size and time), so it processes nothing again and reports the second send under the output path of the first, whatever the policy |
| `events-completed-season-pack` | `DelunoReportsTests` | A season pack of two episodes is reported once, with the folder as `outputPath` and both episodes in `outputFiles` |
| `events-failed-partial-season-pack`, `events-failed-partial-season-pack-corrupt` | `DelunoReportsTests` | A season pack with one episode Weir cannot read, under the hold policy: reported failed, held, with the two episodes it finished (both recordings hold the same report) |
| `events-failed-rejected` | `DelunoReportsTests` | A file with no video under the reject policy, with Deluno advertising it can replace a rejected release: reported failed, rejected, `failureClass` preflight, the download left in place |
| any of the above, changed | `DelunoRecordingComparisonTests` | A recording that stops matching fails naming its file and the JSON path |

A change Weir made on purpose since a recording is never made by editing it: the test pins it as an `ExpectedDifference` with the issue
number, and the pin fails once Weir answers what was recorded again.

| Recording | What Weir does now | Issue |
| --- | --- | --- |
| `events-too-late` (second exchange, rc.30.30) | A send Deluno replaced while Weir was working it is closed as cancelled and never reported finished; only the new send is reported (`events-completed-resent`, second exchange, which still matches). Recorded with rc.13 the replaced send was also reported completed, with no output of its own, and Deluno answered 409. rc.19 recorded the replaced send as `events-failed-superseded` instead | #956 |

## What has no Weir behaviour to replay

Weir never calls three of the routes Deluno recorded replies for, so those recordings are listed in
`../deluno-side-no-weir-behaviour.json` with the reason and the route, and `node scripts/check-recorded-replays.mjs` fails if Weir's
server code starts mentioning the route (then they must be replayed):

- `reply-handoffs-empty`, `reply-handoffs-listed`, `reply-handoffs-with-outcomes`: `GET /api/integrations/processors/handoffs`.
- `reply-import-original-accepted`, `reply-import-original-not-yet`, `reply-import-original-unknown`: `POST .../handoffs/{id}/import-original`.
- the ten `reply-send-again-*`: `POST .../handoffs/{id}/outcome/send-again`.

Their 404s now say what was missing (Deluno #1267): `handoff_not_found` for import-original and `nothing_recorded` for send-again, as
`library_not_found` does for download-destinations, the one Weir reads. A body-less 404 on any of the routes means only that the route is missing.

Only the VM scenario suite (`scripts/scenarios/`) calls the hand-off list and send-again, from outside Weir. The `{error, required, granted}`
403 body belongs to those write routes; `DelunoRepliesTests` serves it to the one Weir call it can meet (the download-destinations read),
and `DelunoReportsTests` to Weir's report.

## What the recordings could not cover

- Weir's reports `accepted`, `started` and `not_taken`: Deluno never recorded Weir sending them (its `MISSING.md`). Weir's tests build
  those; they are not held to a recording.
- Deluno's recorder made the hand-offs for the collision, season pack, partly cleaned pack and rejected-release reports straight to Weir's
  intake and did not keep them. Each replay builds the hand-off from the recorded single-file one with the id, release, library and path the
  report names; the audio-only file's name is the recorder's pattern, as the report names no file. `../deluno-side-awaiting-replay.json` lists a
  recording that is not replayed yet with its open issue; it is empty.
- A 429 or a 5xx other than 503 from Deluno to a report, and Deluno not answering at all: no reply was recorded. `OwedReportRetryTests` and
  `HandoffWaitingReportTests` cover them without a recording.
- Deluno's `external/manifest`, `external/health`, `external/queue` and `external/file-changed` replies: not in this set. The fake
  Deluno answers them with the minimum Weir reads, and the download-destinations tests rely on that manifest.
- A body-less 404 from an older Deluno: none was recorded. `DelunoRepliesTests` makes it from the `library_not_found` reply with its
  body taken away, which is what a Deluno that predates the route answers.

## Permissions

Weir asks Deluno for the **Imports** permission and for nothing else on the two routes Deluno #1265 changed: a key with only `imports`
gets a 200 from `download-destinations` (`reply-key-with-imports-scope-only`, `DelunoRepliesTests`), and a key without it a 403 that
Weir turns into "Give it the Imports permission" (`reply-key-without-imports-scope`). Weir's other calls to Deluno (its connection
test, the manifest and the queue) are reads that Deluno asks the `read` scope for, which is why Deluno's *Media automation* key carries
both `read` and `imports`.
