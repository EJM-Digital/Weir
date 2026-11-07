# Weir vX.Y.Z

Date: YYYY-MM-DD

This release focuses on <plain-language summary in one sentence>.

## What Changed

- <User-facing change 1>
- <User-facing change 2>
- <User-facing change 3>

## Fixes And Stability

- <Reliability fix 1>
- <Reliability fix 2>
- <Reliability fix 3>

## Upgrade Notes

- Windows: **Check for updates** in the tray or **System › About**, or install `Weir-win-Setup.exe` over the top. Existing data is kept.
- Docker: `docker compose pull && docker compose up -d`.
- <Any additional one-time action or compatibility warning>

## Docker

- `ghcr.io/ejm-digital/weir:X.Y.Z`  <!-- no `v`: release.yml strips it for the image tag -->
- `ghcr.io/ejm-digital/weir:latest`  <!-- stable releases only: a release candidate lists its version tag alone and moves no other tag -->
- Images are published for `linux/amd64` and `linux/arm64`, and run the .NET build of the Weir server.

## Full Changelog

https://github.com/EJM-Digital/Weir/compare/vPREVIOUS...vX.Y.Z

---

Authoring guidance:

- Write for operators, not developers.
- Explain impact first, implementation second.
- Avoid internal module names unless the user already sees that term in the UI.
- Keep each bullet short and concrete.
