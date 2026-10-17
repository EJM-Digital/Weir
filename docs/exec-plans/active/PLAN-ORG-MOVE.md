# Plan: move Weir to the EJM-Digital organisation

Status: prepared, not yet moved. Today every value stays `jampat000/Weir`.

Weir moves from the repository `jampat000/Weir` to the GitHub organisation `EJM-Digital`, so the repository becomes `EJM-Digital/Weir` and the container image `ghcr.io/ejm-digital/weir`. This plan is written so the move itself is one small commit, plus the settings and the proof below.

## What is already in place

The owner and the name are written once per language, and nothing else may write them:

| Language | The one place |
| --- | --- |
| Server | `apps/server/src/Weir.Core/Updates/ProjectLocation.cs` |
| Tray | `apps/tray/Weir.Tray/ProjectLocation.cs` |
| Web app | `apps/web/src/lib/repository.ts` |
| Node scripts | `scripts/repository.mjs` |
| PowerShell scripts | `scripts/repository.ps1` (`scripts/runner/Install-WeirRunner.ps1` keeps its own copy, because it is copied alone to a PC) |
| Workflows | `github.repository`, which GitHub fills in; no workflow names the owner |

`node scripts/check-repository-owner.mjs` (run by `repo-checks`) fails when a place differs from `scripts/repository.mjs`, when a source file, test, script or workflow writes the owner, or, once the move is done, when text still names the former owner. CI also passes it `github.repository`, so after a transfer nobody swept, the next build fails and says so. `scripts/sweep-repository-owner.mjs` does the rewrite (below).

Excused, with the reason in the check: the changelog, release notes, archived plans and decisions (history), this plan, `CODEOWNERS` and `FUNDING.yml` (they name the person), the copyright holder, sponsorship links, and links to Weir's own issues and pull requests (they keep working).

## Inventory

### Runtime references: an installed copy uses these

There are 9 uses in 3 places. A copy installed before the move keeps the values it was built with, so these decide whether it still finds updates.

| Where | What it does | After the transfer |
| --- | --- | --- |
| Server, `Updates.cs`: `ReleasesUrl` | Asks the GitHub API for the release list (the update check) | GitHub redirects the old API address |
| Server: `ReleasesFeedUrl` | The releases feed address | Redirected |
| Server: asset download URL | The link to a release file | Redirected |
| Server: `TagUrl` | The link to a release's page | Redirected |
| Server: `docker_image` (two places) | The image name shown to Docker users in the update notice | Not redirected; the old name works only while the dual publish runs |
| Tray, `UpdateService.cs` | The Velopack update source: the releases feed, then the API if the feed cannot be read, then `releases/download/<tag>/` | Redirected |
| Web app, `repository.ts` (two links) | The About page's source and licence links | Redirected |

### Build, packaging, tests and docs: no installed copy reads these

- Build and release: `release.yml` and the other workflows read `github.repository`, so the image name, the provenance subject and the Velopack previous-release address follow the move. `scripts/pinned-download.mjs` and `packaging/windows/pinned-download.ps1` read the release address from the one place.
- Scripts: the Docker smoke, the previous-release image lookup, the scenario runner and the runner installer.
- Tests: read the one place, so they pass on either name.
- Text that cannot read a constant, which the sweep rewrites: the README, the docs, the docs site (including its `url`, `organizationName` and Pages address), `compose.yaml`, `docker/.env.example`, `apps/web/.env.example` and the issue template links.

## What GitHub redirects after a transfer, and what it does not

Redirected, for as long as nobody creates a repository named `jampat000/Weir` again:

- The repository's web address, `git` over HTTPS and SSH, and the issue and pull request links.
- The API address `api.github.com/repos/jampat000/Weir/...`, which answers with a redirect that a client must follow. The server and the tray both do.
- Release pages, the releases feed, and release file downloads, which end up at the same files.
- Actions that other workflows reference by the old name.

Not redirected:

- **Container images.** A `docker pull ghcr.io/jampat000/weir:<tag>` goes to the old package namespace; ghcr does not forward it to `ghcr.io/ejm-digital/weir`. Nothing is published under the old name after the move unless we publish it (below).
- The Pages site: `jampat000.github.io/Weir` is not a dependable redirect. The docs site moves to `ejm-digital.github.io/Weir`; links to the old address may break.
- Repository settings, secrets, variables, rulesets, runners and the Pages configuration need checking after the transfer.

Rule: never create a new repository called `jampat000/Weir`. It would end every redirect above.

## The sweep (one line)

After the transfer, on a branch, run the sweep, then the check, then open one PR:

```
node scripts/sweep-repository-owner.mjs --to EJM-Digital/Weir && node scripts/check-repository-owner.mjs
```

The sweep rewrites the five places and `scripts/repository.mjs`, adds `jampat000` to the check's `FORMER_OWNERS`, and replaces the old address, image name and Pages address in text that cannot read a constant. It leaves history, this plan, `CODEOWNERS`, `FUNDING.yml`, the copyright holder and sponsorship links. The check then names anything left. The sweep's own tests are in `scripts/sweep-repository-owner.test.mjs`.

Also by hand in that PR: move this plan to `docs/archive/` and mark it done.

## Dual image publish

Because ghcr does not redirect, a Docker install that pulls `ghcr.io/jampat000/weir` finds nothing newer unless we keep publishing there.

- `release.yml` derives the image name from `github.repository`. After the move it publishes only `ghcr.io/ejm-digital/weir`. The move PR adds a second name for the same build: the same multi-architecture manifest, pushed under both names in one step, with the provenance attestation and the manifest check run for each name. The old name is `ghcr.io/jampat000/weir`.
- The old ghcr package stays public, and the release job's token must still be able to push to it after the transfer (check this on the first release candidate).
- How long: until the first stable release after the move has been out for 90 days, or until the owner decides sooner. After that the old name is no longer updated, and the old package stays as it is. This is a decision for the owner (below).
- Release notes for every release published under both names, in plain words:

  > Weir's container image has a new home: `ghcr.io/ejm-digital/weir`. The old name, `ghcr.io/jampat000/weir`, keeps getting the same releases until DATE. If you run Weir with Docker, change `image:` in your compose file (or your `docker run` command) to the new name before then, then run `docker compose pull` and `docker compose up -d`. Your settings and data are not touched. Nothing changes for the Windows installer.

## Organisation settings

Set these on `EJM-Digital` before the first release from it:

1. **GitHub-hosted runners only.** Disable self-hosted runners for the organisation (Settings, Actions, Runners and runner groups), so a release is always built on a clean GitHub-hosted machine, as `docs/self-hosted-runner.md` already requires of release jobs. The two Windows CI jobs choose the spare PC with the variable `WEIR_SELF_HOSTED_WINDOWS`; leave it unset after the move, which sends them to `windows-latest`. Remove the PC's runners first (`docs/self-hosted-runner.md`).
2. **Approval for outside pull requests.** Actions, General, "Require approval for all outside collaborators", so no outside contributor's workflow runs without the owner approving it.
3. **Immutable releases.** Turn on immutable releases for the organisation, or for the repository, so a published tag and its files cannot be changed or replaced. The release flow already fits it: `scripts/publish-release.mjs` attaches every file to a draft and publishes once (`docs/release.md`). The setting does not carry over by itself, so check it is on for the new owner before the first release.

Also check after the transfer: the `main` ruleset and the required `ci-passed` check, `CODEOWNERS`, repository variables such as `ATTEST_PROVENANCE`, secrets, Pages, and the security settings.

## Proof after the move

An installed copy from before the move, with no reinstall, finds the next release from the new home. Do both with real releases, not a stand-in.

1. **Windows.** On a clean machine, install the last release made before the move. Publish the first release from `EJM-Digital/Weir`. From the tray, check for updates: the new version is offered, downloads and installs, and the server reports the new version. Record the version before and after, the feed address in the log (it shows the redirect from the old address), and the result.
2. **Docker.** Start a container from the last image under the old name. Publish the first release (it goes to both names). The update notice shows the new version; pull it under the old name and start it: the version matches. Then pull it under the new name and start it: the same digest.
3. **Update check.** On both installs, System, About shows the new version as available before the update, and the source link opens the new repository.
4. Record the evidence in the PR that closes the move, as `docs/release-governance.md` requires for any change to update behaviour.

## Decisions for the owner

- How long the old image name keeps getting releases (proposed: 90 days after the first stable release from the organisation).
- Whether the docs site moves to `ejm-digital.github.io/Weir` or gets a custom domain first.
- Whether the spare PC's runners are removed before the move (the setting above requires it), or the Windows jobs go to `windows-latest` from the day of the move.
