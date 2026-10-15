# Weir operator messaging standard

Weir activity, status, and job messages are for the person operating the app. They must explain what happened in plain language without requiring knowledge of queue keys, worker names, ffmpeg internals, or raw provider payloads.

## Severity

- `info`: normal successful work, no-op/skipped work, and expected empty results.
- `warning`: recoverable provider or environment issue where Weir can continue or retry, but the operator may need to know.
- `error`: a workflow failed and needs operator attention before the expected outcome can be trusted.
- `debug`: implementation detail for runtime logs only, not the primary Activity message.

Normal work must not be logged or displayed as a warning. Provider-level recoverable failures must identify the provider and show that the rest of the workflow continued.

Catching up after Weir was not running, or the computer was asleep, saved, paused or frozen, is normal work: a person has nothing to do about it, so it is `info`, worded plainly (how long, what is queued). Work that comes late while Weir was running is a real fault and stays a `warning`. This covers scheduled scans coming late and a job's lease running out (the job is picked up again). Tell the two apart with `ProcessPulse` (`Weir.Infrastructure/Runtime`), which notes whether anything in the process ran during the delay; never with a monotonic clock, which counts a suspended machine on Windows and not on Linux.

## Required fields

Every operational Activity detail should include stable fields when applicable:

- `module`: `processing`, `auth`, or `system`.
- `action`: plain workflow action such as `scan`, `preview`, `remux`, `cleanup`, `handback`, `connection_test`, or `update`.
- `trigger`: why the work happened now — `manual` (a person asked), `scheduled`, `webhook` (a media manager sent it), `folder_change` (a new file appeared in a watched folder), `retry` (an automatic retry came due), `startup`, `worker` (the worker following up on earlier work, such as handing a file back), or `system`. It is set where the work is queued and carried on the job payload as `trigger`, with `run_id` for work that belongs to a larger run such as a folder scan, so the events it produces can say why and be grouped.
- `result`: `success`, `skipped`, `warning`, `retrying`, `running`, or `failed`.
- `severity`: derived from `result`; do not hand-pick a scarier severity.
- `provider`: media manager name when relevant, for example `Sonarr`, `Radarr`, or `Deluno`.
- `media_scope`: machine-stable scope such as `tv` or `movies`.
- `counts`: numeric result counts such as `checked`, `queued`, `skipped`, `failed`, `audio_removed`, or `subtitles_removed`.
- `user_message`: one plain-language sentence suitable for the UI.
- `next_action`: only when the operator can do something useful.

## Failure Messages

Failures must use the shared failure helper, `FailureMessages` in `Weir.Core/Observability` (`WorkerFailures.JobFailure` in `Weir.Core/Jobs` for job failures), before they reach job `last_error`, Activity detail, or provider result arrays.

- Say what failed with module and action, for example `Processing file remux pass`.
- Say where it failed when known, for example provider, server, or media scope.
- Classify why it failed as `rate_limit`, `credential`, `auth`, `network`, `filesystem`, `validation`, `not_found`, or `internal`.
- Say what happens next: skipped and continued, will retry, or marked failed.
- Add `next_action` for credential, auth, network, filesystem, validation, and rate-limit cases.
- Keep raw stack traces and exception internals in structured logs, not primary UI text.
- A failure of ffprobe, ffmpeg or mkvmerge is worded through `ToolFailureText` in `Weir.Core/Media` wherever a person or a media manager reads it: Activity titles and reasons, the report to a manager, Logs headlines, notices and API errors. The tool's own text (stderr, memory addresses, file paths) goes in `technical_detail` on the Activity detail, or in the exception a log line carries.
- A file Weir cannot read (ffprobe cannot parse it, finds no streams in it, or it will not read from start to finish) may only be still arriving, so it waits and is looked at again (`RemuxPassHandler.UnreadableWaitMinutes`). Once it has stayed unreadable and unchanged through those looks it is refused before any output is written, as a file with no video is: the workflow's reject or leave choice applies, a linked workflow's original is left in place, and pass-through never hands it on. Weir never deletes it as a rejected file, whatever the workflow's "delete rejected files" choice says.
- An API error answer's `detail` follows the same rules: one plain sentence that says what happened and what to do, with nothing of the failure in it (no exception, table, file path or stack trace). Where a client must act on which error it got, the answer also carries a `code` (`database_busy`, `internal_error`, the outcome route's 409 codes), and clients read that, never the wording. A busy database is a 503 with a `Retry-After` taken from the time the write really waited; an unforeseen failure is a 500 with `internal_error`. See the API reference in `docs-site/docs/api/index.md`.

## Wording

- Use product names and user concepts: `Movies`, `TV episodes`, `connection test`, `scan`, `preview`, `processed file`, `passed through`, `handed back`.
- Avoid engineering jargon in titles: no queue kind, durable job kind, raw exception class, HTTP stack trace, `remux` as the leading user-facing noun, or vague copy such as `this tab`.
- Automated or destructive work must be explicit: `scheduled`, `automatic`, `removed`, `deleted`, `skipped`, and counts must be visible.
- No-op work is still successful when the target already matched the configured plan. It should say `No changes needed`, not `warning`.
- Secrets must never appear in messages, Activity details, or logs.

## Visible = Truthful

- Any feature, provider, rule, or setting shown in the UI must either work in this release or be explicitly labelled `Not available`, `Coming soon`, or `Unsupported in this version`.
- Server handlers must never silently no-op on operator-visible work. If nothing was done, log at least a debug line; if a configured capability is unavailable, log a warning and make the UI disclose that state.
- Weir must not spend provider/network quota on features whose results will always be discarded.

## Implementation

Server code should use `OperatorMessages` in `Weir.Core/Observability` for shared labels and detail envelopes (`ActivityDetailEnvelope`, `ProviderLabel`, `MediaScopeLabel`). Frontend pages may render richer layouts, but must preserve the same result/severity meaning and must not invent success states that the server did not report.
