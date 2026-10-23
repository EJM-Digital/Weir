// node --test scripts/wait-for-health-failures.test.mjs
// What wait-for-health.mjs reports when the server answers badly or not at all (fetch is replaced; nothing is reached).
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const script = fileURLToPath(new URL("./wait-for-health.mjs", import.meta.url));

function runWith(fetchBody, ...extraArguments) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-wait-failures-"));
  try {
    const preload = path.join(folder, "preload.mjs");
    writeFileSync(preload, `globalThis.fetch = ${fetchBody};\n`);
    return spawnSync(
      process.execPath,
      ["--import", pathToFileURL(preload).href, script, "--url", "http://health.invalid/health", "--attempts", "2", "--interval-seconds", "0.01", ...extraArguments],
      { encoding: "utf8", timeout: 30000 },
    );
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

test("a server that answers with an error status is retried, and the last status and the start of its body are reported", () => {
  const run = runWith(`async () => new Response("x".repeat(300), { status: 503 })`);
  assert.equal(run.status, 1);
  assert.match(run.stderr, new RegExp(`did not answer after 2 attempts \\(last: HTTP 503: x{200}\\)\\.`));
});

test("a connection that fails says why, from the error's cause when it has one", () => {
  const withCause = runWith(`async () => { throw new TypeError("fetch failed", { cause: { code: "ECONNREFUSED" } }); }`);
  assert.equal(withCause.status, 1);
  assert.match(withCause.stderr, /\(last: ECONNREFUSED\)/);
  const plain = runWith(`async () => { throw new Error("socket closed"); }`);
  assert.match(plain.stderr, /\(last: socket closed\)/);
});

test("with a container named, the failure also asks for its log, and a log that cannot be read is said, not fatal", () => {
  const run = runWith(`async () => new Response("down", { status: 500 })`, "--container", "weir-no-such-container-for-tests");
  assert.equal(run.status, 1);
  assert.match(run.stderr, /Could not read the logs of weir-no-such-container-for-tests: /);
});
