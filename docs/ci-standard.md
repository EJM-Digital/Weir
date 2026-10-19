# CI standard

The CI and release standard for Deluno and Weir. This page is the same, word for word, in both repositories; each repository adds only its own jobs between the shared ones. Decided by the owner, 3 Oct 2026 (Deluno [#1124](https://github.com/jampat000/Deluno/issues/1124)).

## Two copies, on purpose

Deluno and Weir each keep their own identical copy of this standard and of the scripts below. They never share a workflow, a reusable workflow or an action across the two repositories. The owner decided this on 6 Oct 2026, "in case something happens to one repo or we decide to go a different direction on it": either repository must keep working, and be free to change, with the other gone or different. So a change to the standard is made in both repositories, in the same words, and neither points at the other.

## What each repository has

| File | What it is |
|---|---|
| `.github/workflows/ci.yml` | CI. Runs on a push to `main`, on a pull request, and by hand. A manual run is also how any commit gets an installable build for the golden path. |
| `.github/workflows/release.yml` | The one release. Runs on a `v*` tag. The only workflow that publishes anything. |
| `.github/workflows/audit.yml` | The weekly dependency audit. Runs on a schedule and by hand. |
| `.github/dependabot.yml` | Dependabot, security updates only. |

Any other workflow follows the same rules below.

## The local gate matches CI

The local pre-push gate (`npm run gate`, `scripts/gate.ps1`) runs the browser smoke in CI's three shards (`--shard=1/3`, `2/3` and `3/3`, same Playwright config), each against its own fresh smoke host and database on its own ports, so state left by earlier specs is the same here as in a CI shard.

## The required check

**`ci-passed` is the one check branch protection requires.** Nothing else is listed as required, so a job can be added, renamed or skipped by a path filter without touching the repository settings.

`ci-passed` needs every other job in `ci.yml` and runs even when one of them failed (`if: ${{ always() }}`). It passes when each job that was due for the change passed and each job that was not due was skipped, and fails in every other case: a due job that failed, was cancelled or was skipped; a job that ran when it was not due; a missing job. `scripts/ci-passed.mjs` holds the rule, and `scripts/ci-passed.test.mjs` tests it.

## Jobs

The job ids and display names below are the same in both repositories.

| Job | Runs | Purpose |
|---|---|---|
| `changes` | always | Turns the paths a change touches into one `run_*` flag per job. A manual run turns every flag on. |
| `repo-checks` | always | Checks of the repository itself: actions pinned, workflow hygiene, the release gate shape, the `ci-passed` tests, and the repository's own text checks. Takes seconds. |
| `server-linux` | when `run_server_linux` | Builds the server with warnings as errors, runs its tests and scans its NuGet packages for vulnerabilities, on Linux. |
| `server-windows` | when `run_server_windows` | Builds the server with warnings as errors and runs its tests, on Windows. |
| `web-dist` | when `run_web` | Builds the web app once (design tokens, types, bundle budget) and hands the result to the jobs that serve it. |
| `web` | when `run_web` | Checks the web app's API types, lint and formatting, runs its unit tests, guards against dead code and audits its npm packages. |
| `e2e-smoke` | when `run_e2e` | Starts the built server and drives it in a browser (Playwright for .NET). |
| `contract` | when `run_contract` | Calls `ci-contract.yml`: one leg per contract area, each judging a running server over HTTP. |
| `tray` | when `run_tray` | Builds and tests the Windows tray. |
| `packaging` | when `run_packaging` | Calls `ci-packaging.yml`: installs the Windows package and starts the Docker image, scanned with Trivy. |
| `ci-passed` | always | The verdict above. Its step is named `Every job that was due passed`; the release looks for that name. |

### Path filters

- `changes` uses `dorny/paths-filter`, pinned, with `predicate-quantifier: some-with-excludes`.
- Documents only (`*.md`, `docs/**`) run nothing but `repo-checks`.
- A change to something that ships inside a build (for example a release notes file embedded in it) is not a document.
- Each job's flag is the narrowest set of paths that can change its result. A change to a workflow file turns on everything the workflow runs.
- Anything the filter does not recognise counts as code and runs the code jobs. A filter is wrong when it skips too little, never when it skips too much.
- A push to `main` is filtered against the previous commit. A green run on a commit that changed only documents therefore means "nothing that needed testing changed since the last commit that did". To prove a tree in full, run `ci.yml` by hand on it.

## Rules for every workflow

1. **Actions are pinned to a full commit SHA** with the release in a comment: `uses: actions/checkout@<40 hex characters> # v7.0.1`. Resolve a tag with `git ls-remote --tags https://github.com/<owner>/<repo>.git` and dereference annotated tags (the `^{}` line). `scripts/check-github-action-pins.mjs` fails otherwise. A local action or reusable workflow (`./...`) is exempt; the commit pins it.
2. **Permissions.** A top-level `permissions: contents: read` and nothing wider. A job asks for more on the job itself (`packages: write` for the registry, `contents: write` for a release, `id-token` and `attestations` for provenance, `pull-requests: read` for the path filter on a pull request, `statuses: read` for the release's golden-path check). `scripts/check-workflow-hygiene.mjs` fails on a missing block or a write at the top.
3. **Every job has `timeout-minutes`**, about twice the time it has been seen to take. A job with no timeout runs six hours; a Windows job bills at twice the rate. The same script checks it.
4. **Secrets reach scripts through `env:`**, never as `${{ secrets.X }}` inside script text, and the tag or any other value a person controls reaches a script the same way.
5. **Downloads are checked.** A tool fetched during a build is pinned by SHA-256 and verified before it is unpacked or run. A cache for it is keyed by the file that holds the pin.
6. **Concurrency.** CI cancels a superseded run of the same branch or pull request. The release never cancels (`cancel-in-progress: false`).
7. **Test results are reported.** A job that runs tests writes a summary to the job page (`scripts/summarize-test-results.mjs`) and uploads the raw results as an artifact.
8. **Build once, share the result.** A build several jobs need is made in one job and handed on as an artifact (retention of one day).
9. **Containers are scanned.** Every image built in CI and in the release is scanned with Trivy (`aquasecurity/trivy-action`, pinned). The scan fails on a `HIGH` or `CRITICAL` vulnerability that has a fix available (`severity: HIGH,CRITICAL`, `ignore-unfixed: true`, `exit-code: "1"`).
10. **Dependabot watches the actions.** The `github-actions` ecosystem in `dependabot.yml` covers `.github/workflows`, so a security advisory on a pinned action opens a pull request.
11. **Browser tests retry once on CI; backend tests never retry.** Playwright runs with `retries: process.env.CI ? 1 : 0`, so one failed attempt is run again, and the job summary lists every test that passed only on the retry under "Flaky (passed on retry)". Nothing is hidden, and a test on that list is fixed. The backend xUnit tests are not retried at all: a failure is a failure.
12. **A skip is never a pass.** A test that needs a real external tool skips itself where the tool is missing. CI installs the pinned tool, sets a require variable that turns the skip into a failure, and fails the leg unless the expected number of those tests ran; a skip is never a pass.

## The release

1. **A tag is made only after CI is green on that exact commit.** The first job in `release.yml`, `ci-passed`, runs `scripts/verify-ci-for-release.mjs`: it asks GitHub (`gh run list --commit`) whether `ci.yml` has a completed `push` or manual run for the tagged commit whose `ci-passed` job passed. Deluno adds one condition: the Windows installer and both end-to-end job sets run only on a manual run (an ordinary push never builds them), so the run must have passed those jobs, which means a tag needs a green manual run (`gh workflow run ci.yml`) on the commit. It does not wait. A tag made while CI is still running fails at once, and the fix is to wait for CI and re-run the release.

   **A tag the golden path gates is made only after it has passed on that exact commit** (`docs/golden-path.md` says which: every `v*` tag of both apps, Deluno and Weir, release candidates included, each waiting for its own app's scenario suite on that exact commit). The release's `golden-path` job runs `scripts/verify-golden-path-for-release.mjs`: it asks GitHub for the commit's statuses and passes only when the newest status with the context `golden-path` is `success`, on the tagged commit or, when no file that ships changed since, on the nearest earlier commit that has one (and was run for the version being tagged); it says which commit's pass it used and why. It does not wait. The status is set by the scenario suite itself (`scripts/golden-path/Invoke-ScenarioSuite.ps1`, `docs/golden-path.md`); by hand only for a run that was not the suite's: `gh api repos/<owner>/<repo>/statuses/<sha> -f state=success -f context=golden-path -f description=… -f target_url=…`.
2. **Each thing is built and checked before it is published.** The Windows build, the container build and the vulnerability scan run side by side, without credentials, and publish nothing. A publish job starts only when the build it publishes has passed.
3. **Two jobs publish, one per thing published, and neither waits for the other.** `publish-windows` creates the GitHub Release with the Windows installer and its update files; `publish-docker` pushes the container image. Each `needs` the shared checks (`ci-passed`, `golden-path`, `validate`) and the checks of the build it publishes, and nothing else, so a slow or failed build of one never holds back the other, and either can be re-run alone (`gh run rerun --job`). Only `publish-windows` has `contents: write`. Only `publish-docker` has `packages: write` and logs in to the registry. `publish-docker` pushes the image (with provenance and an SBOM), checks the pushed manifest and starts the pushed image, and **only then moves the moving tags** (`major.minor` and `latest`), as the last step of the job, so a moving tag never names an image that failed its check. A release candidate has no moving tags.
4. **`scripts/check-release-workflow-gates.mjs` checks this shape in `repo-checks`:** only `publish-windows` and `publish-docker` can publish, each needs the shared checks and its own build's checks and neither needs the other, the steps of `publish-docker` are in that order with the moving tags last and never moved for a release candidate, `ci-passed` still judges every CI job, and `golden-path` is a job both publish jobs need.
5. **A second push of a tag never cancels a release in progress.**
6. **After a tag, confirm the release is real:** the Release run is green, the GitHub Release has its assets, and the image can be pulled by its version tag.

### Provenance

Image provenance and an SBOM are attached by the build (`provenance: mode=max`, `sbom: true`). Signed build-provenance attestations (`actions/attest-build-provenance`) for the image (in `publish-docker`) and the release files (in `publish-windows`) are switched by the repository variable `ATTEST_PROVENANCE`. They are free on a public repository and need GitHub Enterprise Cloud on a private one, so a private repository leaves the variable unset and sets it to `true` when it goes public.

## Dependabot

Security updates only. Every ecosystem in `dependabot.yml` has `open-pull-requests-limit: 0`, which turns routine version pull requests off. Dependabot security updates must also be switched on in the repository settings (Settings > Code security); that setting, not this file, is what makes Dependabot open a pull request for an advisory. Routine bumps are batched by hand.

## The weekly audit

`audit.yml` runs every Monday and by hand. It restores the .NET solution and fails when `dotnet list package --vulnerable --include-transitive` reports a package, and it runs `npm audit --omit=dev --audit-level=high`. It needs read access only and has a timeout.

## Scripts both repositories share

| Script | What it does |
|---|---|
| `scripts/ci-passed.mjs`, `scripts/ci-passed.test.mjs` | The verdict and its tests. The job-to-flag table (`GATES`) is the repository's own. |
| `scripts/check-github-action-pins.mjs` | Fails on an action that is not pinned to a full SHA with a release comment. |
| `scripts/check-workflow-hygiene.mjs` | Fails on a missing top-level `permissions` or a job with no `timeout-minutes`. |
| `scripts/verify-ci-for-release.mjs` | The release's check that CI passed on the tagged commit. |
| `scripts/verify-golden-path-for-release.mjs` | The release's check that the golden path passed on the tagged commit, or on an earlier one with the same shipped files (`golden-path` commit status). |
| `scripts/check-release-workflow-gates.mjs` | Fails when the release workflow's publish shape is broken. |
| `scripts/summarize-test-results.mjs` | Writes TRX and Playwright results to the job summary. |

## Repository settings that go with this

These are settings, not files, and are made by the repository owner:

- Branch protection or a ruleset on `main` that requires the `ci-passed` check, and blocks force pushes and deletion.
- Dependabot security updates on.
- Secret scanning and push protection on.
- A tag ruleset so that only the owner can create a `v*` tag.

The backend tests run in seven parallel jobs (issue 1346): the six smaller test projects together, and `Deluno.Persistence.Tests` in six parts split by namespace, the large `Api` namespace by the first letter of the test class's name into four of them (`scripts/persistence-parts.mjs`; one job for all of Api ran past its 40 minute limit on 18 Oct). `scripts/check-persistence-parts.mjs` proves, from `dotnet test --list-tests`, that every Persistence test runs in exactly one part; CI runs it on the first leg and the gate runs it before the tests. Coverage from every leg is merged before the ratchet, and a failed leg fails the `backend` job that `ci-passed` judges.
