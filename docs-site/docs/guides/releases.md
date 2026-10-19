---
sidebar_position: 4
title: Versions and updates
---

# Versions and updates

## Version numbers

Weir versions have three numbers, major.minor.patch, for example `1.0.0`. A fix raises the last
number (`1.0.1`), a new feature the middle one (`1.1.0`) and a change that breaks something the
first (`2.0.0`). Before a version is final it is released as a pre-release with `-rc.N` on the end,
for example `1.0.0-rc.1`, then `1.0.0-rc.2`, and `1.0.0` comes after every rc. Each release is
tagged `vX.Y.Z` or `vX.Y.Z-rc.N` on GitHub. The Windows installer and the Docker image of a release
always carry the same version.

To see which version you're running, open **System › About**. The **Updates** section shows the
installed version, the latest release and a **Release notes** link.

## Release notes

Every release has notes describing what changed for users:

- on the [GitHub Releases page](https://github.com/EJM-Digital/Weir/releases)
- in the repository, under [`docs/release-notes`](https://github.com/EJM-Digital/Weir/tree/main/docs/release-notes)

## Updating on Windows

The tray app installs updates itself. To check now, right-click the tray icon and choose **Check
for updates**, or open **System › About** in Weir. How updates are handled is set by **Update
mode** in **System › About**:

| Mode | What happens |
| --- | --- |
| **Auto** (default) | Downloads updates on its own and installs them the next time Weir restarts |
| **Download only** | Downloads updates in the background, then tells you one is ready; you choose when to restart |
| **Notify only** | Tells you an update is available and downloads nothing |

Updates don't need admin rights, and Weir restarts afterwards without opening a browser window.
See [Windows installer](../deployment/windows#updates) for more.

## Updating on Docker

With `image: ghcr.io/ejm-digital/weir:latest` (stable releases only: release candidates are published under their version tag, so until 1.0.0 you pin one, as below), pull the new image and recreate the container:

```bash
docker compose pull
docker compose up -d
```

Your data lives in the `/data/weir` volume, so it carries over. Keep the same volume and the same
`WEIR_SESSION_SECRET` so you stay signed in.

### Pinning a version

Each release is also published under its version number. To stay on one release until you choose
to move, pin it:

```yaml
image: ghcr.io/ejm-digital/weir:X.Y.Z
```

Replace `X.Y.Z` with a version from [the releases page](https://github.com/EJM-Digital/Weir/releases).

To update, change the number to the new version and run the two commands above. Images are
published for linux/amd64 and linux/arm64.

## For maintainers

A release doesn't re-run the test suite. Its `ci-passed` job checks that CI already passed on the
exact commit being tagged, and a second job checks that the golden path (a run of the real product on a
clean machine) was recorded as passed on it. Nothing is published until those jobs, the Windows package smoke test
and the Docker image checks have all passed. The full process is in
[`docs/release.md`](https://github.com/EJM-Digital/Weir/blob/main/docs/release.md).
