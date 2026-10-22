# Plan: every line and branch at 100% (#1060)

The goal is 100% line and branch coverage of production code in the server (Core, Infrastructure, Api, Host), the tray, the web
app and the Node scripts, with every exclusion listed and justified. This page is the plan: where the gaps are, in what order they
are closed, and when code may be left out. [`README.md`](README.md) says how coverage is measured and how floors move.

The figures below are from CI on `main` (the `coverage` job's table). They change with every merge, so read the newest run for the
real number; the order of the gaps changes slowly.

## Where it stands

| Project | Lines | Branches | Lines not run | Branches not run |
| --- | ---: | ---: | ---: | ---: |
| Server: Weir.Core | 93.0% | 84.6% | 1,153 | 1,429 |
| Server: Weir.Infrastructure | 91.1% | 77.9% | 2,571 | 2,380 |
| Server: Weir.Api | 92.9% | 74.0% | 983 | 1,112 |
| Server: Weir.Host | 89.2% | 79.1% | 51 | 15 |
| Tray | 75.2% | 74.1% | 1,209 | 406 |
| Web app | 97.4% | 90.2% | 296 | 914 |
| Node scripts | not measured | not measured | | |

The Weir.Core row is from before the first step. The server rows merge the Linux and Windows unit legs (`Weir.Core.Tests`, `Weir.Infrastructure.Tests`,
`Weir.Api.Tests`); the tray row is `Weir.Tray.Tests` on Windows; the web row is the whole vitest suite. The out-of-process figure
(contract suite and E2E smoke, 74.7% to 94.6% lines by project) is counted a different way and is not part of this target; it rises
as a side effect.

## The order

Biggest uncovered area first. Each step is one or a few pull requests, each raising the floor it earned.

1. **Weir.Infrastructure** (2,571 lines, 2,380 branches), by the files with most never run:
   `Browse/DirectoryBrowser.cs` (110 lines), `Processing/WatchedFolderScanOps.cs` (70), `Processing/RemuxPass/TvSeasonFolderCleanup.cs` (69),
   `RemuxPassRunner.PostSuccessCleanup.cs` (53), `Processing/RemuxPass/RejectRoutes.cs` (50), `MediaManagers/MediaManagerIntake.cs` (49),
   `LibraryMode/LibraryScanHandler.cs` (47), `Runtime/ComServerFirewall.cs` (41; Windows COM, see exclusions), `HandoffLedgerStore.Status.cs` (31),
   `LibraryMode/LibraryCleanHandler.cs` (31). These are file-system and process code: tests use real folders, real files and the real
   tools, never stand-in replies.
2. **Tray** (1,209 lines, 406 branches). `TrayApp.cs` alone is 403 lines at 0.2%: the Windows shell UI (menus, balloon notices,
   dialogs). It needs either tests that drive the real tray through Windows UI Automation (the tray shard), or the logic taken out of
   the form into classes the unit tests can call, leaving only the thin form code for the UI tests. Then `Program.cs` (163),
   `Firewall/ComFirewallPolicy.cs` (67 lines, 82 branches, 0%), `UpdateService.cs` (83, 0%), `LanAccess/LanAccessMenu.cs` (69, 0%),
   `UpdateBackup.cs` (36) and `Firewall/FirewallInstallHooks.cs` (41, 0%).
3. **Weir.Api** (983 lines, 1,112 branches): `Endpoints/ProcessingFileTracksEndpoints.cs` (88), `ProcessingRulesPreviewEndpoints.cs` (106),
   `ProcessingJobsEndpoints.cs` (82), `ProcessingWatchedFolderScanDispatchEndpoints.cs` (82; 19.6%), `Http/ApiHttp.cs` (57),
   `ProcessingLibraryEndpoints.cs` (64), `ProcessingLibraryMapping.cs` (26 lines, 60 branches), `AuthSessionLifecycleEndpoints.cs` (46),
   `LibraryModeFilesEndpoints.cs` (40), `ProcessingRuleSetsEndpoints.cs` (28). Tests call the real endpoints with the refusals
   (wrong role, bad body, missing item) as well as the happy path.
4. **Weir.Core** (the rest of 1,153 lines): the pure-logic files still short are `Notifications/Notifications.cs`, `Settings/SuiteSettings.cs`,
   `Rules/SubtitleCap.cs`, `Json/WireStrings.cs`, `Security/CredentialCipher.cs`, `Jobs/OperatorJobStatus.cs`, `Security/Passwords.cs`,
   `Logs/SuiteLogs.cs`, `MediaManagers/ManagerDialectRules.cs`, `Rules/RemuxTrackExplain.cs`, `Rules/TrackSorterComparison.cs`,
   `MediaManagers/ImportEvents.cs`, `Rules/SortableTrack.cs` and `LibraryMode/LibraryFilePlanner.cs`.
5. **Weir.Host** (51 lines): `WeirServer.cs` (22), `Program.cs` (18), `ExclusiveListenSocket.cs`, `StopRequestListener.cs` and `ServerRun.cs`.
   Start-up and shutdown paths, run against the real host process.
6. **Web app** (296 lines, 914 branches): `lib/activity/activity-display.ts` (57 branches), `components/activity/structured-detail-facts.tsx` (0%),
   `lib/activity/pass-detail.ts` (33 branches), `lib/ui/drag-gesture.ts`, `components/ui/server-folder-picker-button.tsx`, `lib/ui/drag-geometry.ts`,
   `components/error-boundary.tsx`, `components/shared/developer-entry-error.tsx`, `pages/library/library-picker.tsx` and
   `lib/activity/use-activity-stream-invalidation.ts`. The web floors (88.3% lines, 82.7% branches) are far below the measured figures
   (97.4%, 90.2%), so the first web pull request raises them to what `main` measures.
7. **Node scripts** (`scripts/*.mjs`, 118 files, 59 with a test file) are not measured yet. The first step is to measure them
   (`node --test --experimental-test-coverage`), add a `scripts` row to `floors.json` and the check, and list what cannot run in CI
   (a script that needs Docker or a published release) in the exclusions file.

Each step ends with that project's floor raised to the figure the run printed, and a bug found by a new test is fixed in the same
pull request with its failing test first. The first step, done with this plan, covered Weir.Core's pure files and found three bugs
(see the pull request). The second step covered `Browse/DirectoryBrowser.cs`, `Processing/WatchedFolderScanOps.cs`,
`Processing/RemuxPass/TvSeasonFolderCleanup.cs` and `Processing/RemuxPass/RejectRoutes.cs`, and the refusal paths of
`LibraryCleanHandler`, `LibraryScanHandler` and `RemuxPassRunner.PostSuccessCleanup.cs`, with real folders and real SQLite, and
found one bug (a manager that could not be reached when asked to remove a queue item threw out of the reject route instead of
being asked again later). Branches the tests showed no input can reach were deleted rather than excluded. What is left of
Weir.Infrastructure was the third step, which covered `MediaManagers/HandbackOutcomes.cs`, `Processing/LibraryDiscoveryService.cs`,
`RemuxPass/RemuxPassEnqueue.cs`, the job and file states of `MediaManagers/HandoffLedgerStore.Status.cs`, the kept-file and sign-in paths of
`MediaManagers/MediaManagerIntake.cs` and the folder checks of `RemuxPass/RemuxPassPaths.cs`, and deleted code no input could reach (a second
copy of the job reader, and payload parses of jobs found only because their payload already parsed). What is left of Weir.Infrastructure starts with
`MediaManagers/ManagerSetupCheck.cs`, `MediaManagers/HandoffCompletionReporter.cs`, `MediaManagers/LibraryFileChangeNotifier.cs`,
`MediaManagers/DownloadClientConnectionService.cs` and the rest of `LibraryMode/LibraryScanHandler.cs`.
The Weir.Core part of step 4 began with the files that had the most lines and branches never run (settings, alerts and their
addresses, the subtitle cap, job and log wording, the media-manager dialects and import events, the track explanation and preview,
CSRF tokens, glob patterns, direct play, the session and rate-limit rules, the library file planner) and took Weir.Core from 95.9% to
about 98.0% of lines and from 89.7% to about 93.5% of branches. Branches no input can reach were deleted (the unreachable arms of the
float and truth-value readers, an eviction loop the limiter had already run, the byte-order check in the token writer). What is left
of Weir.Core is in the files the new tests did not reach (the Mkvmerge and output-validation helpers, the library and manual track
plans, the folder-hand-off and setup rules, the JSON writer and the rest of the remux planner). In Weir.Host the new tests cover the
exclusive listening socket, a duplicate stop event, the version command, the short-secret warning and a host that can only be disposed
synchronously. What stays is the start-up steps that need a failing database (the `catch` blocks of `WeirServer`) and the
`recover` and `--healthcheck` commands in `Program.cs`, which the contract suite and the E2E smoke run out of process.
>
## When code may be left out

Everything the coverage figures leave out is listed in [`exclusions.json`](exclusions.json), and
`scripts/check-coverage-exclusions.mjs` (run in CI on every change) fails when:

- a collector setting or a marker in the code leaves something out and the file does not list it;
- an entry no longer matches anything, so the list never carries a stale line;
- an entry has no reason, or one that says nothing ("TODO", "n/a", a few words);
- an entry's kind is not one of the kinds below, or a `platform-only` entry does not name where and by what test the code runs.

An entry has an `id`, the `file` that leaves something out, the `marker` (the setting's name, or `ExcludeFromCodeCoverage` / `v8 ignore`
for a marker in code), a `kind`, a `reason`, and optionally a `count` when a file has several markers of one kind, and a `symbol`
naming what is left out.

| Kind | Allowed for | What the reason must say |
| --- | --- | --- |
| `generated` | Code a tool wrote (source generators, compiler output, the generated API types) | What writes it. |
| `not-production` | Test projects, stand-in programs, test setup | That it does not ship. |
| `no-code` | Files with no statement to run (type declarations) | Why there is nothing to run. |
| `platform-only` | Code only one operating system can run **and** that no unit leg runs | `coveredBy` (`linux` or `windows`), `provedBy` (the test or scenario that runs it there) and why a unit test cannot. |
| `unreachable` | A defensive branch no input can reach | Why it cannot be reached. |
| `policy` | A setting that switches a marker on | Only for the setting itself; each use of the marker is listed on its own. |

Rules that go with the kinds:

- **Delete before excluding.** A branch that can never run is removed from the code (as `FileStory.Duration` and
  `MediaToolLocations.OsPathJoin` were in the first step), not excluded. `unreachable` is for the case where removing it would make
  the compiler or a later reader worse off, such as the last arm of a switch over an enum.
- **Platform-only is narrower than it sounds.** The server figure merges the Linux and Windows legs, so a line only Windows can run
  counts as covered once the Windows leg runs it, and needs no entry. An entry is for the rare line neither unit leg can run (a COM
  call that needs administrator rights, the Windows service control manager), and it must name the scenario or contract test that
  proves it does what it says.
- **No file-level exclusions for convenience.** A file is never excluded because it is large, hard to test or "just UI". The tray
  shell is covered by driving it, or by moving its logic out of the form.
- **Review.** A change to `exclusions.json` is called out in the pull request description with its reason, and is read by the
  reviewer first.

Known candidates (to be confirmed, with their proofs, when their project is worked): `Runtime/ComServerFirewall.cs` and the tray's
`Firewall/ComFirewallPolicy.cs` and `Firewall/FirewallInstallHooks.cs` (Windows firewall COM, run by the Windows install scenarios),
and the unreachable last arms of switches over `JsonValueKind` in `Rules/RulesJson.cs`.
