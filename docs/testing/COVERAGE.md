# Coverage register

The owner's bar is that no behaviour of Weir goes untested. This page and `coverage-register.json` make that a number
that a script checks, instead of a claim. Deluno keeps a register of the same shape; the schema below is the shared one, the
ids under `contract.` are shared with it, and the proof types follow each app's own test runners.

## The schema (shared with Deluno's register)

`coverage-register.json` is `{ "version": 1, "generatedFrom": string, "areas": { <area>: { "code": string, "name": string } }, "rows": [ ... ], "gapIncrease"?: { "from": number, "to": number, "reason": string } }`.
(`gapIncrease` is optional and rare: see rule 5.) One row is one behaviour a person or another app can rely on:

| Field | Required | Allowed values |
| --- | --- | --- |
| `id` | yes | A unique string, never reused: `area.screen-or-subsystem.behaviour-slug`. Contract rows use the shared form `contract.<subject>.<behaviour>`. |
| `area` | yes | A key of `areas`. |
| `behaviour` | yes | The behaviour in a sentence. |
| `kind` | yes | `screen action`, `state`, `API route`, `job`, `integration-operation`, `failure-mode`, `migration`, `install-update`, `tray`, and Weir's own `setting`, `event`, `file-lifecycle`. |
| `severity` | yes | `high` (data loss, wrong file or media handling, security, or a main flow), `medium` (a feature not working), `low` (cosmetic). |
| `touches` | yes | `other-app` (Deluno, Sonarr, Radarr, a download client, GitHub, the artwork service), `disk` (media files: clean, move, hard-link, delete, back up, restore), `install` (install, update, uninstall, tray, Docker), `none`. |
| `required` | yes | Derived, and the check refuses any other value: `real` for a `high` row whose `touches` is not `none`; `inproc` for every other row. |
| `vm` | no | Only `true`, and only on a row whose `touches` is `disk` or `install`. Marks a row that only a real install can prove: NTFS-specific behaviour (alternate streams, case rules, long paths, ACLs), network shares (UNC/SMB/NAS), cross-drive or cross-volume moves and hard links, the installer, the update, the tray and Windows service mode. A `vm` row reaches `real` by a `scenario` proof alone; `real-fs` and every other type stay `inproc` for it. |
| `proofs` | yes | A list of `{ "type", "file", "name" }`. **Empty when the row is a gap.** |
| `partial` | no | Proofs that touch the behaviour but do not prove it. Not counted; kept so a gap shows where to start. Checked like `proofs`. |
| `needs` | no | `{ "kind", "test" }`: what would close the gap. |
| `issue` | no | The number of the open coverage issue that will close the row's gap. |
| `covers` | no | The inventory keys the row accounts for (below). |
| `audit` | no | Where the row came from: an id from the 14 October 2026 audit, or `inventory` for a row added from the code. |

Row ids from the audit are in `audit.id` (`R-032`, `JOB-025`, `WEB-447`, `TRAY-036`, `PLAT-05`, `FL-21`, `INT-SONARR`), so the coverage issues
(which name them) can be followed to their rows.

### Proof types (Weir's set)

| `type` | The file | Level |
| --- | --- | --- |
| `xunit` | A test in `apps/server/tests` (Core, Infrastructure, Api) or in `apps/tray/Weir.Tray.Tests`. | inproc |
| `contract` | A test in `apps/server/tests/Weir.Contract.Tests`: a real server process over HTTP, with fake media managers and fake or real tools. | inproc |
| `e2e` | A Playwright test in `apps/server/tests/Weir.E2E.Tests`, driving a real browser against the real host. | real for a screen row when it intercepts no request, otherwise inproc |
| `vitest` | A `*.test.ts(x)` file in `apps/web/src`. | inproc |
| `node` | A `*.test.mjs` file in `scripts`. | inproc |
| `scenario` | A golden-VM scenario of `scripts/scenarios` (named by its id in `Weir.Scenarios.Catalog.ps1`). | real |
| `recording-replay` | A contract test that replays one of Deluno's recordings (`apps/server/tests/fixtures/recorded`). | real |
| `real-fs` | A C# test in `apps/server/tests` (not the E2E project) or `apps/tray/Weir.Tray.Tests` that runs Weir's real code on a real temp folder or a real SQLite file, with no fake. The check verifies it from the test's text (below). | real for a `disk` or `install` row that is not `vm`, otherwise inproc |
| `container-smoke` | A step of a packaging workflow (`.github/workflows`, named by its `- name:`) or a container script in `scripts` (a name containing `docker`, `compose` or `container`, named `"*"`) that starts the shipped container image on a real volume. The check verifies it from the step's text. | real for a `disk` or `install` row that is not `vm`, otherwise inproc |
| `package-smoke` | `scripts/smoke-windows-package.ps1` or the live audit (`apps/server/tools/Weir.LiveAudit`), run by the packaging jobs. | inproc |

A proof's `name` is a test, a test group or a class in `file` (for a scenario, its id); `"*"` means the whole file. An e2e test intercepts requests
when its body, or the part of its file before its first test, calls `RouteAsync`, `FulfillAsync` or `RouteFromHARAsync`; what cannot be decided
counts as intercepting. Weir's e2e tests intercept none today.

A `real-fs` claim is verified statically and never taken on trust. The test, or its class setup (the text before the class's first test), must create a real temp folder or database file
(`Path.GetTempPath`, `Directory.CreateTempSubdirectory`, `Path.GetTempFileName`, `TempDirectory`, `TemporaryFolder`, `MigratedDatabaseTemplate`, `JobsTestDatabase`, `StoreFixture`, `MediaManagerFixture`, `WeirTestServer`/`WeirServer`/`SwapWorld`/`LibraryCleanWorld`/`LeasedJobScenario`/`LibrariesPartBFilm`/`Scenario` started, or one of the contract suite's server fixtures),
do file or database work on it (`File.`, `Directory.`, `FileInfo`, `CreateHardLink`, `SqliteConnection`, `SqliteDatabase`, `UnitOfWork`, a migration), and name no fake or in-memory file system or store (`ISwapFileSystem`, `SwapScenario`,
`IFolderProbe`, `ISwapJournal`, `Fake*` file system, storage, store or journal, `InMemory*`, `:memory:`, a mocked file or storage interface). A class name, a fragment or `"*"` is judged over every test it covers, and every one must qualify.
What the check cannot decide is refused: the proof is reported, counts as `inproc`, and is counted in the totals below; `--strict` fails on it.

A `container-smoke` claim is verified the same way. The named workflow step (or the whole script) must run `docker run` or `docker compose up`; a step that only builds, scans or inspects an image is refused.
It exists so the Docker rows, which no VM scenario reaches, can reach `real` by a CI step that starts the shipped `compose.yaml` and the previous release image on a real volume (#986).

### Contract ids

Weir's side of the Deluno-Weir contract (`docs/deluno-weir-contract.md`) uses exactly Deluno's ids, area `contract`: `contract.capabilities.answered`,
`contract.handoff.accepted`, `contract.events.completed`, `contract.download-destinations.libraries` (the library-not-found reply sits inside it), and so on. A row is
proven here by a `recording-replay` test that replays Deluno's recording of the same exchange.

Weir has no side in 21 of Deluno's contract rows, so its register leaves them out: the five `connection-refused` rows (Deluno's behaviour when Weir does not
answer), the three `handoffs.*` rows, the three `import-original.*` rows and the ten `send-again.*` rows (replies to calls Weir does not make; each recording
is listed with the route Weir does not call in `apps/server/tests/fixtures/recorded/deluno-side-no-weir-behaviour.json`). When Weir starts making one of those calls,
that recording has to be replayed (`scripts/check-recorded-replays.mjs` fails until it is), and the row is added then.

## Proof levels

- **real**: a `scenario`; a `recording-replay`; a verified `real-fs` test or `container-smoke` step, for a row that touches `disk` or `install` and is not `vm`; or, for a screen row only, an `e2e` test that intercepts no request.
  A `vm` row (only a real install can prove it) is real by a `scenario` alone.
- **inproc**: any other proof. It runs Weir's code, but the other side is Weir's own stand-in.
- **none**: the row is a gap.

A row is **proven** when its best proof reaches `required` and it names no `issue`; otherwise it is a **gap**. A row proven in part keeps its `proofs`, says in `needs` what is left, and names the issue that will prove the rest.

## The rules

1. **Everything is mapped.** `scripts/coverage/inventory.mjs` reads the source for every API route (`MapV1`, `MapApi`, `MapMethods`), job kind, hosted service, periodic
   task, setting (every environment variable the server or tray reads, and every field of the stored settings models), Activity event type, web route (the router and
   the setup areas), web control (a button, form, field or link in a screen that carries a `data-testid`), tray menu item and golden-VM scenario. Every item must be in
   the `covers` of some row. New code does not arrive unmapped: CI fails (`scripts/check-coverage-register.mjs`), and `--stubs` prints the rows to add. A control that is not
   something a person operates (a heading, a panel, a row) is not an action and is not read.
2. **Everything mapped still exists.** A row that covers something the code no longer has fails CI.
3. **Every proof exists.** The file is in the repository, the name is in the file, and the type is true of it. A renamed or deleted test fails CI until the row is fixed; the row is then a gap.
4. **Every gap names its issue.** A row with no proof carries the number of the open coverage issue that will prove it. A row whose proofs reach the level and whose issue has landed loses the issue.
5. **The gaps only go down, measured against the branch a change goes into.** A gap is a row whose proofs do not reach `required`, or that names an `issue`. CI counts the gaps in the
   register on the base branch (the pull request's base commit, or the previous commit on a push to main) and fails when this branch has more, or when a row names an issue that is closed.
   No count is stored in the register, so two pull requests that each close gaps do not edit the same line and cannot conflict on it. A rise that cannot be avoided is written under
   `gapIncrease` as `{ "from": <gaps on the base>, "to": <gaps now>, "reason": "..." }`, in a pull request of its own, the same rule as a lowered coverage floor
   (`docs/coverage/floors.json`); the entry only counts for exactly that rise and is removed in the next pull request. With no base to compare with (a first run) the check passes and says so.
6. **`--strict` fails on any gap.** Weir turns it on in CI once the gaps are closed.

Gaps that are not on an issue yet are counted in the statistics (below); they are rows the 14 October audit called covered, but whose proofs are below the level the shared rule asks for (a high row that
touches another app, the disk or an install needs a real proof). Each needs an owner's decision on how it is proven, and then an issue.

## Commands

```
node scripts/check-coverage-register.mjs               # the check CI runs
node scripts/check-coverage-register.mjs --gh          # also checks that every issue a row names is open (needs gh and a token)
node scripts/check-coverage-register.mjs --base origin/main   # also fails on more gaps than the register at that ref has (CI passes this)
node scripts/check-coverage-register.mjs --strict      # also fails on any gap
node scripts/check-coverage-register.mjs --list-gaps   # every gap, high first
node scripts/check-coverage-register.mjs --stubs       # row stubs for code that has no row
node scripts/check-coverage-register.mjs --report      # writes the statistics to artifacts/coverage-register.md (not committed)
node scripts/coverage/inventory.mjs --list             # the inventory
node --test scripts/check-coverage-register.test.mjs   # the check's own tests
```

When you add a route, job, setting, event type, web route, screen control, tray menu item or scenario: add its row (with the key in `covers` and the proof that proves it) in the same change.
When you add a test that closes a gap, put it in `proofs` and drop the row's `issue` and `needs`; nothing else in the register changes. When a test is renamed, change the proof with it.

## Statistics

The totals (rows and gaps by severity, area and best proof, the real-fs and e2e counts, the inventories) are not kept in this page, because a number that every change moves makes
every second pull request conflict. The check writes them to the job summary of the CI run (`repo-checks`), and CI uploads the same text as the `coverage-register` artifact. Locally,
`node scripts/check-coverage-register.mjs --report` writes it to `artifacts/coverage-register.md`, which is not committed.
