// node --test scripts/check-coverage-register-bare.test.mjs
// The coverage-register check on registers and rows that leave their optional parts out, and on a repository with no inventory.
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";

import { issueProblems, main, mapInventory, missingProofs, refusedClaims, REGISTER, statisticsReport, stubFor, totals, validateShape } from "./check-coverage-register.mjs";

const made = [];
after(() => made.forEach((folder) => rmSync(folder, { recursive: true, force: true })));

const bare = { id: "bare", area: "api", behaviour: "A bare row", kind: "API route", severity: "medium", touches: "none", required: "inproc" };

test("a register with no areas and no rows has nothing to say, and a row with no proofs, partial proofs or covers is read as having none", () => {
  assert.deepEqual(validateShape({}), []);
  assert.deepEqual(validateShape({ areas: { api: {} }, rows: [bare] }), [`row bare: proofs must be a list (empty when it is a gap)`]);
  assert.deepEqual(mapInventory({ rows: [bare] }, [{ key: "route:GET /x" }]), { unmapped: [{ key: "route:GET /x" }], stale: [] });
  assert.deepEqual(missingProofs({ rows: [bare] }, {}), []);
  assert.deepEqual(refusedClaims({ rows: [bare] }, {}), []);
  assert.deepEqual(issueProblems({ rows: [bare] }), [`row bare has neither a proof nor an issue that will prove it: A bare row`]);
});

test("the statistics count a row with no proofs as a gap, and name an area the register does not describe by nothing", () => {
  const register = { areas: {}, rows: [bare] };
  const t = totals(register, {});
  assert.equal(t.all.gaps, 1);
  assert.equal(t.byArea.api.level.none, 1);
  assert.match(statisticsReport(register, [], t), /\| api \|  \| 1 \| 0 \| 1 \|/);
});

test("a stub for an item with no label is described by its key", () => {
  assert.equal(stubFor({ kind: "route", key: "route:GET /api/v1/things" }).behaviour, "<one plain sentence about route:GET /api/v1/things>");
  assert.equal(stubFor({ kind: "route", key: "route:GET /api/v1/things", label: "the things list" }).behaviour, "<one plain sentence about the things list>");
});

test("--list-gaps lists the gaps by severity, then by id", async () => {
  const root = mkdtempSync(path.join(tmpdir(), "weir-register-"));
  made.push(root);
  mkdirSync(path.dirname(path.join(root, REGISTER)), { recursive: true });
  const gap = (id, severity) => ({ ...bare, id, severity, proofs: [], issue: 7 });
  writeFileSync(path.join(root, REGISTER), JSON.stringify({ areas: { api: { name: "API" } }, rows: [gap("b.two", "medium"), gap("a.one", "medium"), gap("c.high", "high")] }));
  const lines = [];
  assert.equal(await main(["--root", root, "--list-gaps"], { log: (line) => lines.push(line), error: (line) => assert.fail(line) }), 0);
  assert.deepEqual(lines.map((line) => line.split(/\s+/)[3]), ["c.high", "a.one", "b.two"]);
});

test("without --root the check reads this repository's register", async () => {
  const lines = [];
  assert.equal(await main(["--json"], { log: (line) => lines.push(line), error: (line) => assert.fail(line) }), 0);
  assert.ok(JSON.parse(lines.join("\n")).rows.rows > 0);
});
