# Coverage report fixtures

Real output of the tools CI runs, kept so `scripts/check-coverage-floors.test.mjs` reads the shapes those tools
write and not shapes assumed from their documentation.

- `server/` and `tray/`: the `Cobertura.xml` and `Summary.json` that ReportGenerator 5.5.11 wrote when it merged the
  Cobertura files coverlet.collector 10.0.1 wrote for a full `dotnet test` run of the server and of the tray
  (`apps/server/coverage.runsettings`, `apps/tray/coverage.runsettings`). They are cut down to a few classes
  with ReportGenerator's own `-classfilters`, so the totals are those of the classes that are left.
- `web/coverage-summary.json`: the `json-summary` file vitest 5.0.1 with `@vitest/coverage-v8` wrote for the web
  app, cut down to its `total` entry and five files. `total` is the whole app's, not the sum of the five.

The checkout folder in every file name is rewritten to the one GitHub's runners use, so no fixture names a
workstation folder. Nothing else is changed.
