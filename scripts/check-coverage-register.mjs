#!/usr/bin/env node
// The coverage register (docs/testing/coverage-register.json) must account for everything Weir does, and every proof it names must exist.
// It has the same shape as Deluno's register (docs/testing/COVERAGE.md); Weir's proof types follow its own test runners.
//
//   node scripts/check-coverage-register.mjs                       run the checks
//   node scripts/check-coverage-register.mjs --gh                  also fail on a row whose issue GitHub says is not open (needs gh and a token)
//   node scripts/check-coverage-register.mjs --open-issues 975,976 the same, with the open issues given (for tests and offline runs)
//   node scripts/check-coverage-register.mjs --base origin/main    also fail when there are more gaps than in the register at that git ref (the ratchet)
//   node scripts/check-coverage-register.mjs --strict              also fail on any gap (a row whose proofs do not reach its required level)
//   node scripts/check-coverage-register.mjs --stubs               print a register row stub for every inventory item without a row
//   node scripts/check-coverage-register.mjs --report [file]       write the statistics as markdown (default artifacts/coverage-register.md, not committed)
//   node scripts/check-coverage-register.mjs --list-gaps           print every gap, high first
//   node scripts/check-coverage-register.mjs --json                print the totals as JSON
//
// It fails, naming the item, when:
//   (1) the code has a route, job kind, hosted service, periodic task, setting, event type, web route, web control, tray menu item or
//       golden-VM scenario that no row names in `covers` (scripts/coverage/inventory.mjs reads them from source);
//   (2) a row covers something the code no longer has;
//   (3) a proof names a file or test that does not exist, or claims a type the file does not have (a real-fs claim the check cannot verify
//       is not one of these: it is refused, counts as inproc and is reported, and --strict fails on it);
//   (4) a row has no proof and no `issue`;
//   (5) the number of gaps is above the register's count at --base (the ratchet: the gaps only go down, measured against the branch a change
//       goes into, so no number is kept in the file for parallel changes to fight over) unless the register carries a `gapIncrease` entry
//       for exactly that rise, or a row names an issue that is not open (--gh, --open-issues);
//   (6) the register is malformed.
// The statistics (totals by severity, area and proof level) are not kept in the repository: they go to $GITHUB_STEP_SUMMARY when that is
// set, and to the --report file.
// real-fs: a backend test that runs Weir's real code against real files in a real temp folder (real hard links, moves, deletes, permissions)
// or a real SQLite database file, with no fake or in-memory file system or store. The check decides it statically (the class setup or the
// test creates the temp folder or database, does file or database work, and names no fake). It satisfies "real" for a row that touches disk
// or install, unless the row is marked "vm": true, a row only a real install can prove (NTFS specifics, network shares, cross-volume moves,
// the installer, the update, the tray, Windows service mode): that row reaches "real" by a scenario alone.
// A gap is a row whose proofs do not reach its required level (`required` is derived from `severity` and `touches`), or that names an
// `issue`: a row proven in part keeps its proofs and names the issue that will prove the rest. Gaps that are not
// yet on an issue are counted and reported. --strict fails on every gap; Weir turns it on once the gaps are closed.
import { spawnSync } from "node:child_process";
import { appendFileSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { CONTAINER_SMOKE_FILE, PACKAGE_SMOKE_FILE, REAL_FS_FILE, SCENARIO_FILE, TestIndex, read } from "./coverage/testindex.mjs";
import { buildInventory, countsOf } from "./coverage/inventory.mjs";

export const REGISTER = "docs/testing/coverage-register.json";
export const PROOF_TYPES = ["xunit", "contract", "e2e", "vitest", "node", "scenario", "recording-replay", "real-fs", "container-smoke", "package-smoke"];
export const KINDS = ["screen action", "state", "API route", "job", "integration-operation", "failure-mode", "migration", "install-update", "tray", "setting", "event", "file-lifecycle"];
export const SEVERITIES = ["high", "medium", "low"];
export const TOUCHES = ["other-app", "disk", "install", "none"];
export const REAL_TYPES = new Set(["scenario", "recording-replay"]);
/** real-fs satisfies "real" only for these rows, and never for a row marked "vm": true. */
export const REAL_FS_TOUCHES = new Set(["disk", "install"]);
/** Areas whose rows are behaviours of a screen: an e2e test against the real host may reach "real" for these. */
export const SCREEN_AREAS = new Set(["web-shell", "web-dashboard", "web-activity-library", "web-setup-workflows-rules", "web-setup-connections-performance", "web-system", "web-routes"]);
const LEVEL_NAMES = ["none", "inproc", "real"];

/** A high row that touches another app, files on disk, or install/update/tray needs real proof; every other row at least inproc. */
export const requiredFor = (row) => (row.severity === "high" && row.touches !== "none" ? "real" : "inproc");
export const requiredLevel = (row) => (requiredFor(row) === "real" ? 2 : 1);

/** Is this e2e proof stubbed? Decided statically; what cannot be decided counts as stubbed. */
export const e2eState = (proof, index) => (index && typeof index.stubInfo === "function" ? index.stubInfo(proof.file, proof.name) : { stubbed: true, decided: false });
/** The proof types the check verifies from the text of the test, step or script they name, and the index method that judges each. */
export const VERIFIED_TYPES = { "real-fs": "realFs", "container-smoke": "containerSmoke" };
/** Is this claim one the check can verify from the text? */
export const claimVerified = (proof, index) => {
  const judge = VERIFIED_TYPES[proof.type];
  return !!index && typeof index[judge] === "function" && index[judge](proof.file, proof.name).real;
};
/** Does a verified-type proof count for this row? Only for a disk or install row that is not "vm", and only when the check verifies the claim. */
export const claimCounts = (proof, row, index) => proof.type in VERIFIED_TYPES && !!row && row.vm !== true && REAL_FS_TOUCHES.has(row.touches) && claimVerified(proof, index);
/**
 * scenario and recording-replay are real; real-fs and container-smoke are real for a verified disk or install row that is not "vm"; an e2e
 * test is real only for a screen behaviour and only when its requests are not intercepted. A "vm" row (only a real install can prove it) is
 * real by a scenario alone.
 */
export function proofLevel(proof, row, index) {
  if (row && row.vm === true) return proof.type === "scenario" ? 2 : 1;
  if (proof.type in VERIFIED_TYPES) return claimCounts(proof, row, index) ? 2 : 1;
  if (REAL_TYPES.has(proof.type)) return 2;
  if (proof.type === "e2e" && row && SCREEN_AREAS.has(row.area) && !e2eState(proof, index).stubbed) return 2;
  return 1;
}
export const rowLevel = (row, index) => (row.proofs ?? []).reduce((best, p) => Math.max(best, proofLevel(p, row, index)), 0);
/** A gap is a row whose proofs do not reach its required level, or that still names the issue that will finish proving it. */
export const isGap = (row, index) => row.issue != null || rowLevel(row, index) < requiredLevel(row);

/** A `gapIncrease` entry says which rise it explains and why: { "from": <gaps on the base>, "to": <gaps now>, "reason": "..." }. */
export function gapIncreaseShape(entry) {
  if (entry === undefined) return [];
  const whole = (n) => Number.isInteger(n) && n >= 0;
  if (!entry || !whole(entry.from) || !whole(entry.to) || entry.to <= entry.from || typeof entry.reason !== "string" || !entry.reason.trim()) {
    return ['"gapIncrease" must be { "from": <gaps on the base branch>, "to": <gaps now, more than from>, "reason": "<why the gaps go up>" }'];
  }
  return [];
}

/** The register's own shape: ids, enums, and the rule deriving the required level from severity and what the row touches. */
export function validateShape(register) {
  const problems = [];
  const areas = new Set(Object.keys(register.areas ?? {}));
  const ids = new Set();
  if ("gapCeiling" in register) problems.push('"gapCeiling" is not used: the gaps are held to the count on the base branch (--base), so remove it from the register');
  problems.push(...gapIncreaseShape(register.gapIncrease));
  for (const row of register.rows ?? []) {
    const where = `row ${row.id ?? "(no id)"}`;
    if (!row.id || typeof row.id !== "string") problems.push(`${where}: needs an id`);
    else if (ids.has(row.id)) problems.push(`${where}: id used twice`);
    else ids.add(row.id);
    if (!areas.has(row.area)) problems.push(`${where}: area "${row.area}" is not in the register's areas`);
    if (!row.behaviour || !String(row.behaviour).trim()) problems.push(`${where}: needs a behaviour`);
    if (!KINDS.includes(row.kind)) problems.push(`${where}: kind "${row.kind}" is not one of ${KINDS.join(", ")}`);
    if (!SEVERITIES.includes(row.severity)) problems.push(`${where}: severity "${row.severity}" is not high, medium or low`);
    if (!TOUCHES.includes(row.touches)) problems.push(`${where}: touches "${row.touches}" is not one of ${TOUCHES.join(", ")}`);
    else if (row.required !== requiredFor(row)) problems.push(`${where}: required must be "${requiredFor(row)}" for a ${row.severity} row that touches ${row.touches}`);
    if (row.vm != null && row.vm !== true) problems.push(`${where}: vm may only be true (leave it out otherwise)`);
    if (row.vm === true && !REAL_FS_TOUCHES.has(row.touches)) problems.push(`${where}: vm is for a row that touches disk or install, not ${row.touches}`);
    if (row.issue != null && !(Number.isInteger(row.issue) && row.issue > 0)) problems.push(`${where}: issue must be the number of an open coverage issue`);
    if (!Array.isArray(row.proofs)) problems.push(`${where}: proofs must be a list (empty when it is a gap)`);
    if (row.covers !== undefined && !Array.isArray(row.covers)) problems.push(`${where}: covers must be a list`);
    for (const p of [...(row.proofs ?? []), ...(row.partial ?? [])]) {
      if (!PROOF_TYPES.includes(p.type)) problems.push(`${where}: proof type "${p.type}" is not one of ${PROOF_TYPES.join(", ")}`);
      if (!p.file || !p.name) problems.push(`${where}: a proof needs a file and a name`);
    }
  }
  return problems;
}

/** (1) inventory items no row covers, and (2) covers naming an item that is gone. */
export function mapInventory(register, inventory) {
  const keys = new Set(inventory.map((i) => i.key));
  const covered = new Set();
  for (const row of register.rows) for (const k of row.covers ?? []) covered.add(k);
  const unmapped = inventory.filter((i) => !covered.has(i.key));
  const stale = [];
  for (const row of register.rows) for (const k of row.covers ?? []) if (!keys.has(k)) stale.push({ row: row.id, key: k });
  return { unmapped, stale };
}

/** A proof's type must be true of its file. */
function typeMismatch(p, index) {
  switch (p.type) {
    case "scenario":
      return SCENARIO_FILE.test(p.file) ? null : `a scenario proof must be a script in scripts/scenarios or a module in scripts/scenarios-ci, not ${p.file}`;
    case "e2e":
      return /^apps\/server\/tests\/Weir\.E2E\.Tests\/.+\.cs$/.test(p.file) ? null : `an e2e proof must be a test in Weir.E2E.Tests, not ${p.file}`;
    case "vitest":
      return /\.test\.tsx?$/.test(p.file) ? null : `a vitest proof must be a .test.ts(x) file, not ${p.file}`;
    case "node":
      return /\.test\.mjs$/.test(p.file) ? null : `a node proof must be a .test.mjs file, not ${p.file}`;
    case "contract":
      return p.file.startsWith("apps/server/tests/Weir.Contract.Tests/") ? null : `a contract proof must be a test in Weir.Contract.Tests, not ${p.file}`;
    case "package-smoke":
      return PACKAGE_SMOKE_FILE.test(p.file) || p.file.startsWith("apps/server/tools/Weir.LiveAudit/") ? null : `a package-smoke proof must be a Windows package smoke or the live audit, not ${p.file}`;
    case "real-fs":
      return REAL_FS_FILE.test(p.file) ? null : `a real-fs proof must be a C# test of the server or the tray, not ${p.file}`;
    case "container-smoke":
      return CONTAINER_SMOKE_FILE.test(p.file) ? null : `a container-smoke proof must be a packaging workflow or a container script in scripts, not ${p.file}`;
    case "recording-replay":
      return index.replaysRecording(p.file, p.name) ? null : `"${p.name}" in ${p.file} does not load one of Deluno's recordings`;
    default:
      return /\.ps1$/.test(p.file) ? `an xunit proof cannot be a PowerShell script (${p.file}); use type "scenario"` : null;
  }
}

/** (3) a proof whose file or test is gone, or whose type is not true of it. */
export function missingProofs(register, index) {
  const bad = [];
  for (const row of register.rows) {
    for (const [list, label] of [[row.proofs ?? [], "proof"], [row.partial ?? [], "partial proof"]]) {
      for (const p of list) {
        if (!index.has(p.file)) {
          bad.push({ row: row.id, label, proof: p, why: `the file ${p.file} does not exist` });
          continue;
        }
        const mismatch = typeMismatch(p, index);
        if (mismatch) bad.push({ row: row.id, label, proof: p, why: mismatch });
        else if (!index.hasName(p.file, p.name)) bad.push({ row: row.id, label, proof: p, why: `no test, group or class "${p.name}" in ${p.file}` });
      }
    }
  }
  return bad;
}

/** A real-fs or container-smoke claim the check could not verify from the text: refused, so it counts as inproc. */
export function refusedClaims(register, index) {
  const refused = [];
  const kinds = { "real-fs": REAL_FS_FILE, "container-smoke": CONTAINER_SMOKE_FILE };
  for (const row of register.rows) {
    for (const p of row.proofs ?? []) {
      if (!(p.type in VERIFIED_TYPES) || !index.has(p.file) || !kinds[p.type].test(p.file) || !index.hasName(p.file, p.name)) continue;
      const verdict = index[VERIFIED_TYPES[p.type]](p.file, p.name);
      if (!verdict.real) refused.push({ row: row.id, proof: p, why: verdict.why });
    }
  }
  return refused;
}

/** (4) rows with neither a proof nor an issue. */
export function issueProblems(register) {
  const problems = [];
  for (const row of register.rows) {
    if ((row.proofs ?? []).length === 0 && row.issue == null) problems.push(`row ${row.id} has neither a proof nor an issue that will prove it: ${row.behaviour.slice(0, 80)}`);
  }
  return problems;
}

/**
 * (5) the ratchet: the gaps may only go down from the count on the base branch. A rise is allowed only with a `gapIncrease` entry whose
 * "from" is that count, whose "to" is the count now and whose reason says why (the same rule #976 sets for a lowered coverage floor).
 * With no base to compare with (a first run, or a base without a register) nothing is held back, and a note says so.
 */
export function gapRatchet(register, gapCount, baseGaps) {
  if (baseGaps === null) return { problems: [], notes: ["No register on the base branch to compare with, so the gaps were not held to a count."] };
  if (gapCount <= baseGaps) return { problems: [], notes: [] };
  const entry = register.gapIncrease;
  const reason = typeof entry?.reason === "string" ? entry.reason.trim() : "";
  if (entry && entry.from === baseGaps && entry.to === gapCount && reason) {
    return { problems: [], notes: [`The gaps rise from ${baseGaps} to ${gapCount}: ${reason}`] };
  }
  const mismatch = entry && (entry.from !== baseGaps || entry.to !== gapCount) ? ` The "gapIncrease" entry says ${entry.from} to ${entry.to}, which is not this rise.` : "";
  return {
    problems: [
      `there are ${gapCount} gaps, more than the ${baseGaps} in the register on the base branch: a gap cannot be added; give the new row a proof that reaches its required level. ` +
        `A rise that cannot be avoided is written under "gapIncrease" in ${REGISTER}, in a pull request of its own: { "from": ${baseGaps}, "to": ${gapCount}, "reason": "..." }.${mismatch}`,
    ],
    notes: [],
  };
}

const gapCountOf = (register, index) => register.rows.filter((row) => isGap(row, index)).length;

/** (5) the ratchet, and rows that name an issue that is not open. */
export function gapProblems(register, index, { openIssues = null, baseGaps = null } = {}) {
  const problems = gapRatchet(register, gapCountOf(register, index), baseGaps).problems;
  if (openIssues) {
    for (const row of register.rows) if (row.issue != null && !openIssues.has(row.issue)) problems.push(`row ${row.id} names issue #${row.issue}, which is not open: move the row to the issue that will prove it, or prove it`);
  }
  return problems;
}

export function totals(register, index) {
  const byArea = {};
  const bySeverity = { high: blank(), medium: blank(), low: blank() };
  const byLevel = { none: 0, inproc: 0, real: 0 };
  const all = blank();
  const gaps = [];
  const highByTouches = Object.fromEntries(TOUCHES.map((t) => [t, { rows: 0, gaps: 0 }]));
  const e2e = { proofs: 0, unstubbed: 0, stubbed: 0, undecided: 0 };
  const realFs = { claims: 0, counted: 0, refused: 0, noEffect: 0, vmRows: 0, vmRowGaps: 0 };
  const container = { claims: 0, counted: 0, refused: 0, noEffect: 0 };
  function blank() {
    return { rows: 0, proven: 0, gaps: 0 };
  }
  for (const row of register.rows) {
    const gap = isGap(row, index);
    if (row.severity === "high" && highByTouches[row.touches]) {
      highByTouches[row.touches].rows++;
      if (gap) highByTouches[row.touches].gaps++;
    }
    if (row.vm === true) {
      realFs.vmRows++;
      if (gap) realFs.vmRowGaps++;
    }
    for (const pr of row.proofs ?? []) {
      const tally = pr.type === "real-fs" ? realFs : pr.type === "container-smoke" ? container : null;
      if (!tally) continue;
      tally.claims++;
      if (!claimVerified(pr, index)) tally.refused++;
      else if (claimCounts(pr, row, index)) tally.counted++;
      else tally.noEffect++;
    }
    for (const pr of row.proofs ?? []) {
      if (pr.type !== "e2e") continue;
      const st = e2eState(pr, index);
      e2e.proofs++;
      if (!st.stubbed) e2e.unstubbed++;
      else if (st.decided) e2e.stubbed++;
      else e2e.undecided++;
    }
    const a = (byArea[row.area] ??= { ...blank(), high: 0, highGaps: 0, level: { none: 0, inproc: 0, real: 0 } });
    for (const t of [a, bySeverity[row.severity] ?? blank(), all]) {
      t.rows++;
      if (gap) t.gaps++;
      else t.proven++;
    }
    a.high += row.severity === "high" ? 1 : 0;
    if (gap && row.severity === "high") a.highGaps++;
    const level = LEVEL_NAMES[rowLevel(row, index)];
    a.level[level]++;
    byLevel[level]++;
    if (gap) gaps.push(row);
  }
  const untracked = gaps.filter((g) => g.issue == null);
  return { all, byArea, bySeverity, byLevel, highByTouches, e2e, realFs, container, gaps, untracked, highGaps: gaps.filter((g) => g.severity === "high").length };
}

export function evaluate({ register, inventory, index, openIssues = null, baseGaps = null }) {
  return {
    shape: validateShape(register),
    ...mapInventory(register, inventory),
    missing: missingProofs(register, index),
    refused: refusedClaims(register, index),
    issueProblems: issueProblems(register),
    gapProblems: gapProblems(register, index, { openIssues, baseGaps }),
    gapNotes: gapRatchet(register, gapCountOf(register, index), baseGaps).notes,
    totals: totals(register, index),
  };
}

// ------------------------------------------------------------------------------------------------------------ the report

export const REPORT_FILE = "artifacts/coverage-register.md";

/** The statistics as markdown, for the CI job summary and the --report file. They are not kept in the repository. */
export function statisticsReport(register, inventory, t) {
  const pct = (a, b) => (b ? `${Math.round((a / b) * 100)}%` : "-");
  const lines = [];
  lines.push("### Coverage register", "", "Generated by `node scripts/check-coverage-register.mjs` from `coverage-register.json`; the rules are in docs/testing/COVERAGE.md.", "");
  lines.push(`**${t.all.rows}** behaviours. **${t.all.proven}** reach their required proof level (${pct(t.all.proven, t.all.rows)}); **${t.all.gaps}** are gaps, **${t.highGaps}** of them high; **${t.untracked.length}** gaps are not on an issue yet.`, "");
  lines.push("### By severity", "", "| Severity | Rows | Reach their level | Gaps |", "| --- | ---: | ---: | ---: |");
  for (const s of SEVERITIES) lines.push(`| ${s} | ${t.bySeverity[s].rows} | ${t.bySeverity[s].proven} | ${t.bySeverity[s].gaps} |`);
  lines.push("", "### High rows by what they touch (these need `real`, except `none`, which needs `inproc`)", "", "| Touches | High rows | High gaps |", "| --- | ---: | ---: |");
  for (const k of TOUCHES) lines.push(`| ${k} | ${t.highByTouches[k].rows} | ${t.highByTouches[k].gaps} |`);
  lines.push("", "### By best proof a row has", "", "| Best proof | Rows |", "| --- | ---: |");
  for (const l of LEVEL_NAMES) lines.push(`| ${l === "none" ? "none (a gap)" : l} | ${t.byLevel[l]} |`);
  lines.push("", `E2E proofs: **${t.e2e.proofs}**; unstubbed (can reach \`real\` for a screen behaviour) **${t.e2e.unstubbed}**; stubbed **${t.e2e.stubbed}**; not decidable statically, counted as stubbed **${t.e2e.undecided}**.`);
  lines.push("", `Real-fs proofs: **${t.realFs.claims}** claimed; **${t.realFs.counted}** verified and counted as real; **${t.realFs.refused}** refused (the check could not verify a real temp folder or database file with no fake, so they count as inproc); **${t.realFs.noEffect}** verified but without effect (the row is "vm", or does not touch disk or install). **${t.realFs.vmRows}** rows are "vm" (only a real install can prove them, so they need a scenario); **${t.realFs.vmRowGaps}** of them are still gaps.`);
  lines.push("", `Container-smoke proofs: **${t.container.claims}** claimed; **${t.container.counted}** verified and counted as real; **${t.container.refused}** refused (the named step or script does not start the container image, so they count as inproc); **${t.container.noEffect}** verified but without effect (the row is "vm", or does not touch disk or install).`);
  lines.push("", "### By area", "", "| Area | What | Rows | Reach their level | Gaps | High rows | High gaps | No proof | Inproc | Real |", "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
  for (const [area, a] of Object.entries(t.byArea).sort()) {
    lines.push(`| ${area} | ${register.areas[area]?.name ?? ""} | ${a.rows} | ${a.proven} | ${a.gaps} | ${a.high} | ${a.highGaps} | ${a.level.none} | ${a.level.inproc} | ${a.level.real} |`);
  }
  const byIssue = new Map();
  for (const row of register.rows) {
    if (row.issue == null) continue;
    const e = byIssue.get(row.issue) ?? { rows: 0, high: 0 };
    e.rows++;
    if (row.severity === "high") e.high++;
    byIssue.set(row.issue, e);
  }
  lines.push("", "### Gaps with an open issue", "");
  if (byIssue.size === 0) lines.push("No row names an issue yet.");
  else {
    lines.push("| Issue | Rows | High rows |", "| --- | ---: | ---: |");
    for (const [issue, e] of [...byIssue].sort((a, b) => a[0] - b[0])) lines.push(`| #${issue} | ${e.rows} | ${e.high} |`);
  }
  lines.push("", "### Inventories (generated from source by `scripts/coverage/inventory.mjs`)", "", "| Kind | Items |", "| --- | ---: |");
  for (const [k, n] of Object.entries(countsOf(inventory))) lines.push(`| ${k} | ${n} |`);
  return lines.join("\n");
}

/** What the CI job summary shows: the statistics, then what the ratchet noted (a reasoned rise, or no base to compare with). */
export function jobSummary(register, inventory, t, notes) {
  const lines = [statisticsReport(register, inventory, t)];
  for (const note of notes) lines.push("", `- ${note}`);
  return `${lines.join("\n")}\n`;
}

export function writeReport(root, file, register, inventory, t) {
  const target = resolve(root, file);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, `${statisticsReport(register, inventory, t)}\n`);
  return target;
}

const STUB_KIND = { route: "API route", job: "job", service: "job", task: "job", setting: "setting", event: "event", webroute: "screen action", action: "screen action", tray: "tray", scenario: "install-update" };
const STUB_AREA = { route: "api", job: "services", service: "services", task: "services", setting: "settings", event: "events", webroute: "web-routes", action: "web-routes", tray: "tray", scenario: "scenarios" };

export const stubFor = (item) => ({
  id: `${STUB_AREA[item.kind]}.new.${item.key.split(":").slice(1).join("-").replace(/[^A-Za-z0-9]+/g, "-").toLowerCase()}`,
  area: STUB_AREA[item.kind],
  behaviour: `<one plain sentence about ${item.label ?? item.key}>`,
  kind: STUB_KIND[item.kind],
  severity: "medium",
  touches: "none",
  required: "inproc",
  proofs: [],
  covers: [item.key],
});

export function openIssuesFromGh(root, spawn = spawnSync) {
  const run = spawn("gh", ["issue", "list", "--label", "testing: coverage", "--state", "open", "--limit", "500", "--json", "number"], { cwd: root, encoding: "utf8" });
  if (run.status !== 0) throw new Error(`gh could not list the open coverage issues: ${(run.stderr || run.error?.message || "").trim()}`);
  return new Set(JSON.parse(run.stdout).map((i) => i.number));
}

/** The gaps in the register at a git ref, judged by the tests in this checkout; null when there is no register there to read. */
export function baseGapCount(root, ref, index) {
  const run = spawnSync("git", ["show", `${ref}:${REGISTER}`], { cwd: root, encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });
  if (run.status !== 0) return null;
  try {
    const base = JSON.parse(run.stdout);
    return Array.isArray(base.rows) ? gapCountOf(base, index) : null;
  } catch {
    return null;
  }
}

/** Checks the register for `args` and prints what it finds; returns the exit code. `askGh` lists the open coverage issues (a test replaces it). */
export async function main(args = process.argv.slice(2), { env = process.env, log = console.log, error = console.error, askGh = openIssuesFromGh } = {}) {
  const valueOf = (flag) => (args.indexOf(flag) >= 0 ? args[args.indexOf(flag) + 1] : undefined);
  const root = resolve(valueOf("--root") ?? join(dirname(fileURLToPath(import.meta.url)), ".."));
  const strict = args.includes("--strict");
  const registerPath = join(root, REGISTER);
  if (!existsSync(registerPath)) {
    error(`${REGISTER} is missing.`);
    return 1;
  }
  const register = JSON.parse(read(registerPath));
  const index = new TestIndex(root);
  const inventory = buildInventory(root);
  let openIssues = null;
  if (valueOf("--open-issues") !== undefined) openIssues = new Set(valueOf("--open-issues").split(",").filter(Boolean).map(Number));
  else if (args.includes("--gh")) openIssues = askGh(root);
  const baseRef = valueOf("--base");
  const baseGaps = baseRef ? baseGapCount(root, baseRef, index) : null;
  const result = evaluate({ register, inventory, index, openIssues, baseGaps });
  const t = result.totals;

  const reportValue = valueOf("--report");
  if (args.includes("--report")) log(`wrote ${writeReport(root, reportValue && !reportValue.startsWith("--") ? reportValue : REPORT_FILE, register, inventory, t)}`);

  if (args.includes("--stubs")) {
    log(JSON.stringify(result.unmapped.map(stubFor), null, 2));
    return 0;
  }
  if (args.includes("--json")) {
    log(JSON.stringify({ rows: t.all, bySeverity: t.bySeverity, highByTouches: t.highByTouches, e2e: t.e2e, realFs: t.realFs, container: t.container, byLevel: t.byLevel, byArea: t.byArea, highGaps: t.highGaps, untracked: t.untracked.length, inventory: countsOf(inventory) }, null, 1));
    return 0;
  }
  if (args.includes("--list-gaps")) {
    const order = { high: 0, medium: 1, low: 2 };
    for (const g of [...t.gaps].sort((a, b) => order[a.severity] - order[b.severity] || a.id.localeCompare(b.id))) log(`${g.severity.padEnd(6)} ${g.touches.padEnd(9)} ${g.issue ? `#${g.issue}`.padEnd(6) : "-".padEnd(6)} ${g.id}  ${g.behaviour.slice(0, 100)}`);
    return 0;
  }

  const shown = (list, f, n = 12) => list.slice(0, n).map(f).join("\n    ") + (list.length > n ? `\n    ... and ${list.length - n} more` : "");
  const fail = [];
  const lines = [`Coverage register: ${register.rows.length} rows, ${inventory.length} inventory items (${Object.entries(countsOf(inventory)).map(([k, n]) => `${n} ${k}`).join(", ")}).`];
  if (result.shape.length) fail.push(`(6) the register is malformed (${result.shape.length}):\n    ${shown(result.shape, (x) => x)}`);
  if (result.unmapped.length) fail.push(`(1) ${result.unmapped.length} item(s) in the code have no row. Add a row naming the key in "covers" (node scripts/check-coverage-register.mjs --stubs prints stubs):\n    ${shown(result.unmapped, (i) => `${i.key}  (${i.file}${i.line ? `:${i.line}` : ""})`)}`);
  else lines.push("  OK  every item in the code has a row");
  if (result.stale.length) fail.push(`(2) ${result.stale.length} row(s) cover something the code no longer has (remove the key, or the row):\n    ${shown(result.stale, (s) => `${s.row} -> ${s.key}`)}`);
  if (result.missing.length) fail.push(`(3) ${result.missing.length} proof(s) name a file or test that does not exist, or claim a type they do not have (fix the proof; the row is then a gap and needs an issue):\n    ${shown(result.missing, (m) => `${m.row}: ${m.why}`)}`);
  else lines.push("  OK  every proof names a test that exists, of the type it claims");
  if (result.issueProblems.length) fail.push(`(4) ${result.issueProblems.length} row(s) with neither a proof nor an issue:\n    ${shown(result.issueProblems, (x) => x)}`);
  if (result.gapProblems.length) fail.push(`(5) the gaps:\n    ${shown(result.gapProblems, (x) => x)}`);
  else {
    const held = baseGaps === null ? "" : `, no more than the ${baseGaps} at ${baseRef}`;
    lines.push(`  OK  ${t.gaps.length} gaps${held}${openIssues ? "; every issue named is open" : ""}`);
  }
  if (!baseRef) lines.push("  !!  no --base given, so the gaps were not held to the count on the base branch (CI passes it; locally use --base origin/main)");
  for (const note of result.gapNotes) if (baseRef || baseGaps !== null) lines.push(`  !!  ${note}`);
  const levelLine = `${t.gaps.length} gaps (${t.highGaps} high): ${t.all.proven} of ${t.all.rows} rows reach their required proof level; ${t.untracked.length} gaps are not on an issue yet`;
  lines.push(`  ${t.gaps.length ? "!!" : "OK"}  ${levelLine}`);
  lines.push(`      real-fs proofs ${t.realFs.claims}: ${t.realFs.counted} counted, ${t.realFs.refused} refused (counted inproc), ${t.realFs.noEffect} without effect; vm rows (need a scenario): ${t.realFs.vmRows}, ${t.realFs.vmRowGaps} still gaps; container-smoke proofs ${t.container.claims}: ${t.container.counted} counted, ${t.container.refused} refused`);
  if (result.refused.length) {
    lines.push(`  !!  ${result.refused.length} real-fs or container-smoke claim(s) the check cannot verify (they count as inproc; only --strict fails on them):\n    ${shown(result.refused, (r) => `${r.row}: ${r.proof.file} ${r.proof.name}: ${r.why}`)}`);
    if (strict) fail.push(`${result.refused.length} real-fs or container-smoke claim(s) the check cannot verify (--strict)`);
  }
  if (strict && t.gaps.length) fail.push(`${levelLine} (--strict)`);

  if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, jobSummary(register, inventory, t, result.gapNotes));
  log(lines.join("\n"));
  if (fail.length) {
    error(`\nCoverage register check failed:\n  ${fail.join("\n  ")}`);
    return 1;
  }
  if (t.gaps.length) log("  (gaps do not fail yet; node scripts/check-coverage-register.mjs --strict fails on them)");
  return 0;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) process.exitCode = await main();
