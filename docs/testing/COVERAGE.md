# Coverage register

The owner's bar is that no behaviour of Weir goes untested. This page and `coverage-register.json` make that a number
that a script checks, instead of a claim. Deluno keeps a register of the same shape; the schema below is the shared one, the
ids under `contract.` are shared with it, and the proof types follow each app's own test runners.

## The schema (shared with Deluno's register)

`coverage-register.json` is `{ "version": 1, "generatedFrom": string, "gapCeiling": number, "areas": { <area>: { "code": string, "name": string } }, "rows": [ ... ] }`.
(`gapCeiling` is Weir's addition: see "The gaps" below.) One row is one behaviour a person or another app can rely on:

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
5. **The gaps only go down.** A gap is a row whose proofs do not reach `required`, or that names an `issue`. `gapCeiling` is their number, and CI fails when there are more (so a gap cannot be added), when there are fewer
   (so the ceiling is lowered in the same change), when there are more than on the branch a pull request is going into, or when a row names an issue that is closed.
6. **`--strict` fails on any gap.** Weir turns it on in CI once the gaps are closed.

Gaps that are not on an issue yet are counted below; they are rows the 14 October audit called covered, but whose proofs are below the level the shared rule asks for (a high row that
touches another app, the disk or an install needs a real proof). Each needs an owner's decision on how it is proven, and then an issue.

## Commands

```
node scripts/check-coverage-register.mjs               # the check CI runs
node scripts/check-coverage-register.mjs --gh          # also checks that every issue a row names is open (needs gh and a token)
node scripts/check-coverage-register.mjs --base origin/main   # also fails on more gaps than the register at that ref has
node scripts/check-coverage-register.mjs --strict      # also fails on any gap
node scripts/check-coverage-register.mjs --list-gaps   # every gap, high first
node scripts/check-coverage-register.mjs --stubs       # row stubs for code that has no row
node scripts/check-coverage-register.mjs --write-doc   # rewrites the totals below
node scripts/coverage/inventory.mjs --list             # the inventory
node --test scripts/check-coverage-register.test.mjs   # the check's own tests
```

When you add a route, job, setting, event type, web route, screen control, tray menu item or scenario: add its row (with the key in `covers` and the proof that proves it) in the same change.
When you add a test that closes a gap, put it in `proofs`, drop the row's `issue` and `needs`, and lower `gapCeiling`. When a test is renamed, change the proof with it.

## Totals

<!-- coverage:generated:start -->
Generated by `node scripts/check-coverage-register.mjs --write-doc` from `coverage-register.json`. Do not edit by hand.

**1864** behaviours. **1009** reach their required proof level (54%); **855** are gaps, **82** of them high; **0** gaps are not on an issue yet.

### By severity

| Severity | Rows | Reach their level | Gaps |
| --- | ---: | ---: | ---: |
| high | 174 | 92 | 82 |
| medium | 1325 | 876 | 449 |
| low | 365 | 41 | 324 |

### High rows by what they touch (these need `real`, except `none`, which needs `inproc`)

| Touches | High rows | High gaps |
| --- | ---: | ---: |
| other-app | 63 | 31 |
| disk | 54 | 23 |
| install | 14 | 11 |
| none | 43 | 17 |

### By best proof a row has

| Best proof | Rows |
| --- | ---: |
| none (a gap) | 544 |
| inproc | 1021 |
| real | 299 |

E2E proofs: **191**; unstubbed (can reach `real` for a screen behaviour) **191**; stubbed **0**; not decidable statically, counted as stubbed **0**.

Real-fs proofs: **191** claimed; **191** verified and counted as real; **0** refused (the check could not verify a real temp folder or database file with no fake, so they count as inproc); **0** verified but without effect (the row is "vm", or does not touch disk or install). **75** rows are "vm" (only a real install can prove them, so they need a scenario); **58** of them are still gaps.

Container-smoke proofs: **0** claimed; **0** verified and counted as real; **0** refused (the named step or script does not start the container image, so they count as inproc); **0** verified but without effect (the row is "vm", or does not touch disk or install).

### By area

| Area | What | Rows | Reach their level | Gaps | High rows | High gaps | No proof | Inproc | Real |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| api | API routes and what applies across them | 160 | 131 | 29 | 17 | 5 | 24 | 102 | 34 |
| contract | Deluno-Weir contract: Weir's side of every call, reply and report (ids shared with Deluno) | 57 | 32 | 25 | 39 | 16 | 25 | 0 | 32 |
| events | Activity event types | 44 | 37 | 7 | 0 | 0 | 7 | 33 | 4 |
| file-lifecycle | File lifecycle rules (docs/file-lifecycle-contract.md) | 45 | 29 | 16 | 38 | 9 | 8 | 8 | 29 |
| integrations | Integrations crossed with their operations | 29 | 7 | 22 | 2 | 2 | 17 | 6 | 6 |
| platform | Install, update, uninstall, ports, firewall, Docker, packaging | 29 | 10 | 19 | 6 | 5 | 19 | 2 | 8 |
| scenarios | Golden-VM scenarios | 16 | 1 | 15 | 0 | 0 | 15 | 0 | 1 |
| services | Jobs, hosted services, periodic tasks and schedules | 56 | 42 | 14 | 2 | 0 | 13 | 29 | 14 |
| settings | Settings: stored fields and environment variables | 133 | 82 | 51 | 7 | 1 | 47 | 63 | 23 |
| tray | Windows tray: menu, icon, balloons, supervision, updates | 91 | 39 | 52 | 8 | 6 | 21 | 51 | 19 |
| web-activity-library | Web: Activity and Library | 194 | 74 | 120 | 12 | 12 | 78 | 98 | 18 |
| web-dashboard | Web: Dashboard (Live and System views) | 198 | 136 | 62 | 1 | 1 | 25 | 149 | 24 |
| web-routes | Web routes and the controls that carry a test id | 111 | 93 | 18 | 0 | 0 | 18 | 93 | 0 |
| web-setup-connections-performance | Web: Setup: Connections, Alerts, Performance | 159 | 56 | 103 | 17 | 10 | 55 | 96 | 8 |
| web-setup-workflows-rules | Web: Setup: Workflows, Schedule, Rules, Playback devices | 209 | 81 | 128 | 6 | 6 | 73 | 118 | 18 |
| web-shell | Web: boot, shell, sign-in, first-run setup, wizard, shared widgets, live connection | 174 | 90 | 84 | 11 | 4 | 43 | 89 | 42 |
| web-system | Web: System: About, Backups, Security, Logs | 159 | 69 | 90 | 8 | 5 | 56 | 84 | 19 |

### Gaps with an open issue

| Issue | Rows | High rows |
| --- | ---: | ---: |
| #979 | 1 | 0 |
| #981 | 7 | 0 |
| #982 | 8 | 0 |
| #985 | 2 | 1 |
| #986 | 2 | 2 |
| #987 | 1 | 1 |
| #988 | 3 | 2 |
| #989 | 5 | 2 |
| #990 | 1 | 1 |
| #991 | 10 | 5 |
| #992 | 4 | 2 |
| #993 | 4 | 4 |
| #994 | 14 | 3 |
| #995 | 1 | 0 |
| #1000 | 7 | 0 |
| #1001 | 3 | 0 |
| #1002 | 4 | 0 |
| #1003 | 4 | 0 |
| #1004 | 2 | 0 |
| #1005 | 1 | 0 |
| #1006 | 6 | 0 |
| #1007 | 9 | 0 |
| #1008 | 9 | 0 |
| #1009 | 2 | 0 |
| #1010 | 5 | 0 |
| #1011 | 4 | 0 |
| #1012 | 1 | 0 |
| #1013 | 2 | 0 |
| #1014 | 1 | 0 |
| #1015 | 6 | 0 |
| #1016 | 1 | 0 |
| #1017 | 30 | 16 |
| #1018 | 2 | 0 |
| #1019 | 4 | 0 |
| #1020 | 3 | 0 |
| #1021 | 3 | 0 |
| #1022 | 12 | 0 |
| #1023 | 3 | 0 |
| #1024 | 10 | 0 |
| #1025 | 7 | 0 |
| #1026 | 11 | 0 |
| #1027 | 6 | 0 |
| #1028 | 8 | 0 |
| #1029 | 9 | 0 |
| #1030 | 15 | 0 |
| #1031 | 12 | 0 |
| #1032 | 5 | 0 |
| #1033 | 8 | 0 |
| #1034 | 8 | 0 |
| #1035 | 8 | 0 |
| #1036 | 7 | 0 |
| #1037 | 4 | 0 |
| #1038 | 12 | 0 |
| #1039 | 4 | 0 |
| #1040 | 3 | 0 |
| #1041 | 2 | 0 |
| #1042 | 1 | 0 |
| #1043 | 7 | 0 |
| #1044 | 4 | 0 |
| #1045 | 31 | 1 |
| #1046 | 20 | 0 |
| #1047 | 36 | 0 |
| #1048 | 48 | 0 |
| #1049 | 36 | 0 |
| #1050 | 34 | 0 |
| #1051 | 15 | 3 |
| #1052 | 36 | 0 |
| #1053 | 14 | 0 |
| #1054 | 25 | 0 |
| #1055 | 72 | 5 |
| #1056 | 32 | 3 |
| #1057 | 27 | 0 |
| #1058 | 26 | 0 |
| #1059 | 30 | 3 |
| #1064 | 6 | 0 |
| #1065 | 1 | 1 |
| #1066 | 2 | 2 |
| #1067 | 2 | 2 |
| #1068 | 8 | 8 |
| #1069 | 2 | 2 |
| #1070 | 9 | 9 |
| #1071 | 3 | 3 |
| #1072 | 1 | 1 |
| #1073 | 1 | 0 |

### Inventories (generated from source by `scripts/coverage/inventory.mjs`)

| Kind | Items |
| --- | ---: |
| route | 155 |
| job | 8 |
| service | 15 |
| task | 20 |
| setting | 190 |
| event | 44 |
| webroute | 27 |
| action | 97 |
| tray | 15 |
| scenario | 15 |
<!-- coverage:generated:end -->
