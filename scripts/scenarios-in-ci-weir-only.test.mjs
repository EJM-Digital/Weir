// The Pause and resume and deleted-file scenarios of the weir-only shard, judged against the stand-in for Weir and the container
// (scenarios-ci/fake-world.mjs): each must pass when Weir does what it should, and fail, saying why, for each way Weir can go wrong.
import assert from "node:assert/strict";
import { test } from "node:test";

import { ContainerBox } from "./scenarios-ci/container-box.mjs";
import { FakeWorld } from "./scenarios-ci/fake-world.mjs";
import { runScenarios } from "./scenarios-ci/run.mjs";
import { SCENARIOS } from "./scenarios-ci/weir-only.mjs";

async function run(id, behaviour) {
  const world = new FakeWorld(behaviour);
  const [result] = await runScenarios({ ids: [id], registry: SCENARIOS, world: world.asWorld() });
  return { result, world };
}

// --- pause-resume -------------------------------------------------------------------------------------------------------

test("a film dropped in while Weir is paused is held, then cleaned once, after the resume", async () => {
  const { result } = await run("pause-resume");
  assert.equal(result.status, "passed", result.detail);
  assert.match(result.detail, /found and held \("Processing is paused until you resume it\."\)/);
  assert.match(result.detail, /Activity shows both by the tray/);
});

test("Weir that never takes the pause request fails the scenario", async () => {
  const { result } = await run("pause-resume", { ignoresPauseRequest: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /Weir to pause on the tray's request did not happen/);
});

test("Weir that leaves the request file behind fails the scenario", async () => {
  const { result } = await run("pause-resume", { keepsRequestFile: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /Weir to take the request file away did not happen/);
});

test("Weir that cleans a film while paused fails the scenario", async () => {
  const { result } = await run("pause-resume", { cleansWhilePaused: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /holding it|started no pass|wrote nothing/);
});

test("Weir that does not resume on the request fails the scenario with that, not with a second failure from putting it right", async () => {
  const { result } = await run("pause-resume", { staysPaused: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /Weir to resume on the tray's request did not happen/);
});

test("Weir that never cleans the film after the resume fails the scenario", async () => {
  const { result } = await run("pause-resume", { neverCleans: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /waiting film to be cleaned once Weir resumed did not happen/);
});

// --- deleted-queued-file ------------------------------------------------------------------------------------------------

test("a file deleted while Weir waits for it to settle is marked no longer there, and nothing fails", async () => {
  const { result, world } = await run("deleted-queued-file");
  assert.equal(result.status, "passed", result.detail);
  assert.match(result.detail, /source_gone true/);
  assert.equal(world.workflows[0].min_file_size_mb, 50);
  assert.equal(world.workflows[0].ready_after_seconds, 60);
});

test("a deleted file Weir reports as a failure fails the scenario", async () => {
  const { result } = await run("deleted-queued-file", { failsVanishedFile: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /the vanished file is not a failure/);
});

test("Weir that never notices the file is gone fails the scenario", async () => {
  const { result } = await run("deleted-queued-file", { neverNoticesVanishedFile: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /Weir to notice the file is no longer there did not happen/);
});

// --- the container ------------------------------------------------------------------------------------------------------

function recordingStack() {
  const calls = [];
  return { calls, exec: (command) => (calls.push(command), ""), execResult: (command) => (calls.push(command), { status: 0, stdout: "", stderr: "", output: "" }) };
}

test("a request file is written to a neighbour and renamed into place, so the watcher never reads half of it", async () => {
  const stack = recordingStack();
  await new ContainerBox(stack).writeFileAtomically("/data/weir/pause-request.json", '{"paused":true}');
  assert.deepEqual(stack.calls, [`printf %s '{"paused":true}' > '/data/weir/pause-request.json.partial' && mv '/data/weir/pause-request.json.partial' '/data/weir/pause-request.json'`]);
});

test("a file of exact bytes is written with printf's octal escapes, and a file is removed without complaint when it is not there", async () => {
  const stack = recordingStack();
  const box = new ContainerBox(stack);
  await box.writeBytes("/tmp/w/a.mkv", [1, 2, 3, 4, 5]);
  await box.remove("/tmp/w/a.mkv");
  assert.deepEqual(stack.calls, [String.raw`printf '\001\002\003\004\005' > '/tmp/w/a.mkv'`, `rm -f '/tmp/w/a.mkv'`]);
});
