# Self-hosted Windows runner

Weir's two Windows test jobs can run on a spare Windows PC instead of GitHub's own Windows runners:

- `server-windows` in `ci.yml`;
- `tray` in `ci.yml`.

`windows-package-smoke` always stays on GitHub's runners: it installs Weir for real, which needs a clean machine every time, and a PC someone uses may already have Weir and its data on it.

They are the heaviest jobs, billed at twice the Linux rate on GitHub's runners. On a dedicated PC they also stop competing with local work for the same machine. The Linux jobs (contract, E2E, the Linux server legs, Docker) and every release job stay on GitHub's runners. A release is always built on a clean, GitHub-hosted machine.

## How a job chooses the runner

Each of the two jobs says:

```yaml
runs-on: ${{ vars.WEIR_SELF_HOSTED_WINDOWS == 'true' && !github.event.pull_request.head.repo.fork && fromJSON('["self-hosted","Windows","weir-heavy"]') || 'windows-latest' }}
```

- With the repository variable `WEIR_SELF_HOSTED_WINDOWS` set to `true`, a job runs on a runner labelled `weir-heavy`. Unset, or anything else, and every job uses `windows-latest`, so taking the PC away is one variable.
- A pull request from a fork never runs there. Its code is a stranger's, and this repository is public. The repository also requires approval before any outside contributor's workflow runs.

## Setting the PC up

On the spare PC, in an elevated PowerShell window:

1. Copy `scripts/runner/Install-WeirRunner.ps1` to the PC.
2. On GitHub, open Settings > Actions > Runners > New self-hosted runner, choose Windows, and copy the token from the "Configure" step. It is valid for one hour.
3. Run `.\Install-WeirRunner.ps1` and paste the token when asked. It doesn't echo the token and keeps it nowhere.

The script installs Git and PowerShell 7 if they are missing. It downloads the pinned runner and checks its SHA-256, and turns on long paths. It then registers the runner as `weir-windows-1` with the label `weir-heavy`, and installs it as a service running as NETWORK SERVICE, which can't read anyone's own files. Each job installs what it needs itself (the .NET SDK, Node, the pinned FFmpeg and MKVToolNix), as it does on GitHub's runners.

Then set the repository variable `WEIR_SELF_HOSTED_WINDOWS` to `true` (Settings > Secrets and variables > Actions > Variables).

## Keeping it healthy

- The PC must stay on. A queued job waits for it, so when it goes away for good, unset the variable.
- The runner updates itself. A new version of the pinned runner in the script is only for new installs.
- Each job checks out into `C:\actions-runner-weir\_work`. The checkout step cleans it, and its size can be watched there.

## Removing the runner

In an elevated window on the PC:

```powershell
cd C:\actions-runner-weir
.\config.cmd remove --token <token from Settings > Actions > Runners > the runner > Remove>
```

Then unset `WEIR_SELF_HOSTED_WINDOWS`, and delete `C:\actions-runner-weir`.
