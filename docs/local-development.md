# Weir — local development (server + web)

This **Weir** repository contains **`apps/server`** (the C# / .NET 10 server: HTTP API, **SQLite**, cookie sessions, the job queue and Processing), **`apps/web`** (React/Vite) and **`apps/tray`** (the Windows tray app). Media manager connections (Sonarr, Radarr, Deluno, or anything posting Weir's own payload) live under **Setup › Connections › Media managers**; inbound events all arrive at `POST /api/v1/intake/webhook/{source}`. See [ADR-0013](adr/ADR-0013-media-managers-are-kinds-not-products.md).

**Local web/API ports** are versioned in **`scripts/dev-ports.json`**; the policy is summarized in **[`docs/ports.md`](ports.md)**.

## Prerequisites

- **.NET 10 SDK** (pinned in **`apps/server/global.json`**)
- **Node.js** (pinned in **`.node-version`**; npm on `PATH`)
- **PowerShell 7 (`pwsh`)** only to install the browser for the E2E tests and the live audit (step 3 of [Contract suite, E2E and live audit](#contract-suite-e2e-and-live-audit-local)); Windows PowerShell also works on Windows
- Weir has no Python: the server, the tray and every test suite are .NET, and the web app is TypeScript

## One-time setup (after cloning)

Activate the pre-push hook so the no-Python and contract-area checks, prettier, the dead-code guard and the OpenAPI types drift check run locally before every push:

```powershell
git config core.hooksPath .githooks
```

The hook delegates to `scripts/pre-push.mjs` (a plain Node script; run it by hand with `node scripts/pre-push.mjs`). It skips the web checks gracefully if `apps/web/node_modules` is not installed yet — run `npm ci` in `apps/web` first for full coverage.

The server persists state in **file-backed SQLite** under **`WEIR_HOME`** and creates or migrates its database itself when it starts; there is no separate migration command.
Docker Desktop is not required for normal local development or for shipping a release.

## `.env` (local)

Copy **`.env.example`** → **`.env`** at the repository root.

Required for auth and `/api/v1`:

- **`WEIR_SESSION_SECRET`** — long random value.

Optional path overrides (defaults are under the OS-specific **`WEIR_HOME`**):

- **`WEIR_HOME`**, **`WEIR_DB_PATH`**, **`WEIR_BACKUP_DIR`**, **`WEIR_LOG_DIR`**, **`WEIR_TEMP_DIR`**

The server itself reads **`WEIR_*`** from its environment only. The dev launchers (**`scripts/dev-backend.ps1`**, **`scripts/dev.ps1`**, **`npm run dev`**) load **`.env`** into that environment first; shell variables still override.

**`WEIR_ENV`** defaults to **`production`** when unset, so a source or systemd install gets ASP.NET's production error handling (no developer exception page) out of the box. The dev launchers above set **`WEIR_ENV=development`** explicitly; do the same if you run the server manually and want the developer exception page.

## Server (`apps/server`)

Build and test from the repo root ([ADR-0017](adr/ADR-0017-backend-on-dotnet.md); more in **[`apps/server/README.md`](../apps/server/README.md)**):

```powershell
dotnet build apps/server/Weir.slnx -warnaserror
dotnet test apps/server/Weir.slnx
```

Run it:

```powershell
.\scripts\dev-backend.ps1
```

That loads **`.env`**, defaults **`WEIR_HOME`** to **`.local-dev-home`** in the repository, serves **`apps/web/dist`** when it has been built, and runs **`dotnet watch run`** on **`apps/server/src/Weir.Host`** bound to the API host and port in **`scripts/dev-ports.json`**. By hand:

```powershell
$env:WEIR_SESSION_SECRET = "<long random>"
$env:WEIR_HOME = "$PWD\.local-dev-home"
# $env:WEIR_CORS_ORIGINS = "http://127.0.0.1:8782"
dotnet run --project apps/server/src/Weir.Host -- --host 127.0.0.1 --port 18788
```

Forgot the local admin password? **`.\scripts\dev-reset-auth.ps1 --list`** shows the accounts and **`--yes`** clears users and sessions so **`/setup`** works again.

## Web app

```powershell
cd apps/web
npm ci
npm run dev
```

**`npm run dev`** stops this worktree's previous dev API (by the PID it recorded) and anything listening on the dev web port from **`scripts/dev-ports.json`**, then starts the .NET server (**`dotnet watch run`**) and Vite together (see **`apps/web/scripts/run-dev-stack.mjs`**). Use **`npm run dev:quick`** only when you are sure those ports are already free.

The Vite dev server and **`vite preview`** use **[`scripts/dev-ports.json`](../scripts/dev-ports.json)**. See **[`docs/ports.md`](ports.md)**. To override temporarily, set **`VITE_DEV_API_PROXY_TARGET`** and **`WEIR_DEV_API_PORT`** together.

### A real server on a fresh data folder

There is no mock of the Weir API: design and layout checks run against a real Weir, the same server the product ships. To look at the built web app on a clean, disposable install, serve it from the real server with its own data folder. From the repository root:

```powershell
cd apps/web
npm ci
npm run build                      # writes apps/web/dist
cd ../..
dotnet build apps/server/src/Weir.Host

$env:WEIR_SESSION_SECRET = "<long random>"
$env:WEIR_HOME = Join-Path $env:TEMP "weir-home-review"     # a new folder: a first-run install
$env:WEIR_WEB_DIST = "$PWD\apps\web\dist"
dotnet run --project apps/server/src/Weir.Host --no-build -- --host 127.0.0.1 --port 18795
```

Browse **`http://localhost:18795`** and create the admin account on the first screen. The server never opens a browser itself. Pick any free port that is not in **`scripts/dev-ports.json`**, so an installed Weir and your **`npm run dev`** stack are left alone, and give each instance its own **`WEIR_HOME`**: it holds the database and the settings, so an install you care about is never touched. Cookies are shared across ports on one host name, so browse a spare instance as `localhost` when your main one is at `127.0.0.1`, or the other way round.

An empty install shows the empty screens. To see files flow through it, add a workflow whose folders hold media you generated for the purpose (the end-to-end and live-audit runs make theirs with FFmpeg; see **`apps/server/tests/Weir.E2E.Tests/`** and **`apps/server/tools/Weir.LiveAudit/`**), or connect a real media manager. Press Ctrl+C to stop it, then delete the data folder. If you started it in the background, stop it by the PIDs you recorded (`dotnet run` and the `Weir.exe` it starts), never by image name.

To rebuild the web app as you edit instead, use **`npm run dev`** (above): Vite on the dev web port, proxying to a real server on the dev API port. To put Vite in front of a server you started yourself, as above, set **`VITE_DEV_API_PROXY_TARGET`** to its address (`http://localhost:18795`) and run **`npm run dev:web`**.

### API contract and generated types (OpenAPI)

**`apps/web/openapi/weir-openapi.json`** is the committed API contract. The server embeds it at build time and serves it at **`/openapi.json`** (pruned to the operations it maps, with its own version), and the web app's TypeScript types are generated from it:

```powershell
cd apps/web
npm run api:types:generate   # regenerate src/lib/api/generated/openapi-types.ts
npm run api:types:check      # CI: fails when the generated types are stale
```

When you add or change an endpoint, edit **`weir-openapi.json`** in the same change (it is maintained by hand), regenerate the types, and keep **`OpenApiDocumentParityTests`** in **`apps/server/tests/Weir.Api.Tests`** passing.

## Weir home (product paths)

On-disk runtime defaults must **not** be tied to “the Git clone directory” or the process current working directory.

- **`WEIR_HOME`** (optional): explicit absolute root for product-owned data.
- **Default when unset:**
  - **Windows:** `%PROGRAMDATA%\Weir` (normally `C:\ProgramData\Weir`)
  - **Linux/macOS:** `$XDG_DATA_HOME/weir`, or `~/.local/share/weir`

The default SQLite file is **`{WEIR_HOME}/data/weir.sqlite3`** unless **`WEIR_DB_PATH`** overrides.

**Linux containers:** set `WEIR_HOME` to a volume mount (e.g. `/var/lib/weir`) so data survives restarts.

## CI validation

The **`CI`** workflow (`.github/workflows/ci.yml`) is path-aware: a job runs only when the paths it
cares about changed (a manual run always runs everything). Its jobs:

1. **`repo-checks`**: the agent documentation map, the GitHub Actions pin check, the check that no
   Python file is tracked, the contract-area list check, and the release/CI gate rules.
2. **`server-linux`**: **`dotnet build -warnaserror`**, **`dotnet test`** (the unit and API tests; the contract and
   E2E projects are left to their own jobs) and a NuGet vulnerability scan for **`apps/server`** on Linux.
3. **`server-windows`**: the same server build and tests on Windows (always runs on a push to `main`).
4. **`web-dist`**: builds the production web app once, for `e2e-smoke` to serve.
5. **`web`**: **`npm ci`**, **`api:types:check`**, lint, format and unit tests in **`apps/web`**; the
   dead-code guard; an `npm audit` on pull requests.
6. **`e2e-smoke`**: Playwright for .NET against the real server serving the built web app
   (**`WEIR_E2E=1`**, project [`apps/server/tests/Weir.E2E.Tests`](../apps/server/tests/Weir.E2E.Tests/README.md),
   same as the local E2E below); pull requests only.
7. **`contract`**: the contract suite against the .NET server, one job per area in
   [`areas.json`](../apps/server/tests/Weir.Contract.Tests/areas.json), in parallel
   (`dotnet test --filter "Area=<area>"`).
8. **`tray`** (always runs on a push to `main`): the tray's unit tests.
9. **`coverage`**: the line and branch coverage of the server, the tray and the web app (measured in the jobs above), merged and held to the floors in [`docs/coverage/floors.json`](coverage/floors.json); see [`coverage/README.md`](coverage/README.md).
10. **`packaging`**: the Docker and Windows package smokes (`.github/workflows/ci-packaging.yml`) —
   builds the image, runs it and the live packaged audit (`apps/server/tools/Weir.LiveAudit`), and runs the Velopack build and
   **`scripts/smoke-windows-package.ps1`**.
11. **`ci-passed`**: the verdict. It passes only when every job that was due for the change passed and
    every other job was skipped — this is the one check `main`'s ruleset requires.

Pushing a SemVer tag **`v*`** (`vX.Y.Z`, or `vX.Y.Z-rc.N` for a release candidate) runs the **`Release`** workflow. It does not repeat these tests: it
refuses to publish unless `CI` already passed (`ci-passed`) and the golden path was recorded as passed (`golden-path`) on the tagged commit, then builds, tests
and publishes the release artefacts — see **[`docs/release.md`](release.md)**.

## Contract suite, E2E and live audit (local)

All three are .NET projects under `apps/server` that judge a **running** Weir from outside: each starts the built
server as its own process (own data folder, own port) and talks to it over HTTP or through a browser. None loads
server code.

```powershell
# 1. The server and the test projects (building the solution builds the host first)
dotnet build apps/server/Weir.slnx

# 2. The contract suite: one area, or everything
dotnet test apps/server/tests/Weir.Contract.Tests --filter "Area=activity"
dotnet test apps/server/tests/Weir.Contract.Tests

# 3. The browser tests: the web app, the browser, then the suite
cd apps/web; npm ci; npm run build; cd ../..
pwsh apps/server/tests/Weir.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
$env:WEIR_E2E = "1"
dotnet test apps/server/tests/Weir.E2E.Tests
```

The areas are listed in [`areas.json`](../apps/server/tests/Weir.Contract.Tests/areas.json); the harness, the keep-data
and published-server switches are in **[`Weir.Contract.Tests/README.md`](../apps/server/tests/Weir.Contract.Tests/README.md)**,
and the browser tests are in **[`Weir.E2E.Tests/README.md`](../apps/server/tests/Weir.E2E.Tests/README.md)**. The real-FFmpeg
scenarios run when `ffmpeg` and `ffprobe` are on `PATH` (or in `WEIR_CONTRACT_REAL_FFMPEG_DIR`) and are skipped
otherwise; CI sets `WEIR_REQUIRE_REAL_TOOLS=1`, so there a missing tool fails them instead. A plain `dotnet test apps/server/Weir.slnx --filter "Category!=Stress&Category!=Contract&Category!=E2E"` runs
only the unit and API tests, which is what the `server-linux` and `server-windows` jobs do.

The packaged live audit (`apps/server/tools/Weir.LiveAudit`) walks every screen of an installed Weir, such as the Docker
image or the Windows package. Point it at a running server:

```powershell
$env:WEIR_LIVE_BASE_URL = "http://localhost:9347"
dotnet run --project apps/server/tools/Weir.LiveAudit -c Release
```

Its other settings (`WEIR_LIVE_E2E_*`) are described in **[`Weir.LiveAudit/README.md`](../apps/server/tools/Weir.LiveAudit/README.md)**.

## Property tests and the intake fuzz test

Where an input is wide (paths a media manager reports, the settings an operator types, the JSON bodies, the web's sizes, times and
addresses) the tests state a rule and try it on generated inputs instead of a handful of examples: **FsCheck** in the server
projects (`*PropertyTests.cs`), **fast-check** in the web app (`*.property.test.ts`). Every run uses the same seed, so a pass or a
failure can be repeated exactly, and a failure prints the seed and the smallest input that breaks the rule.

| Variable | Does |
| --- | --- |
| `WEIR_PROPERTY_SEED` | A whole number to run other inputs than the fixed seed's, or `random` to look for new ones (the seed it picks is in a failure). Set it to a failure's seed to run that failure again. |
| `WEIR_PROPERTY_RUNS` | How many inputs each property is tried on (100 unless set). |

```powershell
# Look for failures the fixed seed does not reach
$env:WEIR_PROPERTY_SEED = "random"; $env:WEIR_PROPERTY_RUNS = "1000"
dotnet test apps/server/Weir.slnx --filter "FullyQualifiedName~PropertyTests"
cd apps/web; npx vitest run property
```

A property that fails is a bug to fix: copy the shrunk input into a plain example test beside the others (so the case stays
tested whatever the seed), then fix the code. The intake fuzz test (`IntakeFuzzTests`, contract area `media_managers`) sends mutated
copies of the recorded Deluno bodies to a running server and holds every answer to the statuses the contract documents.

## Split-origin production

If the static site and API are on **different origins**:

- Use **HTTPS** everywhere.
- Set **`WEIR_CORS_ORIGINS`** (and **`WEIR_TRUSTED_BROWSER_ORIGINS`** if stricter POST checks) to the real web origin.
- Session cookies typically need **`SameSite=None; Secure`** on the API for credentialed cross-origin `fetch` when not using a dev proxy.

## Troubleshooting (local dev)

1. **`npm` / `npm run dev` fails**  
   Install **Node.js 24** and open a **new** terminal. From repo root: **`.\scripts\dev-web.ps1`**.

2. **`dotnet` not found, or the API exits during startup**  
   Install the **.NET 10 SDK** and open a new terminal. A build error, a missing **`WEIR_SESSION_SECRET`** or an unwritable **`WEIR_HOME`** stops the server; the reason is in the same terminal.

3. **`npm run dev` starts but login/setup is broken**  
   Use the **Vite proxy** (same origin); do not set **`VITE_API_BASE_URL`** unless you intend split-origin dev.

4. **“Cannot reach the API” vs HTTP 503**  
   **`GET /health`** on the API port (**`scripts/dev-ports.json`**) returns **200** once the server process is up, and **`GET /ready`** reports **`ready: true`** once its database is open. **`/api/v1`** still needs **`WEIR_SESSION_SECRET`** and a **writable** database path under **`WEIR_HOME`**. The web shell treats **network errors** (no TCP response) separately from **HTTP 503** from a live API (see **`apps/web`** error guards + **`ApiEntryError`**).

5. **"Weir cannot start: ... schema"**  
   The server upgrades a database at any earlier schema revision it knows and refuses anything else (a file with no schema, or a revision it does not recognise) rather than guess. Point **`WEIR_HOME`** (or **`WEIR_DB_PATH`**) at a fresh folder for a clean database.

6. **Port already in use**  
   See **`scripts/dev-ports.json`**. **`WEIR_DEV_API_PORT`** + **`VITE_DEV_API_PROXY_TARGET`** can override for one session.

7. **Two dev windows, or the server alone**  
   **`.\scripts\dev.ps1`** (launcher only — preflight warns if `.env`, the session secret or `dotnet` are missing) opens **`dev-backend.ps1`** and **`dev-web.ps1`** in separate windows with separate logs. **`npm run dev`** does not replace this: it always starts the API and Vite together, interleaved in one terminal, from `apps/web`, and cannot run the server on its own. **`dev-backend.ps1`** by itself is also how **`verify-local.ps1`** and manual API testing (curl, Postman, a `WEIR_HOME` you want to inspect between requests) point at a server that keeps running independently of any web tooling. The PowerShell trio therefore stays alongside `npm run dev` rather than being folded into it. Full check: **`.\scripts\verify-local.ps1`** with the API running.

## All-in-one Docker

For a **single-container** runtime (the .NET server, the production web app, ffmpeg and mkvmerge, with SQLite on a volume), see **`docker/README.md`** and root **`compose.yaml`**. Build locally with `docker build -t weir:local .`.

For maintainers without working local Docker, use GitHub-hosted validation:

```powershell
.\scripts\verify-docker-remote.ps1
```

The release workflow also builds, publishes, verifies, and smoke-tests Docker on GitHub-hosted runners.

## Visual shell

The **source of truth** for the product UI is **`apps/web`**. How page content is laid out is in [`design/content-language.md`](design/content-language.md).
