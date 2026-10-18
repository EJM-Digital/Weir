# PLAN: Weir's scenario suite runs in CI, with no VM and no rig

**Decided by the owner, 16 Oct 2026.** Once the release candidates are over there will be no test VM and no rig. Weir's scenario suite (`scripts/scenarios/`, 14 scenarios, run today on a Hyper-V VM) moves to GitHub's own runners, from a clean checkout, the way established teams do it. It matches Deluno's plan (`PLAN-E2E-IN-CI.md` in the Deluno repository).

## Where each kind of proof runs

| Kind | Runs on | How |
| --- | --- | --- |
| Weir on its own | `ubuntu-latest`, Docker Compose | The image this CI run built, from the shipped `compose.yaml`, with the real ffmpeg inside it. Scenarios drive Weir's API as the web app does and read the files with the container's own tools. |
| Weir with Deluno and the real download clients | `ubuntu-latest`, Docker Compose | Weir's image from this run. Deluno's published image, pinned by digest. qBittorrent, Transmission, Deluge, SABnzbd and NZBGet as published images, pinned by digest. Sonarr and Radarr images where a scenario needs them. |
| Windows-only behaviour | `windows-latest` | Silent install from the candidate installer, the service, start with Windows, the tray (UI Automation), the update from the previous release, NTFS behaviour and the recovery command. |
| Contracts with other apps | every push | Already in place: tests replay recorded real replies. |
| Drift in the other apps | scheduled, weekly | See "The drift job". |

## Every scenario and its home

The 14 scenarios of `Weir.Scenarios.Catalog.ps1`, and the ones written for CI alone (marked `catalogue: false` in `homes.json`, and listed after the catalogue's). `scripts/scenarios-ci/homes.json` is the record the code reads; `scripts/scenarios-in-ci.test.mjs` fails when this table and that file disagree, or when a scenario of the catalogue has no row. **No scenario is wholly unportable.** Two have a part that stays on Windows (the tray's side of Pause and resume, and the installed copy of the recovery command); those parts have their own Windows rows below.

| Scenario | Home | Shard | State | Why it lives there |
| --- | --- | --- | --- | --- |
| `update-over-installed-weir` | windows | windows-update | to-port | `Setup --silent` over the previous release's running install, with the tray's pre-update copy. Docker's upgrade smoke covers the volume half. |
| `workflows-from-deluno` | linux | with-deluno | to-port | Weir and Deluno speak HTTP only; Deluno's published image is the same Deluno. |
| `deluno-film-handoff` | linux | with-deluno | to-port | A hand-off followed through both apps, with the real clients in Compose. |
| `deluno-release-with-extra` | linux | with-deluno | to-port | The extra is read where it stays, through the download client's own API. |
| `deluno-resend` | linux | with-deluno | to-port | Deluno's send-again is an API call; both sides are read over HTTP. |
| `deluno-outcomes` | linux | with-deluno | to-port | The outcome each app recorded is compared; both are read over HTTP. |
| `fresh-install` | windows | windows-install | ported | Runs `Weir-win-Setup.exe` silently and starts the installed tray; a clean runner is the clean machine. It also reads the tray's two secrets on the real install. |
| `first-visit-account` | linux | weir-browser | ported | Nothing in it is Windows: a new browser context sees account creation, then sign-in. Chromium under Playwright takes the place of Edge. |
| `weir-only-film` | linux | weir-only | ported | The first shard. The server cleans a film with the image's own ffmpeg. |
| `process-again` | linux | weir-only | ported | The first shard. Asked to process a cleaned film again, the server skips it. |
| `pause-resume` | linux | weir-only | ported | The server hears the tray through a file in its data folder, and that watcher is the same on Linux. The tray's side stays with windows-tray. |
| `deleted-queued-file` | linux | weir-only | ported | A file deleted while Weir waits for it to settle; nothing in it is Windows. |
| `update-requests` | windows | windows-tray | to-port | Needs the real tray to take the update flag files. |
| `recover-locked-out-account` | windows | windows-install | ported | Runs the installed `WeirServer.exe recover` with nothing set, so it must find Weir's data folder itself. The Docker form is already proved by the Docker smoke. |
| `uninstall-keeps-the-data` | windows | windows-install | ported | Written for CI: the VM suite never ran the uninstaller. Uninstalls Weir stopped and running, and finds the data, the secrets and the account afterwards. |
| `library-clean-in-place` | windows | windows-files | ported | Written for CI. The installed server cleans a real film in a library folder at its own path on NTFS, with and without the original kept; a film another program holds open is left as it was and its clean booked for later. |
| `library-swap-crash` | windows | windows-files | ported | Written for CI. The installed server is ended by its process id at each of the swap's eight points and started again: one intact film under its name, nothing left over, and the interrupted clean finishes. |
| `publish-across-volumes` | windows | windows-files | ported | Written for CI. The output is on a second real NTFS volume (a virtual disk): a cleaned film appears at its final name only whole, and an unchanged film is copied where a hard link is refused. |
| `keep-original-across-volumes` | windows | windows-files | ported | Written for CI. The original is kept on another real volume (copy, check, delete), also with the server ended between the steps. |
| `full-disk` | windows | windows-files | ported | Written for CI. A small real volume fills during a pass: the pass waits for room, writes and records nothing, and completes when room returns; so does an unchanged film. |
| `network-share` | windows | windows-files | ported | Written for CI. A real SMB share (New-SmbShare) used by its UNC path: found and used, removed (reported, nothing lost), restored. |
| `network-share-hang` | windows | windows-files | ported | Written for CI. A share that stops answering without refusing, through a relay that can stop passing data: reported, other workflows and the System page unaffected. |
| `network-access` | windows | windows-files | ported | Written for CI. Other devices switched on and off through the API on the installed Weir; the listener, the firewall rule for every profile and the machine's own address are read. |

### Windows-only behaviour with no scenario today

The VM suite does not cover these; the package smokes (`smoke-windows-package.ps1`, `smoke-windows-recover.ps1`) and the manual checklist (`docs/smoke-checklists.md`) do in part. They need a home before the VM goes, so each gets a row in `windows-install` or `windows-tray` when that shard is built: the Windows service, start with Windows, the tray's own menu through UI Automation (including writing the pause request that `pause-resume` reads), and NTFS behaviour (hard links, cross-volume moves). Each is written as a scenario, added to the catalogue and to the table above when it is, so that the table stays the whole list.

## The shards

| Shard | Runner | Budget | What it holds |
| --- | --- | --- | --- |
| weir-only | ubuntu-latest | 15 min | Weir alone in the Docker image: films, Process again, Pause and resume, a deleted file. **Built.** |
| weir-browser | ubuntu-latest | 10 min | Weir alone, driven through a real browser (Chromium under Playwright, from `scripts/scenarios-ci/package.json`). **Built.** |
| with-deluno | ubuntu-latest | 20 min | Weir, Deluno and the real clients; split by client when it outgrows the budget, as Deluno splits `resend`. |
| windows-install | windows-latest | 20 min | Install, the installed server and its secrets, the recovery command, uninstall and reinstall. **Built.** Start with Windows needs the tray's own menu, so it waits for windows-tray. |
| windows-files | windows-latest | 25 min | What the installed server does to real files on real NTFS volumes and a real network share, and the setting that opens its port to other devices. **Built.** |
| windows-update | windows-latest | 20 min | The update from the previous release over a running Weir. |
| windows-tray | windows-latest | 20 min | The real tray through UI Automation, and NTFS behaviour. |

## The matrix

One job per shard in a matrix, each with a Compose stack of its own so shards cannot interfere. The workflow's matrix lists the shards that have a scenario ported (a test keeps it equal to `homes.json`), so porting the next scenario adds a row to the table and nothing to the workflow. `fail-fast` is off: a failing shard does not hide what the others found. Within a shard the scenarios run one after the other, each making its own workflow, and one that fails does not stop the next. Every scenario also fails if System > Logs gained a warning or error while it ran.

## Timing budget

Wall time is the slowest shard, aimed at 15 minutes, with a job limit of twice the budget. **The real default timings stay**: the settle time, the scan and the check intervals are not shortened; waits are polls that end the moment their condition holds, with a ceiling that is a hang guard. Speed comes from sharding and the build cache, not from shorter clocks. The first CI run of each shard writes every scenario's time to `results.json` (a workflow artifact); budgets are set from those measurements, not guessed, and a shard that outgrows its budget is split.

## Fixtures: what is shared with Deluno, and how

The shape is agreed with Deluno:

1. **No shared source files.** Neither repository copies or vendors the other's Compose files or scripts. No Deluno-specific code lives in Weir.
2. **Each repository consumes the other only as its published image, pinned by digest.** Weir's with-Deluno scenarios pull Deluno's image as `ghcr.io/jampat000/deluno@sha256:...`. Bumping the pin is a deliberate commit, and the weekly drift job also tries the newest tag and reports any difference.
3. **Generic fixtures have one owner,** who publishes them as a small fixture image pinned by digest. Deluno owns the indexer, Usenet and torrent fixtures. Weir owns the Weir-only fixtures: today the test film, which is made at run time by the image's own ffmpeg, so nothing is published. If Weir needs nothing beyond Deluno's image, nothing is shared at all, and that is preferred.
4. **Download clients** are always upstream published images, pinned by digest in Weir separately from Deluno's.

Weir's Compose files for the with-Deluno shard therefore live in Weir (`scripts/scenarios-ci/`), name only images, and read their references from one file.

## Pinning by digest

Every image a scenario starts other than Weir's own build is named `name@sha256:...` in `scripts/scenarios-ci/images.json` (Deluno, the five clients, Sonarr, Radarr, Deluno's fixture image, the Chromium image if one is used), and nowhere else. A test will fail on a reference without a digest, on a digest written anywhere but that file, and on an image the Compose files use that is not listed. A tag is kept beside each digest as a label so a person can read what was pinned. Digests are bumped in their own commit, after the drift job shows what changed. The first shard starts no other image, so the file arrives with the first shard that does.

## The drift job

A weekly workflow (`scenarios-drift.yml`, not built yet) starts, for each pinned image Weir talks to, the pinned digest and the newest tag, re-records the replies Weir depends on, and fails on any difference from the committed recordings. A difference becomes an issue; nothing is bumped silently. It covers Deluno's replies (the hand-off and outcome routes Weir calls and receives), Sonarr's and Radarr's, and the clients' status replies where Weir reads them. It runs on `ubuntu-latest`.

## Cadence

- **Every push:** the affected shards. Each shard will list the paths it depends on in `homes.json`, and the filter `ci.yml` uses to choose jobs will pick the shards from them. Today the one shard runs with the packaging smokes, whenever `Dockerfile`, `docker/**`, `compose.yaml`, `apps/server/src/Weir.Host/**` or the scenario files change, and on every push to main.
- **Before a tag:** the full matrix and the Windows shards on the tagged commit, or a pass carried from an ancestor whose shipped files are identical (the shipped-paths rule the owner agreed on 16 Oct).
- **Weekly:** the drift job, and a full matrix run to catch anything the paths filter missed.

## Retirement: twice green in CI

A scenario is **green twice** when it passed in two CI runs on `main`, on different commits, with no failing run of it in between. `homes.json` records the state of each scenario: `to-port`, `ported` (runs in CI) and `green-twice`. The VM suite stays the release gate until **every** scenario is `green-twice` and the release checks (`verify-golden-path-for-release.mjs`, `docs/release.md`) have switched to the CI record; then the VM suite is retired. A scenario that fails in CI after it was green twice goes back to `ported`, and the release waits for it.

## The first shard: built in this change

`scripts/scenarios-in-ci.mjs weir-only --candidate <image>` starts the shipped `compose.yaml` with the image CI built, makes the first account the way an operator does, and runs `weir-only-film`, `process-again`, `pause-resume` (the tray's request file written in the container) and `deleted-queued-file`, each with every check of the VM scenario (the film's tracks, the original and its `.nfo` left alone, the Activity lines, the job count, the output not rewritten). The job is `scenarios-linux` in `ci-packaging.yml`. Docker was not available where it was written, so its first real run is the first CI run of this change; the logic that decides things is covered by `scripts/scenarios-in-ci.test.mjs` against a small stand-in for Weir and the container.

### The windows-install shard

`scripts/scenarios-in-ci.mjs windows-install --setup <Weir-win-Setup.exe>` runs on a GitHub-hosted `windows-latest` runner, which is the clean machine: it refuses to run anywhere else, because it installs, uninstalls and runs the recovery command against the machine's own data folder. The Setup is the one the `windows-package-smoke` job built in the same workflow run. The job is `scenarios-windows` in `ci-packaging.yml`.

- `fresh-install`: Setup `--silent`, the tray started the way an unattended caller starts it, the server's version, no window, loopback only, and the tray's two secrets (`session.secret`, `credentials.secret`) on the real install: long enough, readable only by SYSTEM, the administrators and the installing account, different from each other, and the one the running server signs with.
- `recover-locked-out-account`: `WeirServer.exe recover` from the install with no variable set, which must find the data folder the installed server uses, list the account, reset the password and end the earlier sessions.
- `uninstall-keeps-the-data` (added for CI): uninstalls with Weir stopped, then with Weir running; the data folder, the secrets and the account are as they were, the shortcuts, the Run entry and the running mark are gone, and each reinstall signs in and finds the workflow.

The tray is started without its user interface, so what the tray shows is left to `windows-tray`. Right after each Setup the shard writes `update-settings.json` with `NotifyOnly`, the file the tray reads: a runner can reach the release feed, and a placeholder build (`0.0.1-dev`) is older than every published release, so otherwise the tray downloads the newest one and replaces the build under test the next time it starts. Every start of Weir in a scenario also checks it is still the build under test.

### The windows-files shard

`scripts/scenarios-in-ci.mjs windows-files --setup <Weir-win-Setup.exe>` runs on a GitHub-hosted `windows-latest` runner. Setup installs the build under test, and all but one of the scenarios start the installed `WeirServer.exe` themselves, each with a data folder and a port of its own, so a scenario can end it by its process id and start it again, or point it at another drive. The media is made and cleaned by the ffmpeg the package carries (`binfmpeg`).

- `library-clean-in-place`: Scan and Clean through the API on a real film; the file at the same path has only the English audio, nothing is left beside it, and with "keep the original" the original is under `.weir-originals` byte for byte. A film that another program holds open is left as it was; its clean is booked for a later look (5 minutes, the real backoff, which the scenario does not wait for).
- `library-swap-crash`: `WEIR_LIBRARY_SWAP_HOLD_AT` stops the swap at each of its eight points; the server is ended there, the files are compared with the plan of the contract suite (`SwapCrashPlan`), the server is started with no workers so only start-up recovery has touched anything, and then as on a real install so the interrupted clean finishes. Three points also run with the original kept.
- `publish-across-volumes`, `keep-original-across-volumes`, `full-disk`: a second NTFS volume is a virtual disk made with `diskpart` (the runner has no Hyper-V cmdlets); `full-disk` uses a small fixed one and fills it with the pass's own staged copy.
- `network-share`, `network-share-hang`: the share is `New-SmbShare` on a folder of the runner, used by `\localhost<name>`. The hang goes through a relay (`files-proxy.mjs`) that the SMB client reaches by an alternative port and that can stop passing data without closing anything.
- `network-access`: the installed Weir with its tray. Other devices is chosen through `PUT /suite/network-access`; the tray restarts the server and asks Windows for the rule. The listener, the rule (enabled, inbound, TCP, allow, every profile, the installed server) and the machine's own network address are read, then flipped back. **Not provable on one runner:** that Windows Firewall lets a packet from a second machine in, because traffic from a PC to its own address never meets the firewall; the rule stands in for it.

### Next

1. `with-deluno`: images pinned by digest, the Compose file, then `workflows-from-deluno` and `deluno-film-handoff` on qBittorrent, then the other four scenarios and the other clients.
2. `windows-update`, then `windows-tray` (the shards after `windows-install`).
3. The drift workflow, and the per-shard paths filter.

## Runners

Every job that today can run on the self-hosted runners (`weir-heavy`, the `WEIR_SELF_HOSTED_WINDOWS` switch) moves back to GitHub-hosted runners (`windows-latest`, `ubuntu-latest`) for good: GitHub advises against self-hosted runners on a public repository, and the runners are removed in the clean-up step. Until then no workflow a pull request can trigger may target `weir-heavy` for a fork. Today's guard (`!github.event.pull_request.head.repo.fork` in `runs-on`) together with the strictest fork-approval policy covers that, and a new job must carry the same guard or use a hosted runner. The new jobs in this plan use hosted runners only.

## Clean-up

When every scenario has passed twice in CI and the release checks have switched over, the rig goes to production and the machines are cleaned up. Each deletion is **listed first with its path, size and purpose, and goes to the owner for a yes before anything is removed.** Weir's list:

- the test VMs for the scenario suite, with their checkpoints and disks (the Deluno project drives the rig);
- the old copies of the repository kept beside the working one: release-candidate checkouts, backups, a copy made for a history rewrite, and the history bundles;
- stale worktrees and branches;
- the self-hosted runners on the spare PC: unset `WEIR_SELF_HOSTED_WINDOWS`, delete the runner registrations through the API, and provide a `Remove Weir runners.cmd` for the owner to run there.
