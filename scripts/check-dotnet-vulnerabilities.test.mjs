// node --test scripts/check-dotnet-vulnerabilities.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { scan } from "./check-dotnet-vulnerabilities.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-dotnet-vulnerabilities.mjs");
const repo = path.join(path.sep, "repo");

function run(reports, extra = {}) {
  const seen = { log: [], error: [] };
  const out = { log: (line) => seen.log.push(line), error: (line) => seen.error.push(line) };
  const code = scan(Object.keys(reports), { repo, out, list: (target) => reports[target], ...extra });
  return { code, ...seen };
}

const project = (severity, extra = {}) => ({
  path: path.join(repo, "apps", "server", "A.csproj"),
  frameworks: [{ framework: "net10.0", topLevelPackages: [{ id: "Direct", resolvedVersion: "1.0.0", vulnerabilities: [{ severity, advisoryurl: "https://example.com/a" }] }], ...extra }],
});

test("a report with no vulnerable package is clean", () => {
  const result = run({ "a.slnx": JSON.stringify({ projects: [{ path: path.join(repo, "A.csproj"), frameworks: [{ framework: "net10.0" }] }] }) });
  assert.equal(result.code, 0);
  assert.deepEqual(result.log, ["[dotnet-vulnerabilities] No High or Critical advisories (0 lower-severity finding(s))."]);
  assert.equal(run({ "a.slnx": "{}" }).code, 0);
});

test("a High or Critical advisory fails, direct or transitive, and names the package and the project", () => {
  for (const severity of ["High", "CRITICAL"]) {
    const result = run({ "a.slnx": JSON.stringify({ projects: [project(severity)] }) });
    assert.equal(result.code, 1);
    assert.match(result.error[0], new RegExp(`FAIL .*A\\.csproj \\(net10\\.0\\): Direct 1\\.0\\.0 - ${severity.toLowerCase()} - https://example.com/a`));
  }
  const transitive = project("low", { transitivePackages: [{ id: "Deep", resolvedVersion: "2.0.0", vulnerabilities: [{ severity: "High", advisoryurl: "u" }] }] });
  const result = run({ "a.slnx": JSON.stringify({ projects: [transitive] }) });
  assert.equal(result.code, 1);
  assert.match(result.error.join("\n"), /Deep 2\.0\.0 - high/);
  assert.match(result.error.at(-1), /1 High\/Critical advisory finding\(s\) or scan error\(s\)/);
});

test("lower severities are printed and counted but do not fail", () => {
  const unknown = { projects: [{ path: path.join(repo, "B.csproj"), frameworks: [{ framework: "net10.0", topLevelPackages: [{ id: "P", resolvedVersion: "1", vulnerabilities: [{ advisoryurl: "u" }] }] }] }] };
  const result = run({ "a.slnx": JSON.stringify({ projects: [project("Moderate")] }), "b.slnx": JSON.stringify(unknown) });
  assert.equal(result.code, 0);
  assert.match(result.log[0], /note .*Direct 1\.0\.0 - moderate/);
  assert.match(result.log[1], /note B\.csproj \(net10\.0\): P 1 - unknown/);
  assert.match(result.log[2], /\(2 lower-severity finding\(s\)\)/);
});

test("a problem the scan reports as an error fails it, and any other level is only printed", () => {
  const errored = run({ "a.slnx": JSON.stringify({ problems: [{ level: "Error", text: "source unreachable" }] }) });
  assert.equal(errored.code, 1);
  assert.equal(errored.error[0], "[dotnet-vulnerabilities] a.slnx: Error: source unreachable");
  const warned = run({ "a.slnx": JSON.stringify({ problems: [{ level: "warning", text: "slow" }, { text: "odd" }] }) });
  assert.equal(warned.code, 0);
  assert.deepEqual(warned.error, ["[dotnet-vulnerabilities] a.slnx: warning: slow", "[dotnet-vulnerabilities] a.slnx: problem: odd"]);
});

test("a scan that cannot run stops with 2 and shows what dotnet said", () => {
  const failed = { stdout: "out text", stderr: "err text" };
  const result = run({ "a.slnx": "", "b.slnx": "" }, {
    list: () => {
      throw Object.assign(new Error("boom"), failed);
    },
  });
  assert.equal(result.code, 2);
  assert.deepEqual(result.error, ["[dotnet-vulnerabilities] dotnet list failed for a.slnx:", "out text", "err text"]);
  const bare = run({ "a.slnx": "" }, {
    list: () => {
      throw new Error("no dotnet");
    },
  });
  assert.deepEqual(bare.error.slice(1), ["", "no dotnet"]);
});

test("run as a command with no target it prints the usage and exits 2", () => {
  const result = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /usage: node scripts\/check-dotnet-vulnerabilities\.mjs/);
});
