// node --test scripts/check-release-fixtures.test.mjs
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

import { problems, publishedVersions } from "./check-release-fixtures.mjs";

const whole = (versions) => ({ fixtureVersions: versions, databaseFiles: versions, backupFolders: versions });

test("a release is published when its CHANGELOG line links the release, and the newest comes first", () => {
  const changelog = [
    "- **1.0.0-rc.9** (2026-10-09, tagged but not published). [notes](a)",
    "- **1.0.0-rc.10** (2026-10-09). [notes](a) · [release](b)",
    "- **1.0.0-rc.2** (2026-10-07). [notes](a) · [release](b)",
    "- **1.0.0-rc.11** (2026-10-09). [notes](a) · [release](b)",
    "Some other line with [release](b) in it",
  ].join("\n");

  assert.deepEqual(publishedVersions(changelog), ["1.0.0-rc.11", "1.0.0-rc.10", "1.0.0-rc.2"]);
});

test("the repository's own CHANGELOG yields published releases", () => {
  const published = publishedVersions(readFileSync(new URL("../CHANGELOG.md", import.meta.url), "utf8"));

  assert.ok(published.length >= 2, "the CHANGELOG lists at least two published releases");
  assert.ok(!published.includes("1.0.0-rc.16"), "a release that was never published is not one");
});

test("fixtures that reach the second newest release are fresh", () => {
  assert.deepEqual(problems({ published: ["1.0.0-rc.19", "1.0.0-rc.18"], ...whole(["1.0.0-rc.17", "1.0.0-rc.18"]) }), []);
});

test("fixtures one release behind the newest are still fresh", () => {
  assert.deepEqual(problems({ published: ["1.0.0-rc.19", "1.0.0-rc.18", "1.0.0-rc.17"], ...whole(["1.0.0-rc.18"]) }), []);
});

test("fixtures two releases behind are not, and the sentence says which to make", () => {
  const found = problems({ published: ["1.0.0-rc.20", "1.0.0-rc.19", "1.0.0-rc.18"], ...whole(["1.0.0-rc.18"]) });

  assert.equal(found.length, 1);
  assert.match(found[0], /make the fixtures of 1\.0\.0-rc\.19/);
});

test("no fixtures at all is behind", () => {
  assert.equal(problems({ published: ["1.0.0-rc.2", "1.0.0-rc.1"], fixtureVersions: [], databaseFiles: [], backupFolders: [] }).length, 1);
});

test("a listed version missing its database or its backup, and a file nothing lists, are named", () => {
  const found = problems({
    published: ["1.0.0-rc.2", "1.0.0-rc.1"],
    fixtureVersions: ["1.0.0-rc.2", "1.0.0-rc.1"],
    databaseFiles: ["1.0.0-rc.2", "1.0.0-rc.0"],
    backupFolders: ["1.0.0-rc.1", "1.0.0-rc.0"],
  });

  assert.deepEqual(found, [
    "The manifest lists 1.0.0-rc.2 but recorded/backups/1.0.0-rc.2/configuration-bundle.json is missing.",
    "The manifest lists 1.0.0-rc.1 but databases/1.0.0-rc.1.db.gz is missing.",
    "databases/1.0.0-rc.0.db.gz is not in the manifest.",
    "recorded/backups/1.0.0-rc.0 is not in the manifest.",
  ]);
});
