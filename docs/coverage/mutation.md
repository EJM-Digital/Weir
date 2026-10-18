# Mutation testing

Line coverage says a line ran, not that a test would notice if it were wrong. For the code that can lose a person's
film or lock them out, Weir also runs **mutation testing**: a tool changes the code one small step at a time (flips a
condition, drops a statement, swaps `+` for `-`, empties a string) and runs the tests that cover the changed line. A change
that makes a test fail is **caught**. A change that no test notices is a **survivor**, and every survivor is a test that
is missing. The **score** of a target is the share of the changes that were caught.

This page says what is mutated, how to read a run, and how to raise or lower a floor. Line coverage is in
[`README.md`](README.md).

## What is mutated

Each target is one job of the [Mutation workflow](../../.github/workflows/mutation.yml), one tool run over only the
files it names and the tests that cover them.

| Target | Tool | Files | Tests |
| --- | --- | --- | --- |
| `file-lifecycle-swap-preflight` | Stryker.NET | `SafeSwap` up to and including `Preflight`, and its support file | `Weir.Infrastructure.Tests` |
| `file-lifecycle-swap-prepare` | Stryker.NET | `SafeSwap.RunAsync` from its start to the permissions copy | `Weir.Infrastructure.Tests` |
| `file-lifecycle-swap-commit` | Stryker.NET | `SafeSwap.RunAsync` from recording the commit to its end | `Weir.Infrastructure.Tests` |
| `file-lifecycle-recovery` | Stryker.NET | `SwapJournal`, `SwapRecoverySweep` | `Weir.Infrastructure.Tests` |
| `file-lifecycle-moves` | Stryker.NET | `OriginalsMover`, `FinalPaths`, `ReleaseFolderRemoval` | `Weir.Infrastructure.Tests` |
| `handoff-intake` | Stryker.NET | `HandoffLedgerStore*`, `MediaManagerIntake*` | `Weir.Infrastructure.Tests` |
| `outcome-handling` | Stryker.NET | `HandbackOutcomes`, `HandoffCompletionReporter*` | `Weir.Infrastructure.Tests` |
| `write-gate-database` | Stryker.NET | `SqliteDatabase`, `WriteLockTurns` | `Weir.Infrastructure.Tests` |
| `write-gate-unit-of-work` | Stryker.NET | `UnitOfWork` | `Weir.Infrastructure.Tests` |
| `core-rules` | Stryker.NET | `SafeSwapRules`, `LibraryFolderPublishing`, `HandoffLedgerRules`, `HandoffPaths`, `HandoffSourceFiles`, `HandbackRules`, `CompletionReports` | `Weir.Core.Tests` and `Weir.Infrastructure.Tests` |
| `start-supervision` | Stryker.NET | `ServerHost*`, `StartWatch`, `StartProgress*` (the tray) | `Weir.Tray.Tests` |
| `web-api-client` | StrykerJS | `lib/api/client.ts`, `send-json.ts`, `auth-api.ts`, `activity-api.ts` and `lib/auth/queries.ts` | the tests under `src/lib/api` and `src/lib/auth` |
| `web-processing-files` | StrykerJS | the file, kept-file, rejected-file, remux, direct-play, maintenance, settings, stats, preview and scan request builders in `lib/processing` | the tests under `src/lib/processing` |
| `web-processing-libraries` | StrykerJS | the library, clean, folder-chain, manager, mode, redownload, setup and rule-set request builders in `lib/processing`, and `jobs-inspection/api.ts` | the tests under `src/lib/processing` |
| `web-settings-requests` | StrykerJS | `lib/settings/settings-api.ts` | the tests under `src/lib/settings` |
| `web-dialogs-library` | StrykerJS | `confirm-dialog.tsx`, the Library clean dialog and the workflow remove dialog | the tests that import them (`vitest related`) |
| `web-dialogs-activity-profile` | StrykerJS | the Activity remove dialog and the profile remove dialog (`profiles-tab.tsx`) | the tests that import them (`vitest related`) |

A target is one job, and a job has to finish inside its time limit, so the bigger areas are split into several targets.

A part of a file is a span of characters in the target's `mutate` list (`SafeSwap.cs{0..6789}`: Stryker.NET counts characters, not lines). When `SafeSwap.cs` changes, move the spans to the same places in the new text, so that the three together still cover the whole file.

The config of each target is `stryker-config.json` (Stryker.NET) or `stryker.config.json` (StrykerJS) in the `mutation`
folder of `apps/server`, `apps/tray` or `apps/web`. The mutated files are its `mutate` list; a new file in an area is
added there. The Stryker.NET version is pinned in `dotnet-tools.json` and StrykerJS in `apps/web/package.json`.

Stryker.NET runs the tests that cover each changed line (its per-test coverage). StrykerJS runs the web target's test
command once for each change, with `coverageAnalysis` off. Its Vitest runner is not used: version 10.0.0 selects the tests
to run by their names joined with a space, and Vitest 5 joins a test's name to its `describe` blocks with ` > `, so for any
test inside a `describe` it selects nothing and every change looks like a survivor. The command runner needs no test
names, and a time limit generous enough that a slow run is not counted as a catch (the check refuses a report where most
changes ended by timing out). For that reason the web survivors list "the related tests ran" in place of the tests that ran the line, and a line
no test runs shows as a survivor, not as "none run this line".

Stryker.NET's `Standard` mutation level is used, and the tests that need ffmpeg or mkvmerge skip themselves, as they do
in any run without the real tools. The contract suite and the E2E smoke are not run: they start the server as a
separate process, which a mutation run cannot swap code into.

## When it runs

Weekly (Sunday, 02:41 UTC) and on demand (Actions, Mutation, Run workflow). Never on a pull request: a target takes
tens of minutes, because Stryker first runs the whole test project once to learn which tests cover which line. To try
one target by hand, run it from its test project:

```
cd apps/server/tests/Weir.Infrastructure.Tests
dotnet tool restore
dotnet stryker -f ../../mutation/file-lifecycle/stryker-config.json -O ../../../../artifacts/mutation/file-lifecycle --concurrency 4
```

and for the web app, from `apps/web`: `npx stryker run mutation/web-api-client/stryker.config.json` (the report is
`reports/mutation/web-api-client/mutation-report.html`). Both take far longer on a busy machine than in CI, so run them
on a quiet one.

## Reading a run

The `check` job writes two things to its summary page:

- a table of every target: its score beside its floor, how many changes were caught, how many survived, and how many
  are on lines no test runs;
- the survivors of every target, grouped by file (the file with the most first, each one a test issue), then line by
  line: the line, the mutator, the change (the code and what it became) and the tests that ran that line without
  noticing. A line that "none run" is not tested at all.

The full list is `survivors.md` in the `mutation-survivors` artifact, and each target's own report (a page you can open
in a browser, and the JSON the check reads) is the `mutation-<target>` artifact.

A run fails when a score is below its floor, and then the log names every survivor of that target.

## The score and its floor

The score is the share of changes that could be judged and were caught: (killed + timed out) / (killed + timed out +
survived + not run by any test). Changes that did not compile, or that Stryker skipped, are left out. A change that makes
a test hang until its time limit counts as caught.

`mutation-floors.json` holds a minimum score per target, set from a measured run of the Mutation workflow (the run ids
are in `measuredFrom.runs`), rounded down to one decimal. A floor only goes up.

**Raising a floor.** When the `check` summary says "ok, floor can rise", a pull request that adds the missing tests may
set the floor to the new score (rounded down). Raise it in the same pull request that earned it.

**Lowering a floor** is only done in a pull request of its own, with an entry under `lowered` that names the old and the
new figure and says why, for example when a mutated file is deleted or split:

```json
"lowered": { "file-lifecycle": { "from": 81.2, "to": 79.5, "reason": "SwapJournal.cs was split and its tests moved." } }
```

`scripts/check-mutation-floors.mjs` fails a pull request that lowers a floor without that entry, the same rule as the
coverage floors.

## Killing a survivor

For each survivor, write the test that fails on the changed line and passes on the real one, and look at it fail against
the mutated line before you keep it: edit the line to what the survivor says it became, run the test, see it fail, put
the line back. Tests use real recorded replies or real tools, never an assumed shape, and wait on events and fake clocks,
never on a guessed delay. When a survivor cannot be killed because the change does not alter behaviour (an equivalent
mutant, for example a different but equal way to build the same string), say so in the issue.
