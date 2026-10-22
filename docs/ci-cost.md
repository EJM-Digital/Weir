# CI cost

GitHub Actions bills by the minute, and a Windows minute costs twice a Linux one. A few jobs are most of the bill, so
they run when their result can change something, not on every pull request. This page says which jobs those are, when
each runs, how a release still gets their proof, and how to watch the cost. The rules both repositories share are in the "CI cost" section of
[`ci-standard.md`](ci-standard.md); this page is how Weir applies them.

Nothing is dropped. Every heavy job still runs on `main`, nightly or before a tag; a pull request only skips the ones
its change cannot affect.

## When the heavy jobs run

| Job | Pull request | Push to `main` | Nightly | By hand | Before a tag |
| --- | --- | --- | --- | --- | --- |
| Mutation testing (`mutation.yml`) | never | never | weekly (Sunday) | yes | must have passed |
| Contract suite, Windows legs (`contract-windows`) | only with a Windows path | when the server changed | yes | yes | must have passed |
| Windows package smoke, update package and scenario shards (`packaging-windows`) | only with a Windows path | when the package or app changed | yes | yes | must have passed |
| Contract suite, Linux legs, E2E shards, Docker smokes, Linux scenario shards, web, server on Linux | by their own path filters in `ci.yml` | as today | no | yes | `ci-passed` on the tagged commit |

"Nightly" is the 03:17 UTC run of `ci.yml`; it runs only the two Windows jobs above, so it costs those and a few Linux
minutes. "By hand" is Actions, then CI or Mutation, then Run workflow; a manual CI run skips nothing.

A regression in Windows code is caught on `main` within the hour, because the push to `main` runs the Windows legs, and
it is always caught before a tag.

### Which paths count as Windows code

A pull request runs the Windows legs when it changes a file in the `windows` filter of the `changes` job in
[`.github/workflows/ci.yml`](../.github/workflows/ci.yml), or any workflow file:

- the tray (`apps/tray`) and the server build files it imports;
- Windows-only server code: the `*.Windows.cs` files, the firewall and network-access code (`Runtime`), the host readings
  (`SystemReadings`), the Velopack updater (`Weir.Core/Updates`), the swap file system (`LibraryMode`), the directory browser,
  and the three files that handle output ownership and source files by hand;
- the host the installer packs (`Weir.Host`), and the database migrations and recovery command a Windows upgrade carries
  across;
- the installer packaging (`packaging/windows`), the smoke scripts, and the scenario scripts (`scripts/scenarios-ci`,
  `scripts/scenarios`, `scripts/scenarios-in-ci.mjs`);
- the contract suite's own project, and the pinned media tools it runs against.

The list is kept honest by `scripts/check-windows-paths.mjs` (a step of `repo-checks`): it finds the server files that
are Windows-only by their name or by what they call (`DllImport`, `Microsoft.Win32`, Velopack and so on) and fails when the
filter does not match one. A new Windows-only file therefore cannot slip past the filter. When the check fails, add the file
or its folder to the `windows` filter.

Not changed by this policy: the server unit tests on Windows (`server-windows`) and the tray tests (`tray`) keep the path
filters they had.

## How a release gets the proof

`release.yml` has two jobs next to `ci-passed` and `golden-path`, and both publish jobs need them:

- **`windows-proof`** looks for a push, nightly or manual run of `ci.yml` whose `ci-passed` job passed and whose step "The
  Windows legs ran and passed" succeeded. That step runs only when both Windows jobs were due, so a run that skipped them
  does not count. The run may be on the tagged commit or on an earlier one with the same shipped files
  (`scripts/release/shipped-paths.mjs`, the rule the golden-path gate uses): a documentation commit after the last run needs
  no new one.
- **`mutation-proof`** looks for a weekly or manual run of `mutation.yml` whose `check` job held every score to its floor. The
  run may be on an earlier commit when the shipped files are the same and nothing under `apps/` (the tests and the Stryker
  setup) or the mutation files (`.github/mutation-targets.json`, the floors, the workflow) changed, because a changed test
  can lower a score without changing a shipped file.

Both use `scripts/verify-heavy-ci-for-release.mjs`, read at most the 100 commits before the tag, never wait, and fail at
once with the command to run: `gh workflow run ci.yml --ref vX.Y.Z` or `gh workflow run mutation.yml --ref vX.Y.Z`. A
`pull_request` run never counts. Mutation takes hours, so start it before tagging when the shipped code or tests changed
since the last weekly run.

`scripts/check-release-workflow-gates.mjs` keeps this shape: both jobs exist, both publish jobs need them, neither can be
skipped or waved through, `mutation.yml` has no pull-request trigger, and `ci-passed` keeps the step the release reads.

## Keeping it this way

- Every workflow has `concurrency` with `cancel-in-progress` for pull requests, so a pushed commit stops the run it
  replaces. A push to `main` and the nightly run are never cancelled, and the nightly run has a group of its own so it cannot
  replace a push to `main` that is waiting.
- `scripts/check-workflow-hygiene.mjs` fails a workflow that a pull request triggers and that runs mutation testing, or has
  a Windows job (a Windows runner, a Windows scenario shard, or a job named for Windows) without a job-level
  `if: needs.changes.outputs.run_<name> == 'true'`. A new heavy job needs its flag in the `changes` job, its row in
  `scripts/ci-passed.mjs`, and its name in `ci-passed`'s `needs`.

## Watching the cost

```
node scripts/ci-minutes.mjs --days 7
```

sums the last N days of Actions minutes per operating system, workflow, job and trigger from the GitHub API
(`gh api repos/<owner>/<repo>/actions/runs` and each run's jobs), with Windows minutes also shown billed as Linux minutes. It
needs `gh` signed in. It makes one request for every 100 runs and one for each run, so `--max-runs` (default 400) caps the
newest runs it reads; the token's request budget is shared, so avoid running it in a loop. `--legs` keeps matrix legs
apart, `--top` sets how many jobs are listed and `--json` prints the numbers.

Read it weekly. The lines to watch are the Windows column of `Mutation` (should be one weekly run), `contract / contract` and
`packaging / scenarios-windows` by trigger (`pull_request` should be small and `push` and `schedule` the rest), and `By
trigger` as a whole.

## What it saves

The figures below come from `scripts/ci-minutes.mjs` and are estimates of what stops running on pull requests, not a promise.

In the 200 newest runs (12 to 20 October 2026, a heavy stretch) Weir used about 9,700 Windows minutes and 8,350 Linux
minutes, and pull requests were 8,400 of those Windows minutes.

- **Mutation** used about 4,900 Windows minutes, all of it from pull requests. One full run of every target is about 1,000
  Windows minutes, so one weekly run replaces it: about 3,200 Windows minutes a week saved.
- **The Windows legs** (the contract legs, the Windows package smoke, the update package and the scenario shards) used about
  4,000 Windows minutes, about 2,900 of them on pull requests. If six pull requests in ten touch no Windows path, they stop
  running there: about 1,500 a week saved. The nightly run adds about 80 Windows minutes a night, 560 a week, so the net is
  about 950.

Together that is about 4,000 Windows minutes a week, which bill as about 8,000 Linux minutes. The Linux jobs are not changed.
Run `node scripts/ci-minutes.mjs --days 7` a week after this lands to replace the estimate with the measured figure.
