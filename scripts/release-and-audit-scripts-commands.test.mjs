// node --test scripts/release-and-audit-scripts-commands.test.mjs
// The command lines of ci-passed, the npm audit retry and the shipped-files comparison, and the mutation-input shapes the release scripts read.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { GATES, verdict } from "./ci-passed.mjs";
import { auditWithRetry, classifyAuditOutput, main as auditMain, meetsAuditLevel, runNpmAuditOnce, unapprovedAdvisories } from "./npm-audit-retry.mjs";
import { entryChanged, inputsOf } from "./release/mutation-inputs.mjs";
import { repository } from "./release/temporary-repository.mjs";
import { sameShippedFiles } from "./release/shipped-paths.mjs";

const here = (name) => fileURLToPath(new URL(name, import.meta.url));

function inFolder(body) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-release-audit-"));
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

// --- ci-passed ---

function needsFor(results = {}) {
  const outputs = Object.fromEntries([...new Set(Object.values(GATES).filter(Boolean))].map((flag) => [flag, "true"]));
  return { changes: { result: "success", outputs }, ...Object.fromEntries(Object.keys(GATES).map((job) => [job, { result: results[job] ?? "success" }])) };
}

test("ci-passed, run as a command, prints each job's verdict and exits 0 when all passed, 1 with an error annotation when not", () => {
  const run = (needs) => spawnSync(process.execPath, [here("./ci-passed.mjs")], { encoding: "utf8", env: { PATH: process.env.PATH, ...(needs ? { NEEDS: JSON.stringify(needs) } : {}) } });

  const passed = run(needsFor());
  assert.equal(passed.status, 0, passed.stderr);
  assert.match(passed.stdout, /repo-checks\s+always\s+success\n/);
  assert.match(passed.stdout, /server-linux\s+due\s+success\n/);
  assert.match(passed.stdout, /Every job that was due passed, and every other job was skipped by path filtering\.\n$/);

  const failed = run(needsFor({ web: "failure" }));
  assert.equal(failed.status, 1);
  assert.match(failed.stderr, /^::error::web ended failure; it was due, so it must pass\./);

  const idleNeeds = needsFor(Object.fromEntries(Object.keys(GATES).map((job) => [job, "skipped"])));
  idleNeeds.changes.outputs = { ...Object.fromEntries(Object.keys(idleNeeds.changes.outputs).map((flag) => [flag, "false"])), idle: "true" };
  const idle = run(idleNeeds);
  assert.equal(idle.status, 0, idle.stderr);
  assert.match(idle.stdout, /repo-checks\s+idle\s+skipped\n/);

  const nothing = run();
  assert.equal(nothing.status, 1);
  assert.match(nothing.stdout, /repo-checks\s+always\s+missing\n/);
  assert.match(nothing.stderr, /::error::changes ended missing/);
});

// --- the npm audit retry ---

test("a registry error envelope with no detail of its own is read from standard error, and an unknown level is refused", () => {
  const outcome = classifyAuditOutput({ stdout: JSON.stringify({ error: {} }), stderr: "ECONNRESET while talking to the registry" });
  assert.equal(outcome.transient, true);
  assert.equal(outcome.reason, "ECONNRESET while talking to the registry");
  assert.throws(() => meetsAuditLevel({}, "bogus"), /Unknown audit level: bogus/);
  assert.equal(meetsAuditLevel(null, "high"), false);
});

test("npm audit run once in a folder with no lock file answers with an error, which the retry does not mistake for the registry", async () => {
  await inFolder(async (folder) => {
    const once = runNpmAuditOnce(folder);
    assert.notEqual(once.status, 0);
    assert.equal(typeof once.stdout, "string");
    const report = await auditWithRetry({ cwd: folder, retries: 0 });
    assert.ok(report.error, "the report is npm's own error, passed back to be judged once");
  });
});

test("npm-audit-retry, run as a command, passes a report with nothing at the level, and exits 1 for a level it does not know", () => {
  inFolder((folder) => {
    const run = (...args) => spawnSync(process.execPath, [here("./npm-audit-retry.mjs"), ...args], { cwd: folder, encoding: "utf8" });

    const clean = run("--level=high");
    assert.equal(clean.status, 0, clean.stderr);
    assert.match(clean.stdout, /^\[npm-audit\] No vulnerabilities at or above "high"\./);

    const unknown = run("--level=bogus");
    assert.equal(unknown.status, 1);
    assert.match(unknown.stderr, /^\[npm-audit\] Error: Unknown audit level: bogus/);

    put(folder, "none.json", "{}");
    put(folder, "listed.json", JSON.stringify({ exceptions: [] }));
    for (const file of ["none.json", "listed.json"]) {
      const excused = run("--level=high", `--exceptions=${file}`);
      assert.equal(excused.status, 0, excused.stderr);
      assert.match(excused.stdout, /covered by a current exception/);
    }
  });
});

test("a report with no metadata is read as having no vulnerabilities, and a finding below the threshold is only noted", async () => {
  const lines = [];
  const log = (line) => lines.push(line);
  assert.equal(await auditMain(["--level=high"], { audit: async () => ({}), log, error: log }), 0);
  assert.equal(await auditMain(["--level=high"], { audit: async () => ({ metadata: { vulnerabilities: { low: 2 } } }), log, error: log }), 0);
  assert.match(lines.at(-1), /\(2 low below threshold\)/);
  assert.equal(await auditMain(["--level=low"], { audit: async () => ({ metadata: { vulnerabilities: { low: 2 } } }), log, error: log }), 1);
  assert.match(lines.at(-1), /Found vulnerabilities at or above "low": 2 low\./);
});

test("npm audit is run through the command shell on Windows and directly elsewhere, and a run that printed nothing has empty text", () => {
  const seen = [];
  const spawn = (command, args) => (seen.push([command, ...args]), { status: 1 });
  assert.deepEqual(runNpmAuditOnce("/web", { platform: "linux", spawn }), { status: 1, stdout: "", stderr: "" });
  runNpmAuditOnce("/web", { platform: "win32", spawn });
  assert.deepEqual(seen[0], ["npm", "audit", "--json"]);
  assert.deepEqual(seen[1].slice(1), ["/d", "/s", "/c", "npm audit --json"]);
});

test("between attempts the retry waits on a timer that doubles, 3 s at first", async (context) => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  let calls = 0;
  const run = () => ((calls += 1), calls < 3 ? { stdout: "npm ERR! network", stderr: "" } : { stdout: "{}", stderr: "" });
  const lines = [];
  context.mock.method(console, "log", (line) => lines.push(line));
  const audit = auditWithRetry({ cwd: ".", run });
  for (const delay of [3000, 6000]) {
    await Promise.resolve();
    context.mock.timers.tick(delay);
    await Promise.resolve();
  }
  assert.deepEqual(await audit, {});
  assert.equal(calls, 3);
});

test("a ci-passed run whose change detection published no outputs is judged on the flags it lacks", () => {
  assert.match(verdict({ ...needsFor(), changes: { result: "success" } }).join("\n"), /changes published no usable run_server_linux/);
});

test("a package with no advisory list is skipped and a missing report is clean", () => {
  assert.deepEqual(unapprovedAdvisories({ vulnerabilities: { bare: {} } }), []);
  assert.deepEqual(unapprovedAdvisories(undefined), []);
});

// --- the shipped-files comparison ---

test("changes that cannot be read are reported as such, not as a pass", () => {
  const git = (args) => {
    if (args[0] === "merge-base") return "";
    throw new Error("fatal: bad object\nmore");
  };
  assert.deepEqual(sameShippedFiles("a", "b", git), { ok: false, reason: "the changes between a and b could not be read (fatal: bad object)", shipped: [] });
});

test("shipped-paths, run as a command in a repository, compares two commits and exits 0 or 1 with the reason", () => {
  const { git, commit } = repository();
  const first = commit({ "docs/a.md": "a", "apps/server/src/Weir.Core/Plan.cs": "a" }, "first");
  const docs = commit({ "docs/a.md": "b" }, "docs");
  const code = commit({ "apps/server/src/Weir.Core/Plan.cs": "b" }, "code");
  const root = git(["rev-parse", "--show-toplevel"]).trim();
  const run = (...args) => spawnSync(process.execPath, [here("./release/shipped-paths.mjs"), ...args], { cwd: root, encoding: "utf8" });

  const refused = run("--base", first);
  assert.equal(refused.status, 2);
  assert.match(refused.stderr, /^Usage: node scripts\/release\/shipped-paths\.mjs --base <sha> --head <sha>/);
  const same = run("--base", first, "--head", docs);
  assert.equal(same.status, 0, same.stderr);
  assert.match(same.stdout, /^Same shipped files: only tests, scripts, docs or CI files changed \(1 file\)\./);
  const different = run("--base", first, "--head", code);
  assert.equal(different.status, 1);
  assert.match(different.stdout, /^Different shipped files: 1 file that ships changed: apps\/server\/src\/Weir\.Core\/Plan\.cs\./);
});

// --- the mutation inputs ---

test("a target's entry that cannot be read at a commit counts as changed, and one that is in neither list counts as unchanged", () => {
  const target = { target: "core" };
  assert.equal(entryChanged(target, "a", "b", () => { throw new Error("no such commit"); }), true);
  assert.equal(entryChanged(target, "a", "b", () => "[]"), false);
  assert.equal(entryChanged(target, "a", "b", (args) => (args[1].startsWith("a:") ? "[]" : '[{"target":"core","x":1}]')), true);
});

test("the inputs of a .NET target follow its test projects' references, once each, skipping source projects and projects that are gone", () => {
  inFolder((root) => {
    const tests = "apps/server/tests/Weir.Core.Tests";
    put(root, `${tests}/stryker-config.json`, JSON.stringify({ "stryker-config": { "test-projects": ["Weir.Core.Tests.csproj", "../Gone/Gone.csproj"] } }));
    put(
      root,
      `${tests}/Weir.Core.Tests.csproj`,
      '<Project><ItemGroup><ProjectReference Include="..\\Support\\Support.csproj" /><ProjectReference Include="../Support/Support.csproj" /><ProjectReference Include="../../src/Weir.Core/Weir.Core.csproj" /></ItemGroup></Project>',
    );
    put(root, "apps/server/tests/Support/Support.csproj", "<Project />");
    const target = { target: "core", kind: "dotnet", directory: tests, config: "stryker-config.json", restore: "apps/server" };

    const inputs = inputsOf(target, root);

    assert.deepEqual(inputs.mutated, []);
    assert.equal(inputs.tests("apps/server/tests/Support/Helper.cs"), true);
    assert.equal(inputs.tests("apps/server/tests/Gone/Helper.cs"), true, "the folder of a listed project still counts when the project file is gone");
    assert.equal(inputs.tests("apps/server/src/Weir.Core/Plan.cs"), false);
    assert.ok(inputs.setup.includes("apps/server/Directory.Build.props"));
  });
});

test("a web target whose config names no command counts every web test file", () => {
  inFolder((root) => {
    put(root, "apps/web/stryker.config.json", JSON.stringify({ mutate: ["src/lib/**/*.ts", "!src/lib/skip.ts"] }));
    const inputs = inputsOf({ target: "web", kind: "web", directory: "apps/web", config: "stryker.config.json", restore: "apps/web" }, root);
    assert.equal(inputs.tests("apps/web/src/lib/a.test.ts"), true);
    assert.equal(inputs.tests("apps/web/src/lib/a.ts"), false);
    assert.equal(inputs.mutated.length, 1);
  });
});
