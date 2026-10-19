# Release databases and backups

Each `<version>.db.gz` is the database that the published image of that release (`ghcr.io/ejm-digital/weir:<version>`) wrote after a short scripted session through its own API: an alert, a rule set, direct play devices, a workflow fed by a fake media manager (one file finished and imported, one refused) and a workflow with a scanned library. `manifest.json` names the image and its digest, the schema revision, the file's SHA-256 and the row count of every table. The configuration backup that release made is beside the other recorded replies, in `../recorded/backups/<version>/`.

Nothing here is written by hand and no schema is invented: the "Release fixtures" workflow (`.github/workflows/release-fixtures.yml`, run by hand with the versions to add) pulls each image, runs `scripts/make-release-fixtures.mjs` and uploads the two folders as an artifact. The machine's name and the account's folder are scrubbed out of every text column.

The tests that use them:

- `Weir.Infrastructure.Tests` `ReleaseDatabaseUpgradeTests` opens each database with the current build and checks the upgrade and the data.
- `Weir.Contract.Tests` `ReleaseUpgradeTests` starts the current server on each database, and `ReleaseBackupRestoreTests` restores each backup on a fresh install and compares every section.

`scripts/check-release-fixtures.mjs` fails CI while the newest fixture is more than one release behind.
