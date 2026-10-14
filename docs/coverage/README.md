# Test coverage

Every CI run measures what fraction of Weir's code its tests run, per project, and holds each figure to a floor
that only goes up. This page says what is measured, how to read it and how to change a floor.

## What is measured

| Project | Measured by | Which tests |
| --- | --- | --- |
| Weir.Core, Weir.Infrastructure, Weir.Api, Weir.Host | coverlet.collector, merged by ReportGenerator | `Weir.Core.Tests`, `Weir.Infrastructure.Tests` and `Weir.Api.Tests` on Linux and on Windows, merged into one report |
| Tray | coverlet.collector, merged by ReportGenerator | `Weir.Tray.Tests` on Windows |
| Web app | vitest with the v8 provider | the whole vitest suite |

Two figures are kept for each: **lines** and **branches** (both sides of every `if`, `?:`, `&&`, `switch` arm and
the like). What counts is set in `apps/server/coverage.runsettings` and `apps/tray/coverage.runsettings` (the
shipped assemblies; not the test projects, the test child process, the fake tools, or code the compiler generated)
and in `apps/web/vite.config.ts` (every file under `src`, tested or not, except test files, test support and the
generated API types).

The numbers are exact counts, not a sample: a line either ran in some test or it did not.

### What is not measured

The contract suite and the E2E smoke start the server as a separate process and talk to it over HTTP, so a
collector attached to the test run sees only the test code, never the server. They run as before and are not part
of these figures; the figures are what the in-process tests alone run. Measuring the server from outside is a
separate piece of work (#976 records what was tried).

## Reading a run

The `coverage` job in CI writes a table to its summary page: lines and branches per project beside the floor, and,
for each project below 100%, the ten files with the most lines and branches never run, which is where the next
tests pay back the most. The merged reports (an HTML report per project, the merged Cobertura file, and the web
app's lcov report) are the `coverage-reports` artifact of the run.

## Floors

`docs/coverage/floors.json` holds a floor per project and measure. The check
(`scripts/check-coverage-floors.mjs`) fails a run when any figure, rounded down to one decimal, is below its floor, and
names the project and the measure. A project whose tests did not run for the change (the path filter skipped them)
is not judged, and a project whose tests ran but wrote no report fails.

- **Raising a floor.** A pull request that adds tests raises the floors it earned, to the figure the run printed
  (the table marks "floor can rise"). Nothing raises them for you.
- **Lowering a floor.** A floor only goes down in a pull request of its own, with the reason in plain words written
  under `lowered` in `floors.json`, naming the old and the new figure:
  `"web.branch": { "from": 82.7, "to": 80, "reason": "..." }`. Without that entry the check fails and says so;
  with it the check passes and prints the reason in the job summary.
- **Starting figures.** `measuredFrom` records the CI run the first floors were measured from. They were taken from
  that run, not chosen.

## Running it locally

```powershell
# Server: the in-process suites, then the merged report (needs: dotnet tool install --global dotnet-reportgenerator-globaltool --version 5.5.11)
cd apps/server
dotnet test Weir.slnx --filter "Category!=Stress&Category!=Contract&Category!=E2E" --collect:"XPlat Code Coverage" --settings coverage.runsettings
reportgenerator "-reports:tests/*/TestResults/*/coverage.cobertura.xml" "-targetdir:../../artifacts/coverage/server" "-reporttypes:Html;Cobertura;JsonSummary"

# Tray: the same from apps/tray with Weir.Tray.slnx, and "-reports:*/TestResults/*/coverage.cobertura.xml"

# Web app
cd apps/web
npm run test:coverage

# The check, against what you just measured
cd ../..
node scripts/check-coverage-floors.mjs --floors docs/coverage/floors.json --server artifacts/coverage/server --web apps/web/coverage
```

A local figure can differ from CI's: CI's server figure merges Linux and Windows, and tests that need FFmpeg or
mkvmerge run only where those tools are installed.
