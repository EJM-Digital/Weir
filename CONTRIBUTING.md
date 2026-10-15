# Contributing - Weir

Weir is a self-hosted media workflow app with a C# / .NET 10 server and SQLite in `apps/server`, a React + Vite web shell in `apps/web`, and a .NET Windows tray app in `apps/tray`.

## Workflow

Use short-lived branches and open pull requests into `main`. Keep CI green before merge.

## Local checks

Server build and unit tests. Needs the .NET 10 SDK (pinned in `apps/server/global.json`). Warnings are errors, including the unused-code analyzers.

```powershell
dotnet build apps/server/Weir.slnx -warnaserror
dotnet test apps/server/Weir.slnx
```

`RealFfmpegTests` skip unless ffmpeg and ffprobe are on `PATH` or `WEIR_FFMPEG_DIR` names a folder holding them. Dependency advisories: `node scripts/check-dotnet-vulnerabilities.mjs apps/server/Weir.slnx`.

Web checks. `package-lock.json` is committed, so prefer reproducible installs.

```powershell
cd apps/web
npm ci
npm run lint
npm run format
npm run api:types:check
npm run build
npm run test
cd ../..
node scripts/check-dead-code.mjs
```

Web tests never reach the network: `apps/web/src/test/setup.ts` refuses every `fetch` that a test has not stubbed and fails the test that made it. Stub the api function the component calls (`vi.spyOn(someApi, "fetchThing")`), or the query hook, in the test. Where a component waits on a debounce, move the clock with fake timers instead of waiting for it.

The contract suite and the E2E smoke are .NET projects that judge a running server from outside: each starts the built server as its own process, with its own data folder and port, and talks to it over HTTP or through a browser. Weir has no Python.

Contract suite (every area in [`areas.json`](apps/server/tests/Weir.Contract.Tests/areas.json); see [`README.md`](apps/server/tests/Weir.Contract.Tests/README.md)):

```powershell
dotnet build apps/server/Weir.slnx
dotnet test apps/server/tests/Weir.Contract.Tests --filter "Area=activity"   # one area
dotnet test apps/server/tests/Weir.Contract.Tests                            # every area
```

E2E smoke (Playwright for .NET, a fresh data folder, the built web app served by the .NET server):

```powershell
dotnet build apps/server/Weir.slnx
cd apps/web; npm ci; npm run build; cd ../..
pwsh apps/server/tests/Weir.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium   # once
$env:WEIR_E2E = "1"
dotnet test apps/server/tests/Weir.E2E.Tests
```

A failed browser test fails the CI job and is not run again. A test never waits for the wall clock: it waits for a signal, moves a fake clock, or polls with a ceiling it names a hang guard, and `node scripts/check-wall-clock-waits.mjs` fails on a new sleep or guessed delay (`scripts/README.md`). The browser tests drive the server's own clocks through its test-only `WEIR_PERIODIC_TASK_SPEEDUP` and the page's through Playwright's clock.

The server's own unit and API tests leave both out: `dotnet test apps/server/Weir.slnx --filter "Category!=Stress&Category!=Contract&Category!=E2E"`, which is what the CI server jobs run.

Tray (Windows): `dotnet build apps/tray/Weir.Tray.slnx` and `dotnet test apps/tray/Weir.Tray.slnx`.

Windows package: `powershell -ExecutionPolicy Bypass -File packaging/windows/build-velopack.ps1`, then `powershell -ExecutionPolicy Bypass -File scripts/smoke-windows-package.ps1`.

See `docs/local-development.md` for env layout and CI parity.

Remote Docker validation does not require local Docker Desktop:

```powershell
.\scripts\verify-docker-remote.ps1
```

This triggers the GitHub `Test` workflow for the current ref and watches it. The Docker build and smoke test run on GitHub-hosted runners.

## Security

Do not commit `.env`, real secrets, production database files, logs, backups, or machine-specific media paths. Use `.env.example` patterns only.

## License

By contributing to Weir, you agree that your contribution is provided under the repository license: AGPL-3.0-or-later.
