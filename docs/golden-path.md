# Golden path

The run that gates every `v*` tag of both apps, Deluno and Weir, release candidates included. Each app's tag waits for its own app's scenario suite, passed on that exact commit. Since 11 Oct 2026 (Deluno #1238, Weir #954) the run is a scripted scenario suite that proves the release candidate before the tag, not a session driven by hand; no tag is waived from it. This page is the same, word for word, in both repositories. A change to it is made in both, in the same words.

## Why

Green tests have shipped bugs that only showed when the product was used: a pause that did not pause, seeding originals deleted, the same file cleaned twice, a set-up that logged "could not set up", and a cleaned file shown as "handed back as it was". So before a tag is made, the exact commit is installed on a clean machine and used the way a person uses it. Decided by the owner, 7 Oct 2026 (Deluno #1158, Weir #903).

## When, where and who

- **When:** before the tag, on the exact commit that will be tagged, for every `v*` tag of either app, release candidates included. A release whose tagged commit has no passing record of its own app's suite does not publish, and nothing is waived: the suite runs first, then the tag is made.
- **Where:** a clean Hyper-V Windows VM with a saved clean checkpoint, reverted before every run. The real-data machine is not the golden path.
- **What is installed:** Deluno's installer built from Deluno's commit, and Weir's Setup built from Weir's commit. Weir's comes from a manual CI run given the release it will become, `gh workflow run ci.yml --repo jampat000/Weir --ref main -f version=<version>`, as the artifact `weir-windows-<short sha>`. It reports `<version>+<short sha>`.
- **Who:** whoever is releasing starts the suite (below) and reads its results file. It runs unattended and records the commit status itself.
- **How each check is passed:** by clicking or watching, never by reading code or a test result. A check that fails stops the run. The fix gets a test that does what the user did, and the run starts again from step 1 on the new commit.

## The scenario suite

`scripts/golden-path/Invoke-ScenarioSuite.ps1` is the run. It proves a release candidate on the golden VM, unattended, and passes or fails each scenario with its evidence. A full run takes **about two hours** (124 minutes in the rc.30.29 run, which included Weir's own phases); the table under "How long a full run takes" gives the time of each part.

```powershell
./scripts/golden-path/Invoke-ScenarioSuite.ps1 -RigHost <host> -InstallerPath <Deluno-rc-Setup.exe> -Version <x.y.z-rc.N> -Commit <sha>
```

- **Before the tag.** Build the installer of the commit that will be tagged with a manual `ci.yml` run (the `windows-installer` artifact), run the suite on it, and tag only when it passes. The suite checks that the installed Deluno carries the commit given (`Deluno.exe` reports `<version>+<sha>`), so a status can never be recorded for a build that was not the one tested.
- **What it does.** It restores the clean checkpoint, installs the candidate the way a person does (the component picker ticks the five download clients plus Weir), starts a tray watcher before Setup so the first run can be read, then copies the suite into the VM and runs it in the signed-in desktop session. The suite sets Deluno up through its own API (first account, Connect all, Connect Weir, libraries, routing, the Usenet stub and a local Torznab feed), with throwaway logins made inside the VM that are never printed or leave it, and then runs every scenario.
- **The result.** `scenario-suite-results.md` and `.json` in the run's folder (under `artifacts/`, not committed): a pass or fail for every scenario, the checks behind it with their timings, and the harness's own install lines. A scenario that never reports counts as an error. The run's folder also keeps, next to the result, `vm-logs`: Deluno's and Weir's full logs, the request lines for `/processors/events` (`processors-events-requests.log`, matched in Deluno's `deluno-<date>.jsonl` log, which holds the line Deluno writes for each answer it gives to Weir's call; `processors-events-requests.diag.txt` beside it names the folders and files searched, and the results file's warning says what is missing if the log is empty), the tray watcher's log, the suite's own log and each scenario's evidence.
- **The status.** On a full pass it records `golden-path` = `success` on the commit (`gh api repos/<owner>/<repo>/statuses/<sha> -f state=success -f context=golden-path ...`), which is what `scripts/tag-release.ps1` and the release workflow read. On any failure it records `failure` and names the failed scenarios in the description, so an earlier success can never be used by mistake. A run limited to some groups (`-Groups`) or to single scenarios (`-Scenarios`) records nothing and can never be a pass for the tag; its exit code still says whether what it ran passed. `-DryRun` runs everything and prints the status command instead of running it.
- **One scenario at a time (`-Scenarios`).** After a fix, run only the scenario that needed it; the whole suite is not re-run because one scenario failed (owner, 14 Oct 2026). `-Scenarios` takes ids, comma separated, or patterns: `-Scenarios stuck.short.deluge`, `-Scenarios 'resend.during.sabnzbd,resend.after.sabnzbd'`, `-Scenarios 'resend.*.sabnzbd'`. A mistyped id is an error naming the nearest ids. Every id in the plan can be chosen alone. Each group prepares only what the chosen scenarios read: `film.sabnzbd` sends SABnzbd's film and no other; the "before" resend scenarios share one window, but only for the clients chosen; the failures of `tryagain` and the two vanish phases are made for the chosen clients only; `activity.one-story` sends its own film through each client when no earlier scenario of the run made one. A scenario run alone sets up the same environment, with the same rules, as in a full run, and gives the same verdict. `-Scenarios` and `-Groups` are not used together. The update scenario is the host's own: `-Scenarios update.over-running-previous` runs on a clean machine of its own and needs no install of the candidate in the VM. Weir's own suite takes the same name: `scripts/scenarios/Invoke-WeirScenarios.ps1 -Scenarios <ids>` (comma separated; `*` and `?` stand for any text; a mistyped id is an error naming the nearest ids). Each Weir scenario makes what it needs itself and reads nothing another left, so any of them runs alone and again on the same machine. A run of chosen scenarios keeps its record in a folder of its own and never sets the `golden-path` status.
- **A fast start for those runs (`-UseReadyCheckpoint`).** Installing the candidate through the picker and setting Deluno up take about 15 minutes. With `-UseReadyCheckpoint` the first run of a commit does them as usual and then saves the machine as a Hyper-V checkpoint named `ready-<version>-<first 8 of the sha>` (`-ReadyCheckpoint <name>` chooses another). Every later run of that commit restores it with `Restore-VMSnapshot`, as the harness restores `clean`, and goes straight to the scenarios; the suite's scripts are copied in again each time, and the installed build is still checked against the commit. The checkpoint is a standard one (disk and memory), so the restored machine has Deluno, Weir and the clients running and the desktop signed in; the VM's own checkpoint type is put back after it is taken. A new commit has a new name, so a rebuild never reuses an old checkpoint, and saving a new one removes any other `ready-*` (one at a time keeps the disk small). If a restore fails the normal install runs instead and the checkpoint is saved again. It is never used for a full run (a full run always installs from `clean`, so the install scenario stays covered), for `install-first-run` (that scenario reads the install itself), with `-AfterSetupScript` or with `-SkipInstall`. To throw one away: `Remove-VMSnapshot -VMName <vm> -Name ready-...` on the host.
- **Checking the suite's own code** needs no VM: `./scripts/golden-path/Test-ScenarioSuite.ps1` (run by `npm run ci:check`) parses every script, checks the plan against the group files, the host's judging and status code, and the Deluno, Weir, Edge and news-stub code against local mocks. `Invoke-ScenarioSuite.ps1 -ListOnly` prints the scenarios.

### How long a full run takes

About two hours, not forty minutes (the earlier figure was an estimate that was never measured). From the rc.30.29 run (4 to 6 am, 124.1 minutes in all); the groups' times are the wall clock between their first and last line in the suite's log:

| Part | Time |
| --- | ---: |
| Restore, Setup and the component picker | 10 min |
| Deluno's setup through its API | 4 min |
| Weir's own update phase (the after-setup hook) | 3 min |
| `install` | under 1 min |
| `films` | 3 min |
| `removal` | 8 min |
| `resend` | 31 min |
| `stuck` | 20 min |
| `outage` | 4 min |
| `tryagain` | 3 min |
| `vanish` | 15 min |
| `problems` | 5 min |
| Log collection, Weir's remaining phases and the `update` scenario | 16 min |

The groups alone are about 89 minutes. The slow ones wait on real clocks: `resend` (three timings on five clients, the first sends held at Weir until the moment is made), `stuck` (a one-minute check interval and two strikes), and `vanish` (Weir takes about 11 minutes to report a missing source). That is why a run of single scenarios, not the whole suite, is the way to check a fix.

### What each scenario proves

Every scenario that applies to a download client runs on each of qBittorrent, Transmission, Deluge, SABnzbd and NZBGet.

| Group | Scenarios | What it proves |
| --- | --- | --- |
| install | `install-first-run` | The tray icon blinks (brightness alternating, one colour) with its two-item menu, "Open Deluno" and "Quit Deluno", while Deluno sets itself up and the picker runs; Weir was installed through the picker, its installer's sha256 is in the log, and it answers on this PC only. |
| nzbgetsetup | `nzbget.setup-twice` | #1257. The harness has already set the Usenet provider once; this sets it again through Deluno's API (`POST /api/download-clients/usenet-provider`), the "set the provider again" case, before any film. NZBGet is then asked directly (its JSON-RPC, status and log): downloads are not paused, its log holds no "Invalid option" and no "errors in configuration", and Deluno shows no "settings have errors" warning on it. Run alone: `-Scenarios nzbget.setup-twice`. |
| films | `film.<client>`, `film.extra-left-out` | A film is sent, downloaded, handed to Weir with its completed path, cleaned, imported and written up in Activity. A film with a small extra: the extra is left out of the hand-off and out of the library, and stays in the client's folder. |
| removal | `remove-copy.<client>.folder`, `remove-copy.<torrent client>.file` | "Remove this copy" on a finished download leaves no folder, file or extra behind and does not touch the library file; removing it twice reads as a failure and writes no second Activity row. |
| resend | `resend.before\|during\|after.<client>` | "I don't want it", then a resend of the same release to the same client: before Weir starts the first send, while Weir works on it, and after Weir has finished it. The new hand-off settles by itself with one import, the first send is closed, nothing is left unmatched in Weir's output, and the closing line tells one story. `resend.single-file.takeover` (qBittorrent, one single file; #1262): sent again while Weir works on it, Weir's final answer for the resend is `completed` with no output path (asked of Weir itself); Deluno imports the file from the output folder at the source's path below the watched folder, its Activity says it found the cleaned file itself, one copy is in the library and nothing is left in Weir's output. |
| stuck | `stuck.zero.<client>`, `stuck.short.<client>`, `stuck.peers.<torrent client>`, `stuck.slow.<usenet client>` | Downloads stuck at 0 bytes, stuck just short of the end, with too few peers and too slow are each struck at their own count, with the plural wording right (1 check, 2 checks), replaced, and a download at 99.99% never reads 100% or "Not handed over". |
| keepseeding | `keepseeding.moved.<torrent client>`, `keepseeding.public-removed.<torrent client>` | #1240, on qBittorrent, Transmission and Deluge (rTorrent and uTorrent are not installed in the VM). A torrent stuck just short of the end from an indexer the suite marks private, struck by No progress with the rule set to move it, is moved to the keep-seeding category and not deleted: the client itself (not Deluno) still holds it in that category, its data is on disk, it is gone from Transfers and stays gone through another check, no problem is raised, and Activity says it was moved, to what, on which client and tracker, and that Deluno is looking for another copy. A public tracker's torrent with the same strike is removed and replaced as before. |
| outage | `outage.idle`, `outage.inflight` | Weir quits (from its tray menu), with nothing in flight and with a hand-off in flight: the Deluno tray icon (hover text and dot), System and the Dashboard each go amber within a minute and come back within a minute of Weir answering; the in-flight hand-off ends clearly. |
| tryagain | `tryagain.<client>` | A hand-off that failed while Weir was down: with the drawer read every 200 ms after "Send to Weir ... again", the old failure never shows at any step, the header reads "Sending to Weir ... again" at once and follows the real hand-off within 10 seconds. |
| vanish | `vanish.before.<client>`, `vanish.waiting.<client>` | Files deleted before the hand-off read "Files are gone" within 30 seconds and are never handed off; files deleted while the hand-off waits end it as "source gone" with nothing imported and nothing left in Weir's output. |
| problems | `problems.dismiss-everywhere`, `activity.one-story` | A problem shown in several places (Transfers, the bell, System) clears everywhere within 15 seconds of being dismissed in one; every hand-off's closing line in Activity tells one story (never "imported" and "will not import" together, no doubled full stop, one line per hand-off). |
| seasonpack | `seasonpack.no-library` | #1188 follow-up. A show in a TV library of its own owns S01E01 and S01E02; the library's folder is then changed, so the two files sit outside every TV library; a season pack for the same two episodes is imported as a replacement with the exact episode-to-file manifest (overwrite and force replacement set). The old files are hashed first. Afterwards both are still on disk (left in place, or at the `.deluno-pack-backup-` name the pack path gives them while it places the new files), byte for byte, and Deluno's log says, once for each file, that the replaced file was left because no library matched it and nothing was deleted. The new files are under the library's new folder. The words come from `ImportPipelineService.ExecuteTvPackAsync`; Deluno says them in its log only, not in Activity. |
| layout | `layout.390-folder-chain` | #1191. A library routed to qBittorrent whose downloads folder disagrees with the client's category folder gives a real folder-chain problem (confirmed from the folder check itself); Health raises it as "The <library> library's category and save path", with its cause and fix naming the library. Every screen of the smoke walk (the sidebar screens, every Settings page and area tab, the guided setup) and a film's page is then opened at 390 px wide in the VM's Edge over DevTools (never a window on the workstation): none scrolls sideways or has a control outside the screen, and a screen that lists problems shows the folder-chain row with the library's name. The screen list is compared with the router by `Test-ScenarioSuite.ps1`. |
| github | `github-limit.messages` | #1181. The VM uses GitHub's anonymous hourly allowance up (anonymous GETs of one public repository's page, at most 70, stopping at the first answer that says none are left; `/rate_limit` is free and is only read). If that cannot be done from the VM's address, the scenario passes as "Not applicable" and says why. Then: the tracker definitions' Update now (`POST /api/trackers/definitions/update-check/run`, and the button on Settings > General) names GitHub's limit with the reset in the viewer's local time, never UTC; and the connections' version check (`GET /api/connections/versions`, and the line beside a connection's name) says "can't look for a newer version until <local time> (GitHub's limit)". The connections' check reads github.com's free release feed first, so the limit shows only when that feed cannot be read and nothing was read before: the scenario points github.com at the VM in its hosts file and restarts Deluno from its tray menu, then puts the hosts file back. The allowance stays used up for up to an hour, so this is the last group in the VM. |
| githubpicker | `github-limit.picker` | #1181. Run by the host on a clean machine, after the groups and before the update phase. The VM uses the allowance up before Setup runs, then Setup and the first-run picker run the way a person does them. Weir is installed through github.com's `releases.atom` feed: the install log names Weir's installer and sha256, says the checksum matches the vendor's and never says the limit stopped Weir, and the installed Weir is one of the newest three releases the feed lists. Other components that need the API may fail in this phase, which is expected and noted. Needs only `-InstallerPath`, `-Version` and `-Commit`: `-Scenarios github-limit.picker` needs no install of the candidate in the VM. |
| update | `update.over-running-previous` | Run by the host last of all, after the groups, the log collection and the after-suite script, on a machine of its own: it restores the clean checkpoint, installs the PREVIOUS release silently, starts it the way a person does and gives it a library, a download client and Activity rows through its API. Then the candidate's Setup runs silently over the running previous release (never over the candidate the groups used: that would be a downgrade onto a newer schema). Nothing starts Deluno afterwards. It must come back by itself, as a new process, answering, as the candidate; its start marker reads "ready" for the new process; its start log shows no retry and no error; and its download clients, libraries and Activity are the same. It assumes Velopack's Setup, run silently over a running instance, starts the new build itself (as Weir saw in #956). "Deluno was never started again after Setup" (the installer did not relaunch it) is told apart from "started again but never answered" (a hung start, reported with the start marker's phase and step). Needs `-PreviousInstallerPath` (and `-PreviousVersion` unless the file name carries it); without it the phase and the verdict fail, saying so. (#1256)

The controlled films are made from a small test video made in the VM, named like releases and offered by a local Torznab feed and the Usenet stub, so nothing depends on a tracker or the internet. They are sent with "Send anyway" to the one client a scenario means, by routing the library to that client alone.

### Not scripted yet

These checklist lines below are still done by a person, because the suite does not cover them: the guided setup's Yes/No screens (step 3), public trackers and 1337x (step 4), a title's own profile (step 7), remove a title and restore it (step 8), Weir's pause-and-repeat screens (step 9), reaching Weir from another computer (step 10) and the screens at three widths (step 11). The suite sets Deluno up through the API, so it does not prove the guided setup's screens. Anything a person finds on those lines becomes a scenario.

## The checklist, in run order

The suite covers steps 1, 2 (the clients' folders, through the films), 5, 6 and 8 (failure and recovery) as listed above. The lines it does not cover are named under "Not scripted yet". Copy this list into the run's record and tick each line with what was seen.

1. **Install.**
   - [ ] The clean checkpoint is reverted.
   - [ ] The exact-SHA Deluno installer runs. In the picker, tick the five download clients plus Weir. Weir comes from the picker's own lookup of its latest release, and the run record names Weir's version and its installer's sha256.
   - [ ] The picker shows progress and a finish screen, and offers to open Deluno.
   - [ ] Weir answers on this PC only, and its tray starts silently.
2. **Clients come up ready.**
   - [ ] Each client has the Movies and TV categories and the documented folders (`C:\Downloads\Completed\<cat>`, `C:\Downloads\Incomplete\<client>`).
   - [ ] Deluge's Label plugin is on.
   - [ ] Library Routing's folder check shows every folder as Matches.
3. **Account and guided setup, end to end.**
   - [ ] Every automatic choice is asked as Yes or No, and saved as answered.
   - [ ] **Connect Weir** works: it creates Weir's account, and Weir's Movies and TV workflows are set up from Deluno and linked, with the Folder chain all ✓ and nothing typed in Weir.
4. **Public trackers.**
   - [ ] At least three test Healthy, and the result is kept after Add.
   - [ ] Routing is automatic.
   - [ ] 1337x works through the Cloudflare helper when one is set up. Without one, its test says plainly that a helper is needed.
5. **Usenet** (round two on).
   - [ ] The provider is entered once.
   - [ ] SABnzbd and NZBGet show Healthy only after it is set.
   - [ ] At least one Newznab indexer is Healthy.
6. **The real path.** One torrent film, one Usenet film, one TV episode, and one 4:3 film (Nosferatu, 1922, 1440×1080). For each:
   - [ ] Download, then hand-off to Weir with the completed path. Weir shows it moving through its lanes: at least Queued, Analysing and Processing. Incoming and Delivering can pass too quickly to see. Weir cleans it, and Deluno imports it, renamed.
   - [ ] Posters show.
   - [ ] The download client's folder still holds every original file: the video, the `.nfo` and every other sidecar.
   - [ ] Activity tells the story in plain words, in both apps. Weir's entry is correct: the tracks kept and removed, the size before and after, and what was saved.
7. **A title's own profile is honoured** in its search.
   - [ ] 4K and 720p are each targeted correctly, and each title's page shows its own profile as the target.
8. **Failure and recovery.**
   - [ ] A stalled torrent is replaced, and only it.
   - [ ] A failed hand-off sits under Needs action, is never deleted by the stall rules, and **Try again** from the download's drawer works.
   - [ ] Remove a title with its files, restore it from the recycle bin, and the title is back.
9. **Weir's pause and repeats.**
   - [ ] Pause Weir "until I resume" with **Keep looking for new files** on. A hand-off during the pause waits, and nothing is processed or removed. On resume it is processed once. Weir's Activity shows Paused and Resumed.
   - [ ] In Weir, **Process again** on a file already cleaned says Weir already cleaned this file and skipped it, with no second output. Deluno never sends a finished hand-off a second time, so the repeat is started from Weir.
10. **Reaching Weir from another computer.**
    - [ ] In Weir's **System › About**, **Devices on my network** makes Weir reachable from another computer, with the VM's network set to Public.
11. **Screens.**
    - [ ] In Deluno, at 1920 and 1366 wide and at phone width: the poster Search and Refresh buttons work; Transfers, Activity, the Dashboard and System are clean, and every counted problem can be cleared on screen.
    - [ ] In Weir, Logs shows no unexpected warnings or errors, and every Activity entry has a readable title.
    - [ ] No issue numbers or old page names appear in either app's copy.
12. **Pass record.** The suite records it. A failed run records `state=failure` on that commit, so an earlier success can never be used by mistake. By hand (a run that was not the suite's), the same command:

    ```bash
    gh api repos/<owner>/<repo>/statuses/<sha> -f state=success -f context=golden-path -f description="Golden path passed" -f target_url=<checklist>
    ```

## What the install script covers

The install half of the suite is Deluno's `scripts/golden-path/Invoke-GoldenPath.ps1 -RigHost <host> -InstallerPath <Setup.exe> -Version <version>` does the mechanical part of step 1 and leaves the VM running for the rest. It restores the clean checkpoint, starts the VM and waits for PowerShell Direct, copies the installer in (and checks its SHA256), installs it with `--silent`, starts Deluno in the VM's signed-in desktop session, and checks that the installed build is the version under test, that Deluno answers on its port, what the first-run state is, which components are installed (with versions), where their data folders are and which ports listen. It writes `golden-path-<version>-<date>.md` under `artifacts/`, one line per step: passed, failed or manual.

Run with `-Interactive`, as the suite does, it installs the way a person does (Setup.exe in the desktop session) and ticks the component picker with `Invoke-ComponentPicker.ps1`. Run silently (the default) it cannot tick the picker: Setup.exe and the app take no component choice on the command line, and a silent Setup does not run the first-run hook that shows the picker or sets the firewall rule.

## The Usenet film

The VM has no news server and no Usenet account, and no real login is ever used. Step 5's indexer line and step 6's Usenet film use a stub that runs inside the VM, the way the torrent films use a small local Torznab feed: a news server on `127.0.0.1` (port 1119) that serves a Creative Commons film (Big Buck Bunny, 2008) as yEnc articles, and a Newznab indexer on `127.0.0.1` (port 1180) that offers it with its `.nzb`. The film is about 1.58 GiB and named for WEB 1080p. That fits Deluno's own size rule both ways: above its minimum for that quality when the runtime is unknown (1.5 GB), and below its maximum for Big Buck Bunny's real 8-minute runtime (about 1.67 GB). So Deluno picks it by itself and the run needs no "Send anyway". Run Deluno's script with `-UsenetStub` (and `-DelunoUsername`/`-DelunoPassword`, the run's throwaway login, once the account exists). It makes the release with the ffmpeg Deluno ships, starts both stubs in the signed-in session, and points Deluno at them through Deluno's own API. Without the login it prints the one command to run later (`Set-UsenetStubInDeluno.ps1`, in `C:\golden\usenet-stub` in the VM). The stub's code is `scripts/golden-path/usenet-stub/` in Deluno; Weir needs nothing from it, because Weir only sees the finished download in the client's folder.

## After the run

The tag is made on the commit its own app's suite passed, through `scripts/tag-release.ps1`, and each release's `golden-path` job (`scripts/verify-golden-path-for-release.mjs`) confirms the record before anything is published. Issues fixed in that release close with the run's evidence in the closing note.
