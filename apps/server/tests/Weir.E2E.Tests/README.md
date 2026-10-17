# Weir browser end-to-end tests

Playwright for .NET (Chromium) driving a **real** Weir server that serves the built web app, as the Docker and
Windows packages do. It is the browser counterpart of the contract suite and starts the server through that
suite's harness (`Weir.Contract.Tests`), so neither project loads a Weir assembly: the host is only built before
them and started as a process, with a fresh data folder, on a free port.

The tests are opt-in. Without `WEIR_E2E=1` every test is skipped, and they all carry `Category=E2E`.

## Running it

```bash
# 1. The web app, which the server serves (once, and after a web change)
cd apps/web && npm ci && npm run build

# 2. The server and these tests
cd ../server && dotnet build Weir.slnx

# 3. The Chromium that Microsoft.Playwright 1.63.0 drives (once per machine, and after a Playwright upgrade)
pwsh tests/Weir.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium

# 4. The suite
WEIR_E2E=1 dotnet test tests/Weir.E2E.Tests            # PowerShell: $env:WEIR_E2E = "1"
```

| What | How |
| --- | --- |
| Leave these tests out of a wider run | `--filter "Category!=E2E"` (they are skipped without `WEIR_E2E=1` anyway) |
| Browser install on Linux CI | `pwsh .../playwright.ps1 install --with-deps chromium` |
| Browser install without PowerShell | `dotnet tool install --global Microsoft.Playwright.CLI`, then `playwright install --with-deps chromium` |
| A published server instead of the built one | `WEIR_CONTRACT_SERVER=/path/to/Weir.dll` (or an executable) |
| Keep the server's data folder and log after the run | `WEIR_CONTRACT_KEEP_DATA=1` (log in `<folder>/contract-logs`) |
| A fixed session secret | `WEIR_SESSION_SECRET` (the harness default is used otherwise) |

A missing browser fails the first test with Playwright's own message naming the install command.

Screenshots of the high-risk pages are written to `artifacts/screenshots/` at the repository root (ignored by git,
overwritten each run). They are for looking at, not compared.

## When a test fails

A test that fails keeps its evidence in `artifacts/e2e-failures/<run>/<Class.Method>/` (ignored by git; a passing test
deletes its own, and each run has a folder of its own, so a failure that does not repeat is still there after later runs):

| File | What it shows |
| --- | --- |
| `error.txt` | The full error: message, Playwright call log and stack trace. |
| `context-N-trace.zip` | Every step the browser took, with screenshots and the page at each step: `pwsh bin/Debug/net10.0/playwright.ps1 show-trace <file>`. |
| `context-N-page-M.png` | The page as it was when the test ended. |
| `browser.log` | The console, page errors, failed requests, responses of 400 and over and every navigation, with times. |
| `server.log` | The last 2,000 lines the server wrote. |

CI uploads the folder as `e2e-failure-evidence`. A failed test fails the job and nothing runs it again: an intermittent test is a bug to
root-cause from the trace, never one to run twice.

## How it works

- **`Harness/E2EServer`** is one server and one Playwright driver for the whole run (`ICollectionFixture`). It starts the
  built host with `WEIR_WEB_DIST=apps/web/dist` through `WeirServer.StartNewAsync`. The harness gives it high sign-in
  limits (every test signs up and signs in against the one server) and switches artwork lookups off; the settings
  that make the server quiet (workers, file watcher, periodic scan, work file sweeps) are put back to their
  defaults, because the Logs tests expect the job rows a running server writes. Without `WEIR_E2E=1` it starts nothing.
- **`Support/E2ETestBase`** resets per-test state before each test (users, sessions, suite settings and the library
  folders are cleared, and the cleanup and resolution-budget settings go back to their defaults) and gives the test a headless Chromium of its own, traced and logged. All test
  classes share one collection, so tests run one at a time against the one server. **`Harness/FailureArtifacts`** and
  **`Harness/FailureKeepingFramework`** keep what a failed test wrote and delete the rest.
- **`Harness/E2EDatabase`** writes plain SQL to the running server's SQLite file for what the browser cannot create, an
  activity event the open Logs tab must pick up live, and an account's role (Weir has one account, so no screen makes a viewer).
- **`Support/ProcessingRig`** is a second Weir for a test that watches a file move through the screens: one worker over a fake
  ffmpeg, so a pass stays under way until the test releases it, and a fake Deluno. Files arrive and finish through the hand-off
  webhook and the outcome route, as a media manager makes them, never by writing to the database. It can also drop a file nobody handed
  over into the watched folder, as a download client does, link the Movies workflow to the manager, and say what the manager was told.
- **`Support/LibraryRig`** is a third Weir for a test that works the Library page: one movie workflow whose library folder holds films the fake ffprobe reads
  tracks from, the fake ffmpeg to clean them, and optionally a Radarr that lists the films and deletes one when asked. A scan, a clean and a rules change all go through the API.
- **`Support/RequestLog`** records every request that can change something which a page sends to Weir (anything but a read). A test about a dialog takes a mark when the
  dialog is open and asks what the page sent since: nothing for a dialog that was cancelled, exactly the request the dialog is for for one that was confirmed, with the body it carried and the CSRF token it sent in a header. It sits beside the
  state read back through the API, so a request the page hid and a change the page never sent are both caught.
- **`Support/Navigation`** is sign-in, the side menu, the tabs of an area, Logs and the Activity chips. **`Support/PageChecks`**
  holds the structural checks and screenshots of the visual smoke tests.
