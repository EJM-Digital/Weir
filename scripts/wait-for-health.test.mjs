// node --test scripts/wait-for-health.test.mjs
// scripts/wait-for-health.mjs waits for a server to answer /health. An attempt that hangs must count as a failed attempt and the
// loop must go on: AbortSignal.timeout's timer does not keep Node alive, so a fetch that hung with nothing else holding the event
// loop open ended the process with exit 13 ("unsettled top-level await"), with no retry and no container logs (docker-smoke on 93173440).
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const script = resolve(dirname(fileURLToPath(import.meta.url)), "wait-for-health.mjs");

// A fetch that holds nothing open for its first `hung` calls and settles only when its signal aborts, as a request stuck behind a
// proxy can; later calls answer. What it does not do is keep the event loop alive: only the script's own timer can do that.
function runWith(hung, extraArguments) {
  const folder = mkdtempSync(join(tmpdir(), "weir-wait-for-health-"));
  const preload = join(folder, "preload.mjs");
  writeFileSync(
    preload,
    `let calls = 0;
globalThis.fetch = (url, options = {}) => {
  calls += 1;
  if (calls <= ${hung}) {
    return new Promise((resolve, reject) => {
      options.signal?.addEventListener("abort", () => reject(options.signal.reason ?? new Error("aborted")), { once: true });
    });
  }
  return Promise.resolve(new Response('{"status":"ok"}', { status: 200 }));
};
`,
  );
  return spawnSync(
    process.execPath,
    ["--import", pathToFileURL(preload).href, script, "--url", "http://127.0.0.1:1/health", "--interval-seconds", "0.05", ...extraArguments],
    { encoding: "utf8", timeout: 30000 },
  );
}

test("a hung attempt counts as a failed attempt and the loop tries again (the default timeout)", () => {
  const run = runWith(1, ["--attempts", "3"]);
  assert.equal(run.status, 0, `exit ${run.status}: ${run.stderr}${run.stdout}`);
  assert.match(run.stdout, /"status":"ok"/);
});

test("a hung attempt counts as a failed attempt and the loop tries again (a short timeout)", () => {
  const run = runWith(2, ["--attempts", "5", "--attempt-timeout-seconds", "0.3"]);
  assert.equal(run.status, 0, `exit ${run.status}: ${run.stderr}${run.stdout}`);
  assert.match(run.stdout, /"status":"ok"/);
});

test("when every attempt hangs it says so after the last one, not before", () => {
  const run = runWith(99, ["--attempts", "3", "--attempt-timeout-seconds", "0.2"]);
  assert.equal(run.status, 1, `exit ${run.status}: ${run.stderr}${run.stdout}`);
  assert.match(run.stderr, /did not answer after 3 attempts/);
});

test("an attempt that answers at once is not delayed by the timeout", () => {
  const started = Date.now();
  const run = runWith(0, ["--attempts", "3", "--attempt-timeout-seconds", "20"]);
  assert.equal(run.status, 0, run.stderr);
  assert.ok(Date.now() - started < 10000, "the finished attempt's timer must not hold the process open");
});
