# Weir contract tests

A test suite that judges a **running** Weir server from the outside. It starts the built server as its own
process with its own data folder and port, and talks to it only over HTTP and server-sent events. It reads or
writes the SQLite file only while the server is stopped. It never loads server code: the project does not
reference any Weir assembly (no `WebApplicationFactory`), it is only built *after* the host so the binary exists.

## Running it

```bash
dotnet build apps/server/Weir.slnx                                   # builds the host first, then this project
dotnet test apps/server/tests/Weir.Contract.Tests --filter Area=activity   # one area
dotnet test apps/server/tests/Weir.Contract.Tests                          # every area
```

| What | How |
| --- | --- |
| One area | `--filter Area=<area>` (the folder name, lower case) |
| Leave these tests out of a wider run | `--filter "Category!=Contract"` |
| A published server instead of the built one | `WEIR_CONTRACT_SERVER=/path/to/Weir.dll` (or an executable) |
| Keep each server's data folder and log after the run | `WEIR_CONTRACT_KEEP_DATA=1` |
| A fixed session secret | `WEIR_SESSION_SECRET` (a default is used otherwise) |

A server that does not become ready fails the test with the end of its own log: it exits, or it goes quiet. A start is judged by
its progress, as the tray judges one (`docs/start-supervision.md`): a server that keeps reporting in its
`startup-progress.json` is waited for however long it takes, and one whose record has not changed for 30 seconds (longer if it asks
for more) has hung. Each server writes its output to `<data folder>/contract-logs/server-<n>.log`.

## When a test fails

A test that fails keeps what its servers wrote in `artifacts/contract-failures/<run>/<Class.Method>/` at the repository
root (ignored by git, or the folder in `WEIR_CONTRACT_FAILURES`); a passing test deletes its own. CI uploads the folder
as `contract-failure-evidence-<area>`, and the data folders of a failed leg as `weir-contract-data-<area>`.

| What | Where |
| --- | --- |
| The error, with its stack trace | `error.txt` |
| The server's own log | `<server>/logs/weir.log` |
| Everything the server process printed, standard output and error, one file per start | `<server>/contract-logs/server-<n>.log` |
| How far it had got when it stopped | `<server>/startup-progress.json` |

A row of a theory has the start of its id after the method name. A server a whole class shares (`IClassFixture`) is kept
as `<Class>.shared-server`, with the names of the class's failed tests in its `error.txt`, because the one server
served them all. The framework that does this is `Harness/FailureKeepingFramework.cs`; `Harness/FailureEvidence.cs`
says how a name is made.

## How the harness works

Everything is in `Harness/`. Areas live in folders beside it (`Activity/`), one folder per area.

- **`WeirServer`** starts one real server (`dotnet Weir.dll --host 127.0.0.1 --port <free port>`) with a fresh
  `WEIR_HOME`, waits for `/ready`, and stops it by killing only the process it started and that process's children.
  `StartNewAsync(environment)` makes a server; `RestartAsync` stops and starts it again on a new port (create
  clients afterwards); `StopForDatabaseAsync()` stops it, opens the SQLite file, and restarts it when disposed
  (`StopForDatabaseAsync(restart: false)` leaves it stopped, to look at the file after a shutdown).
  Every server has its own folder and port, so test classes and areas run side by side. The harness reaches a server
  on `127.0.0.1`, the address it binds, and so do the fakes. `ServerStartGate` lets only as many servers start at
  the same moment as the machine has processors (never fewer than two), so a small CI runner is not asked to start a
  dozen at once; a server that is ready gives its place back. A port is picked by asking the system for a free one, so
  another process can take it before the server binds; a server that exits with "address already in use" is started
  again on a new port (three attempts in all), and any other early exit fails at once. `/ready` answering is not enough
  to call a server ready: the process the harness started must also have logged that it listens on its port, because the
  process that won the port may be the server of another test, which answers `/ready` until its own test ends. Disposing a server waits until
  nothing holds its database files before it deletes the folder.
  `ServerLog` reads a server's output and error on threads of their own, never the thread pool: on Windows the wait for a
  redirected pipe holds its thread, so two pool threads per running server would leave a suite with more than a handful of
  servers up with no thread for the fakes' answers, the start wait or any request. Read a long-lived process's output this way, never with
  `BeginOutputReadLine` or `ReadAsync`.
- **`ServerEnvironment`** is the quiet default environment: no workers, no watcher, no periodic enqueue, high
  sign-in limits, artwork off. Pass `environment` to add or override. `ServerEnvironment.OperatorDefaults` is what
  an installed Weir runs with instead (ten workers, the watcher, periodic scans, the work-file sweeps), for tests of
  the whole product.
- **`ServerFixture`** is the per-class server: `IClassFixture<ServerFixture>` gives a class one server for all its
  tests. Derive from it and override `Environment` to give a class different settings. `SeededServerFixture` is the
  same with rows seeded into the stopped database before the first test (override `Seed`).
- **`WeirClient`** is a browser-like session (its own cookie jar) with CSRF and sign-in: `EnsureAdminAsync()`,
  `LoginAsync(...)`, `GetAsync(path, query...)`, `PostWithCsrfAsync/PutWithCsrfAsync/PatchWithCsrfAsync`
  (token in the body), `DeleteWithCsrfAsync` (token in `X-CSRF-Token`), `OpenStreamAsync` for SSE. Responses are
  `WeirResponse` (`Status`, `Header(name)`, `RawHeader(name)` as the server wrote it, `Text`, `Bytes` as sent (the
  client does not decompress), `SetCookieValue(name)`, `Fields`/`Elements` as JSON). A client also has `HeadAsync`,
  `OptionsAsync`, `AttemptLoginAsync` (returns whatever the server answers) and a cookie jar to read and change
  (`Cookie`, `SetCookie`, `ClearCookies`). A client sends no
  `Origin` or `X-Requested-With` unless asked, as a media manager or a script would not: `server.CreateClient(headers)` or
  `CreateAdminClientAsync(headers)` set headers for every request (a browser-like client passes both), and
  `RequestAsync`, `GetAsync(path, headers, query...)` and the `...WithCsrfAsync` methods take headers for one request.
- **`HeldWriteLock`**: SQLite's write lock on a *running* server's database, held by the test the way a backup tool would hold it, so every write the server is asked for waits and then gives up. Start the server with `WEIR_SQLITE_BUSY_TIMEOUT_SECONDS=1` (the shortest) so the wait is a second, not thirty. Disposing lets go.
- **`SseReader`** reads a stream block by block: `NextBlockAsync`, `NextEventAsync`, `NextEventNamedAsync`. Event data is
  any JSON (`JsonNode`). Each call fails after 10 seconds of silence unless it is given a longer `idleTimeout`.
- **`StoppedDatabase` and `SeedSql`**: plain SQL against the shared schema for rows no API creates. `SeedSql.UtcText`
  writes a timestamp the way the schema stores it. `SeededAccounts` holds the admin `alice` and the viewer `bob`
  with precomputed Argon2 hashes (the harness has no hasher); `LibraryBodies.Unchanged` is the body of a whole-library save.
- **`Poll.UntilAsync`** waits for an outcome with a deadline; never sleep for a fixed time.
- **`JobWait`** waits for what a job produces (a scan, a clean) for as long as a worker holds the job, because how long a tool takes depends on the machine. It gives up only on a job nobody takes up for two minutes, and at once on jobs that ended without the result. `LibraryCleanWorld` waits this way; use it, not `Poll.UntilAsync`, for work that runs a media tool. In `Processing`, `Scenario.WaitWhileWorkedOnAsync` watches every job, so a job the first one queues before it ends (a reject, a pass-through, a hand-off's retry) keeps the wait going; work Weir queues on its own timer (the next periodic scan, a retry after its backoff) is passed as `owed` and counts as a pending job. `Jobs/SwapWorld.WaitWhileWorkedOnAsync` is the same for the swap tests: a held swap keeps its lease, so it is waited for as long as it takes to reach its point.
- **`FakeGateway.HoldSearches`** keeps every metadata search waiting for its answer until `ReleaseSearches`, and `SearchHeldAsync` says when one is waiting: a slow service, on demand. A test that counts searches seeds all its files with one `LibrariesPartBPosters.SeedFilesAsync`, because every stop of the server restarts it and a restarted server looks titles up within seconds.
- **`FakeDownloadClient`** is a fake qBittorrent, SABnzbd, NZBGet, Transmission or Deluge that lets in only the credentials it is set to expect (`Require` changes them) and writes down every sign-in attempt in `Attempts`, so a test can show which secret Weir really sent. Its replies are written from each product's published interface, not recorded from a running client.
- **`ServerLedger`** records every server this run started in the temp folder. The next run stops servers whose
  test run died before teardown; it never touches a server whose run is still alive, so runs side by side are safe.
  Its folder is shared by every fixture and every run, so a read, a delete or a claim that meets someone else's is theirs and
  never throws, and an orphan is stopped by the one caller that makes its claim file (`ServerLedgerTests`).
- **`WEIR_LIBRARY_SWAP_HOLD_AT`** is a server setting for tests, not a harness class: set to the name of a point in a library swap (`OriginalMovedAside`, `CleanedCopyInPlace`, ...) it stops the swap there for good and writes the point's name to `swap-held-at.txt` in the data folder. The test waits for that file, ends the process with `StopAsync`, and starts it again to see what recovery does. `Jobs/SwapWorld` builds a library with one film that needs cleaning and does this; `Jobs/SwapCrashPlan` lists what each point leaves on disk. A value that names no point stops the server from starting, and so does any value at all when `WEIR_ENV` is `production` (the default), so a real install never honours it; `SwapWorld` runs its servers with `WEIR_ENV=development`.
- **`FakeAlertReceiver`** is the far end of an alert: a local HTTP server that records every post (`PostsTo`, `WaitForPostsAsync`) and answers each path with the status a test sets (`Answers`, `RedirectsTo`; 204 otherwise). Start a server with its `Env` and the server connects every alert to the receiver instead of the internet: `WEIR_NOTIFICATION_STAND_IN_PORT` moves only the connection, so a channel's address is still a public-looking name (`http://hooks.example.com/...`) that the server resolves to a public address and holds to the same rules as for real. Like `WEIR_LIBRARY_SWAP_HOLD_AT`, the setting is refused at start when `WEIR_ENV` is `production`, and so is a value that is not a port; `Env` sets `WEIR_ENV=development`.
- **`ContractAreaAttribute`** puts a class in an area (`[ContractArea("activity")]`) and in `Category=Contract`.

## Adding tests and areas

An area is a folder (`Weir.Contract.Tests/<Area>/`, namespace `Weir.Contract.Tests.<Area>`) whose test classes are marked
`[ContractArea("<area>")]`. CI runs one leg per area in `areas.json` (`dotnet test --filter "Area=<area>"`), and
`node scripts/check-contract-areas.mjs` fails when that list and the classes disagree, so a new area is added in both
places and a leg can never run zero tests.

1. One test class per subject. Use `IClassFixture<ServerFixture>` to share a server across a class; start a server of
   your own (`await using var server = await WeirServer.StartNewAsync(env)`) where a test needs a clean database or its
   own settings. Each server start takes seconds, so keep classes few and fixtures shared.
2. **Black-box only.** Drive the server through HTTP and SSE. Where no API sets up the state, seed SQLite while the server
   is stopped (`await using var database = await server.StopForDatabaseAsync()`); create clients *after* the block,
   because the server comes back on a new port. Never reference a Weir assembly.
3. Name a test as a sentence (`A_filtered_range_exports_as_csv_and_json`). Wait with `Poll.UntilAsync`, never with a
   fixed sleep.
4. Share only stateless helpers. Area-specific helpers (seed rows, settings bodies) stay in the area's folder.
5. Scenarios that need the real `ffmpeg` and `ffprobe` use `[RealFfmpegFact]`: they run when both are on `PATH` (or
   in `WEIR_CONTRACT_REAL_FFMPEG_DIR`) and are skipped otherwise. CI installs ffmpeg so they run there.
   mkvmerge is found the same way (on `PATH`, or in `WEIR_CONTRACT_REAL_MKVTOOLNIX_DIR`) and the server uses it for Matroska when it is there;
   `[RealMkvmergeFact]` marks a scenario that needs it. `Libraries/LibraryClean*Tests` clean library files in place with these tools: a scan of
   generated media, the clean the web sends, and the file, its leftovers and the kept original read back from disk with ffprobe.

## Recorded replays

`MediaManagers/Recorded/` replays what Deluno recorded of its talk with Weir (`apps/server/tests/fixtures/recorded/deluno/<version>/`, with
its own README naming where and when it was captured) against a running Weir. A test never builds a Deluno request or a Weir reply:

- `Recording` loads a recording as it is, and fails with how to get one when a state was never recorded.
- `RecordedWorld` rebuilds the state the recording was made in (a Movies and a TV Shows workflow a fake Deluno manages), and
  `Translation` maps the recorded paths and ids onto this test's own, changing nothing else.
- `Replay` sends a recorded request and holds the reply to the recorded one. A mismatch fails with the recording's file and the JSON
  path of each field that differs. Values that change every time (ids, times, the machine name, the version) are held to their type.
- A change made on purpose since the recording is never made by editing it: the test pins it as an `ExpectedDifference` with the issue
  number, and the pin fails once Weir answers what was recorded again.

`RecordedComparisonTests` proves the failures say what they should.

`fixtures/recorded/deluno-side/` is the other half (#971): Deluno's recordings of its own requests, Weir's reports to it and its own replies, a
byte-for-byte copy of Deluno's folder pinned by its `MANIFEST.sha256` (`scripts/check-recorded-manifest.mjs`). Its README names the Deluno commit and
says which test replays what.

- `DelunoRequestsTests` and `RecordedLibraryFoldersTests` send Deluno's recorded requests (hand-offs, status poll, cancel, outcomes, the two asks) to a
  Weir set up as that session had it and hold Weir's reply and state to the recording.
- `DelunoReportsTests` makes Weir produce each ending (finished, failed, source gone, too late) against a fake Deluno that records what Weir POSTs and
  answers with Deluno's recorded reply; `EmittedReport` holds the body to the recorded one, member names, order, types and the exact words.
- `DelunoRepliesTests` serves Deluno's recorded replies (`DelunoPlaceholders` fills in the placeholders) to Weir's reads and refusals.
- `DelunoRecordingComparisonTests` proves a recording that stops matching fails naming its file and JSON path.
- A recording Weir has no behaviour for is listed in `fixtures/recorded/deluno-side-no-weir-behaviour.json`; `scripts/check-recorded-replays.mjs` fails on
  one that is neither replayed nor listed, and one that waits on an open issue in `fixtures/recorded/deluno-side-awaiting-replay.json` is listed there until a test replays it.
