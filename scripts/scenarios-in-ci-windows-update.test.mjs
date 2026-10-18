// What the windows-update shard is made of, tested without a Windows PC or GitHub: the versions it builds and installs, the release feed it
// answers with (over real HTTP, in every mode), the hosts file text, and the judgements its scenarios make. The real run is the windows-update
// leg of scenarios-windows in ci-packaging.yml.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { REPOSITORY } from "./repository.mjs";
import { GithubStandIn, atomFeed, withOneByteChanged, withoutDeltas } from "./scenarios-ci/github-stand-in.mjs";
import { hostsRedirecting, hostsRestored } from "./scenarios-ci/github-redirect.mjs";
import { loadHomes, scenariosOfShard } from "./scenarios-ci/homes.mjs";
import { updateVersions } from "./scenarios-ci/update-versions.mjs";
import { SCENARIOS, activityProblems, expectPlainFailure } from "./scenarios-ci/windows-update.mjs";
import { packagesOf } from "./scenarios-ci/windows-shard.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const homes = loadHomes();

// --- the versions ---------------------------------------------------------------------------------------------------------------------

test("the build is the newest release's patch number plus one, and that release is the one installed first", () => {
  assert.deepEqual(updateVersions(["v1.0.0-rc.15", "v1.0.0-rc.16", "v1.0.0-rc.9"]), { previous: "1.0.0-rc.16", version: "1.0.1" });
  assert.deepEqual(updateVersions(["v3.2.16", "v3.2.9", "not-a-version", "v3.1.40"]), { previous: "3.2.16", version: "3.2.17" });
  assert.deepEqual(updateVersions(["3.2.16"]), { previous: "3.2.16", version: "3.2.17" });
});

test("with no release published there is nothing to update from", () => {
  assert.throws(() => updateVersions([]), /No release is published/);
  assert.throws(() => updateVersions(["latest"]), /No release is published/);
});

// --- the release feed -----------------------------------------------------------------------------------------------------------------

const releases = {
  Assets: [
    { PackageId: "Weir", Version: "1.0.1", Type: "Full", FileName: "Weir-1.0.1-full.nupkg", SHA256: "AA", Size: 8 },
    { PackageId: "Weir", Version: "1.0.1", Type: "Delta", FileName: "Weir-1.0.1-delta.nupkg", SHA256: "BB", Size: 4 },
  ],
};

test("the feed of the build under test names its full package and the delta, and refuses a build packed without a delta", () => {
  assert.deepEqual(packagesOf(JSON.stringify(releases)), { full: releases.Assets[0], delta: releases.Assets[1] });
  assert.throws(() => packagesOf(withoutDeltas(JSON.stringify(releases))), /needs a full package and a delta package; it holds Weir-1\.0\.1-full\.nupkg/);
});

test("the release feed lists each release newest first with the address of its page", () => {
  const feed = atomFeed(["v1.0.1", "v1.0.0-rc.16"], { updated: "2026-10-17T10:00:00Z" });
  assert.ok(feed.indexOf("v1.0.1") < feed.indexOf("v1.0.0-rc.16"));
  assert.ok(feed.includes(`href="https://github.com/${REPOSITORY}/releases/tag/v1.0.1"`));
  assert.ok(feed.includes("<updated>2026-10-17T10:00:00Z</updated>"));
});

test("one byte of a package is changed, and only one", () => {
  const bytes = Buffer.from("abcdefgh");
  const changed = withOneByteChanged(bytes);
  assert.equal(changed.length, bytes.length);
  assert.equal([...changed].filter((byte, at) => byte !== bytes[at]).length, 1);
  assert.equal(bytes.toString(), "abcdefgh");
});

/** A stand-in listening on a free port with the files of a build under test, and the tag of the release the machine runs. */
async function withStandIn(use) {
  const folder = await mkdtemp(resolve(tmpdir(), "weir-stand-in-test-"));
  await writeFile(resolve(folder, "Weir-1.0.1-full.nupkg"), "full package bytes");
  await writeFile(resolve(folder, "Weir-1.0.1-delta.nupkg"), "delta package bytes");
  const feed = new GithubStandIn({
    folder,
    current: { tag: "v1.0.1", releasesJson: JSON.stringify(releases) },
    previous: { tag: "v1.0.0-rc.16", releasesJson: JSON.stringify({ Assets: [] }) },
    updated: "2026-10-17T10:00:00Z",
  });
  const port = await feed.listen({ port: 0 });
  const get = async (path, host = "github.com") => {
    const answer = await fetch(`http://localhost:${port}${path}`, { headers: { host: `${host}:${port}` } });
    return { status: answer.status, text: await answer.text() };
  };
  try {
    await use({ feed, get });
  } finally {
    await feed.close();
    await rm(folder, { recursive: true, force: true });
  }
}

const atom = `/${REPOSITORY}/releases.atom`;
const download = (file, tag = "v1.0.1") => `/${REPOSITORY}/releases/download/${tag}/${file}`;

test("in newer mode the feed shows the build and serves its Velopack feed and packages", async () => {
  await withStandIn(async ({ feed, get }) => {
    const listed = await get(atom);
    assert.equal(listed.status, 200);
    assert.ok(listed.text.indexOf("releases/tag/v1.0.1") < listed.text.indexOf("releases/tag/v1.0.0-rc.16"));
    assert.deepEqual(JSON.parse((await get(download("releases.win.json"))).text), releases);
    assert.equal((await get(download("Weir-1.0.1-delta.nupkg"))).text, "delta package bytes");
    assert.equal((await get(download("Weir-1.0.1-full.nupkg"))).text, "full package bytes");
    assert.equal((await get(download("Weir-9.9.9-full.nupkg"))).status, 404);
    assert.deepEqual(feed.packagesAskedFor(), ["Weir-1.0.1-delta.nupkg", "Weir-1.0.1-full.nupkg"]);
    assert.deepEqual(feed.packagesAskedFor(feed.requests.length), []);
  });
});

test("in newer-full mode there is no delta to take", async () => {
  await withStandIn(async ({ feed, get }) => {
    feed.use("newer-full");
    assert.deepEqual(JSON.parse((await get(download("releases.win.json"))).text).Assets.map((asset) => asset.Type), ["Full"]);
    assert.equal((await get(download("Weir-1.0.1-delta.nupkg"))).status, 404);
    assert.equal((await get(download("Weir-1.0.1-full.nupkg"))).status, 200);
  });
});

test("in current mode only the release the machine runs is published", async () => {
  await withStandIn(async ({ feed, get }) => {
    feed.use("current");
    const listed = await get(atom);
    assert.ok(!listed.text.includes("v1.0.1") && listed.text.includes("v1.0.0-rc.16"));
    assert.equal((await get(download("releases.win.json"))).status, 404);
  });
});

test("in corrupt mode every package differs from the file its hash was recorded for", async () => {
  await withStandIn(async ({ feed, get }) => {
    feed.use("corrupt");
    const served = (await get(download("Weir-1.0.1-full.nupkg"))).text;
    assert.notEqual(served, "full package bytes");
    assert.equal(served.length, "full package bytes".length);
  });
});

test("in down mode everything answers 503, and an unknown mode is refused", async () => {
  await withStandIn(async ({ feed, get }) => {
    feed.use("down");
    assert.equal((await get(atom)).status, 503);
    assert.equal((await get(download("releases.win.json"))).status, 503);
    assert.throws(() => feed.use("sideways"), /no mode "sideways"/);
  });
});

test("the API address is answered 'not found', so a client that falls back to it finds nothing newer", async () => {
  await withStandIn(async ({ get }) => {
    assert.equal((await get(`/repos/${REPOSITORY}/releases`, "api.github.com")).status, 404);
  });
});

// --- the hosts file -------------------------------------------------------------------------------------------------------------------

test("the hosts file names github.com and api.github.com as this PC, and puts back exactly what it had", () => {
  const original = "# Copyright\r\n127.0.0.1 localhost\r\n::1 localhost";
  const redirected = hostsRedirecting(original);
  assert.match(redirected, /127\.0\.0\.1 github\.com # weir-scenario\r\n127\.0\.0\.1 api\.github\.com # weir-scenario/);
  assert.equal(hostsRedirecting(redirected), redirected, "an earlier run's lines are not added twice");
  assert.equal(hostsRestored(redirected).trimEnd(), original);
});

// --- judging an update ----------------------------------------------------------------------------------------------------------------

test("an Activity entry that is gone or changed after the update is a finding, and a new one is not", () => {
  const entry = (id, title, result = "ok") => ({ id, title, result, created_at: "t", event_type: "e", module: "m", detail: "d", trigger: "x", library_id: 1, relative_path: "p", run_key: "r" });
  const before = [entry(2, "Second"), entry(1, "First")];
  assert.deepEqual(activityProblems(before, [entry(3, "Third"), ...before]), []);
  assert.deepEqual(activityProblems(before, [before[0]]), ['#1 "First" is gone']);
  assert.deepEqual(activityProblems(before, [entry(2, "Second", "failed"), before[1]]), ['#2 "Second" changed (result)']);
});

test("a failure the page shows has to be a sentence of plain words that names what failed", () => {
  expectPlainFailure("Weir could not download the update. Check the connection and try again.", "download the update");
  assert.throws(() => expectPlainFailure(null, "download the update"), /not explained/);
  assert.throws(() => expectPlainFailure("Checksum mismatch for Weir-1.0.1-full.nupkg", "download the update"), /plain words/);
  assert.throws(() => expectPlainFailure("Weir could not reach GitHub.", "download the update"), /could not download the update/);
  assert.throws(() => expectPlainFailure("Weir could not download the update: Velopack.Exception at Velopack.Sources.Get", "download the update"), /shows what the code threw/);
});

// --- the shard ------------------------------------------------------------------------------------------------------------------------

test("every ported scenario of the shard has code, in the order it runs, and every scenario with code is ported", () => {
  const ported = scenariosOfShard(homes, "windows-update").filter((scenario) => scenario.state !== "to-port");
  assert.deepEqual(Object.keys(SCENARIOS), ported.map((scenario) => scenario.id));
});

test("the entry point names what the update shard needs, and refuses a machine that is not a hosted runner", () => {
  const entry = (args) =>
    spawnSync(process.execPath, ["scripts/scenarios-in-ci.mjs", "windows-update", ...args], {
      cwd: repository,
      encoding: "utf8",
      env: { ...process.env, GITHUB_ACTIONS: "", RUNNER_ENVIRONMENT: "" },
    });
  const missing = entry(["--feed", "feed"]);
  assert.equal(missing.status, 2);
  assert.match(missing.stderr, /needs --previous-setup, --previous-feed, --previous-version, --version/);
  const refused = entry(["--feed", "feed", "--previous-setup", "a.exe", "--previous-feed", "r.json", "--previous-version", "1.0.0", "--version", "1.0.1"]);
  assert.equal(refused.status, 2);
  assert.match(refused.stderr, /installs and uninstalls Weir for real, and this is (not a GitHub-hosted runner|linux, not Windows)/);
});
