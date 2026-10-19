#!/usr/bin/env node
// The two versions the windows-update shard needs. An update only goes to a newer version, and the build under test carries a placeholder
// (0.0.1-dev) that is older than every release, so the shard builds the commit again under a version just above the newest published
// release, and installs that release first. The build is a stable version, never a pre-release: an install of a stable release is offered
// stable releases only, and an install of a pre-release is offered both.
//
// Usage: gh api --paginate repos/<owner>/<repo>/releases --jq '.[] | select(.draft | not) | .tag_name' \
//          | node scripts/scenarios-ci/update-versions.mjs
// Prints `previous=<version>` and `version=<version>`, the lines a workflow step appends to $GITHUB_OUTPUT.
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { findPreviousVersion } from "../find-previous-release.mjs";
import { compareSemver, parseSemver } from "../semver.mjs";

/** The newest release among `tags` (versions, with or without the leading v); null when there is none. */
export function newestRelease(tags) {
  let newest = null;
  for (const tag of tags) {
    const candidate = parseSemver(tag);
    if (candidate && (newest === null || compareSemver(candidate, newest) > 0)) newest = candidate;
  }
  return newest;
}

/**
 * The release to install first and the version to build: the build is the newest release's patch number plus one, which is newer than that
 * release whether it is stable or a release candidate.
 */
export function updateVersions(tags) {
  const newest = newestRelease(tags);
  if (!newest) throw new Error("No release is published, so there is nothing to update from.");
  const version = `${newest.major}.${newest.minor}.${BigInt(newest.patch) + 1n}`;
  const previous = findPreviousVersion(tags, version);
  return { previous, version };
}

function main() {
  const tags = readFileSync(0, "utf8").split(/\r?\n/).filter((line) => line.trim().length > 0);
  const { previous, version } = updateVersions(tags);
  console.log(`previous=${previous}`);
  console.log(`version=${version}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main();
}
