// node --test scripts/dotnet-and-coverage-reports-commands.test.mjs
// The command lines of the NuGet vulnerability gate and the coverage-report aligner, and the report shapes they have to read.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { checkoutOf } from "./align-coverage-reports.mjs";
import { dotnetList, main, scan } from "./check-dotnet-vulnerabilities.mjs";

const alignScript = fileURLToPath(new URL("./align-coverage-reports.mjs", import.meta.url));

function inFolder(body) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-reports-"));
  try {
    return body(folder);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

test("dotnetList asks dotnet for the vulnerable packages of the target, transitive ones included, as JSON", () => {
  const seen = [];
  const answer = dotnetList("apps/server/Weir.slnx", (program, args, options) => (seen.push({ program, args, encoding: options.encoding }), "{}"));
  assert.equal(answer, "{}");
  assert.deepEqual(seen, [
    { program: "dotnet", args: ["list", "apps/server/Weir.slnx", "package", "--vulnerable", "--include-transitive", "--format", "json"], encoding: "utf8" },
  ]);
});

test("a framework with no packages, a package with no advisories and an advisory with no severity are all read", () => {
  const report = {
    projects: [
      {
        path: "/repo/a/A.csproj",
        frameworks: [
          { framework: "net10.0" },
          { framework: "net9.0", topLevelPackages: [{ id: "Clean", resolvedVersion: "1.0.0" }, { id: "Odd", resolvedVersion: "2.0.0", vulnerabilities: [{ advisoryurl: "https://x/1" }] }] },
        ],
      },
      { path: "/repo/b/B.csproj" },
    ],
  };
  const lines = { log: [], error: [] };
  const code = scan(["t.slnx"], { list: () => JSON.stringify(report), repo: "/repo", out: { log: (line) => lines.log.push(line), error: (line) => lines.error.push(line) } });
  assert.equal(code, 0);
  assert.match(lines.log[0], /note .*Odd 2\.0\.0 - unknown - https:\/\/x\/1/);
  assert.equal(lines.error.length, 0);
});

test("main asks for a target, and otherwise returns what the scan returns", () => {
  const errors = [];
  assert.equal(main([], { scanTargets: () => assert.fail("no targets"), error: (line) => errors.push(line) }), 2);
  assert.match(errors[0], /^usage: node scripts\/check-dotnet-vulnerabilities\.mjs/);
  assert.equal(main(["a.slnx"], { scanTargets: (targets) => (assert.deepEqual(targets, ["a.slnx"]), 1) }), 1);
});

test("a coverage report whose source is not under an apps folder has no checkout to find", () => {
  assert.equal(checkoutOf('<source>/work/elsewhere</source>'), undefined);
});

test("the aligner needs a folder and a checkout, and aligns the reports under the folder", () => {
  const refused = spawnSync(process.execPath, [alignScript, "only-a-folder"], { encoding: "utf8" });
  assert.equal(refused.status, 1);
  assert.match(refused.stderr, /^Usage: align-coverage-reports\.mjs <folder> <checkout>/);

  inFolder((folder) => {
    mkdirSync(path.join(folder, "run"));
    const report = path.join(folder, "run", "coverage.cobertura.xml");
    writeFileSync(report, '<coverage><sources><source>C:\\runner\\work\\Weir\\Weir\\apps\\server\\src</source></sources></coverage>');
    const run = spawnSync(process.execPath, [alignScript, folder, "/checkout/Weir"], { encoding: "utf8" });
    assert.equal(run.status, 0, run.stderr);
    assert.equal(run.stdout.trim(), `Aligned 1 coverage file(s) under ${folder} with /checkout/Weir.`);
    assert.match(readFileSync(report, "utf8"), /\/checkout\/Weir\/apps\/server\/src/);
  });
});
