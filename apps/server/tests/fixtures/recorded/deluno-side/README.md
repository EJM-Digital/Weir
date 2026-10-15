# Deluno's recordings of its own side of the talk with Weir

What Deluno sends Weir, what Weir sends Deluno, and Deluno's own replies to the calls Weir makes of it. Deluno recorded them
(Deluno #1239 and #1265) and checksum-pins them with `MANIFEST.sha256`; Weir copies them and replays them (Weir #971). The
recordings of Weir's replies, made by Deluno the other way round, are in `../deluno/` (Weir #953).

## Where they come from

| | |
| --- | --- |
| Source | Deluno repository, `tests/fixtures/recorded/deluno/` |
| Source commit | `73905126bbf27db1be75b7bdefcb983805fe01bb` (Deluno `main`). It carries Deluno #1267, which re-made five recordings so that a body-less 404 means only that the route is missing, and the reports Weir 1.0.0-rc.18 sent Deluno on the golden VM; every file here is byte-identical to that commit (git blob ids match) |
| Copied | byte for byte, with Deluno's `MANIFEST.sha256`; 51 recordings |
| Never edited here | `node scripts/check-recorded-manifest.mjs` fails (CI, `repo-checks`) when a file differs from its line in the manifest, and names the file |
| Copy again | `git -C <Deluno> archive <commit> tests/fixtures/recorded/deluno` and put the `1.0.0-rc.*` folders and `MANIFEST.sha256` here, then change the commit above |

| Version folder | What it holds | How it was recorded |
| --- | --- | --- |
| `1.0.0-rc.30.30/` | Deluno's requests to Weir (`handoff-*`, `outcome-*`, `status-ask-queued`, `cancel-queued`, `capabilities-ask`, `library-folders-ask`) with Weir's replies, and Weir's reports to Deluno (`events-*`) with Deluno's replies | 13 October 2026, a real Deluno rc.30.30 and a real Weir 1.0.0-rc.13 on Deluno's golden VM, with a recording proxy in front of each |
| `1.0.0-rc.30.32/` | Deluno's own replies (`reply-*`) to the calls Weir makes of it or a person makes with a key: the hand-off list, where downloads land, import the original, send the outcome again, and the refusals of a key | 14 October 2026, from Deluno's server code through its in-process test host, not from the VM |
| `1.0.0-rc.30.32/` | Weir's reports to Deluno (`events-*`) with Deluno's answers: the send that was replaced, the second send of a release, the two name collisions, a season pack and a partly cleaned one, a rejected release, and a report Deluno answered 503, 401 or 403 and Weir did not send again | 15 October 2026, a real Weir 1.0.0-rc.18 on Deluno's golden VM behind a recording proxy; the 503, 401 and 403 are the proxy's own answers and the request was not passed on |

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
| `capabilities-ask`, `library-folders-ask`, `handoff-single-file`, `handoff-release-folder`, `handoff-usenet-folder`, `handoff-resend`, `status-ask-queued`, `cancel-queued`, `outcome-imported`, `outcome-not-imported-removed`, `outcome-imported-for-replaced-send` | `DelunoRequestsTests`, `RecordedLibraryFoldersTests` | Deluno's request is sent as recorded (its Idempotency-Key too); Weir's reply is the recorded one, member by member; and Weir's state afterwards is what the call implies: the output where the Deluno table below puts it, the queued hand-off cancelled and never worked, the file's record of what Deluno did |
| `events-completed`, `events-completed-resent`, `events-failed-under-minimum`, `events-failed-unreadable`, `events-failed-source-gone`, `events-too-late` | `DelunoReportsTests` | A fake Deluno records what Weir POSTs for each ending: the same members, in the same order, of the same types, with the exact words (`status`, `failureClass` `source_gone`, `disposition`, `sourceRemoved`, the ids and the output paths). The fake answers with Deluno's recorded reply and Weir must read it: a 202 is delivered and no longer owed; a 409 `dismissed` is Deluno having stopped waiting, said so in plain words, and not retried |
| `reply-download-destinations-libraries`, `reply-download-destinations-empty`, `reply-key-without-imports-scope`, `reply-key-with-imports-scope-only`, `reply-no-sign-in` | `DelunoRepliesTests`, `DelunoReportsTests` | A fake Deluno serves each reply to Weir: the folders it names (and the path mappings of the processor connection, and a null one) are read and judged; a 200 with no library, a 404 `library_not_found` (and a 404 with no body, which still means the route is missing), a 401 and both 403 shapes each get a sentence, no crash, and no retry |
| `events-completed-answered-503-not-repeated` | `DelunoReportsTests` | Deluno answers a finished hand-off's report 503 once, as recorded, then 202: Weir sends the same report again on its own heartbeat, and then it is no longer owed. The recording shows Weir rc.18 never sent it again; waiting for the heartbeat is a poll with a ceiling, and the schedule (a minute, doubling to an hour, `Retry-After`, a week) is held by `OwedReportRetryTests` on a fake clock |
| `events-answered-401-not-repeated`, `events-answered-403-not-repeated` | `DelunoReportsTests` | Deluno answers the report 401 or 403 as recorded: Weir stops owing it, says it could not tell Deluno, and sends it once |
| `events-superseded`, `events-completed-after-takeover` | `DelunoReportsTests` | A release sent twice while Weir is working the first: the first send is reported failed with `failureClass` superseded, the second finished with the output, with the members and exact words recorded |
| any of the above, changed | `DelunoRecordingComparisonTests` | A recording that stops matching fails naming its file and the JSON path |

A change Weir made on purpose since a recording is never made by editing it: the test pins it as an `ExpectedDifference` with the issue
number, and the pin fails once Weir answers what was recorded again.

| Recording | What Weir does now | Issue |
| --- | --- | --- |
| `outcome-imported-for-replaced-send` | Weir's reply to the import of a resent hand-off is about that hand-off only: `released` true and "Weir recorded that Deluno imported the file and released its copy." Recorded with rc.13 it carried the first send's refusal and kept its copy | #955 |
| `events-too-late` (second exchange) | A send Deluno replaced while Weir was working it is closed as cancelled and never reported finished; only the new send is reported (`events-completed-resent`, second exchange, which still matches). Recorded with rc.13 the replaced send was also reported completed, with no output of its own, and Deluno answered 409 | #956 |

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
- Recorded, with the replay not written yet: the two name collisions, the season pack, the partly cleaned season pack and the rejected
  release. They are listed, with the issue that will replay them, in `../deluno-side-awaiting-replay.json`; `node scripts/check-recorded-replays.mjs`
  fails when one is replayed and still listed.
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
