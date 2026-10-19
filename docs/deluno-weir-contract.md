# The Deluno-Weir contract

Every call Deluno makes to Weir, every report Weir makes to Deluno, each reply, what Deluno does with it, and the
recording and test that prove the row (#1239). Weir's own tests replay Deluno's side; Deluno's tests replay Weir's
recorded replies. A change to either side's shape fails a test before it ships.

- **Deluno's requests** are recorded from the real Deluno (`tests/fixtures/recorded/deluno/<version>/<state>.json`, 1.0.0-rc.30.30),
  written down with their headers by a recording proxy between the real Deluno and the real Weir (`Start-RecordingProxy.ps1`,
  `Collect-DelunoSide.ps1`; the proxy was in front of Weir and in front of Deluno, and both connections were put back after). The same
  folder holds Weir's reports to Deluno and Deluno's replies to them.
- **Weir's replies** are recorded from the real Weir (`tests/fixtures/recorded/weir/<version>/<state>.json`, newest
  1.0.0-rc.18, with rc.13 kept where rc.18 holds no recording of a state; how they are recorded: `tests/fixtures/recorded/README.md`). No test writes a Weir reply itself.
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
| `422` (unknown outcome word, missing `outcome`, a body that is not JSON; checked before the secret) `{ detail: [...] }`, and `415` (not declared JSON) `{ detail: "Send the body as application/json." }`, a sentence, not a list | `outcome-malformed` | refused | the report was wrong; never sent again (Deluno never sends such a body) | `An_outcome_body_Weir_refuses_is_a_refused_report_not_a_report_owed_again` |
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
| `completed` after Deluno stopped waiting for another reason (the hand-off ended dismissed, not by a removal) | `events-too-late` | `409` `{ message, handoffId, status: "dismissed" }` | no import is queued | `A_finished_hand_off_Weir_reports_after_the_person_stopped_waiting_is_a_409_and_queues_no_import` |
| `completed` or `failed` for a hand-off Deluno closed itself by removing the download from its client, moving it to keep seeding, or on "stop tracking" (dismissed, with that reason), whether or not the release is sent again later | `events-completed` (a body the same as any finished report) | `202` `{ status: "superseded", handoffId, alreadyHandled: true }`; nothing is imported, blocklisted or searched; the hand-off stays dismissed (#1242: CI run 39104138771, the resend came six seconds after the report) | none | `A_finished_report_for_a_send_whose_download_Deluno_removed_is_settled_before_any_resend_exists`, `A_rejection_Weir_reports_for_a_send_whose_download_Deluno_removed_is_settled_and_blocks_nothing` |
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

## 9. Deluno's replies to the calls Weir makes

Weir reads four routes of Deluno with its Deluno API key (`X-Api-Key`). Deluno's reply to each is recorded from the server code
(the whole application over HTTP, as `ApplicationTestHost` composes it: the same two composition calls the shipped hosts make), not from a
proxy: `tests/fixtures/recorded/deluno/1.0.0-rc.30.32/reply-*.json`. `DelunoRepliesToWeirTests` runs the same calls again for every
recording and holds Deluno's live reply to it member by member: the status, the `content-type` and `location` headers, and every member of
the body, in order, with its type and its words. A reply that changes shape fails there, before Weir meets it. Only what no run can repeat
is replaced in a recording: a generated id (`<id>`, or `<handoff:label>` / `<library:label>` for one the scenario made), a time (`<utc>`),
the test's data folder (`<data-root>`, with forward slashes) and the key (a name for its scopes). To re-record on purpose, run
`DelunoRepliesToWeirTests` with `DELUNO_RECORD_REPLIES=1`, run `node scripts/check-recorded-manifest.mjs --write`, and tell the Weir session.
A reply Deluno's code takes from Weir (the answer to "send again") is Weir's own recording (`weir/1.0.0-rc.13/outcome-*`) served by a
stand-in, never written here.

The routes: `GET /api/integrations/processors/handoffs[?libraryId=&take=]`, `GET /api/integrations/processors/download-destinations[?libraryId=]`,
`POST /api/integrations/processors/handoffs/{id}/import-original` and `POST /api/integrations/processors/handoffs/{id}/outcome/send-again`.

| Deluno answers | Recording | Test |
|---|---|---|
| `200 []` on a Deluno with no hand-off, for every library and for one | `reply-handoffs-empty` | `Deluno_still_answers_what_it_was_recorded_answering` |
| `200` a list of hand-offs (`id`, `libraryId`, `mediaType`, `clientId`, `queueItemId`, `releaseName`, `sourcePath`, `processorName`, `status`, `outputPath`, `importJobId`, `failureMessage`, `createdUtc`, `updatedUtc`, `processor`, `outcome`, `processorConnectionId`, `sourceFiles`), newest change first, filtered by `libraryId`, limited by `take` | `reply-handoffs-listed` | same |
| `200` hand-offs carrying `outcome` in each delivery state (`pending`, `delivered`, `refused`, `given-up`, `never-received`, `settled`, `replaced`) | `reply-handoffs-with-outcomes` | same |
| `200 { libraries: [] }`, and `404 { error: "library_not_found", message: "Deluno has no library with the id <id>." }` for a `libraryId` Deluno does not have (a `404` with no body means only that the route is missing, #1267) | `reply-download-destinations-empty` | same |
| `200 { libraries[]: libraryId, libraryName, mediaType, downloadsPath, processorOutputPath, importWorkflow, destinations[], processorConnection }`, with the processor connection and its `pathMappings` for a library that hands to a processor, `null` for one that does not; `404 { error: "library_not_found", message }` for an unknown library | `reply-download-destinations-libraries` | same |
| `202` with the hand-off as it now stands (status `released`) and a `location` of the library's hand-off list, for a hand-off that is `failed`, `timed-out`, `stalled`, `lost` or `cancelled` | `reply-import-original-accepted` | same |
| `409 { message }` ("The original can only be imported once the processor has stopped working on it...") for `waiting`, `submitted`, `accepted`, `started`, `completed`, `rejected`, `released` and `dismissed` | `reply-import-original-not-yet` | same |
| `404 { error: "handoff_not_found", message: "Deluno has no hand-off with the id <id>." }` for a hand-off Deluno has no record of | `reply-import-original-unknown` | same |
| `404 { error: "nothing_recorded", message }` ("Deluno has nothing recorded to tell the processor...") for a hand-off with no outcome recorded, and for an unknown one | `reply-send-again-nothing-recorded` | same |
| `200 { message, outcome }`, the outcome given up because the processor connection was removed | `reply-send-again-connection-removed` | same |
| `200 { message, outcome }`, Weir acknowledged it and kept its copy (Weir `200`, `released: false`) | `reply-send-again-delivered` | same |
| `200 { message, outcome }`, Weir acknowledged it and let go of its copy (Weir `200`, `released: true`) | `reply-send-again-delivered-released` | same |
| `200`, Weir says it holds an answer (Weir `409 outcome_already_recorded`): settled | `reply-send-again-weir-has-its-answer` | same |
| `200`, Weir says the hand-off ended (Weir `409 handoff_ended`): refused for good | `reply-send-again-weir-hand-off-ended` | same |
| `200`, Weir has not finished (Weir `409 handoff_not_finished`): still owed, `nextAttemptUtc` set | `reply-send-again-weir-not-finished` | same |
| `200`, Weir never received the hand-off (Weir `404`): `never-received` | `reply-send-again-weir-never-received` | same |
| `200`, Weir refuses the secret (Weir `401` on its capabilities): still owed | `reply-send-again-weir-refuses-secret` | same |
| `200`, Weir cannot be reached: still owed | `reply-send-again-weir-unreachable` | same |
| `401` (no body) on each of the four routes, with no credential | `reply-no-sign-in` | same |
| a key with only the `read` scope: `403 { message }` on the two reads (the route's own scope check, "This API key does not include the required scope: imports.") and `403 { error, required, granted }` on the two writes (the authorization policy) | `reply-key-without-imports-scope` | same |
| a key with only the `imports` scope: the two reads answer `200` and the two writes pass the scope check (`404` for an unknown id) | `reply-key-with-imports-scope-only` | same |

**The scopes, per route (#1265).** A `GET` asks the authorization policy for `read`, except the two reads Weir makes, which the policy also opens to `imports`; each route then checks `imports` itself:

| Route | Scopes that pass |
|---|---|
| `GET /api/integrations/processors/handoffs` | `imports` |
| `GET /api/integrations/processors/download-destinations` | `imports` |
| `POST /api/integrations/processors/handoffs/{id}/import-original` | `imports` |
| `POST /api/integrations/processors/handoffs/{id}/outcome/send-again` | `imports` |

A key with `imports` alone is the key for Weir's work. A key with `read` alone is refused on all four: the route checks `imports` itself after the policy. The opening of the two reads is metadata on those two routes (`DelunoImportsScopeMayReadAttribute`), never a wildcard; `AnImportsKeyReadsOnlyTheHandOffRoutesTests` walks every `GET` route in the endpoint map and fails if an `imports`-only key gets through anywhere else.

Guards: `EveryWeirRecordingIsUsedByAContractTestTests` fails on a recording no test names or this document does not list;
`DelunoRepliesToWeirTests.Every_scenario_has_its_recording_and_every_reply_recording_has_its_scenario` fails on a scenario with no recording
and a recording with no scenario.

### The manifest Weir checks

`tests/fixtures/recorded/deluno/MANIFEST.sha256` lists a SHA-256 for every file under `tests/fixtures/recorded/deluno/<version>/` (the
line is `<hash>  <version>/<state>.json`, hashed with line endings read as LF so a Windows and a Linux checkout agree).
`scripts/check-recorded-manifest.mjs` fails when a recording changes, appears or goes without the manifest being rewritten
(`npm run ci:check` runs it, and `scripts/check-recorded-manifest.test.mjs` tests it). The Weir session copies
`tests/fixtures/recorded/deluno/` with its manifest, runs `node check-recorded-manifest.mjs --root <copy> --manifest <copy>/MANIFEST.sha256`
(the script has no dependencies), and so knows when a copy has fallen behind Deluno's.

### Reports Weir sends that are still built inline

The reports in section 7 are posted from the recordings by `WeirRealReportsTests`, and also by `ProcessorCallbackContractTests`,
`AProcessorIsNotTakenAtItsWordTests`, `ALateReportForARemovedSendIsSettledTests` and `AResentReleaseSettlesItsOwnHandOffTests` (through
`RecordedWeirReports`) wherever a recording matches the case. A case with no real Weir report stays inline and is listed with the Weir
state that would produce it in `tests/fixtures/recorded/MISSING.md` ("Reports Weir sends Deluno that the tests still build inline").

## 10. Weir 1.0.0-rc.18 (#1239, #1262, #1266)

Recorded on the golden VM on 15 October 2026 (Deluno 1.0.0-rc.30.32, Weir 1.0.0-rc.18) in one run of
`scripts/recorded-replies/Invoke-RecordReplies.ps1 -Apps weir -Extra weir-states,weir-reports,weir-errors -IncludeBaseWeir`. A state recorded by
rc.13 and rc.18 alike is read from rc.18 (the helpers take the newest version that holds it); a state only rc.13 holds (`completed-without-output-path`,
the `real-*` layouts, the burst and wrong-secret states) stays readable from rc.13. The states below are the ones rc.18 added; the tests that read
each are `WeirContractClientTests` (`A_status_is_read_as_Weir_said_it`, `A_file_sent_again_and_a_name_that_is_taken_are_read_as_Weir_recorded_them`,
`A_status_that_is_rejected_is_a_state_Deluno_reads_by_its_word_and_keeps_Weirs_message`), `WeirContractBehaviourTests` (the watcher rows) and
`WeirBusyRetryAfterTests`.

What the run showed about Weir that differs from rc.13:

- **A resend's finished status carries its output path.** `resend-status-completed`, `completed-after-resend` and `resend-takeover-second-final-status`
  all name `outputPath`. rc.13 answered a resend's finished status without one (`completed-without-output-path`, kept); the "rc.14 clears the path" reading was
  not seen from rc.18. Deluno handles both (`ProcessorHandoffWatcher`, status row "`completed`, `outputPath` null").
- **A file sent twice at once**: the first send ends `cancelled` ("This release was sent again, so Weir is answering the newer send and has nothing to add here.") and
  its report is `failed` with `failureClass: "superseded"` (`events-superseded`); the second finishes `completed` with its output path (`events-completed-after-takeover`).
- **A name already taken in the output**: with "replace" the second file lands on the same output path; with "keep both" it is `stem (2).ext` beside the first
  (Weir numbers from 2, not 1).
- **Weir's status has a `rejected` state** (`failed-rejected-status-rejected`): an audio-only file under the reject failure policy. Deluno ends the hand-off failed in Weir's words.
- **A 503 on a finished report was not repeated by Weir in 300 seconds** (`events-completed-answered-503-not-repeated`), and neither were a 401 or a 403
  (`events-answered-401-not-repeated`, `events-answered-403-not-repeated`, nothing more arrived in 130 seconds).

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `200` `queued` with a place in line (8, 2) | `status-queued` | state, place in line | as in section 4 | `A_hand_off_Weir_is_still_working_on_stays_open_and_the_screen_shows_what_Weir_said` |
| `200` `queued` for a hand-off that names its files | `source-files-status-queued` | state, place in line (2), no message | waits | same |
| `200` `queued` with the settle-time message ("This file changed too recently. Weir waits 60s ...") | `failed-rejected-status-queued`, `failed-partial-season-pack-corrupt-status-queued` | state, message | stored and shown; waits; ends nothing | `A_queued_hand_off_Weir_is_letting_settle_stays_open_and_ends_nothing`, `A_queued_season_pack_Weir_is_letting_settle_stays_open_and_ends_nothing` |
| `200` `working` for a resend, a taken-over send, a season pack and a pack with a bad episode | `resend-status-working`, `resend-takeover-second-status-working`, `completed-season-pack-status-working`, `failed-partial-season-pack-corrupt-status-working` | state, message | stored and shown; waits | same as `working` in section 4 |
| `200` `completed` with an output path for a taken-over send, both collision policies, and a season-pack folder | `resend-takeover-second-status-completed`, `resend-takeover-second-final-status`, `output-collision-replace-first-status`, `output-collision-replace-second-status`, `output-collision-keep-both-first-status`, `output-collision-keep-both-second-status`, `completed-season-pack-status-completed` | state, output path | stored and shown; waits for Weir's report or the output folder | `A_hand_off_Weir_says_is_completed_stays_open_for_its_own_report_or_the_output_folder` |
| the two sends of one source: the older `cancelled` with Weir's words, the newer `completed` with its path | `completed-after-resend`, `resend-takeover-first-status` | state | the older hand-off ends stopped ("Weir stopped work on ..."); Activity `processing.cancelled` | `A_hand_off_Weir_says_it_cancelled_ends_stopped`, `The_older_send_of_a_file_sent_again_is_cancelled_by_Weir_and_ends_stopped` |
| `200` `rejected`, message "Weir only processes movie and TV files that contain a video stream ..." | `failed-rejected-status-rejected` | state `rejected`, message | the hand-off ends failed ("Weir reports rejected for <release>: <Weir's message>"); Activity `processing.failed`; a case to clear (`processor-failed`) | `A_hand_off_Weir_says_it_rejected_ends_failed_in_Weirs_words_and_leaves_a_case_to_clear` |
| `200` for a hand-off sent again while Weir held the first (the second send is accepted, the body is `{}`) | `resend-takeover-accepted` | accepted | submitted | `A_file_sent_again_and_a_name_that_is_taken_are_read_as_Weir_recorded_them` |
| `409` `{ code: "handoff_not_finished" }` to an outcome for a hand-off still queued | `outcome-handoff-not-finished` | busy (by the code) | owed again in a minute | `A_409_outcome_is_read_by_its_code_never_by_its_words` |
| `503` `{ detail, code: "database_busy" }` with `Retry-After: 1` (another program held Weir's database write lock longer than its busy timeout, set to one second on a second Weir made for this) | `database-busy-outcome` | unreachable, `RetryAfter` 1 s | held back for that time (plus at most a tenth), then asked again; an outcome report is owed again after it | `WeirBusyRetryAfterTests` |
| `404` `{ detail: "Weir has never received this hand-off." }` for the same report sent again once the lock was gone | `database-busy-retried` | not found | the ordinary answer for a hand-off Weir has no record of | `The_same_report_sent_again_after_the_lock_is_gone_is_an_ordinary_unknown_hand_off` |
| `500` `{ detail, code: "internal_error" }` (a table the route reads is missing; second Weir) | `internal-error-outcome` | unreachable, no wait asked | retryable like the plain-text 500 | `Weirs_JSON_500_internal_error_is_retryable_like_the_plain_text_one_and_asks_for_no_wait` |

### Reports Weir 1.0.0-rc.18 sends (`deluno/1.0.0-rc.30.32/events-*`)

Weir made these hand-offs straight to its own intake, so the real Deluno behind the recording proxy had no row for them and answered `409`
(`events-superseded`, `events-completed-after-takeover`, the collision and season-pack states); where a recording's reply says `202` or `403` or `503`, the proxy answered it
itself and the recording's note says so. `WeirRealReportsTests` posts each body to a Deluno that holds the hand-off (and to one that holds none, for the 409).

| Weir reports | Recording | Deluno answers | Deluno does | Test |
|---|---|---|---|---|
| `failed`, `failureClass: "superseded"`, "This release was sent again ..." (the older of two sends) | `events-superseded` | `202` failed (a hand-off held); `409` with no row | the older hand-off ends failed; the newer one is settled by its own report | `A_failure_Weir_reports_for_a_dropped_or_partly_unreadable_hand_off_is_accepted_as_failed` |
| `completed` for the send that took over | `events-completed-after-takeover` | `202` with an import; `409` with no row | imported from the path named | `A_single_file_Weir_finished_under_a_taken_name_is_accepted_and_imported_from_where_it_put_it` |
| `completed` under "replace", twice, one output path | `events-completed-collision-replace-first`, `events-completed-collision-replace-second` | same | same | same |
| `completed` under "keep both": `stem.ext`, then `stem (2).ext` | `events-completed-collision-keep-both-first`, `events-completed-collision-keep-both-second` | same | same | same |
| `failed`, `disposition: "rejected"`, `failureClass: "preflight"`, no output (an audio-only file under Weir's reject policy; Weir left the file) | `events-failed-rejected` | `202` `{ status: "failed", importJobId: null }` when Deluno finds the download in a client (the proxy's own answer here; real Deluno said 409 for want of a row); `409` "could not find the download ... so it could not refuse that release" when it cannot | with the download: the release is blocked ("Weir refused it", Deluno will remove it) and a regrab is searched; without it: nothing is refused and the hand-off stays for a person | `A_rejection_says_who_removed_the_download`, `A_rejection_Weir_reports_for_a_download_Deluno_cannot_find_is_a_409_that_says_so`, `A_failed_report_for_a_removed_send_that_was_asked_for_again_is_settled_too` |
| `completed` for a season-pack folder of two episodes, `outputFiles` both | `events-completed-season-pack` | `202` with one pack import; `409` with no row | one import of the folder | `A_season_pack_Weir_finished_is_accepted_and_imported_as_one_pack_from_its_folder`, `A_folder_hand_off_with_several_output_files_imports_as_one_pack` |
| `failed`, `disposition: "held"`, `outputFiles` the two good episodes of three, no `outputPath` | `events-failed-partial-season-pack`, `events-failed-partial-season-pack-corrupt` (the same body) | `202` failed with the two files imported; `409` with no row | what succeeded is imported; the hand-off ends failed with the count | `A_partial_failure_imports_what_succeeded_and_reports_the_count`, `A_main_video_the_processor_failed_still_fails_the_hand_off` |
| `completed`, answered `503` by the proxy; nothing more arrived in 300 s | `events-completed-answered-503-not-repeated` | (not answered by Deluno) | the report is owed; sent again, it imports once | `A_completed_report_Deluno_did_not_answer_is_imported_once_whenever_it_is_sent_again` |
| `completed`, answered `401` by the proxy; nothing more in 130 s | `events-answered-401-not-repeated` | `401` (Deluno's own, `reply-no-sign-in`) | Weir does not send it again | `A_report_Deluno_refuses_for_its_credential_is_not_sent_again_and_the_refusal_is_Delunos_own` |
| `completed`, answered `403` by the proxy; nothing more in 130 s | `events-answered-403-not-repeated` | `403` (Deluno's own, `reply-key-without-imports-scope`) | same | same |

## 11. Recorded in Docker, Deluno 1.0.0-rc.30.33 and Weir 1.0.0-rc.18 (#1237, #1239)

The rig is retiring, so these were recorded from the published images running in Docker (`scripts/recorded-replies/docker/`: `weir.mjs`, `flows.mjs`,
`proxy.mjs`, `releases.mjs`, on the stack of `tests/e2e/compose.yaml` under a project name of its own). `flows.mjs` puts a recording proxy between the real
Deluno and the real Weir in both directions and drives Deluno the way a person does (a release sent to qBittorrent through Deluno, the download finishes,
Deluno hands the file to Weir), so the requests below are what Deluno really sent, not a body written for a test. `weir.mjs` asks Weir's own API. A state
recorded for Weir lives in `weir/1.0.0-rc.18/`, a state recorded for Deluno in `deluno/1.0.0-rc.30.33/` (with its manifest line).

What the run showed about Weir:

- **The source key is the kind, not the connection.** A second media-manager connection of kind `deluno` answers on the same address
  (`/api/v1/intake/webhook/deluno`); Weir tells the connections apart by their webhook secret. A status poll made with the secret of a different connection
  than the one that sent the hand-off is `401`, the same words as a wrong secret (`status-other-connections-secret`).
- **A second connection's send of a file the first connection's pass owns is cancelled at once**, with Weir's words, and Weir reports it `failed` with
  `failureClass: "not_taken"` to the second connection's Deluno (`status-not-taken`, `events-failed-not-taken`). Deluno accepts that report (`202`,
  `willRetry`, `retryAfterMinutes: 2`) and sends the file again.
- **`scheduled` is not "outside the processing hours".** With a weekly grid that has no open hour the hand-off stays `queued`; Weir says `scheduled`, with
  `scheduledFor`, when a hand-off waits on a file it holds because it cannot read it to the end (`status-scheduled-file-on-hold`).
- **A hand-off to a source with no Deluno connection left** is `401` "No Deluno is set up in Weir ..." and the status poll `403` "Set a webhook secret in Weir ..."
  (`handoff-refused-deluno-source-no-secret`, `status-deluno-source-no-secret`).
- **`/openapi.json`** answers without a secret; `info.version` is Weir's version (`openapi`, shortened to the identity of the document).

| Weir answers | Recording | Deluno reads | Deluno does | Test |
|---|---|---|---|---|
| `200` for a film that stops before its end; `200` again for the same file while Weir holds it | `handoff-accepted-cut-off-file`, `handoff-accepted-again-while-on-hold` | sent | recorded as submitted | `An_accepted_hand_off_is_recorded_as_submitted` |
| `200` `scheduled`, `queuePosition` null, `scheduledFor` the end of the hold, Weir's words | `status-scheduled-file-on-hold` | state `scheduled`, the time, the message | stored and shown; waits | `A_hand_off_Weir_schedules_is_read_with_the_time_it_will_next_be_looked_at_and_Weirs_words`, `A_status_is_read_in_full` |
| `200` `cancelled` "Weir is already working on this file for another connection ..." (polled with the second connection's secret) | `status-not-taken` | state `cancelled`, the message | as `cancelled` in section 4 | `A_status_is_read_as_Weir_said_it` |
| `401` for the secret of a different connection than the sender's | `status-other-connections-secret` | Forbidden | as for a wrong secret | `A_status_poll_Weir_refuses_for_its_secret_is_forbidden_and_says_to_check_the_secret` |
| `403` "Set a webhook secret in Weir ..." on Deluno's own source once no connection has a secret (with the old secret and with none) | `status-deluno-source-no-secret` | Forbidden | as the 401 | `A_status_poll_on_Deluno_own_source_once_no_connection_has_a_secret_is_forbidden_with_Weirs_instruction`, `A_refused_question_is_said_as_the_setting_to_change` |
| `401` "No Deluno is set up in Weir ..." to a hand-off | `handoff-refused-deluno-source-no-secret` | refused with Weir's `detail` | the hand-off fails with HTTP 401 and Weir's words | `A_hand_off_to_a_source_with_no_connection_that_has_a_secret_is_refused_with_Weirs_instruction` |
| `GET /openapi.json`, no secret: `{ openapi, info: { version }, servers }` | `openapi` | the version, when the capabilities answer cannot be had | the connection shows Weir's version | `When_the_capabilities_answer_cannot_be_had_the_version_comes_from_the_processors_api_description` |

Deluno's side, recorded through the proxy (the request tests are `The_request_Deluno_sends_for_a_hand_off_is_the_request_it_was_recorded_sending`; Deluno's client
sends the same call for the same hand-off, member for member and header for header, and Weir's reply is what it then reads):

| Recording | What it holds | Test |
|---|---|---|
| `handoff-collision-replace`, `handoff-collision-keep-both` | the hand-off for a single file whose name is already in Weir's output from an earlier, different file; Weir finished it at the same path ("replace") or numbered beside the first (`stem (2).ext`, "keep both") | the request tests |
| `handoff-season-pack`, `handoff-partial-season-pack` | the hand-off for a TV release folder of two episodes, and of three where one cannot be read (failure policy hold); `mediaType: "tv"` and `sourcePath` the folder | the request tests |
| `handoff-rejected-release` | the hand-off for an audio-only film under Weir's reject policy | the request tests |
| `handoff-second-connection` | the hand-off through a second processor connection to the same Weir for a file the first connection owns; Weir accepts the send with a `200` (it cancels it afterwards) | the request tests |
| `events-failed-not-taken` | Weir's report of that send as `failed`, `failureClass: "not_taken"`, no disposition, `sourceRemoved: false`, and Deluno's real reply `202` `{ status: "failed", handoffId, activityId, willRetry: true, retryAfterMinutes: 2 }` | `A_hand_off_Weir_did_not_take_because_another_connection_owns_the_file_is_accepted_and_will_be_sent_again`, and `AHandOffWeirDidNotTakeBecauseAnotherConnectionOwnsTheFileTests` post this body |
| `events-failed-rejected-known-handoff` | Weir's report for the audio-only film, and Deluno's real reply for a hand-off it holds: `202` `{ status: "rejected", handoffId, activityId, blockedReleaseId, searchJobId }` | `The_report_for_a_rejected_release_and_the_reply_Deluno_gave_it_are_what_the_rejection_tests_replay`, `A_rejection_says_who_removed_the_download` |
