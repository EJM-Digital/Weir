// node --test scripts/script-edge-cases.test.mjs
// Small scripts, each shown the input its own test file leaves out: missing parts of a report, a version that is not one, a folder with
// nothing in it, a command line that is wrong.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { findOtherVersionAssets } from "./check-release-assets-single-version.mjs";
import { parseReleaseVersion } from "./check-release-version.mjs";
import { classesIn } from "./e2e-shards.mjs";
import { EXACT } from "./merge-web-coverage.mjs";
import { main as newestMain, newestDelunoImage } from "./newest-deluno-image.mjs";
import { findWeirReleaseAssetUrl } from "./pinned-download.mjs";
import { resolveTargets } from "./resolve-mutation-targets.mjs";
import { mutatePatterns } from "./select-mutation-targets.mjs";
import { compareSemver, parseSemver } from "./semver.mjs";
import { readTrx, readTrxResults, summarizePlaywright } from "./summarize-test-results.mjs";

const here = (name) => fileURLToPath(new URL(name, import.meta.url));
const run = (script, args = [], options = {}) => spawnSync(process.execPath, [here(script), ...args], { encoding: "utf8", ...options });

function inFolder(body) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-edge-"));
  try {
    return body(folder);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

const put = (root, file, text) => {
  mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
  writeFileSync(path.join(root, file), text);
};

test("a version that is missing is not a version, and pre-release identifiers order as semver says", () => {
  assert.equal(parseSemver(undefined), null);
  assert.equal(parseSemver(null), null);
  assert.equal(parseReleaseVersion(undefined).ok, false);
  const order = (left, right) => compareSemver(parseSemver(left), parseSemver(right));
  assert.equal(order("1.0.0-alpha", "1.0.0-1"), 1, "a name outranks a number");
  assert.equal(order("1.0.0-1", "1.0.0-alpha"), -1);
  assert.equal(order("1.0.0-beta", "1.0.0-alpha"), 1);
  assert.equal(order("1.0.0-alpha", "1.0.0-beta"), -1);
});

test("test results are read when a result has no name, the counters are missing, and a Playwright suite has no specs", () => {
  assert.deepEqual(readTrxResults('<UnitTestResult outcome="Passed" />'), [{ name: "(unnamed test)", outcome: "Passed", message: "" }]);
  assert.deepEqual(readTrx("<TestRun />"), { total: 0, passed: 0, failed: 0, skipped: 0, failures: [] });
  assert.deepEqual(readTrx('<Counters total="2" />').total, 2);
  inFolder((folder) => {
    const file = path.join(folder, "playwright.json");
    writeFileSync(file, JSON.stringify({ suites: [{ title: "a.spec.ts", suites: [{ title: "group" }] }] }));
    assert.match(summarizePlaywright(file), /^### Browser smoke tests/);
  });
});

test("web coverage points are compared by position, a missing position or location being a dash", () => {
  assert.equal(EXACT.statement(undefined), "-");
  assert.equal(EXACT.statement({ start: { line: 1, column: 2 }, end: {} }), "1:2--");
  assert.equal(EXACT.statement({ start: null, end: { line: 3, column: 4 } }), "--3:4");
});

test("a folder whose release feed cannot be read for a reason other than being absent stops the check", () => {
  inFolder((folder) => {
    mkdirSync(path.join(folder, "releases.win.json"));
    assert.throws(() => findOtherVersionAssets(folder, "1.0.0"), { code: "EISDIR" });
  });
});

test("the asset lookup takes its token from GITHUB_TOKEN, else GH_TOKEN, else asks without one", async () => {
  const saved = { GITHUB_TOKEN: process.env.GITHUB_TOKEN, GH_TOKEN: process.env.GH_TOKEN };
  try {
    const authorizationWith = async (env) => {
      delete process.env.GITHUB_TOKEN;
      delete process.env.GH_TOKEN;
      Object.assign(process.env, env);
      let sent;
      const fetchImpl = async (url, options) => ((sent = options.headers.Authorization), { ok: true, json: async () => [{ assets: [{ name: "a.zip", browser_download_url: "https://example.test/a.zip" }] }] });
      assert.equal(await findWeirReleaseAssetUrl("a.zip", { fetchImpl }), "https://example.test/a.zip");
      return sent;
    };
    assert.equal(await authorizationWith({ GITHUB_TOKEN: "one" }), "Bearer one");
    assert.equal(await authorizationWith({ GH_TOKEN: "two" }), "Bearer two");
    assert.equal(await authorizationWith({}), undefined);
  } finally {
    for (const [name, value] of Object.entries(saved)) {
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
});

test("mutation targets that do not exist are named, and a request that matches none runs nothing", () => {
  const targets = [{ target: "core" }];
  assert.throws(() => resolveTargets("core,a,b", targets), /^Error: Unknown mutation targets: a, b\. The targets are: core\./);
  assert.throws(() => resolveTargets("a", targets), /^Error: Unknown mutation target: a\./);

  const none = run("resolve-mutation-targets.mjs", [], { env: { PATH: process.env.PATH, REQUESTED: "," } });
  assert.equal(none.status, 0, none.stderr);
  assert.match(none.stdout, /^No mutation target has changed; nothing to run\./);
  const unknown = run("resolve-mutation-targets.mjs", [], { env: { PATH: process.env.PATH, REQUESTED: "nothing-like-it" } });
  assert.equal(unknown.status, 1);
  assert.match(unknown.stderr, /^::error::Unknown mutation target: nothing-like-it\./);
});

test("a Stryker config whose mutate list is not a list names the target", () => {
  inFolder((root) => {
    put(root, "apps/web/stryker.json", JSON.stringify({ mutate: "src/**" }));
    assert.throws(() => mutatePatterns({ target: "web", kind: "web", directory: "apps/web", config: "stryker.json" }, root), /^Error: web: its Stryker config lists nothing to mutate\./);
  });
});

const E2E_CLASS = (name, tests) => `namespace Weir.E2E;\npublic class ${name} : E2ETestBase\n{\n${"  [E2EFact] public async Task T() {}\n".repeat(tests)}}\n`;

test("a source file with no browser test class has none to report, and one with a class but no namespace is refused", () => {
  assert.deepEqual(classesIn("public class Helper {}\n"), []);
  assert.throws(() => classesIn("public class A : E2ETestBase { }\n", "A.cs"), /A\.cs has no file-scoped namespace\./);
});

test("e2e-shards, run as a command on a project, lists the shards and refuses what it cannot do", () => {
  inFolder((root) => {
    const project = "apps/server/tests/Weir.E2E.Tests";
    const shards = (...args) => run("e2e-shards.mjs", [...args, "--root", root]);

    put(root, `${project}/Helper.cs`, "public class Helper {}\n");
    const empty = shards("--matrix");
    assert.equal(empty.status, 1);
    assert.match(empty.stderr, /No browser test classes found under apps\/server\/tests\/Weir\.E2E\.Tests\./);

    put(root, `${project}/A.cs`, E2E_CLASS("ATests", 3));
    put(root, `${project}/bin/Built.cs`, E2E_CLASS("BuiltTests", 99));
    assert.equal(shards("--matrix").stdout.trim(), "[1]");
    assert.equal(shards("--shard", "1").stdout.trim(), "FullyQualifiedName~Weir.E2E.ATests.");
    assert.equal(shards("--summary").stdout.trim(), "Shard 1: 3 tests in 1 classes");

    const missing = shards("--shard", "9");
    assert.equal(missing.status, 1);
    assert.match(missing.stderr, /There is no shard 9; there are 1\./);
    const usage = shards("--wrong");
    assert.equal(usage.status, 1);
    assert.match(usage.stderr, /Usage: node scripts\/e2e-shards\.mjs --matrix \| --shard <n> \| --summary/);
  });
});

test("the newest Deluno image is printed as 'tag reference', and a registry that sends no digest is an error", async () => {
  const lines = [];
  await newestMain(["ghcr.io/owner/deluno"], { newest: async (repository) => ({ tag: "1.2.3", reference: `${repository}@sha256:abc` }), log: (line) => lines.push(line) });
  assert.deepEqual(lines, ["1.2.3 ghcr.io/owner/deluno@sha256:abc"]);

  const answer = (body) => ({ ok: true, status: 200, json: async () => body, headers: { get: () => null } });
  const fetchImpl = async (url) => (String(url).includes("/releases") ? answer([{ tag_name: "v1.2.3", published_at: "2026-10-01T00:00:00Z" }]) : answer({ token: "t" }));
  await assert.rejects(newestDelunoImage("ghcr.io/owner/deluno", fetchImpl), /The registry gave no digest for ghcr\.io\/owner\/deluno:1\.2\.3 \(answered 200\)\./);

  const refused = run("newest-deluno-image.mjs", ["not-a-registry/deluno"]);
  assert.notEqual(refused.status, 0);
  assert.match(refused.stderr, /not-a-registry\/deluno is not a ghcr\.io repository\./);
});
