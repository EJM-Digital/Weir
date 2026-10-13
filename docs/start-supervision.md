# How the Windows tray supervises the server's start

The tray starts `WeirServer.exe` and keeps it running. Every way a server comes up goes through one supervisor in
`apps/tray/Weir.Tray` (`ServerHost.Supervision.cs`): the first start, a start after the server crashed, and **Restart Weir**.
This note says what it does, which proven practice each part follows, where its numbers come from, and what it writes to the logs.

## What it does

1. The tray starts the server with its standard output and error redirected and read, and with `WEIR_PROGRESS_STDOUT=1`.
2. The server reports how far it has got (`StartupProgress`): at every phase and, while SQLite is working inside one, about once a
   second. The phases with no database work write one record each.
3. The tray waits while that progress keeps changing and asks `/ready` every quarter second. `/ready` answering 200 is the
   readiness signal; the progress only decides whether a start that has not answered yet is **working** or **hung**.
4. A start fails when the server exits, or is **hung**: silent for longer than its phase allows. A hung server is asked to stop,
   then killed with its whole process tree. Only a process the tray started is ever touched. The next attempt does not begin until
   the old process is confirmed gone; if it is still running after the kill, the tray stops there and says so, because a second
   server would find the first holding the port and the database.
5. A failed start is tried again after a capped, jittered wait, until too many fail close together; then the tray gives up and says so.

## The practices it follows

| Practice | Where it comes from | Here |
| --- | --- | --- |
| Restart on failure, with a delay | systemd `Restart=`, Windows service recovery actions | `StartSupervision` |
| Capped exponential back-off | Kubernetes `CrashLoopBackOff`, systemd `RestartSteps=` / `RestartMaxDelaySec=` | `StartPolicy.BackoffCeiling` |
| Jitter | "Equal jitter" in the AWS Architecture Blog's back-off study | `StartPolicy.Backoff` |
| Start limit | systemd `StartLimitBurst=` / `StartLimitIntervalSec=` | `StartLimitBurst` failures within `StartLimitInterval` |
| Failure count resets after a stable run | Kubernetes resets back-off after 10 minutes; the Windows service manager's reset period | `StableAfter` |
| A startup check separate from a readiness check | Kubernetes startup, liveness and readiness probes | progress (`StartWatch`) and `/ready` |
| A service lengthens its own start-up time by reporting | systemd `sd_notify EXTEND_TIMEOUT_USEC` | `expect_within_s` in the progress record |
| Do not restart what restarting cannot fix | systemd `RestartPreventExitStatus=` | a server that wrote `startup-error.txt` is not tried again; this includes a refusal for its settings or its database |
| Stop politely, then kill the tree | systemd `KillMode=` / `TimeoutStopSec=` | `ServerProcessStop` |

## The policy

| Value | Setting | Why |
| --- | --- | --- |
| Wait for a first report | 30 s | The slowest first report measured was 2.3 s, with the CPU oversubscribed twofold; this is 13 times that. |
| Silence allowed in a phase | 30 s | Progress comes about once a second while work runs; this is 30 beats. |
| Silence a server may ask for | 30 s plus 300 s per GB of database, never more than 1 h | See "Measurements". |
| Wait before the second attempt | 2 s, doubling to 60 s, equal jitter (half to all of it) | Kubernetes and systemd shapes; the jitter keeps several programs from restarting together. |
| Failures that end the trying | 5 within 15 minutes | systemd's burst and interval. Five hung starts take about three minutes. |
| Stable run | 10 minutes ready | Kubernetes' reset time. |

Restart Weir, chosen by a person, gets the whole budget again. A change of port or of who may connect starts the server once, as a
start of its own (the earlier failures are forgotten and are not passed to it), and goes back to what worked if it fails; it does not
count against the budget.

## Measurements

Taken on the development PC (16 cores) with the single-file server published as the release is.

| Measurement | Result |
| --- | --- |
| Process start to first progress, idle, 20 cold starts (new copy of the exe, fresh extraction folder) | 163 to 188 ms |
| The same with 32 busy processes on the 16 cores | 1.35 to 2.29 s |
| Start to ready, idle / loaded | 0.74 to 0.78 s / 4.9 to 6.6 s |
| A 1.5 GB database (300,000 files, 600,000 history rows, 600,000 Activity rows, 100,000 hand-backs) updated from the previous release, idle | 8.2 s in all: copy saved in 6.8 s, two migrations in 1.0 s, listener in 0.1 s |
| Longest gap between two progress writes in that start | 1.02 s |
| The same database, CPU oversubscribed twofold | Copy saved in 140 s; longest silence 120 s |
| The same, with the disk also busy writing back another copy of the file | Copy saved in 201 s; longest silence 183 s |

The copy and the migrations are SQLite working, which reports from inside the statement (`SqliteWorkProgress`), so a start that is
blocked stops reporting. Parts of them cannot report: syncing a finished copy to disk, the final commit, and whatever a virus
scanner does to a large file just written. Those silences grew to three minutes on a busy PC, which a fixed 30 s window would
have called hung, so a server doing data-heavy work says how long it needs (`StartupPatience`): 30 s plus 300 s per GB, which is
468 s for the 1.5 GB database above, 2.3 times the slowest start seen. The tray believes that up to an hour and no further.

That allowance is for the silence **after SQLite has been seen working** in the phase (the first beat of the phase carries it, and a
later migration step of the same phase carries it on). A start that has not beaten gets the ordinary 30 s, and so does one waiting for
a lock: SQLite gives up on a lock after 30 s and the start ends with an error. The allowance is not asked for on entering a phase, and
not in the phases that do no database work.

## Progress

The record is the same on both channels:

```json
{ "pid": 5764, "phase": "migrating", "step": 3, "steps": 7, "expect_within_s": 468, "at": "2026-10-13T06:22:58Z" }
```

Phases: `starting`, `opening_database`, `saving_copy`, `migrating`, `preparing` (the database is open: noting the start,
clearing old sessions, naming connections), `starting_listener`, `ready`. `starting`, `preparing` and `starting_listener` write one
record each and are held to the ordinary 30 s window.

* **Standard output, the tray's channel.** A line `weir-start-progress {record}`. The tray owns the pipe and reads it, so a line is
  progress the moment it is written; it is not refused or made late by a file. If nothing were reading, the server's queue of 1024 lines
  would fill and further lines would be dropped (and counted), which is why the file is the second channel.
* **`startup-progress.json` in the data folder**, for readers outside the tray (the scenario suite, support). It is replaced whole,
  then renamed into place. Windows refuses a rename over a file that is open or being scanned for a moment, as access denied or a
  sharing violation, so the server tries again (5 times, 100 ms apart) and the next beat tries again after that; a reader treats
  access denied and a sharing violation as "busy", opens the file again after a moment, and when it stays busy has no news.
  Neither a failed write nor a busy read is ever taken for silence: the tray counts the time since a record last changed on either
  channel, and a record that has not changed is not progress however often it is read.

Progress is only ever "this record is not the one before it". The tray never compares the server's clock with its own, so a clock
that steps back (a virtual machine's time sync, a snapshot resumed) cannot make good records look stale. A record of another process is
ignored, and the tray removes the file before it starts a server, so a file left by an earlier one is never read as this one's.

## The server's console never blocks

The server's lines on standard output go through `NonBlockingConsole`: a bounded queue and a thread of their own, which also opens
the console stream. When nothing reads the output the queue fills and lines are dropped and counted; `weir.log` always has them.
A pipe holds a few kilobytes. When whatever holds its other end does not read it, a write into it waits for ever, and so does
`Console.OpenStandardOutput()` in a process whose standard output is that pipe while a write is waiting. A log call that wrote to the
console itself would wait with it, and the server would never reach its own log, listen, create the event the tray stops it by, or
exit. So the server does not touch the console on any thread it runs on, and the tray also gives the server pipes of its own and
reads them (`server-host.log`), and replaces its own standard handles with the null device.

## What is logged

`tray-host.log` and `server-host.log` carry every attempt:

```
Start attempt 1 failed: hung in phase 'opening the database' for 30 s. Trying again in 1.4 s (1 of 5 failures allowed within 15 min).
Start attempt 2 failed: exited with code 3 after running 0.4 s (last phase: starting). Trying again in 3.2 s (2 of 5 failures allowed within 15 min).
Weir started on attempt 3, after 2 failed attempts: attempt 1: hung in phase 'opening the database' for 30 s; attempt 2: exited with code 3 after running 0.4 s (last phase: starting).
Start attempt 1 failed: hung in phase 'opening the database' for 30 s, and could not be stopped (process 5764 is still running). Not trying again: another server would find this one still holding the port and the database. End process 5764 (Task Manager, or restart the PC), then use Restart Weir.
Start attempt 5 failed: hung before it reported anything for 30 s. Giving up (5 of 5 failures allowed within 15 min); Weir is not running. Restart Weir tries again.
```

A hung server is also described before it is stopped (its processor time, threads and the files it had loaded). A server started on a
retry is told its attempt number and why the earlier ones failed, and writes a **warning** to `weir.log` and so to System > Logs.
The scenario suite's "Logs with no unexpected warnings" reads that warning, the `failed` in the tray log, and `weir.log`, so a start that
only worked on a retry fails it unless the suite names it. When the tray gives up, no server is running to write to `weir.log`; the
reason is in the two tray logs.

## What the person sees

* While it retries the icon shows Starting. When it gives up the icon is red with "Didn't start after 5 tries", and a notice says
  "Weir didn't start after 5 tries, so it has stopped trying. The details are in the tray log. Click to try again." Clicking it, or
  Restart Weir, starts over with a fresh budget.
* When Weir was started by something with no one at the desktop (an installer, Deluno), the tray exits with code 1 and the reason is in
  `tray-host.log`.
* A server that stopped with a reason of its own (it could not save a copy of its data before updating, for instance) is not retried;
  its own words are shown, as before.

## Tests

`StartSupervisionTests` (back-off, jitter bounds, start limit, reset after a stable run), `StartWatchTests` (hung or working, two channels,
clocks that step back or jump, with fake time), `StartProgressFileTests` and `StartProgressBusyTests` (torn, busy, refused and
out-of-range records), `ServerHostTests` (real stand-in servers on a fake clock: exits at once, hangs in a phase, slow but reporting on either
channel, a hung server that will not stop, a port change, the start limit, crash after running, Restart Weir), `ServerOutputLogTests`,
`InheritedStdioHandlesTests`, `StartupProgressTests`, `StartupProgressChannelsTests`, `NonBlockingConsoleTests`, `StartRefusalTests`, and
`ServerOutputPipeTests`, which starts the real server with a full pipe nobody reads, once to run and once to refuse its settings.

## Reading the files that are replaced under a reader

The server and the tray talk through small files in the data folder, and each is replaced by writing a new file and renaming it over the
old one (`AtomicFileWriter` in the server, `AtomicFile` in the tray). A reader that opens the file at that moment can be refused (access
denied, a sharing violation) or can find the name missing for an instant, and a virus scanner holding a file just written does the same.
None of this says anything about the file. Every reader of these files therefore goes through `ReplacedFile` (one copy in the server, one in
the tray), which looks again after 20 ms, up to 5 looks, before it says what it found:

* **Read**: the text, and when the file was written.
* **Absent**: no file on any look. This is the state of a fresh install, and it is the only state that means "nothing saved".
* **Busy**: refused on every look. This says nothing about the file. A reader never takes it for damaged or absent, and never changes
  anything because of it. What it does instead depends on what it is deciding, and is always the safe side:

| File (written by) | Reader | While it is busy |
| --- | --- | --- |
| `lan-access` (server, tray) | tray watcher | Leaves the server as it is and looks again at the next tick. The start, which must choose, falls back to this PC only. |
| `update-settings.json` (server, tray) | tray, Settings page | The tray uses NotifyOnly (installs nothing) and says it was busy; the page reports the same fallback it uses for an unreadable file. |
| `work-state.json` (server) | tray's wait for idle | `ServerWork.NotRead`: the wait keeps its place. |
| `update-backup-ready` (server) | tray | The request for a copy is a failed request, so the update is held back, not sent on with no copy. |
| `update-backup-failed`, `update-not-applied.json` (tray) | tray at start; server for the reason | The start-up install leaves the update waiting; the reason is neither shown missing nor removed. The server keeps the last reason it read. |
| `update-start-attempt` (tray) | tray at start | Counts as an earlier attempt: no install. |
| `tray-heartbeat.json` (tray) | server | The last heartbeat read, still ageing: a tray that is refused for a moment has not quit. |
| `update-state.json` (tray) | server, Settings page | The last state read, so a downloaded update stays downloaded and Apply is not refused. |
| `pause-request.json`, `update-backup-request.json` (tray) | server watchers | The request is looked at again a moment later (2 s, 1 s) and is never dropped. |
| `port.txt` (tray) | tray at start | The port stays unknown and the start is a failed attempt like any other: the same capped, jittered back-off, logged, up to the same start limit, then the same give-up (the notice, the red icon, Restart Weir; exit 1 only with nobody at the desktop). It is never read as "no port chosen": no first-run dialog is shown and no port is saved while the file is busy, and Change port and the LAN choice wait until the port is known. |
| `startup-error.txt` (server) | tray | Looked at again; a missing file is final, since the server deletes the last one first. |
| `startup-progress.json`, `tray-status.json`, `update-backup-result.json` (server) | tray | Readers of their own that follow the same rule: no news, nothing changed. |

A reader with no earlier reading to fall back on (the first look after a restart) takes the plainer side of the same rule: a heartbeat
that cannot be read is no tray yet, a state that cannot be read is the idle state.

Files written once and never renamed over (`firewall-prompt-answered`, `firewall-rule-widening-asked`) cannot be missing or refused
because of a rename, and `session.secret` and `credentials.secret` are read by the tray alone, at its start, and replaced only when
unusable; none of these has a reader that needs the rule.
