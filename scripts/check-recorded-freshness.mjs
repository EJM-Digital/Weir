#!/usr/bin/env node
// Fails when the newest recording of Weir talking to Deluno is of a build more than one release behind the repository (#979).
//
// The contract suite replays what Deluno recorded of its talk with Weir (apps/server/tests/fixtures/recorded/deluno-side/) and Weir's own
// replies to Deluno (recorded/deluno/). A recording says nothing about a Weir built after it: a change to the wire since is held only by
// an ExpectedDifference pin. So each set of recordings names the Weir build it was made against in recorded/weir-builds-recorded.json,
// and this check compares the newest of those builds with the repository's version. The repository's version is the newest line of
// CHANGELOG.md, or --version when a tag is being cut (the release gate passes the tag).
//
// "Behind" counts releases in order (SemVer precedence, release candidates and stable versions alike): a recording of rc.17 is one
// behind rc.18 and passes, and one of rc.16 is two behind and fails. A stable release needs a recording of the last candidate or of the
// stable itself. A recording of a build newer than the repository (made before its changelog line was written) is not behind.
//
// Every folder under deluno/ and deluno-side/ must be listed, with a well-formed Weir version, so a new set cannot skip the check.
//
// Usage: node scripts/check-recorded-freshness.mjs [--version <tag or version>]
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { rootOf } from "./repository-root.mjs";
import { compareSemver, parseSemver } from "./semver.mjs";

const repositoryRoot = rootOf(import.meta.url);
const recordedRoot = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded");
const buildsFile = path.join(recordedRoot, "weir-builds-recorded.json");
const changelogFile = path.join(repositoryRoot, "CHANGELOG.md");
const recordedFolders = ["deluno", "deluno-side"];

/** How many releases a recording may be behind the repository. */
export const MAX_RELEASES_BEHIND = 1;

/** The versions of CHANGELOG.md's release lines (`- **1.0.0-rc.18** (date). ...`), in the order written. */
export function changelogVersions(changelog) {
  const versions = [];
  for (const line of changelog.split(/\r?\n/)) {
    const match = /^- \*\*([^*]+)\*\*/.exec(line);
    const parsed = match ? parseSemver(match[1]) : null;
    if (parsed) versions.push(parsed);
  }
  return versions;
}

/** The recording folders found on disk, as `deluno/<version>` and `deluno-side/<version>`. */
export function foldersOnDisk(root) {
  const found = [];
  for (const group of recordedFolders) {
    const groupPath = path.join(root, group);
    if (!existsSync(groupPath)) continue;
    for (const version of readdirSync(groupPath).sort()) {
      if (statSync(path.join(groupPath, version)).isDirectory()) found.push(`${group}/${version}`);
    }
  }
  return found;
}

/**
 * How many releases the repository is ahead of `recorded`: the changelog's releases after it, up to the repository's, and, for two
 * candidates of one version, the difference of their numbers (so a candidate the changelog never listed still counts).
 */
export function releasesBehind(recorded, repository, released) {
  const listed = new Set(
    released.filter((version) => compareSemver(version, recorded) > 0 && compareSemver(version, repository) <= 0).map((version) => version.version),
  ).size;
  const candidate = (version) => (version.prerelease.length === 2 && version.prerelease[0] === "rc" && /^[0-9]+$/.test(version.prerelease[1]) ? Number(version.prerelease[1]) : null);
  const sameVersion = ["major", "minor", "patch"].every((part) => recorded[part] === repository[part]);
  const [from, to] = [candidate(recorded), candidate(repository)];
  const numbered = sameVersion && from !== null && to !== null ? Math.max(0, to - from) : 0;
  return Math.max(listed, numbered);
}

/**
 * What is wrong, one sentence each; empty when the newest recording is recent enough.
 * `sets` is the manifest's `sets`, `folders` the recording folders on disk, `released` the repository's release versions (parsed),
 * `repository` the parsed version the recordings are held against.
 */
export function problems(sets, folders, released, repository) {
  const found = [];
  const parsed = [];

  for (const folder of folders) {
    if (!sets?.[folder]) found.push(`${folder} holds recordings but weir-builds-recorded.json does not say which Weir build they were made against. Add its "weirVersion".`);
  }
  for (const [folder, entry] of Object.entries(sets ?? {})) {
    if (!folders.includes(folder)) {
      found.push(`weir-builds-recorded.json lists ${folder}, which holds no recordings.`);
      continue;
    }
    if (typeof entry?.weirVersion !== "string" || entry.weirVersion.trim() === "") {
      found.push(`${folder}: its entry in weir-builds-recorded.json has no "weirVersion".`);
      continue;
    }
    const version = parseSemver(entry.weirVersion);
    if (!version) {
      found.push(`${folder}: "${entry.weirVersion}" in weir-builds-recorded.json is not a Weir version such as 1.0.0-rc.18.`);
      continue;
    }
    parsed.push({ folder, version });
  }

  if (found.length > 0) return found;
  if (parsed.length === 0) return ["weir-builds-recorded.json lists no recordings, so nothing shows how recent they are."];

  const newest = parsed.reduce((best, entry) => (compareSemver(entry.version, best.version) > 0 ? entry : best));
  const behind = releasesBehind(newest.version, repository, released);
  if (behind > MAX_RELEASES_BEHIND) {
    found.push(
      `The newest recordings of Weir (${newest.folder}) were made against ${newest.version.version}, and the repository is at ${repository.version}, ${behind} releases later (at most ${MAX_RELEASES_BEHIND} is allowed). Record against the current build on the golden VM with Deluno's recording script, copy the new set in, and add its "weirVersion" to weir-builds-recorded.json (docs/release.md).`,
    );
  }
  return found;
}

function main() {
  const flag = process.argv.indexOf("--version");
  const given = flag >= 0 ? process.argv[flag + 1] : undefined;
  if (flag >= 0 && !given) {
    console.error("Usage: node scripts/check-recorded-freshness.mjs [--version <tag or version>]");
    process.exit(2);
  }

  const released = changelogVersions(readFileSync(changelogFile, "utf8"));
  let repository;
  if (given) {
    repository = parseSemver(given);
    if (!repository) {
      console.error(`::error::"${given}" is not a release version in the form 1.0.0-rc.1 or 1.0.0.`);
      process.exit(1);
    }
    if (!released.some((version) => version.version === repository.version)) released.push(repository);
  } else {
    if (released.length === 0) {
      console.error("::error::CHANGELOG.md has no release line, so the repository's version cannot be read.");
      process.exit(1);
    }
    repository = released.reduce((best, version) => (compareSemver(version, best) > 0 ? version : best));
  }

  const found = problems(JSON.parse(readFileSync(buildsFile, "utf8")).sets, foldersOnDisk(recordedRoot), released, repository);
  if (found.length > 0) {
    console.error("The recordings of Weir are too old (#979):");
    for (const problem of found) console.error("  " + problem);
    process.exit(1);
  }
  console.log(`The newest recordings of Weir are at most ${MAX_RELEASES_BEHIND} release behind ${repository.version}.`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) main();
