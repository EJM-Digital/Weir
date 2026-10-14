# Settings truthfulness audit

Weir settings must describe runtime behaviour as shipped, not intended behaviour. Each entry below
says where a setting lives and what actually happens when it is saved.

## System

- Setup wizard (System › About): `Open setup wizard` goes straight to the guided setup at
  `/setup-wizard`. It is not a navigation item.
- Updates (System › About): shows the installed version, the latest public release known to the
  update service, and where Weir was installed from. The update mode (Auto, Download only, Notify
  only) is saved to the database.
- Backups (System › Backups): the automatic backup schedule is saved to the database, and the running
  backup worker reads the saved schedule before each tick. Export and restore act immediately.
- Security (System › Security): username, password and sessions are database-backed. The "How
  sign-in is protected" group (auth cookie, HTTPS and rate-limit configuration) is read-only and says
  it comes from startup configuration and changes only with a restart.
- Retention (System › Logs › Log settings, "How long things are kept"): saved to the database and enforced by runtime
  log pruning; no restart is required.

## Settings

- Workflows (the API still calls them libraries): each workflow carries its own folders, file
  types, exclusions, scan interval and safety checks, saved in the database and used by new scans
  and per-file work after save. Missing folders are warnings at runtime, not save blockers.
  Removing a workflow is refused while it still has queued or running work, because those jobs
  resolve their folders from it. Saving one part of a workflow (its hours on Schedule, its folders in
  setup) sends back every other setting as it was, so nothing else is reset.
- Rejected files (a workflow's "When a file is rejected"): "Delete only the rejected file" applies to a file
  the workflow's own size, date or path settings turn away, to a file its rules find nothing in to
  keep, and to a file with no video. A file Weir cannot read is refused after a wait, before any output
  is written, never passed through and never deleted. The minimum size is the workflow's own (50 MB for a new workflow), so it is the one the editor shows,
  and deleting is tied to that value alone.
- Writes files with (workflow editor, Workflows): "The best tool for each file" writes Matroska with
  mkvmerge when it is installed and everything else with FFmpeg, and writes a file again with FFmpeg
  when mkvmerge cannot write it or its copy fails Weir's checks. It applies to new downloads and to
  cleaning files already in the library; when FFmpeg had to step in for a library file, that file's
  story says so. "FFmpeg only" uses FFmpeg for everything. FFmpeg compatibility (advanced) applies to
  both as well.
- Hardware decoding: there is no setting. Weir copies video and audio without decoding them, so a
  graphics card has nothing to do. Older saves and backups that carry the old hardware decoding or
  "rewrite with FFmpeg" fields are still accepted; the values are ignored and not reported.
- Search lanes, the signed-in home note, and the hours window for Movies and TV: there are no settings for
  them, because nothing acted on any of them. Searching for missing files and upgrades belongs to the media
  manager, so a connection has no search lanes. No page showed a home note. Each workflow's weekly schedule
  on Schedule decides when it works, so Performance has no hours window of its own; the Movies and TV
  periodic-scan switches (`movie_schedule_enabled`, `tv_schedule_enabled`) stay. Their database columns are
  gone. An older client, or a backup that still carries them, is answered as though it had not sent them:
  saving a lane (`PUT /api/v1/media-managers/connections/{id}/lanes/{lane}`) returns the connection, the two
  settings saves ignore the fields, and a restore reads past them.
- Your playback devices (Setup › Rules › Playback devices): information only. It drives the "can play directly"
  badge on files and changes nothing about how a file is processed.
- When a file fails (a workflow's own section): "Maximum automatic attempts" is the whole number of tries, the
  first one included, so 3 means the first try and two retries; nothing else caps it. Each retry
  waits twice as long as the one before (the first delay, then double, up to an hour), and the workflow's
  folder is looked at again shortly after the wait ends, not only at the next periodic scan. A file
  Weir gives up on, because it ran out of attempts or the failure is not one the workflow retries, is
  one state: Failed, with the reason. What happens next is "When retries run out": hand the original
  back, keep it until someone acts, or reject the release. Activity's remove dialog and "Try again"
  work on a failed file the same way whichever it was.
- Rules: audio and subtitle handling is a named rule set a workflow points at, so two workflows can
  share one. Deleting a rule set a workflow still uses is refused rather than silently stripping that
  handling.
- When a new file is ready (a workflow's File readiness group): one wait, "A new file is ready once it hasn't
  changed for N seconds", saved on the workflow (60 seconds for a new one). Both the file's size and its
  last-changed time must stay the same for N seconds, so the scan holds a new file until then and the
  pass checks the file's age against the same number. It replaces the three waits a workflow used to
  have (wait after a change, hold every new file, wait for the size to stop growing); an upgrade gives
  each workflow the longest of them, so no file is picked up sooner than before. A media-manager
  hand-off skips the scan, because the manager says the download is finished. Setup › Performance › Speed
  holds neither this wait nor a minimum size; an older client that still sends them to
  `PUT /api/v1/processing/operator-settings` is answered and they are ignored.
- Minimum file size (a workflow's Intake rules): each workflow's own value, 50 MB for a new one, shown
  beside the maximum size. A media-manager hand-off still applies the minimum size and the video type,
  but skips the path, date and maximum-size rules, because the manager chose the file.
- Performance: "Files at once" (1 to 10) decides how many files run together and needs no restart. A
  workflow's "Most files at once from this workflow" is a share of it: blank or 0 is no limit of its
  own, and a number above Files at once is refused when saved. A workflow already above a lowered
  Files at once still saves its other settings, and its editor says it is limited by the total. The
  resolution budget (runner capacity and per-resolution costs) only applies when "Also weigh files by
  resolution" is switched on, and then counts every job that reads a whole video: a pass however it
  arrived (a hand-off costs the resolution already recorded for its file, and is corrected to the
  measured one once its pass has probed it) and a library clean (costed by the resolution its scan
  probed). A file whose resolution is not known costs what a 1080p file does. When files are waiting,
  `GET /api/v1/processing/files-at-once` and the screens that use it name the one limit they are
  waiting on.
- Keeping space free (a workflow's "Keep at least this many GB free on the drive this workflow writes
  to", 5 GB unless set, 0 turns it off): checked before every write the workflow makes. A remux pass
  and an unchanged publish check the output drive (and the work folder's drive) before the file is
  read, and again before the output is published. A hand-back copy after retries run out checks the
  output drive before copying. A library clean needs the file's size plus this much free beside the
  file. Below it the file waits (On hold, "Waiting: the output drive has less than X free") and is
  looked at again 10, then 30, then every 60 minutes, and is never recorded as done or failed. An
  upgrade gives every workflow the value Setup › Performance › Speed had; Performance still accepts the
  old field and ignores it.
- Cleanup: holds the leftover-work-file sweep, "Keep a failed file's half-written copy for a day" (a failed
  copy stays in the work folder until it is a day old, at which point the sweep removes it; a restart
  removes it no sooner) and the cleaned-copies-nobody-picked-up job. There is no cleanup of the
  downloads of failed files: a failed download is handled from Activity's remove dialog and by the
  workflow's "When retries run out".
- System › Logs: "Keep a file's history for N days after it's gone" (0 keeps it for ever) is saved to the database
  and enforced by an hourly job. A file's history is kept for as long as Weir still knows the file, then
  for N days from the hour Weir first finds the file gone or forgotten; a file that comes back before then
  keeps it. It sits with the log and Activity retention settings, which save on their own button.
- System › About: the time zone is saved to the database and every time in Weir, including those on
  Schedule, is read in it.
- Schedule: each workflow's hours are saved per workflow and applied without a restart. `Scan now`
  queues a one-off scan of that workflow.

## Startup configuration

- `GET /api/v1/processing/runtime-settings` is read-only startup configuration. Any value that needs
  an environment change and a restart must stay labelled as restart-required.
- `WEIR_PROCESSING_WORKER_COUNT` is an internal startup slot cap (default 10, the most "Files at
  once" can be), not the number of files processed at once. A server started with fewer slots than
  "Files at once" asks for says so beside the setting rather than promising a number it cannot run.
- `WEIR_PROCESSING_WATCHED_FOLDER_REMUX_SCAN_DISPATCH_SCHEDULE_ENABLED` (default on) is a global
  switch for the periodic watched-folder scan timer. With it off, no scheduled scan runs for any
  workflow, whatever that workflow's own schedule says. A manual scan is unaffected.
- The Cleanup timers' environment switches each govern their own media type:
  `WEIR_PROCESSING_WORK_TEMP_STALE_SWEEP_MOVIE_SCHEDULE_ENABLED` and `..._TV_SCHEDULE_ENABLED` for the
  leftover work file sweep. An explicit 0 stops that timer for that media type whatever is saved in
  Setup › Performance › Cleanup; the variables cannot switch a timer on. The `..._FAILURE_CLEANUP_...` variables of
  the removed failed-download cleanup are accepted and ignored.
- `WEIR_PROCESSING_WATCHED_FOLDER_MIN_FILE_AGE_SECONDS` is retired. Setting it changes nothing: the wait
  for a new file is the workflow's own.

## Planned: video conversion

When Weir gains video conversion (the direction towards what FileFlows does), the settings land in
these places so each one keeps a single meaning:

- **Hardware** (use the graphics card, which method, which vendors to avoid) goes on Setup › Performance › Speed and is machine-wide. It is about speed, so it belongs beside "Files at once". The
  detection behind it already exists as `GET /api/v1/processing/hardware`.
- **Convert video** (codec and format) goes on Setup › Rules › Profiles, per profile, next to the rules that
  decide what a file keeps. "Your playback devices" is the list a rule such as "convert so my TV can
  play it directly" will aim at.
