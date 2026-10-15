# Agent Harness Operating Model

Weir should be easy for coding agents to inspect, modify, validate, and repair without relying on hidden context. People set priorities and acceptance criteria; agents make changes through repo-local tools, tests, docs, and pull requests.

## Principles

1. **Repository knowledge is the source of truth.**
   If a decision matters after the current conversation ends, put it in docs, tests, scripts, schemas, or a GitHub issue.

2. **The root agent guide is a map, not a manual.**
   [`../AGENTS.md`](../AGENTS.md) points to deeper docs. Keep it short enough that agents can read it on every task.

3. **Make the app legible to agents.**
   Prefer reproducible local commands, deterministic fixtures, logs, health endpoints, screenshots, and smoke scripts over manual-only QA.

4. **Promote repeated review comments into enforcement.**
   If the same rule is repeated twice, encode it in a test, linter, script, checklist, or durable doc.

5. **Preserve safety contracts mechanically.**
   File lifecycle, release, security, and upgrade rules should have tests or smoke checks where practical.

6. **Keep PRs small and recoverable.**
   Each change needs a clear scope, a validation path, and a rollback story.

## Expected Agent Loop

1. Read [`../AGENTS.md`](../AGENTS.md), then the smallest relevant docs.
2. Inspect current code before proposing implementation details.
3. Implement focused changes on a branch.
4. Run the narrowest meaningful validation first, then broader checks as risk increases.
5. Update docs when the change alters durable behavior.
6. Open or update a pull request with summary, validation, and remaining risks.

## Where things live

- Docs index: [`README.md`](README.md). Architecture: [`../ARCHITECTURE.md`](../ARCHITECTURE.md).
- Server: `apps/server` (.NET 10). Web app: `apps/web` (React/Vite). Windows tray: `apps/tray`.
- Page layout rules: [`design/content-language.md`](design/content-language.md).
- Planned and in-flight work: [GitHub issues](https://github.com/jampat000/Weir/issues). Completed historical plans: [`archive/`](archive/).
- Local setup and ports: [`local-development.md`](local-development.md), [`ports.md`](ports.md).

## Validation commands

| Area | Command |
|------|---------|
| Server | `dotnet build apps/server/Weir.slnx -warnaserror` and `dotnet test apps/server/Weir.slnx` |
| Web (from `apps/web`) | `npm run lint`, `npm run format`, `npm run build`, `npm run test` |
| API types (from `apps/web`) | `npm run api:types:check` |
| Dead code | `node scripts/check-dead-code.mjs` |
| Docs map | `node scripts/check-agent-docs.mjs` |
| Coverage register | `node scripts/check-coverage-register.mjs` and `node --test scripts/check-coverage-register.test.mjs` ([`testing/COVERAGE.md`](testing/COVERAGE.md)) |
| Contract suite | `dotnet test apps/server/tests/Weir.Contract.Tests --filter "Area=<area>"` ([README](../apps/server/tests/Weir.Contract.Tests/README.md)) |
| E2E | `WEIR_E2E=1 dotnet test apps/server/tests/Weir.E2E.Tests` (see [`local-development.md`](local-development.md)) |
| No Python | `node scripts/check-no-python.mjs` |
| No test waits for the wall clock | `node scripts/check-wall-clock-waits.mjs` |
| Nothing private in public files | `node scripts/check-public-privacy.mjs` |
| Windows and Docker smoke | [`smoke-checklists.md`](smoke-checklists.md) |

## The coverage register

[`testing/coverage-register.json`](testing/coverage-register.json) lists every route, job kind, hosted service, periodic task, setting, event type, web route, screen control, tray menu item and golden-VM scenario, read from the code, with the tests that prove each. CI fails on new code with no row, on a row for code that is gone, on a proof whose test no longer exists, and on a gap count above the register's on the branch the change goes into (a reasoned `gapIncrease` entry is the only way up), and on a gap that sits on a closed issue. Add or change the rows in the same pull request as the code, and when a test closes a gap put it in the row's `proofs` and drop its `issue`; no ceiling or totals are kept in the files, so parallel pull requests do not conflict. The rules are in [`testing/COVERAGE.md`](testing/COVERAGE.md).

## Feedback loops to prefer

- Server unit tests for service logic, schema behavior, file lifecycle, and worker decisions.
- Web unit tests for query states, the Processing, Activity and Library screens, settings flows, and user-facing text.
- E2E smoke tests for sign-in, navigation, and live refresh.
- Windows package smoke for installer, tray, startup, and upgrade behavior.
- Docker smoke for container startup and health.
- GitHub issues for backlog items that should survive beyond the current PR. Use `priority: low` for cleanup candidates that are real but not release blockers.
