import assert from "node:assert/strict";
import { test } from "node:test";

import { pullImage } from "./docker-smoke/pull.mjs";

const THROTTLE = { status: 1, output: "toomanyrequests: retry-after: 801.086µs, allowed: 44000/minute" };
const OK = { status: 0, output: "pulled" };

function sequence(...results) {
  const calls = [];
  return { calls, run: (command) => (calls.push(command), results[Math.min(calls.length, results.length) - 1]) };
}

test("a throttled pull is asked again after each wait until it succeeds", () => {
  const { run, calls } = sequence(THROTTLE, THROTTLE, OK);
  const waited = [];
  assert.equal(pullImage("img@sha256:a", { run, wait: (ms) => waited.push(ms), waitsMs: [1, 2, 3] }), "pulled");
  assert.equal(calls.length, 3);
  assert.deepEqual(waited, [1, 2]);
});

test("a throttle that outlasts the waits fails with the registry's words", () => {
  const { run, calls } = sequence(THROTTLE);
  assert.throws(() => pullImage("img@sha256:a", { run, wait: () => {}, waitsMs: [1, 2] }), /docker pull img@sha256:a exited 1\ntoomanyrequests/);
  assert.equal(calls.length, 3);
});

test("a failure that is not a throttle is not retried", () => {
  const { run, calls } = sequence({ status: 1, output: "manifest unknown" });
  assert.throws(() => pullImage("img@sha256:a", { run, wait: () => assert.fail("waited"), waitsMs: [1] }), /manifest unknown/);
  assert.equal(calls.length, 1);
});
