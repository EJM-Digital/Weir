# Weir — web app

**React + TypeScript + Vite** app served by the .NET server (`apps/server`) on the same origin as the API. It uses the server's cookie session auth. **This directory is the source of truth** for Weir's UI: tokens, logo, the **Inter** and **JetBrains Mono** fonts (self-hosted through Fontsource), the shell and every screen.

The version in the user menu at the foot of the side menu is the one the running server reports (`GET /api/v1/system/readiness`), not `package.json`.

## Stack

- React 19, React Router 7, TanStack Query
- Tailwind CSS 4 (`@tailwindcss/vite`, `@theme` in `src/index.css`). The look is owned by `src/styles/weir-tokens.css`, `weir-shell.css`, `weir-content.css` and the screen stylesheets `weir-processing.css`, `weir-history.css` and `weir-library.css`. See [`../../docs/visual-identity.md`](../../docs/visual-identity.md) and [`../../docs/design/content-language.md`](../../docs/design/content-language.md). A Tailwind utility cannot override a property a `weir-*` class sets; the content-language doc explains why.
- Vitest + Testing Library for unit tests

## Development

```powershell
cd apps/web
npm ci
npm run dev
```

**`npm run dev`** first runs **`dev:stop-api`** and **`dev:stop-web`**, then starts **both** the .NET server (`dotnet watch run`, same as `../../scripts/dev-backend.ps1`) and Vite in one terminal via `scripts/run-dev-stack.mjs` (no reliance on `node_modules/.bin` shims, which some Windows setups omit). **`dev:stop-api`** stops only *this worktree's* dev API — the process `run-api-dev.mjs` spawned and recorded in `.dev-api.pid` at the repo root, verified by its command line before it is touched — never whatever else happens to be listening on the dev API port, so an installed Weir is never taken down (see **`../../docs/ports.md`**). **`dev:stop-web`** stops only *this worktree's* dev Vite server the same way — the process `run-dev-stack.mjs` spawned and recorded in `.dev-web.pid` at the repo root, verified by its command line before it is touched — never whatever else happens to be listening on the dev web port. Override the dev API port with **`WEIR_DEV_API_PORT`** / the dev web port with **`WEIR_DEV_WEB_PORT`**. The stack **waits for `GET /health` on the API port before starting Vite**, so the browser is not served until the server has finished starting up (including creating or migrating its SQLite database). Override wait with **`WEIR_DEV_STACK_API_WAIT_MS`** (default `120000`). Use **`npm run dev:quick`** to skip the port-stop step when you know the default ports are free. Use **`npm run dev:web`** for Vite only (e.g. when the API is already running elsewhere).

**`package-lock.json`** is committed; use **`npm ci`** for reproducible installs (CI uses **`npm ci`**).

- **Web app:** **`http://127.0.0.1:8782`** (from **`../../scripts/dev-ports.json`**; see **`../../docs/ports.md`**)
- **Server (standalone):** **`../../scripts/dev-backend.ps1`** (the .NET server via `dotnet watch run`) on the **API** host/port from the same JSON file.

Vite **`server`** and **`preview`** proxy **`/api`** to that API origin (override with `VITE_DEV_API_PROXY_TARGET`). The browser uses one origin for the page and `/api/*`, so **HttpOnly** cookies work in dev and **`npm run preview`** (E2E uses ephemeral ports).

Do not point the app at the raw API port unless CORS and cookie **`SameSite`** / **`Secure`** are set for cross-origin deployment.

### Server CORS / trusted origins

With **`npm run dev`**, the browser talks to **`/api` on the same origin as the Vite page**, so you normally **do not** need CORS entries for that path. In **`WEIR_ENV=development`**, the server also pairs **`http://localhost:<port>`** with **`http://127.0.0.1:<port>`** for any loopback origin you list, so either URL works.

For **non-proxied** setups (e.g. static hosting on another port), set on the server:

- `WEIR_CORS_ORIGINS` — include the exact web origin (e.g. `http://127.0.0.1:8782`)
- Optionally `WEIR_TRUSTED_BROWSER_ORIGINS` for stricter POST Origin/Referer checks

Then set in this app:

- `VITE_API_BASE_URL` — split-origin **production** (or `vite preview`) API origin (no trailing slash); **ignored in `vite dev`** so `/api` always goes through the dev proxy

**Production (split origins):** use **HTTPS** end-to-end; set **`WEIR_CORS_ORIGINS`** / **`WEIR_TRUSTED_BROWSER_ORIGINS`** to the **single** public web origin you ship; set **`VITE_API_BASE_URL`** here to the API origin (no trailing slash). Session cookies on the API host generally need **`SameSite=None; Secure`** so credentialed `fetch` from the web origin works. The server's cookie flags are env-driven — see ADR-0003 and **`../../docs/local-development.md`**.

## Routes

Defined in `src/app/router.tsx`.

| Path | Screen |
|------|--------|
| `/login` | Sign in |
| `/setup` | Create admin (first run, while no admin exists) |
| `/setup-wizard` | Setup wizard: time zone, the first Movies and TV folders, automatic backups |
| `/` | Dashboard, the first screen: what Weir is working on now |
| `/activity` | Activity: every file Weir has touched |
| `/library` | Library: files already in a library, and what Weir would do to each |
| `/setup/workflows`, `/setup/workflows/schedule` | Workflows: File paths, Schedule |
| `/setup/connections`, `/setup/connections/download-clients`, `/setup/connections/alerts` | Connections: Media managers, Download clients, Alerts |
| `/setup/rules`, `/setup/rules/metadata`, `/setup/rules/devices` | Rules: Profiles, Metadata & artwork, Playback devices |
| `/setup/performance`, `/setup/performance/cleanup`, `/setup/performance/timers` | Performance: Speed, Cleanup, Weir's timers |
| `/system` | System: About, Backups, Security, Logs (`?tab=`) |
| `/settings`, `/history`, `/processing` | Redirects from earlier addresses (`src/app/legacy-redirects.tsx`); `/settings?tab=` lands on the matching setup tab |

Everything under `/` needs a session and a finished or skipped setup wizard.

## API usage

All calls use `credentials: 'include'`. Types are generated from `openapi/weir-openapi.json` (`npm run api:types:generate`; CI runs `npm run api:types:check`).

**No** `localStorage` (or other browser storage) for session or tokens — TanStack Query caches in memory only.

## Scripts

| Command | Description |
|---------|-------------|
| `npm run dev` | Stop the dev API and web port, then API + Vite |
| `npm run dev:quick` | API + Vite without stopping anything first |
| `npm run lint` | ESLint, no warnings allowed |
| `npm run format` | Prettier check |
| `npm run build` | Token check, typecheck, production bundle, bundle budget |
| `npm run preview` | Preview production build |
| `npm run test` | Vitest |
| `npm run api:types:check` | Regenerate API types and fail on drift |
| `npm run ci` | `lint`, `format`, `check:tokens`, `build`, `test` |

## Request tests and recorded replies

Every function that asks the server for something has a case in `src/test/wire/*.cases.ts`, run by `src/lib/api/request-builders.test.ts` against a stub of `fetch`: the method, the path, the query string in its sent order, the JSON body and the CSRF token must be exactly what is written there, and a refusal must reach the caller in the server's words. `request-builders-guard.test.ts` fails when a request function has no case, or when a file that makes requests is neither covered nor listed as still to do.

The answers those cases use are not written by hand. `src/test/recorded/*.json` holds what a running Weir server sent, recorded by `scripts/record-replies.mjs` (it starts the built server on a scratch folder, makes the requests as the web app does, and stops the server by its process id). Run it again after changing a route or its reply:

```
dotnet build apps/server/src/Weir.Host -c Release
node apps/web/scripts/record-replies.mjs --work C:/weir-recorded-replies
```

It needs FFmpeg on the `PATH` (or `--ffmpeg`) to make two small test videos. `recorded-replies.test.ts` checks every recorded answer against the server's document (`openapi/weir-openapi.json`), and `hand-kept-types.test.ts` ties the hand-written types to the same document, so a field the server adds, drops or renames fails the build.

## CI

The GitHub Actions **Test** workflow runs `npm ci`, `api:types:check`, lint, format, build and unit tests in this directory after the .NET server build and tests, then Playwright E2E (`apps/server/tests/Weir.E2E.Tests`) against the real .NET server serving the built web app (not `vite preview`) (see **`../../docs/local-development.md`**).

## Intentionally not built

- Non-session auth (JWT-in-browser, token storage)

See **`../../docs/adr/`** (especially ADR-0003).
