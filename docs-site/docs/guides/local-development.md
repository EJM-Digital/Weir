---
sidebar_position: 1
title: Local Development
---

# Local Development

This guide is for developers building Weir from source. If you just want to run Weir, see the
[Quickstart](../quickstart) for Docker or the Windows installer instead — you don't need any of
this to use the app.

## Prerequisites

- **.NET 10 SDK** (`dotnet` on `PATH`; the exact version is pinned in `apps/server/global.json`)
- **Node.js 24** (npm on `PATH`)

The server uses file-backed SQLite under `WEIR_HOME`. No PostgreSQL required. Weir has no
Python; the contract suite and the E2E tests are .NET too (see below).

## Clone and run

```bash
git clone https://github.com/EJM-Digital/Weir.git
cd Weir
```

Copy `.env.example` to `.env` in the repository root and set:

- **`WEIR_SESSION_SECRET`** — a long random string (required for auth)
- **`WEIR_CREDENTIALS_SECRET`** — a separate long random value (required before saving provider credentials)

Then:

```bash
cd apps/web
npm ci
npm run dev
```

`npm run dev` starts the .NET server (with `dotnet watch`) and the Vite dev server together. If
you prefer two terminals, run `.\scripts\dev-backend.ps1` in one and `.\scripts\dev-web.ps1` in
the other.

There is no separate migration step. The server creates its SQLite database, or brings an
existing one up to date, when it starts.

Open **http://127.0.0.1:8782/** in your browser. You'll be guided through first-run setup.

| Component | URL | Port |
|-----------|-----|------|
| Web UI (Vite dev server) | http://127.0.0.1:8782 | 8782 |
| API (the .NET server) | http://127.0.0.1:18788 | 18788 |

The Vite dev server proxies `/api` requests to the server automatically — no CORS configuration needed for local development.

## Server setup

The server lives in `apps/server` (solution `apps/server/Weir.slnx`).

### Environment file

`WEIR_SESSION_SECRET` signs sessions and CSRF tokens; `WEIR_CREDENTIALS_SECRET` encrypts saved
provider credentials (see [Clone and run](#clone-and-run) above for setting them).

Optional path overrides (defaults are under `WEIR_HOME`):

- `WEIR_HOME`, `WEIR_DB_PATH`, `WEIR_BACKUP_DIR`, `WEIR_LOG_DIR`, `WEIR_TEMP_DIR`

`WEIR_ENV` defaults to `production` when unset, so the developer exception page only appears when
you set `WEIR_ENV=development` yourself — `npm run dev` and `dev-backend.ps1` already do this for you.

### Build and test

```powershell
dotnet build apps/server/Weir.slnx -warnaserror
dotnet test apps/server/Weir.slnx
```

CI builds with warnings treated as errors, so a warning locally will fail CI too.

### Database

There is no migration command. The server creates its SQLite database, or applies any pending
numbered SQL migrations, every time it starts.

### Start the API

```powershell
.\scripts\dev-backend.ps1
```

This loads the repository-root `.env`, sets `WEIR_HOME` to `.local-dev-home` in the repository
unless you set it yourself, and runs the server with `dotnet watch run` on the API address from
`scripts/dev-ports.json` (`127.0.0.1:18788`).

Or manually:

```powershell
$env:WEIR_SESSION_SECRET = "<long random>"
$env:WEIR_WEB_DIST = "apps/web/dist"   # optional: also serve a built web app
dotnet run --project apps/server/src/Weir.Host -- --host 127.0.0.1 --port 18788
```

When run manually the server reads only `WEIR_*` environment variables; it does not load `.env` by itself.

To check that the local setup is healthy (build, `.env`, `/health`, `/ready`, bootstrap status):

```powershell
.\scripts\verify-local.ps1
```

## Web app

`npm run dev` (see [Clone and run](#clone-and-run) above) clears processes on the default dev
ports, then starts the .NET server (with `dotnet watch`) and Vite together. The web app itself
lives in `apps/web`.

### OpenAPI type generation

The API contract is the committed file `apps/web/openapi/weir-openapi.json`. It is maintained by
hand: when a server request or response shape changes, update that file in the same change. Then
regenerate the TypeScript types:

```powershell
cd apps/web
npm run api:types:generate
```

This regenerates `apps/web/src/lib/api/generated/openapi-types.ts`. CI runs `npm run api:types:check` to catch types that were not regenerated.

## Weir home paths

| Platform | Default `WEIR_HOME` |
|----------|------------------------|
| Windows | `%PROGRAMDATA%\Weir` |
| Linux/macOS | `$XDG_DATA_HOME/weir` or `~/.local/share/weir` |

`scripts/dev-backend.ps1` uses `.local-dev-home` in the repository instead, so development data stays out of a real install.

The default SQLite file is `{WEIR_HOME}/data/weir.sqlite3` unless `WEIR_DB_PATH` overrides.

## Forgot the dev admin password

```powershell
.\scripts\dev-reset-auth.ps1 --list   # show the accounts, change nothing
.\scripts\dev-reset-auth.ps1 --yes    # delete every user and session
```

This clears users and sessions so `/setup` works again (it runs `node scripts/dev-reset-auth.mjs`
after loading `.env`). It uses `WEIR_HOME` from your environment or `.env`; if you rely on
`dev-backend.ps1`'s `.local-dev-home` default, set `$env:WEIR_HOME` to that folder first. Stop
the server first, or restart it afterwards.

## Contract and E2E tests (optional)

The contract suite (`apps/server/tests/Weir.Contract.Tests`) and the browser E2E tests
(`apps/server/tests/Weir.E2E.Tests`) are .NET projects. They act as outside judges of the running
server: each starts the built server as its own process and talks to it over HTTP or through a
browser. Nothing else needs installing.

```powershell
dotnet build apps/server/Weir.slnx
cd apps/web
npm ci
npm run build
cd ../..
```

Contract suite (one area, or all of them):

```powershell
dotnet test apps/server/tests/Weir.Contract.Tests --filter "Area=activity"
dotnet test apps/server/tests/Weir.Contract.Tests
```

E2E (the browser comes from Playwright's own install script, built with the project):

```powershell
pwsh apps/server/tests/Weir.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
$env:WEIR_E2E = "1"
dotnet test apps/server/tests/Weir.E2E.Tests
```

Both start the server from the build output, so build it first. Set `WEIR_CONTRACT_SERVER` to test a
published server instead (a `Weir.dll` or an executable).

## Troubleshooting

| Problem | Solution |
|---------|----------|
| `dotnet` not found | Install the .NET 10 SDK and open a new terminal |
| SDK version error from `global.json` | Install the SDK version named in `apps/server/global.json` |
| `npm run dev` fails | Install Node.js 24 and open a new terminal |
| Login/setup broken | Use the Vite proxy (same origin); don't set `VITE_API_BASE_URL` |
| "Cannot reach the API" | Check `GET /health` on port 18788 and look at the server output for startup errors |
| SQLite or migration errors at startup | Read the server output; check `WEIR_HOME` / `WEIR_DB_PATH` point at a writable folder |
| Port already in use | `npm run dev` stops its *own* previous dev API/web processes first (`.dev-api.pid`, `dev:stop-web`); it never kills an unrelated process on that port — including an installed Weir, whose port can coincide with the dev one. Free it yourself, or see `scripts/dev-ports.json` for defaults |
| Forgot the dev admin password | Run `.\scripts\dev-reset-auth.ps1 --yes` (see above) |
