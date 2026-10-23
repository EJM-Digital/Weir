// node --test scripts/scenarios-in-ci-helpers.test.mjs
// The small helpers of the scenarios in CI: the container's files, the SABnzbd stand-in and the versions the update shard needs.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { ContainerBox } from "./scenarios-ci/container-box.mjs";
import { SabnzbdStandIn } from "./scenarios-ci/sabnzbd-stand-in.mjs";
import { newestRelease, updateVersions } from "./scenarios-ci/update-versions.mjs";

/** A stack that remembers what it was told to run and answers each result command from `answers`. */
function stackAnswering(answers = {}) {
  const calls = [];
  return {
    calls,
    exec: (command) => calls.push(command),
    execResult: (command) => {
      calls.push(command);
      const answer = answers[command.split(" ")[0]] ?? { status: 0, stdout: "", output: "" };
      return answer;
    },
  };
}

test("the box makes folders, a film and a sound-only file in the container, and removes files and trees", async () => {
  const stack = stackAnswering();
  const box = new ContainerBox(stack);

  await box.ensureFolders("/work/a", "/work/b");
  await box.makeFilm({ path: "/work/a/film.mkv", seconds: 5, videoBitrate: "1M" });
  await box.makeAudioOnly({ path: "/work/a/sound.mkv", seconds: 5 });
  await box.remove("/work/a/film.mkv");
  await box.removeTree("/work/b");

  assert.equal(stack.calls[0], "mkdir -p '/work/a' '/work/b'");
  assert.match(stack.calls[1], /ffmpeg.*\/work\/a\/film\.mkv/s);
  assert.match(stack.calls[2], /ffmpeg.*\/work\/a\/sound\.mkv/s);
  assert.equal(stack.calls[3], "rm -f '/work/a/film.mkv'");
  assert.equal(stack.calls[4], "rm -rf '/work/b'");
  for (const unsafe of [() => box.makeFilm({ path: "/a b" }), () => box.makeAudioOnly({ path: "/a;b" }), () => box.remove("/a b"), () => box.removeTree("/a b"), () => box.sizeOf("/a b"), () => box.findNamed("/a b", "x"), () => box.exists("/a b"), () => box.modified("/a b")]) {
    await assert.rejects(unsafe, /not a plain path/);
  }
});

test("the box reads a file's size and finds files by name, and says when stat cannot read one", async () => {
  const stack = stackAnswering({
    stat: { status: 0, stdout: "1234\n", output: "1234\n" },
    find: { status: 0, stdout: "/a/one.mkv\n/a/b/one.mkv\n\n", output: "" },
  });
  const box = new ContainerBox(stack);

  assert.equal(await box.sizeOf("/a/one.mkv"), 1234);
  assert.deepEqual(await box.findNamed("/a", "one.mkv"), ["/a/one.mkv", "/a/b/one.mkv"]);
  assert.equal(stack.calls[0], "stat -c %s /a/one.mkv");
  assert.equal(stack.calls[1], "find /a -type f -name 'one.mkv'");

  const missing = new ContainerBox(stackAnswering({ stat: { status: 1, stdout: "", output: "No such file\n" }, sha256sum: { status: 1, stdout: "", output: "gone\n" }, test: { status: 1, stdout: "", output: "" } }));
  await assert.rejects(missing.sizeOf("/a/x"), /stat could not read \/a\/x: No such file/);
  await assert.rejects(missing.modified("/a/x"), /stat could not read \/a\/x: No such file/);
  await assert.rejects(missing.sha256("/a/x"), /sha256sum could not read \/a\/x: gone/);
  assert.equal(await missing.exists("/a/x"), false);
  assert.equal(await box.exists("/a/one.mkv"), true);
});

test("the box fails a probe that ffprobe could not make, with what it said", async () => {
  const box = new ContainerBox(stackAnswering({ ffprobe: { status: 1, stdout: "", output: "Invalid data\n" } }));
  await assert.rejects(box.probe("/a/x.mkv"), /ffprobe could not read \/a\/x\.mkv: Invalid data/);
});

async function ask(port, query) {
  const answer = await fetch(`http://localhost:${port}${query}`);
  return { status: answer.status, json: await answer.json() };
}

test("the SABnzbd stand-in answers the settings read only for the key it was given, and notes who asked", async () => {
  const standIn = new SabnzbdStandIn("right-key");
  const port = await standIn.listen();
  try {
    const right = await ask(port, "/api?mode=get_config&section=misc&apikey=right-key");
    const wrongKey = await ask(port, "/api?mode=get_config&section=misc&apikey=other");
    const otherMode = await ask(port, "/api?mode=queue&apikey=right-key");
    const otherPath = await ask(port, "/elsewhere?mode=get_config&section=misc&apikey=right-key");

    assert.deepEqual([right.status, right.json.config.misc.complete_dir], [200, "C:\\Downloads\\complete"]);
    assert.deepEqual([wrongKey.status, wrongKey.json.error], [401, "API Key Incorrect"]);
    assert.deepEqual([otherMode.status, otherMode.json.error], [404, "Not found"]);
    assert.equal(otherPath.status, 404);
    assert.deepEqual(standIn.asked.map((request) => request.right), [true, false, true, true]);
    assert.deepEqual(standIn.asked[0], { mode: "get_config", section: "misc", right: true });
  } finally {
    await standIn.close();
  }
});

test("stopping a stand-in that never started, or twice, is harmless", async () => {
  const standIn = new SabnzbdStandIn("key");
  await standIn.close();
  const port = await standIn.listen();
  await standIn.close();
  await standIn.close();
  assert.ok(port > 0);
});

test("the version to build is one patch above the newest release, and the release to install first is the one before it", () => {
  assert.equal(newestRelease(["v1.2.3", "v1.10.0", "v1.9.9", "not-a-version"]).minor, "10");
  assert.equal(newestRelease([]), null);
  assert.equal(newestRelease(["junk"]), null);
  const { previous, version } = updateVersions(["v3.2.5", "v3.2.4", "3.1.0"]);
  assert.equal(version, "3.2.6");
  assert.equal(previous, "3.2.5");
  assert.throws(() => updateVersions(["junk"]), /No release is published/);
});

test("run as a command it reads release tags from standard input and prints the two lines a workflow step appends", () => {
  const script = fileURLToPath(new URL("./scenarios-ci/update-versions.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script], { input: "v3.2.5\nv3.2.4\n\n", encoding: "utf8" });
  assert.equal(result.status, 0);
  assert.equal(result.stdout, "previous=3.2.5\nversion=3.2.6\n");
});
