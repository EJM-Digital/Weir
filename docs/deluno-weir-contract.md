# The Deluno-Weir contract

Every call Deluno makes to Weir, every report Weir makes to Deluno, each reply, what Deluno does with it, and the
recording and test that prove the row (#1239). Weir's own tests replay Deluno's side; Deluno's tests replay Weir's
recorded replies. A change to either side's shape fails a test before it ships.

- **Deluno's requests** are recorded from the real Deluno (`tests/fixtures/recorded/deluno/<version>/<state>.json`, 1.0.0-rc.30.30),
  written down with their headers by a recording proxy between the real Deluno and the real Weir (`Start-RecordingProxy.ps1`,
  `Collect-DelunoSide.ps1`; the proxy was in front of Weir and in front of Deluno, and both connections were put back after). The same
  folder holds Weir's reports to Deluno and Deluno's replies to them.
- **Weir's replies** are recorded from the real Weir (`tests/fixtures/recorded/weir/<version>/<state>.json`, newest
  1.0.0-rc.13; how they are recorded: `tests/fixtures/recorded/README.md`). No test writes a Weir reply itself.
- **The tests** are in `tests/Deluno.Integrations.Tests/Processors/WeirContract/`:
  - `WeirContractClientTests`: the real `ProcessorConnectionService` reads each recording of Weir's replies.
  - `WeirRealExchangeTests`: the request Deluno really sent (hand-off, outcome, status poll, cancel, the two asks) is held
    against Deluno's own client, member for member and header for header, and Weir's recorded reply is read from it;
    Weir's real error replies (wrong secret, unknown id, refused body) are read by the client.
  - `WeirPlainTextFiveHundredTests`: the one reply that cannot be recorded (a plain-text 500); see `MISSING.md`.
  - `tests/Deluno.Persistence.Tests/Api/WeirRealReportsTests.cs`: Weir's real reports to Deluno, posted to the real events
    endpoint, answered as recorded.
  - `WeirContractBehaviourTests`: the real outcome delivery, hand-off cancellation and hand-off watcher act on what the
    client read; each test names what is stored, what Activity says and whether Deluno asks again.
  - `EveryWeirRecordingIsUsedByAContractTestTests`: the guard. It fails on a recording no contract test names, and on
    a recording with no row in this document.
- **Deluno's reading is by code, never by Weir's wording**: a 409 to an outcome is read by its `code` field. Weir's
  words are kept and shown, not parsed.
- **Authentication**: every call carries the connection's secret in `X-Webhook-Secret` (the header name is the
  connection's). A hand-off also carries `Idempotency-Key: <handoffId>:<row revision>`.
- **Outage** (Weir switched off, connection refused): no Weir reply exists; the tests make the connection fail
  (`Down`) and assert the same rows below as "unreachable". See `tests/fixtures/recorded/MISSING.md`.

Words in the tables: *Test* names a method in `WeirContractClientTests` (client) or `WeirContractBehaviourTests`
(behaviour). Request rows are held by `WeirRealExchangeTests` against the requests under `deluno/`.

## 1. Capabilities: `GET /api/v1/intake/capabilities`

Asked before a status poll, a cancel, an outcome and a library-folder read, and for the product, version and machine
name. Deluno never asks Weir a question the capabilities did not offer.

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `200` `{ capabilities[], machine_name, product, version }` listing status, cancel, outcome, outcome codes, library folders | `capabilities` | can ask status, can cancel, can be told the outcome, has library folders; product `Weir`; version; machine | asks and tells accordingly; stores the version | `Capabilities_with_the_secret_say_what_Weir_can_be_asked_and_told`, `Capabilities_name_Weir_its_version_and_its_machine` |
| `401` `{ detail }` (no secret sent) | `capabilities-no-secret` | Forbidden, can do nothing | status: hand-offs are not judged and the connection shows "refused the webhook secret"; outcome: stays owed | `A_refused_secret_on_the_capabilities_call_is_said_as_that_and_nothing_is_assumed` |
| `401` `{ detail }` (wrong secret) | `capabilities-wrong-secret` | same | status: Activity `processing.status-forbidden`, connection "degraded", hand-off kept; outcome: `processing.outcome-delayed`, owed, no outcome post sent | `A_refused_secret_on_the_capabilities_call_is_said_as_that_and_nothing_is_assumed`, `A_hand_off_is_not_given_up_while_Weir_refuses_the_secret_and_the_connection_says_so`, `An_outcome_sent_with_a_secret_Weir_refuses_stays_owed_and_names_the_fix` |
| connection refused | none (simulated) | Unreachable | hand-off kept, silence noted on the hand-off; outcome owed | `A_hand_off_is_not_given_up_while_Weir_is_switched_off` |

## 2. Library folders: `GET /api/v1/intake/library-folders`

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `200` `{ libraries[]: id, name, media_type, watched_folder, work_folder, output_folder, manager_library_key, folders_from_manager, minimum_file_size_bytes }` | `library-folders` | one entry per library: key, watched folder, output folder, folders-from-manager, minimum size | checks each library's folders against Weir's; a hand-off leaves out files under the minimum size | `Library_folders_list_each_library_with_its_folders_and_minimum_size` |

## 3. Hand-off: `POST /api/v1/intake/webhook/deluno`

Request body (pinned member by member, in order, against the recorded request): `eventType`, `handoffId`, `libraryId`,
`mediaType`, `sourcePath`, `releaseName`, `queueItemId`, `callbackPath`, then when Deluno knows them `title`, `year`,
`tmdbId`, `tvdbId`, `imdbId`, `season`, `episode`, `posterUrl`, then `sourceFiles` when the hand-off names files.

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `200` `{ status: "ok", source, event, media_scope, enqueued }` for a file | `handoff-accepted` | sent | hand-off recorded as submitted | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` for a file not in the watched folder yet | `handoff-accepted-missing-file` | sent | same; Weir's status then says what it found | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` for a release folder | `handoff-release-folder-accepted` | sent | same | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` for a release folder with `sourceFiles` | `handoff-source-files-accepted` | sent | same | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` for the same source sent again | `handoff-resend-accepted` | sent | same; the older send's own report is settled apart (section 7) | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` `{ status: "ignored", source }` (no source path in the body) | `handoff-ignored-no-source` | any 2xx is "sent" | recorded as submitted. Deluno never sends a body without a source path, which the request test pins | `A_2xx_that_says_ignored_is_read_as_sent_because_Deluno_always_names_a_source_path` |
| `400` `{ detail }` (no workflow watches the folder) | `handoff-refused-not-watched` | refused with Weir's `detail` | hand-off marked failed with "The processor refused the hand-off with HTTP 400: <Weir's words>" | `A_folder_no_workflow_watches_is_refused_with_Weirs_own_words` |
| `401` `{ detail }` (wrong secret) | `handoff-refused-wrong-secret` | refused with Weir's `detail` | same, HTTP 401 | `A_wrong_secret_on_a_hand_off_is_refused_with_Weirs_own_words` |
| connection refused | none (simulated) | could not reach | "Could not submit the hand-off. Check the processor connection"; sent again by the planner | `A_hand_off_with_Weir_switched_off_is_not_sent_and_says_to_check_the_connection` |

Request tests, for the recorded bodies of `handoff-accepted`, `handoff-accepted-missing-file`,
`handoff-release-folder-accepted`, `handoff-source-files-accepted`, `handoff-resend-accepted`,
`handoff-refused-not-watched`, `handoff-refused-wrong-secret` and `outcome-superseded`:
`The_hand_off_Deluno_sends_is_the_hand_off_Weir_was_recorded_receiving`;
headers: `A_hand_off_carries_the_secret_and_a_key_that_changes_when_the_row_is_put_back_to_waiting`.

## 4. Status: `GET /api/v1/intake/handoffs/deluno/{handoffId}`

Asked on every planning pass for each hand-off still with Weir. Body: `{ handoffId, state, queuePosition,
scheduledFor, lastChangedUtc, outputPath, outputFiles, message }`. All reads are held by
`A_status_is_read_as_Weir_said_it`.

| Weir answers | Recording | Deluno reads | Deluno does (stored, shown, Activity) | Test |
|---|---|---|---|---|
| `200` `queued` with `queuePosition` (12, 6) | `status-queued` | state, place in line | stored on the hand-off and shown as the place in line; waits; no Activity line | `A_hand_off_Weir_is_still_working_on_stays_open_and_the_screen_shows_what_Weir_said`, `A_queued_hand_off_says_its_place_in_line_and_a_completed_one_with_no_output_path_says_none` |
| `200` `working`, message "Weir has claimed this file and is checking it now." | `status-working`, `missing-file-status-working`, `release-folder-status-working`, `source-files-status-working` | state, message | stored and shown; waits; no stall until the library's limit | same |
| `200` `completed` with `outputPath` and `outputFiles` | `status-completed`, `completed-with-output-path`, `release-folder-status-completed`, `source-files-status-completed` | state, output path | stored and shown; waits for Weir's report or the output folder to bring the file in; never ended on the status alone | `A_hand_off_Weir_says_is_completed_stays_open_for_its_own_report_or_the_output_folder` |
| `200` `completed`, `outputPath` null (Weir had cleaned the file for an older send) | `completed-without-output-path`, `resend-status-completed` | state, no output path | stored; for a hand-off that replaced an earlier send Deluno matches the cleaned file it finds at once and queues the import (`processing.completed.import-queued`); otherwise waits | `A_resend_Weir_answers_completed_without_an_output_path_is_matched_to_the_cleaned_file_at_once`, `A_completed_answer_with_no_output_path_for_a_hand_off_that_replaced_nothing_waits_for_Weirs_report` |
| `200` `failed`, message (file under the minimum size) | `status-failed` | state, message | hand-off ends failed with "Weir reports failed for <release>: <Weir's message>"; Activity `processing.failed`; a case to clear is raised (`processor-failed`) | `A_hand_off_Weir_says_failed_ends_failed_in_Weirs_words_and_leaves_a_case_to_clear` |
| `200` `cancelled` | `status-cancelled`, `handoff-cancelled` (second call) | state | hand-off ends stopped, "Weir stopped work on <release>."; Activity `processing.cancelled` | `A_hand_off_Weir_says_it_cancelled_ends_stopped` |
| `404` `{ detail: "Weir has never received this hand-off." }` | `no-record` | not found | inside 15 minutes of the last change: nothing (not believed). After: hand-off ends lost, Activity `processing.lost`, case `processor-lost` raised | `A_hand_off_Weir_has_no_record_of_is_not_believed_inside_the_grace_window`, `A_hand_off_Weir_has_no_record_of_after_the_grace_window_is_lost_and_a_case_is_raised`, `A_hand_off_Weir_never_received_is_not_found` |
| status of a hand-off read after a refused cancel | `handoff-cancel-working`, `handoff-cancel-too-late`, `handoff-cancel-refused`, `outcome-superseded`, `outcome-superseded-newer` | as above | as above | `A_status_is_read_as_Weir_said_it` |
| `200` `completed`: a single-file source (qBittorrent) | `real-single-file-status-completed` | state, output path | as above. **`outputPath` is flat in the output folder (`<output>\<file>.mkv`), no folder named after the source** (#1262) | `Weirs_output_for_a_single_file_source_is_flat_and_for_a_release_folder_is_inside_a_folder_named_after_it` |
| `200` `completed`: a release folder (Transmission) and a Usenet job (SABnzbd; Deluno names the file inside the job folder) | `real-release-folder-status-completed`, `real-usenet-folder-status-completed` | state, output path | as above; the output keeps the folder: `<output>\<release>\<release>.mkv` | same |
| `200` `queued`, polled every minute while Weir is paused | `deluno/status-ask-queued` | state, place in line | waits | `The_status_poll_Deluno_sends_is_the_one_it_was_recorded_sending_and_a_queued_hand_off_is_read_as_queued` |
| `401` `{ detail: "Invalid or missing X-Webhook-Secret header." }` (wrong secret, or none) | `status-wrong-secret`, `status-no-secret` | Forbidden | as for the capabilities refusal | `A_status_poll_Weir_refuses_for_its_secret_is_forbidden_and_says_to_check_the_secret` |
| `404` `{ detail }` for an unknown or malformed id | `status-unknown-id`, `status-malformed-id` | not found | as `no-record` | `A_status_poll_for_an_id_Weir_does_not_know_is_not_found_whatever_the_id_looks_like` |
| `404` `{ detail: "Unknown media manager source 'x'." }`, checked before the secret | `status-unknown-source` | (Deluno never asks under another source) | none | `A_source_key_Weir_does_not_know_is_answered_404_before_the_secret_is_looked_at` |
| `403` `{ detail: "Set a webhook secret in Weir so a media manager can ask about hand-offs." }` (no connection of that source has a secret) | `status-no-connection-for-source` | Forbidden | as the 401 | `A_status_poll_under_a_source_Weir_has_no_connection_for_is_forbidden_with_Weirs_instruction` |
| connection refused | none (simulated) | Unreachable | hand-off kept; the observation records when Weir went quiet; never counted against the file | `A_status_asked_while_Weir_is_switched_off_is_unreachable_not_an_answer`, `A_hand_off_is_not_given_up_while_Weir_is_switched_off` |

## 5. Cancel: `DELETE /api/v1/intake/handoffs/deluno/{handoffId}`

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `204` (still queued) | `handoff-cancelled`, `handoff-cancelled-in-burst-204` | answered | hand-off ends stopped, "...and it was asked to drop <release>"; Activity `processing.cancelled` | `A_cancel_Weir_accepts_with_204_is_answered`, `A_cancel_Weir_accepts_ends_the_hand_off_as_stopped_and_says_Weir_was_asked_to_drop_it` |
| `409` `{ detail: "This hand-off is working, so Weir did not cancel it." }` | `handoff-cancel-working` | busy, with Weir's words | nothing changed; "Weir has started work ... Deluno will not interrupt it" (a removed title ends it anyway and its output is not imported) | `A_cancel_Weir_refuses_with_409_is_busy_and_carries_Weirs_words`, `A_cancel_Weir_refuses_leaves_the_hand_off_alone_and_says_why`, `A_cancel_Weir_refuses_for_a_title_that_was_removed_ends_the_hand_off_anyway_and_does_not_import_its_output` |
| `409` `{ detail: "...is completed, so Weir did not cancel it." }` | `handoff-cancel-too-late`, `handoff-cancel-refused` | busy, with Weir's words | same: nothing changed; the next status poll shows `completed` | same |
| `204` as Deluno sent it: "Stop waiting" on a hand-off Weir holds queued | `deluno/cancel-queued` | answered | as the row above | `A_cancel_of_a_queued_hand_off_is_the_DELETE_it_was_recorded_sending_and_the_204_is_answered` |
| `401` (wrong secret) | `cancel-wrong-secret` | Forbidden | nothing changed; the hand-off is not ended as dropped | `A_cancel_Weir_refuses_for_its_secret_is_forbidden` |
| `404` `{ detail }` (an id Weir never received) | `cancel-unknown-id` | not found | hand-off ends stopped, as if it were dropped | `A_cancel_for_a_hand_off_Weir_never_received_is_not_found` |
| `409` `{ detail: "This hand-off is completed, so Weir did not cancel it." }` (a hand-off finished long ago) | `cancel-completed` | busy, with Weir's words | nothing changed | `A_cancel_of_a_finished_hand_off_is_busy_with_Weirs_words` |
| `403` `{ detail: "Missing X-Requested-With header…" }` (an Origin header with no X-Requested-With on a POST or DELETE; Deluno sends no Origin) | `origin-without-requested-with` | Forbidden | not sent by Deluno; recorded so Weir's rule stays known | `A_change_with_an_Origin_and_no_X_Requested_With_is_refused_403_and_read_as_forbidden` |
| connection refused | none (simulated) | Unreachable | hand-off ends stopped and says "it could not be told, so it may still have <release>" | `A_cancel_while_Weir_is_switched_off_ends_the_hand_off_and_says_Weir_may_still_have_it` |

## 6. Outcome: `POST /api/v1/intake/handoffs/deluno/{handoffId}/outcome`

Request body (pinned against every recorded request): `outcome` (`imported` or `not-imported`), `occurredUtc` (when it
happened, the same on every attempt), `importedPath` (only for `imported`), `reason` (the note a season pack sends about
skipped files, or why it was not imported).

| Weir answers | Recording | Deluno reads | Deluno does (stored, Activity, asks again?) | Test |
|---|---|---|---|---|
| `200` `{ handoffId, outcome, released: true, message }` | `outcome-delivered-without-output-path`, `outcome-finished-in-burst-200` | answered, released | delivered, one attempt; `processing.outcome-reported` "... It released its copy."; never asked again | `An_outcome_Weir_acknowledges_with_200_is_answered_and_says_whether_it_let_go_of_its_copy`, `An_outcome_Weir_acknowledges_is_stored_delivered_and_said_in_Activity_and_never_asked_again` |
| `200` `released: false` (Weir's copy is the library file) | `outcome-delivered`, `outcome-already-recorded`, `outcome-superseded`, `outcome-superseded-newer` | answered, not released | delivered; Activity says Deluno told Weir it has the file and repeats Weir's own words | same |
| `200`, imported with a `reason` note | `outcome-imported-with-reason` | answered | delivered; Activity carries the note in brackets | same |
| `200`, `not-imported`, no reason / the default words / an import refusal | `outcome-not-imported-no-reason`, `outcome-not-imported-default-reason`, `outcome-not-imported-import-refused` | answered | delivered; Activity "Deluno told Weir it will not import <release>: <reason>"; Weir kept its copy | same |
| `409` `{ detail, code: "handoff_not_finished" }` (queued, working) | `outcome-handoff-not-finished-queued`, `outcome-handoff-not-finished-working` | busy | stays owed: pending, next attempt a minute later, then doubling to an hour; Activity `processing.outcome-delayed` said once; gives up after a week | `A_409_outcome_is_read_by_its_code_never_by_its_words`, `A_hand_off_Weir_has_not_finished_stays_owed_and_is_asked_again_in_a_minute`, `An_outcome_sent_before_and_during_Weirs_work_is_kept_and_retried_until_Weir_has_finished` |
| `409` `{ detail, code: "handoff_ended" }` (cancelled, failed) | `outcome-handoff-ended`, `outcome-handoff-ended-after-failed` | refused for good | refused, one attempt, never asked again; Activity `processing.outcome-not-reported` with Weir's words | `A_409_outcome_is_read_by_its_code_never_by_its_words`, `A_hand_off_Weir_says_has_ended_is_refused_for_good_and_Weirs_words_are_in_Activity` |
| `409` `{ detail, code: "outcome_already_recorded" }` | `outcome-outcome-already-recorded` | settled | settled, never asked again; Activity `processing.outcome-kept` "kept its earlier answer" | `A_409_outcome_is_read_by_its_code_never_by_its_words`, `An_answer_Weir_already_has_is_settled_and_Deluno_stops_telling_it` |
| `404` `{ detail }` | `no-record` | not found | "never received", not a failure, never asked again; Activity `processing.outcome-never-received` | `An_outcome_for_a_hand_off_Weir_never_received_is_not_found`, `A_hand_off_Weir_never_received_is_not_a_failure_and_is_not_asked_again` |
| `401` (wrong secret) | `outcome-wrong-secret` | Forbidden | stays owed; the connection is shown refused | `An_outcome_Weir_refuses_for_its_secret_is_forbidden_and_so_stays_owed` |
| `404` `{ detail: "Weir has never received this hand-off." }` | `outcome-unknown-id` | not found | as the row above | `An_outcome_for_a_hand_off_Weir_never_received_is_not_found` |
| `422` (unknown outcome word, missing `outcome`, a body that is not JSON; checked before the secret) and `415` (not declared JSON) `{ detail: [...] }` | `outcome-malformed` | refused | the report was wrong; never sent again (Deluno never sends such a body) | `An_outcome_body_Weir_refuses_is_a_refused_report_not_a_report_owed_again` |
| connection refused | none (simulated) | Unreachable | stays owed with "Deluno could not reach Weir."; `processing.outcome-delayed` | `An_outcome_sent_while_Weir_is_switched_off_is_unreachable_and_so_owed_again`, `An_outcome_sent_while_Weir_is_switched_off_stays_owed_and_says_it_could_not_be_told` |

A plain-text `500 Internal Server Error` (the only 5xx Weir can give, for an exception nothing handled) cannot be produced on
purpose, so it is not recorded; `WeirPlainTextFiveHundredTests` builds it, as a deliberate case (it needs no allow-list entry: the inline-reply check does not recognise a Weir reply), and pins that
Deluno reads it as "not now" on the status poll, the cancel and the outcome. `429` is never sent by Weir. See
`tests/fixtures/recorded/MISSING.md`.

## 7. Reports Weir sends to Deluno: `POST /api/integrations/processors/events`

Recorded by the proxy in front of the real Deluno during real hand-offs (`deluno/<version>/events-*`); each recorded body is
posted to the real endpoint by `WeirRealReportsTests` and must get the recorded answer. The documented replies and limits
(`docs/external-integration-api.md`, "Replies and limits") stay held by `ProcessorCallbackContractTests` and
`AProcessorIsNotTakenAtItsWordTests`; a late report for a superseded send by `ALateReportForARemovedSendIsSettledTests` and
`AResentReleaseSettlesItsOwnHandOffTests`.

| Weir reports | Recording | Deluno answers | Deluno does | Test |
|---|---|---|---|---|
| `completed` with `outputPath` and `outputFiles` (a single file; a release folder; a Usenet job) | `events-completed` | `202` `{ status, handoffId, activityId, importJobId }` | an import is queued | `A_finished_hand_off_Weir_reports_is_accepted_and_an_import_is_queued` |
| `completed` for a release that was sent again | `events-completed-resent` | `202` | the newer send settles from its own report | same |
| `completed` after the person stopped waiting (the hand-off ended dismissed) | `events-too-late` | `409` `{ message, handoffId, status: "dismissed" }` | no import is queued | `A_finished_hand_off_Weir_reports_after_the_person_stopped_waiting_is_a_409_and_queues_no_import` |
| `failed`, file under the minimum size (`disposition: "held"`) | `events-failed-under-minimum` | `202` `{ status: "failed", importJobId: null }` | nothing imported; the hand-off ends failed | `A_failed_hand_off_Weir_reports_is_accepted_and_nothing_is_imported` |
| `failed` with `failureClass: "source_gone"` (the download was deleted) | `events-failed-source-gone` | `202` `{ status: "failed", settled }` | the hand-off ends as source gone | same |
| `failed`, a file Weir cannot read (`disposition: "held"`) | `events-failed-unreadable` | `202` | nothing imported | same |

The report bodies carry `handoffId`, `status`, `processorName`, `libraryId`, `releaseName`, `message`, then `outputPath` and
`outputFiles` (a finished file), or `disposition`, `sourceRemoved`, `failureClass` (a failure). Authentication is Weir's Deluno
API key, which the proxy forwards and never writes down. Not recorded, and listed in `MISSING.md`: `accepted`, `started` and
`rejected` reports, `not_taken`, and a report for a hand-off Deluno has no row for at all.

## 8. Deluno's side as fixtures Weir can load

`tests/fixtures/recorded/deluno/1.0.0-rc.30.30/` is the real Deluno's side of the talk, recorded by the proxy: its requests (with
headers: `x-webhook-secret` scrubbed, `idempotency-key`, `content-type`) and Weir's replies. Weir's own tests can load it.

| Recording | What it holds | Test |
|---|---|---|
| `handoff-single-file`, `handoff-release-folder`, `handoff-usenet-folder`, `handoff-resend` | the hand-off Deluno sends for a single file, a release folder, a Usenet job, and a release sent again (a new id and key for the same source) | `The_request_Deluno_sends_for_a_hand_off_is_the_request_it_was_recorded_sending`, `A_resend_carries_the_same_source_under_a_new_hand_off_id_and_a_new_key` |
| `outcome-imported`, `outcome-imported-for-replaced-send`, `outcome-not-imported-removed` | the outcome Deluno tells (importedPath, reason) and Weir's `200` | `The_outcome_Deluno_tells_is_the_outcome_it_was_recorded_telling` |
| `status-ask-queued`, `cancel-queued` | the status poll and the cancel | the status and cancel tests of `WeirRealExchangeTests` |
| `capabilities-ask`, `library-folders-ask` | the two asks | `The_capabilities_ask_Deluno_sends_is_the_one_it_was_recorded_sending`, `The_library_folders_ask_Deluno_sends_is_the_one_it_was_recorded_sending` |
| `events-*` | Weir's reports and Deluno's answers (section 7) | `WeirRealReportsTests` |

The older requests inside `weir/<version>/*.json` were sent by `Record-Weir.ps1` and `Record-WeirStates.ps1` from the documented
shapes. They are no longer what Deluno's requests are held against; they remain only as the requests that produced Weir's replies.
