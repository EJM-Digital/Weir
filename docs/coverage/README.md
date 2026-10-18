# Test coverage

Every CI run measures what fraction of Weir's code its tests run, per project, and holds each figure to a floor
that only goes up. This page says what is measured, how to read it and how to change a floor.

## What is measured

| Project | Measured by | Which tests |
| --- | --- | --- |
| Weir.Core, Weir.Infrastructure, Weir.Api, Weir.Host | coverlet.collector, merged by ReportGenerator | `Weir.Core.Tests`, `Weir.Infrastructure.Tests` and `Weir.Api.Tests` on Linux and on Windows, merged into one report |
| Weir.Core, Weir.Infrastructure, Weir.Api, Weir.Host, **out of process** | dotnet-coverage, merged by ReportGenerator | every contract area and every E2E smoke shard, measured in the server process they start |
| Tray | coverlet.collector, merged by ReportGenerator | `Weir.Tray.Tests` on Windows |
| Web app | vitest with the v8 provider | the whole vitest suite |

Two figures are kept for each: **lines** and **branches** (both sides of every `if`, `?:`, `&&`, `switch` arm and
the like). What counts is set in `apps/server/coverage.runsettings` and `apps/tray/coverage.runsettings` (the
shipped assemblies; not the test projects, the test child process, the fake tools, or code the compiler generated)
and in `apps/web/vite.config.ts` (every file under `src`, tested or not, except test files, test support and the
generated API types).

The numbers are exact counts, not a sample: a line either ran in some test or it did not.

### The out-of-process figure

The contract suite and the E2E smoke start the server as a separate process and talk to it over HTTP, so a collector
attached to the test run sees only the test code, never the server. They are measured by `dotnet-coverage collect`
(pinned in `ci.yml` and `ci-contract.yml`), which instruments the server process they start. The settings in
`apps/server/coverage.out-of-process.xml` count the same four shipped assemblies as `coverage.runsettings`.

- **A separate figure.** The two tools count lines and branches differently, so the out-of-process figure is
  never added to the unit figure and has floors of its own (`out-of-process-*` in `floors.json`). It is the server
  code that the contract suite and the E2E smoke run *together*: each contract area and each E2E shard uploads its own
  report, and the `coverage` job merges them. It is judged only when both suites ran for the change; on a push to main,
  where the E2E smoke does not run, it is shown as not run.
- **The server must end to be measured.** A process writes what it ran as it exits, and a killed server writes nothing.
  With `WEIR_CONTRACT_COVERAGE=1` the harness asks each server to stop when its test ends (the stop event on Windows,
  the termination signal elsewhere) and kills it only if it has not stopped within a minute. A test that stops a
  server by hand to simulate a crash still kills it, so the part of the server that ran before the crash is not in the figure.
- **A leg that wrote nothing fails.** A contract area or E2E shard whose tests passed but which wrote no report fails its
  upload step.

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
  (the table marks "floor can rise"). Nothing raises them for you. A change to `floors.json` alone runs the server,
  tray, web, contract and E2E tests too, so a raised floor is checked against a fresh measurement.
- **Lowering a floor.** A floor only goes down in a pull request of its own, with the reason in plain words written
  under `lowered` in `floors.json`, naming the old and the new figure:
  `"web.branch": { "from": 82.7, "to": 80, "reason": "..." }`. Without that entry the check fails and says so;
  with it the check passes and prints the reason in the job summary.
- **Starting figures.** `measuredFrom` records the CI runs the first floors were measured from, and `note` says how each
  floor follows from them. They were taken from those runs, not chosen. A few server files (background services and
  file watchers) are run by tests that race them, so the lines they cover vary a little from run to run; the floors
  of the two projects they are in leave room for that.

## Running it locally

```powershell
# Server: the in-process suites, then the merged report (needs: dotnet tool install --global dotnet-reportgenerator-globaltool --version 5.5.11)
cd apps/server
dotnet test Weir.slnx --filter "Category!=Stress&Category!=Contract&Category!=E2E" --collect:"XPlat Code Coverage" --settings coverage.runsettings
reportgenerator "-reports:tests/*/TestResults/*/coverage.cobertura.xml" "-targetdir:../../artifacts/coverage/server" "-reporttypes:Html;Cobertura;JsonSummary"

# Tray: the same from apps/tray with Weir.Tray.slnx, and "-reports:*/TestResults/*/coverage.cobertura.xml"

# Out of process: one contract area (Linux or Windows), measured in the server it starts
# (needs: dotnet tool install --global dotnet-coverage --version 18.11.2)
cd apps/server
$env:WEIR_CONTRACT_COVERAGE = "1"
dotnet-coverage collect --settings coverage.out-of-process.xml --output-format cobertura --output ../../artifacts/coverage/out-of-process/coverage.cobertura.xml --nologo -- dotnet test tests/Weir.Contract.Tests --filter "Area=auth"
reportgenerator "-reports:../../artifacts/coverage/out-of-process/coverage.cobertura.xml" "-targetdir:../../artifacts/coverage/out-of-process-report" "-reporttypes:Html;Cobertura;JsonSummary"

# Web app
cd apps/web
npm run test:coverage

# The check, against what you just measured
cd ../..
node scripts/check-coverage-floors.mjs --floors docs/coverage/floors.json --server artifacts/coverage/server --out-of-process artifacts/coverage/out-of-process-report --web apps/web/coverage
```

A local figure can differ from CI's: CI's server figure merges Linux and Windows, and tests that need FFmpeg or
mkvmerge run only where those tools are installed.

## Route access

`route-access.json` says who may call each route the server maps. It is a different kind of coverage: not how much of the code the tests
run, but whether every route keeps the guard it is meant to have. Two tests hold the server to it.

- `RouteAccessTableTests` (API tests) compares the file with the server's own endpoint table. A route the server maps that the file does
  not list fails, and so does a line for a route the server no longer maps.
- `RouteAccessTests` (contract suite, area `auth`) calls every route on a running server, as each kind of caller, and compares the
  answer with the line.

| `access` | Who may call | What the walk checks |
| --- | --- | --- |
| `public` | Anyone, with a `reason` | An anonymous caller is not turned away at the door. |
| `signed-in` | Any account | An anonymous caller gets 401. A viewer, an operator and an administrator all get past the guard. |
| `operator` | An operator or an administrator | As above, and a viewer gets 403. |
| `admin` | An administrator only | As above, and an operator gets 403. |
| `manager-key` | A media manager presenting its webhook secret, with a `reason` | Neither an anonymous caller nor the administrator's browser session is let in. |

Every write except a `manager-key` one is also called with no token and from an origin the server does not trust: the first has to be
refused for the token, and the second with a good token has to be refused for the origin. The anonymous, viewer and operator calls send an
empty body, so the guard has to answer before the server looks at what was asked.

When you add a route, add its line here with the narrowest access that fits. When a write needs fields besides the token, add them to
`RouteWalk.RequiredFields`, so the walk reaches the token and origin checks instead of stopping at validation.

### The order of the checks

Every write checks, in this order, who is asking, then the size of the body (413 over 8 MB), its content type (415), and last the body
itself (422). An anonymous caller therefore learns nothing about what a route reads, and a caller who may not use the route is told
so before anything about the body. `ApiRequest.ReadBodyAsync` enforces it: it throws if the handler has not called `RequireUserAsync`
first. A route anyone may call, or that its caller proves itself to with something other than a session (sign-in, first-run setup, a media
manager's webhook secret), reads its body with `ReadAnonymousBodyAsync`. A route that reads nothing from its body calls
`RefuseOversizedBody` once the caller is let in.

`RouteCheckOrderTests` (contract suite, area `auth`) sends every write in `route-access.json` an oversized body, a body that is not
JSON, invalid JSON and a JSON body with the wrong fields, as an anonymous caller, as a caller who may not use the route, and as one who
may. A write that is not guarded by a sign-in is listed in the test with the reason it keeps its own order.

## Paths that can change a media file

`file-changing-paths.json` lists every way Weir can change a media file and what the pause does to it: a job kind the worker claim refuses
while paused, a request a person makes by hand that is refused, or a path that is exempt, with the reason. It also lists the server source
files that do the changing, and which path each serves. Tests hold it to the server.

- `FileChangingPathsTests` (API tests) compares the file with the job kinds the server registers, the routes it maps, and a scan of
  `apps/server/src` for calls that change files (`File.Delete`, `File.Move`, `Directory.Delete`, a stream opened to write, the swap file
  system) and for calls to the helpers that change files for someone else (publishing, removing a release folder, asking a media manager to
  delete). A new call, a new caller of such a helper, or a new job kind fails until you classify it.
- `PauseGatesTests` (infrastructure tests) pauses every held job kind, attempts it, and shows the file untouched and the job still queued, then
  shows it run once after resuming. `PausedActionsApiTests` (API tests) does the same for the refused requests.

When it fails, add the path or site to the file with the gate that fits. A new job kind that changes files must be held at the claim, which is
the default for every kind but the one scan that may run while paused. A request that changes a file must be refused while paused
(`PausedRequests`). Anything exempt needs its reason in the file.
