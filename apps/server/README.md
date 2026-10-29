# Weir server (.NET)

Weir's server is C# on .NET 10. It serves the HTTP API under `/api/v1` and the built web app, runs the durable job queue and the Processing and library-mode work, and owns the SQLite database. The Docker image and the Windows package both run it. Why the server is .NET: [ADR-0017](../../docs/adr/ADR-0017-backend-on-dotnet.md).

## Projects

Solution: `apps/server/Weir.slnx`.

| Project | Holds |
| --- | --- |
| `src/Weir.Host` | The process: configuration, logging, database startup, Kestrel, Windows Service and systemd support. Builds `Weir` / `Weir.exe`. |
| `src/Weir.Api` | Endpoints and HTTP behaviour: auth and CSRF, security headers, request ids, the OpenAPI document, serving the web app. |
| `src/Weir.Infrastructure` | SQLite (connections, stores, numbered migrations in `Migrations/`), the job queue and workers, the remux pass, library mode, the ffmpeg and mkvmerge runners, media manager clients, notifications, the filesystem and log files. |
| `src/Weir.Core` | Records and rules with no IO: the rules engine, job rules, media manager rules, configuration, settings and security primitives. |
| `tests/Weir.Core.Tests` | Unit tests for `Weir.Core`, including the rules and ffmpeg golden fixtures. |
| `tests/Weir.Infrastructure.Tests` | Tests against a real SQLite database and real files, including migration tests. |
| `tests/Weir.Api.Tests` | Endpoint tests over HTTP, including `OpenApiDocumentParityTests`. |
| `tests/Weir.TestChild` | A slow-program stand-in that the process runner tests start instead of a system tool. |

The contract suite (`tests/Weir.Contract.Tests`) and the E2E smoke (`tests/Weir.E2E.Tests`) judge a running server from outside, as a process over HTTP. See [`tests/Weir.Contract.Tests/README.md`](tests/Weir.Contract.Tests/README.md) and [`tests/Weir.E2E.Tests/README.md`](tests/Weir.E2E.Tests/README.md); the packaged live audit is [`tools/Weir.LiveAudit`](tools/Weir.LiveAudit/README.md). The recordings of a real Sonarr and Radarr that tests replay are made by [`tools/Weir.ArrRecorder`](tools/Weir.ArrRecorder/README.md), and those of the five download clients by [`tools/Weir.ClientRecorder`](tools/Weir.ClientRecorder/README.md).

## Build and test

Needs the .NET SDK pinned in `apps/server/global.json` (10.0.400, no roll-forward). From the repository root:

```powershell
dotnet build apps/server/Weir.slnx -warnaserror
dotnet test apps/server/Weir.slnx
```

The real-tool tests (`RealFfmpegTests`, `MkvmergeRealTests`, `RemuxPassRealFfmpegTests`) run real ffprobe, ffmpeg and mkvmerge and skip unless the tools are found through `WEIR_FFMPEG_DIR`, `WEIR_MKVTOOLNIX_DIR` or `PATH`. With `WEIR_REQUIRE_REAL_TOOLS=1` a missing tool fails them instead; CI sets it, installs the pinned tools (`.github/actions/setup-real-media-tools`) and fails the leg unless all 31 ran (`scripts/check-real-tool-tests.mjs`). To run them yourself with the CI tools on Windows, run `scripts/install-media-tools-windows.ps1` and point the two variables at `packaging/windows/vendor/ffmpeg` and `packaging/windows/vendor/mkvtoolnix`.

What makes the server tests fast, so a new test keeps them that way:

- **Accounts are seeded with a cheap password hash.** The server verifies a password at the cost stored in its hash, and the product's own cost (Argon2id, 64 MiB, three passes) is about a second of managed code. A test that only needs an account signs in to one seeded with `CheapPasswordHash` (infrastructure and API tests) or the constants in `SeededAccounts` (contract tests). The product's cost stays covered where the product makes the hash (bootstrap, password change, recovery) and by `AuthApiTests.A_password_hashed_at_the_production_cost_signs_in_and_a_wrong_one_does_not`. Never add a setting to the server that lowers the cost.
- **Databases are copied, not migrated.** `MigratedDatabaseTemplate` migrates once per test process and each test gets its own copy of the file. `WeirTestServer.StartAsync(freshDatabase: true)` and the migration tests start from an empty folder.
- **A server's start-up work finishes in the background after it is listening** (a retention pass, the first job a timer queues). A test that seeds rows older than the retention horizon, or counts all jobs, either waits for that work (`TestDatabase.WaitForStartupPruneAsync`) or switches the timer off for its server.
- **Contract tests talk to `127.0.0.1`.** On Windows a client that resolves `localhost` tries `::1` first, and the refused connection takes about two seconds to fail before it falls back.

A test never judges the thread pool's speed, and never asserts that something is prompt against a wall clock, because every test class in the assembly runs at the same time on the one process-wide pool, and what one measures is the others (the write gate test failed on CI this way, 13 Oct 2026). To ask whether code holds a thread while it waits, start it on `OneThreadScheduler`, which has a thread of its own: code that held the thread keeps the next piece of work from starting, and code that did not lets every piece start. To ask whether a call leaves the pool alone, have the call report `Thread.CurrentThread.IsThreadPoolThread`. A wait that bounds something that must happen is a hang detector: it ends a hang as a failure and says nothing about speed. A test that can only be stable by sitting alone in the process belongs in `SerialTestGroup` or `WriteLockTimingGroup`, which keep their own members apart and do not pause the rest of the assembly.

The infrastructure tests give the pool 128 threads ready (`TestThreadPool`), so that a test waiting inside a limit for a timer's callback or the end of an await is not kept waiting while other tests hold the threads the pool started with (a thread per processor, four on a CI runner, then one more about every half second). Without them, a test such as the write gate's fails whenever other tests hold the pool's threads for about as long as its limit. A run that sets `WEIR_POOL_SAMPLE` (below) leaves the pool as the machine gives it, so that the tests holding many threads still show.

The infrastructure tests can report when the pool was slow. Set `WEIR_POOL_SAMPLE` to a file name and run them (with `DOTNET_PROCESSOR_COUNT=4` to get the pool a CI runner has, and `--logger "trx;LogFileName=run.trx"`): `PoolStarvationSampler` writes a line for every wait of half a second or more for work to begin, and `node scripts/attribute-pool-stalls.mjs run.trx pool.csv` names the tests that were running in each. It only reports, never fails a test. A stall of two seconds or more is a test to fix: give work that must be in flight together a thread of its own (`OwnThreads`), or make the wait asynchronous. This is how the 24-caller barrier in `ClaimConcurrencyTests` was found, which held 24 pool threads and starved the process for 10 s on four processors. To see which test starts a stall rather than which ones wait in it, run the same command with `-- xUnit.ParallelizeTestCollections=false`: only one test is then running. On a machine that is itself overloaded the pool is slow too (the pool adds no threads while the processors are all busy), so judge a stall on a quiet machine.

A test that needs a slow or long-lived child uses a program of our own: `Weir.TestChild` for the server's tests and `Weir.Tray.StandInServer` (a Windows-subsystem program with no console, standing in for `WeirServer.exe`) for the tray's, never a system console program such as `ping` run through `cmd.exe`. `scripts/check-test-console-programs.mjs` fails CI if a test source under `apps/` starts one (#821). When a console program inherits its shell's console and the shell is killed while the program starts, Windows can hand the program a fresh console, and on a desktop where Windows Terminal is the default terminal that opens an error window (#806). No automated test can watch for that: the window belongs to the desktop session, CI has no Windows Terminal, and it needs a specific kill timing. After changing `ProcessRunnerTests` or anything that starts processes in tests, run them on a Windows desktop and check that no console, Windows Terminal or `PING.EXE` window appears.

## Run

```powershell
$env:WEIR_HOME = "$env:TEMP\weir-dev"          # optional; defaults to %PROGRAMDATA%\Weir
$env:WEIR_WEB_DIST = "$PWD\apps\web\dist"      # optional; after `npm run build` in apps/web
dotnet run --project apps/server/src/Weir.Host -- --host 127.0.0.1 --port 9347
```

`--port` (what the Windows tray passes) wins over `PORT` (what the Docker entrypoint sets). Both default to 9347, and the server binds every interface unless `--host` narrows it. `Weir --version` prints the version and exits.

For day-to-day work, `scripts/dev-backend.ps1` loads the repository-root `.env`, picks the dev ports and starts the server. See [`docs/local-development.md`](../../docs/local-development.md).

## Configuration

The server reads `WEIR_*` environment variables and nothing else. It does not read a `.env` file itself. `WeirOptionsLoader.Load` (in `Weir.Core/Configuration`) turns the environment into one immutable `WeirOptions` record at startup; `RuntimePaths` resolves `WEIR_HOME`, the database path (`WEIR_DB_PATH`, default `data/weir.sqlite3` under `WEIR_HOME`) and the other runtime folders. An invalid value (for example `WEIR_CORS_ORIGINS=*`, a malformed octal mode or a bad port) stops startup with a message instead of being ignored.

The variables and their defaults are documented in the repository's [`.env.example`](../../.env.example). Operator settings that can change while Weir runs (libraries, rules, schedules, cleanup intervals, alerts) live in the database and are edited in the app under Settings, not through the environment.

## Schema and migrations

The numbered SQL scripts in `src/Weir.Infrastructure/Migrations/` are the only source of schema changes. `Weir.Infrastructure.Sqlite.SchemaMigrator` lists them in order, each with the revision it leaves behind, and records the current revision in the `schema_version` table (one `revision` row). A database from before migration `0039` records it in `alembic_version` instead; its first start on this build renames that table and carries the revision over. A database holding both tables is refused and left unchanged.

The current head is migration `0039_schema_version_table`, revision `0074_schema_version_table`. The first script, `0001_baseline_0036_drop_pruner_tables.sql`, is the frozen baseline (revision `0036_drop_pruner_tables`).

On startup the server:

- creates a missing database at head;
- opens a database already at head without writing to it;
- upgrades a database at any earlier revision in the list by applying every later migration, in order, in one transaction;
- refuses anything else with a message, and changes nothing: an unknown revision (a newer release, or a database Weir did not create, including one the Python backend made), a file with no recorded revision, or a malformed version table.

To change the schema, add the next numbered script (`0021_…sql`; the project embeds every `Migrations/*.sql`), append it to `SchemaMigrator.Migrations` with the next revision (`0056_…`), and add a migration test under `tests/Weir.Infrastructure.Tests/Sqlite/Migrations/` that builds a database at the previous head, writes a row in the old shape, upgrades and reads it back through the real store.

## API contract

`apps/web/openapi/weir-openapi.json` is the committed API contract, maintained by hand. The server embeds it at build time and serves it at `/openapi.json`, pruned to the operations it maps. When an endpoint changes, edit that file in the same change, run `npm run api:types:generate` in `apps/web` to regenerate the TypeScript types, and keep `OpenApiDocumentParityTests` passing. `npm run api:types:check` is the CI gate that the two agree.

## Publish and packaging

Self-contained single-file builds for `win-x64`, `linux-x64` and `linux-arm64` use the checked-in profiles in `src/Weir.Host/Properties/PublishProfiles`:

```powershell
dotnet publish apps/server/src/Weir.Host -p:PublishProfile=win-x64       # apps/server/artifacts/publish/win-x64/Weir.exe
dotnet publish apps/server/src/Weir.Host -p:PublishProfile=linux-arm64
```

Always publish through a profile. Passing `-r`, `--self-contained` or `-p:PublishSingleFile=true` on the command line makes those global properties, and the single-file analyzer then fails `Weir.Infrastructure` with IL3000. The profiles scope them to `Weir.Host`.

`MediaToolResolver` looks for ffmpeg and ffprobe in `WEIR_FFMPEG_DIR`, then (in a single-file build) `<app>/bin/ffmpeg`, then `PATH` (`.exe` only on Windows; PATHEXT and the current directory are never consulted). mkvmerge is found the same way (`WEIR_MKVTOOLNIX_DIR`, `<app>/bin/mkvtoolnix`, `PATH`) but is optional: without it a library's writer setting falls back to ffmpeg. `WEIR_HOME` is never searched — a default Windows install lets every local account write to it, so a tool found there could never be trusted. `GET /api/v1/system/media-tools` reports what an install has.

The product version is `WeirVersion` in `apps/server/Directory.Build.props`. The assembly version, `--version`, `/openapi.json` and both packages take it from there.

- **Docker** (`Dockerfile`, `docker/entrypoint.sh`, [`docker/README.md`](../../docker/README.md)): the SDK stage cross-publishes `linux-x64` or `linux-arm64` on the build machine's architecture; the final stage is `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble` with ffmpeg and mkvtoolnix from the distribution, user `weir` (UID/GID 1000), data under `/data/weir`, port 9347 and a `/health` healthcheck. The entrypoint has no migration step; the server migrates its own database.
- **Windows** (`packaging/windows/build-velopack.ps1`): builds the web app, publishes the server with the `win-x64` profile, publishes the tray app and packs them with Velopack. The tray is `Weir.exe` at the root; the server is `server/WeirServer.exe`, next to `server/web-dist`, `server/bin/ffmpeg` and `server/bin/mkvtoolnix`. `scripts/smoke-windows-package.ps1` then starts the packaged server the way the tray does and runs a real pass-through job.

## History

The notes kept while the server was ported from the Python backend, and the per-issue design notes that followed, are in [`docs/archive/server-port-notes.md`](../../docs/archive/server-port-notes.md). They are a historical record, not current documentation. The retired Python source itself is in git history before #523.
