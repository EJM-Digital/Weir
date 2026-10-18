#!/usr/bin/env node
// Holds the release fixtures fresh and whole.
//
// apps/server/tests/fixtures/databases/ holds the database each published build wrote, and apps/server/tests/fixtures/recorded/backups/ the
// configuration backup it made (scripts/make-release-fixtures.mjs). The tests upgrade every database and restore every backup with the
// current build, so a person upgrading from the release before this one is always covered. This fails when
//   - the newest fixture is more than one published release behind: the published releases are the CHANGELOG lines that link a release,
//     and the fixtures must reach at least the second newest of them, so each release is followed by the fixtures of the one before it;
//   - the manifest lists a version with no database file or no backup, or a file or backup that the manifest does not list.
//
// Usage: node scripts/check-release-fixtures.mjs
import { existsSync, readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { compareSemver, parseSemver } from "./semver.mjs";

const repositoryRoot = fileURLToPath(new URL("..", import.meta.url));

/** The versions of the CHANGELOG lines that link a release, newest first. A line for a release that was never published has no such link. */
export function publishedVersions(changelog) {
  const versions = [];
  for (const line of changelog.split(/\r?\n/)) {
    const match = /^- \*\*(\S+)\*\*/.exec(line);
    if (match && line.includes("[release](") && parseSemver(match[1])) versions.push(match[1]);
  }
  return versions.sort((left, right) => compareSemver(parseSemver(right), parseSemver(left)));
}

/** What is wrong with the fixtures, one sentence each; nothing when they are fresh and whole. */
export function problems({ published, fixtureVersions, databaseFiles, backupFolders }) {
  const found = [];
  const newest = [...fixtureVersions].sort((left, right) => compareSemver(parseSemver(right), parseSemver(left)))[0];
  const needed = published[1] ?? published[0];
  if (needed && (!newest || compareSemver(parseSemver(newest), parseSemver(needed)) < 0)) {
    found.push(
      `The newest release fixture is ${newest ?? "missing"}, but ${needed} is published and the newest is ${published[0]}: make the fixtures of ${needed} with scripts/make-release-fixtures.mjs.`,
    );
  }
  for (const version of fixtureVersions) {
    if (!databaseFiles.includes(version)) found.push(`The manifest lists ${version} but databases/${version}.db.gz is missing.`);
    if (!backupFolders.includes(version)) found.push(`The manifest lists ${version} but recorded/backups/${version}/configuration-bundle.json is missing.`);
  }
  for (const version of databaseFiles) if (!fixtureVersions.includes(version)) found.push(`databases/${version}.db.gz is not in the manifest.`);
  for (const version of backupFolders) if (!fixtureVersions.includes(version)) found.push(`recorded/backups/${version} is not in the manifest.`);
  return found;
}

function main() {
  const fixtures = path.join(repositoryRoot, "apps", "server", "tests", "fixtures");
  const databases = path.join(fixtures, "databases");
  const backups = path.join(fixtures, "recorded", "backups");
  const manifest = JSON.parse(readFileSync(path.join(databases, "manifest.json"), "utf8"));
  const result = problems({
    published: publishedVersions(readFileSync(path.join(repositoryRoot, "CHANGELOG.md"), "utf8")),
    fixtureVersions: manifest.databases.map((entry) => entry.version),
    databaseFiles: readdirSync(databases).filter((name) => name.endsWith(".db.gz")).map((name) => name.slice(0, -".db.gz".length)),
    backupFolders: existsSync(backups)
      ? readdirSync(backups, { withFileTypes: true }).filter((entry) => entry.isDirectory() && existsSync(path.join(backups, entry.name, "configuration-bundle.json"))).map((entry) => entry.name)
      : [],
  });
  if (result.length > 0) {
    console.error(result.join("\n"));
    process.exit(1);
  }
  console.log(`Release fixtures are fresh: ${manifest.databases.length} releases, the newest ${manifest.databases.map((entry) => entry.version).sort((a, b) => compareSemver(parseSemver(b), parseSemver(a)))[0]}.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main();
}
