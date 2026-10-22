# Files the tray and the server write for each other

Weir's Windows tray and its server talk through small files in Weir's data folder. Each file here is what one side really wrote, kept as it
was written, with fixed values (the moment is always 15 January 2026, 10:00 UTC). Both sides' tests use these files:

- A test of the side that writes a file runs its real writer and checks the output is this file, byte for byte (line endings aside, since git
  makes every checkout LF).
- A test of the side that reads a file puts this file in a data folder and runs its real reader on it.

So a key renamed, a format changed or a name that stops matching on either side fails a test on that side, and the file is changed in one place
for both. The tray's tests are in `apps/tray/Weir.Tray.Tests` (`TrayFilesTheServerWritesTests`, `TrayFilesTheTrayWritesTests`); the server's are in
`apps/server/tests/Weir.Infrastructure.Tests/Runtime` (the same two names).

| File | Written by | Read by |
| --- | --- | --- |
| `pause-request.json` | tray (Pause, Resume) | server (`TrayPauseRequestWatcher`) |
| `update-backup-request.json` | tray, before it installs an update | server (`TrayUpdateBackupWatcher`) |
| `update-state.json` | tray, at each step of an update | server (`UpdateFiles`) |
| `tray-heartbeat.json` | tray, every 30 seconds | server (`UpdateFiles`) |
| `lan-access-on.txt`, `lan-access-off.txt` | either (saved as `lan-access`) | the other |
| `tray-status.json` | server | tray (`TrayStatusFile`) |
| `work-state.json` | server, while an update waits | tray (`WorkStateFile`) |
| `update-backup-ready` | server, while it can save a copy | tray (`UpdateBackupReady`) |
| `update-backup-result-saved.json`, `update-backup-result-failed.json` | server | tray (`UpdateBackupRequest`) |
| `startup-error.txt` | server | tray (`StartupNotes`) |
| `startup-progress.json` | server (also as a line on its standard output) | tray (`StartProgressFile`) |

The marker `update-backup-ready` names the process that wrote it and when it started, which differ on every run, so the server's test checks its
keys and the form of its values against this file; the tray's test reads this file. The `path` in the saved answer is where the copy went on the
machine that wrote it, so the server's test puts this file's path in before it compares.
