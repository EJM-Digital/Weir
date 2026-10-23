// node --test scripts/check-npm-audit-main.test.mjs
// The command around the nightly audit: how it runs npm, what it prints and its exit code (npm itself is replaced).
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { blockingAdvisories, main, npmAudit } from "./check-npm-audit.mjs";

const script = fileURLToPath(new URL("./check-npm-audit.mjs", import.meta.url));
const highAdvisory = { name: "braces", severity: "high", title: "braces", url: "https://github.com/advisories/GHSA-vfj7-8cjw-p6xm", range: "*" };
const blocking = { vulnerabilities: { braces: { via: [highAdvisory], fixAvailable: false } } };
const exception = { folder: "docs-site", advisory: "GHSA-vfj7-8cjw-p6xm", expires: "2026-11-14" };

test("a report with no vulnerabilities, a package with no via list and an advisory with no url are all handled", () => {
  assert.deepEqual(blockingAdvisories({}), []);
  assert.deepEqual(blockingAdvisories({ vulnerabilities: { bare: {} } }), []);
  const [found] = blockingAdvisories({
    vulnerabilities: { odd: { via: [{ name: "odd", severity: "critical", range: "<2", title: "t" }], fixAvailable: true } },
  });
  assert.equal(found.id, "odd@<2");
  assert.equal(found.fixAvailable, true);
});

test("npmAudit returns npm's report whether npm exits clean or not", () => {
  const asked = [];
  assert.equal(npmAudit("/web", (command, options) => (asked.push([command, options.cwd]), "{}")), "{}");
  assert.deepEqual(asked, [["npm audit --omit=dev --json", "/web"]]);
  const failing = () => {
    throw Object.assign(new Error("exit 1"), { stdout: '{"found":1}' });
  };
  assert.equal(npmAudit("/web", failing), '{"found":1}');
});

test("main audits each folder, prints what blocks and what is excused, and returns 1 on a problem", () => {
  const lines = [];
  const log = (line) => lines.push(line);
  const exceptions = [exception, { folder: "other", advisory: "GHSA-other", expires: "2099-01-01" }];
  const audit = (path) => JSON.stringify(path.endsWith("docs-site") ? blocking : { vulnerabilities: {} });

  assert.equal(main(["docs-site", "apps/web"], { root: "/repo", audit, exceptions, today: "2026-10-14", log }), 0);
  assert.deepEqual(lines, ["docs-site: nothing blocking (excused: GHSA-vfj7-8cjw-p6xm)", "apps/web: nothing blocking"]);

  lines.length = 0;
  assert.equal(main(["docs-site"], { root: "/repo", audit, exceptions: [], today: "2026-10-14", log }), 1);
  assert.match(lines.join("\n"), /docs-site: 1 blocking\n {2}GHSA-vfj7-8cjw-p6xm \(high, braces\)/);
});

test("run as a command with no folder it reads the exceptions file and passes without touching npm", () => {
  const run = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
});
