// node --test scripts/check-coverage-register-command.test.mjs
// The coverage register check beyond its core rules: every way the register's shape can be wrong, every proof type's file, the totals of the
// special proofs, and the command with its options, run in process on a small repository tree.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, describe, test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  REGISTER,
  isGap,
  main,
  missingProofs,
  openIssuesFromGh,
  proofLevel,
  rowLevel,
  statisticsReport,
  totals,
  validateShape,
} from "./check-coverage-register.mjs";

const made = [];
after(() => {
  for (const folder of made) rmSync(folder, { recursive: true, force: true });
});

const row = (id, extra = {}) => ({
  id,
  area: "api",
  behaviour: `Behaviour of ${id}`,
  kind: "API route",
  severity: "medium",
  touches: "none",
  required: "inproc",
  proofs: [{ type: "xunit", file: "apps/server/tests/Weir.Api.Tests/ThingTests.cs", name: "Gets_the_things" }],
  covers: [],
  ...extra,
});
const register = (rows) => ({ version: 1, areas: { api: { code: "R", name: "API" }, "web-routes": { code: "W", name: "Web" } }, rows });

describe("a register that is malformed says how", () => {
  const problems = (reg) => validateShape(reg).join("\n");

  test("a ceiling that is no longer used, and a rise that is not explained", () => {
    assert.match(problems({ ...register([]), gapCeiling: 3 }), /"gapCeiling" is not used/);
    assert.match(problems({ ...register([]), gapIncrease: { from: 5, to: 3, reason: "x" } }), /"gapIncrease" must be/);
    assert.match(problems({ ...register([]), gapIncrease: { from: 3, to: 5, reason: " " } }), /"gapIncrease" must be/);
    assert.match(problems({ ...register([]), gapIncrease: null }), /"gapIncrease" must be/);
    assert.equal(problems({ ...register([]), gapIncrease: { from: 3, to: 5, reason: "a new area" } }), "");
  });

  test("a row's id, area, wording and enums", () => {
    const text = problems(
      register([
        row("a", { area: "nowhere", behaviour: "  ", kind: "odd", severity: "huge", touches: "moon" }),
        row("a"),
        { ...row("b"), id: undefined },
        row("c", { id: 7 }),
      ]),
    );
    for (const expected of [
      'row a: area "nowhere" is not in the register\'s areas',
      "row a: needs a behaviour",
      'row a: kind "odd" is not one of',
      'row a: severity "huge" is not high, medium or low',
      'row a: touches "moon" is not one of',
      "row a: id used twice",
      "row (no id): needs an id",
      "row 7: needs an id",
    ]) {
      assert.ok(text.includes(expected), `${expected}\n${text}`);
    }
  });

  test("the required level follows from severity and what the row touches", () => {
    assert.match(problems(register([row("a", { severity: "high", touches: "disk", required: "inproc" })])), /row a: required must be "real" for a high row that touches disk/);
    assert.equal(problems(register([row("a", { severity: "high", touches: "none" })])), "");
  });

  test("vm, issue, proofs and covers", () => {
    const text = problems(
      register([
        row("a", { vm: "yes" }),
        row("b", { vm: true, touches: "none" }),
        row("c", { issue: 0 }),
        row("d", { issue: "12" }),
        row("e", { proofs: "none" }),
        row("f", { covers: "route:GET /x" }),
        row("g", { proofs: [{ type: "magic", file: "", name: "" }], partial: [{ type: "xunit", file: "a.cs" }] }),
      ]),
    );
    for (const expected of [
      "row a: vm may only be true",
      "row b: vm is for a row that touches disk or install, not none",
      "row c: issue must be the number of an open coverage issue",
      "row d: issue must be the number of an open coverage issue",
      "row e: proofs must be a list",
      "row f: covers must be a list",
      'row g: proof type "magic" is not one of',
      "row g: a proof needs a file and a name",
    ]) {
      assert.ok(text.includes(expected), `${expected}\n${text}`);
    }
  });
});

/** An index that finds every file and name, and judges the special proofs as `verdicts` says. */
function indexOf({ has = () => true, hasName = () => true, replays = () => true, realFs = () => ({ real: true }), containerSmoke = () => ({ real: true }), stubInfo } = {}) {
  return { has, hasName, replaysRecording: replays, realFs, containerSmoke, ...(stubInfo ? { stubInfo } : {}) };
}

describe("a proof's type has to be true of its file", () => {
  const wrong = (proof, index = indexOf()) => missingProofs(register([row("r", { proofs: [proof] })]), index).map((entry) => entry.why);
  const cases = [
    [{ type: "scenario", file: "apps/server/tests/A.cs" }, /a scenario proof must be a script in scripts\/scenarios/],
    [{ type: "e2e", file: "apps/server/tests/Weir.Api.Tests/A.cs" }, /an e2e proof must be a test in Weir\.E2E\.Tests/],
    [{ type: "vitest", file: "apps/web/src/a.ts" }, /a vitest proof must be a \.test\.ts\(x\) file/],
    [{ type: "node", file: "scripts/a.mjs" }, /a node proof must be a \.test\.mjs file/],
    [{ type: "contract", file: "apps/server/tests/Weir.Api.Tests/A.cs" }, /a contract proof must be a test in Weir\.Contract\.Tests/],
    [{ type: "package-smoke", file: "scripts/a.ps1" }, /a package-smoke proof must be a Windows package smoke or the live audit/],
    [{ type: "real-fs", file: "apps/web/src/a.test.ts" }, /a real-fs proof must be a C# test of the server or the tray/],
    [{ type: "container-smoke", file: "docs/a.md" }, /a container-smoke proof must be a packaging workflow or a container script/],
    [{ type: "xunit", file: "scripts/scenarios/a.ps1" }, /an xunit proof cannot be a PowerShell script/],
  ];
  for (const [proof, expected] of cases) {
    test(`${proof.type} in ${proof.file}`, () => {
      assert.match(wrong({ ...proof, name: "n" })[0], expected);
    });
  }

  test("the right file for each type, a recording that is not replayed, a file that is gone and a name that is not there", () => {
    const right = [
      ["scenario", "scripts/scenarios/Weir.Scenarios.ps1"],
      ["e2e", "apps/server/tests/Weir.E2E.Tests/A.cs"],
      ["vitest", "apps/web/src/a.test.tsx"],
      ["node", "scripts/a.test.mjs"],
      ["contract", "apps/server/tests/Weir.Contract.Tests/A.cs"],
      ["package-smoke", "scripts/smoke-windows-package.ps1"],
      ["package-smoke", "apps/server/tools/Weir.LiveAudit/Program.cs"],
      ["real-fs", "apps/tray/Weir.Tray.Tests/A.cs"],
      ["container-smoke", ".github/workflows/ci-packaging.yml"],
      ["xunit", "apps/server/tests/Weir.Api.Tests/A.cs"],
    ];
    for (const [type, file] of right) assert.deepEqual(wrong({ type, file, name: "n" }), [], `${type} ${file}`);
    assert.match(wrong({ type: "recording-replay", file: "apps/server/tests/A.cs", name: "n" }, indexOf({ replays: () => false }))[0], /does not load one of Deluno's recordings/);
    assert.match(wrong({ type: "xunit", file: "apps/server/tests/A.cs", name: "n" }, indexOf({ has: () => false }))[0], /the file apps\/server\/tests\/A\.cs does not exist/);
    assert.match(wrong({ type: "xunit", file: "apps/server/tests/A.cs", name: "n" }, indexOf({ hasName: () => false }))[0], /no test, group or class "n" in apps\/server\/tests\/A\.cs/);
  });
});

describe("what a row's proofs are worth", () => {
  const disk = { severity: "high", touches: "disk", required: "real" };

  test("a vm row is real by a scenario alone, and a claim is only as good as the text can show", () => {
    const vm = row("vm", { ...disk, vm: true });
    assert.equal(proofLevel({ type: "scenario" }, vm, indexOf()), 2);
    assert.equal(proofLevel({ type: "real-fs" }, vm, indexOf()), 1);
    const plain = row("plain", disk);
    assert.equal(proofLevel({ type: "real-fs", file: "f", name: "n" }, plain, indexOf()), 2);
    assert.equal(proofLevel({ type: "real-fs", file: "f", name: "n" }, plain, indexOf({ realFs: () => ({ real: false }) })), 1);
    assert.equal(proofLevel({ type: "real-fs", file: "f", name: "n" }, plain, undefined), 1);
    assert.equal(proofLevel({ type: "real-fs", file: "f", name: "n" }, plain, {}), 1);
    assert.equal(proofLevel({ type: "recording-replay" }, plain, indexOf()), 2);
    assert.equal(proofLevel({ type: "xunit" }, plain, indexOf()), 1);
  });

  test("an e2e test is real for a screen behaviour whose requests are not intercepted, and only then", () => {
    const screen = row("screen", { area: "web-routes", touches: "none" });
    const proof = { type: "e2e", file: "f", name: "n" };
    assert.equal(proofLevel(proof, screen, indexOf({ stubInfo: () => ({ stubbed: false, decided: true }) })), 2);
    assert.equal(proofLevel(proof, screen, indexOf({ stubInfo: () => ({ stubbed: true, decided: true }) })), 1);
    assert.equal(proofLevel(proof, screen, undefined), 1);
    assert.equal(proofLevel(proof, row("api"), indexOf({ stubInfo: () => ({ stubbed: false, decided: true }) })), 1);
  });

  test("a row's level is its best proof, and a row naming an issue is a gap whatever its level", () => {
    assert.equal(rowLevel(row("none", { proofs: [] }), indexOf()), 0);
    assert.equal(rowLevel({ id: "bare" }, indexOf()), 0);
    assert.equal(isGap(row("done"), indexOf()), false);
    assert.equal(isGap(row("partial", { issue: 12 }), indexOf()), true);
    assert.equal(isGap(row("short", { ...disk }), indexOf()), true);
  });

  test("the totals count each special proof as counted, refused or without effect, and the e2e proofs as unstubbed, stubbed or undecided", () => {
    const stubs = { f1: { stubbed: false }, f2: { stubbed: true, decided: true }, f3: { stubbed: true, decided: false } };
    const index = indexOf({
      stubInfo: (file) => stubs[file],
      realFs: (file) => ({ real: file !== "refused.cs" }),
      containerSmoke: (file) => ({ real: file !== "refused.yml" }),
    });
    const rows = [
      row("e2e", { area: "web-routes", proofs: ["f1", "f2", "f3"].map((file) => ({ type: "e2e", file, name: "n" })) }),
      row("fs", { ...disk, proofs: [{ type: "real-fs", file: "ok.cs", name: "n" }, { type: "real-fs", file: "refused.cs", name: "n" }] }),
      row("fs-none", { proofs: [{ type: "real-fs", file: "ok.cs", name: "n" }] }),
      row("box", { ...disk, vm: true, proofs: [{ type: "container-smoke", file: "ok.yml", name: "n" }, { type: "container-smoke", file: "refused.yml", name: "n" }] }),
      row("high-other", { severity: "high", touches: "other-app", required: "real", issue: 3, proofs: [] }),
    ];

    const t = totals(register(rows), index);

    assert.deepEqual(t.e2e, { proofs: 3, unstubbed: 1, stubbed: 1, undecided: 1 });
    assert.deepEqual(t.realFs, { claims: 3, counted: 1, refused: 1, noEffect: 1, vmRows: 1, vmRowGaps: 1 });
    assert.deepEqual(t.container, { claims: 2, counted: 0, refused: 1, noEffect: 1 });
    assert.deepEqual(t.highByTouches["other-app"], { rows: 1, gaps: 1 });
    assert.equal(t.untracked.length, 1);
    assert.equal(t.highGaps, 2);
  });

  test("the statistics name the gaps that have an issue and show a dash where a share has nothing to divide", () => {
    const index = indexOf();
    const withIssue = register([row("a", { issue: 31, severity: "high" }), row("b")]);
    const t = totals(withIssue, index);
    const text = statisticsReport(withIssue, [], t);
    assert.match(text, /### Gaps with an open issue\n\n\| Issue \| Rows \| High rows \|\n\| --- \| ---: \| ---: \|\n\| #31 \| 1 \| 1 \|/);
    assert.match(text, /\*\*2\*\* behaviours\. \*\*1\*\* reach their required proof level \(50%\)/);
    const empty = statisticsReport(register([]), [], totals(register([]), index));
    assert.match(empty, /\(-\)/);
    assert.match(empty, /No row names an issue yet\./);
  });
});

// The command, on a small tree: one route, one test for it, and a register that accounts for both.
const TREE = {
  "apps/server/src/Weir.Api/Endpoints/ThingEndpoints.cs": 'endpoints.MapV1("GET", "/things", handlers.ListAsync);\n',
  "apps/server/tests/Weir.Api.Tests/ThingTests.cs": "public sealed class ThingTests\n{\n    [Fact]\n    public void Gets_the_things()\n    {\n    }\n}\n",
};

function repository({ rows, files = {} }) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-register-"));
  made.push(root);
  for (const [file, text] of Object.entries({ ...TREE, ...files })) {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), text);
  }
  if (rows) {
    mkdirSync(path.dirname(path.join(root, REGISTER)), { recursive: true });
    writeFileSync(path.join(root, REGISTER), JSON.stringify(register(rows)));
  }
  return root;
}

const accounted = [row("things", { covers: ["route:GET /api/v1/things"] })];

async function command(root, args = [], options = {}) {
  const printed = [];
  const complaints = [];
  const code = await main(["--root", root, ...args], { env: {}, log: (line) => printed.push(line), error: (line) => complaints.push(line), ...options });
  return { code, out: printed.join("\n"), err: complaints.join("\n") };
}

test("a register that accounts for everything passes, and says that there is no base to hold the gaps to", async () => {
  const { code, out, err } = await command(repository({ rows: accounted }));
  assert.equal(code, 0);
  assert.equal(err, "");
  assert.match(out, /Coverage register: 1 rows, 1 inventory items \(1 route\)\./);
  assert.match(out, /OK {2}every item in the code has a row/);
  assert.match(out, /no --base given/);
});

test("a repository with no register fails and says so", async () => {
  const { code, err } = await command(repository({}));
  assert.equal(code, 1);
  assert.equal(err, `${REGISTER} is missing.`);
});

test("every kind of fault is named, numbered and counted, and the command exits 1", async () => {
  const root = repository({
    files: { "apps/server/src/Weir.Core/Jobs/Kinds.cs": 'public const string K = "processing.thing.do.v1";\n' },
    rows: [
      row("things", { covers: ["route:GET /api/v1/things", "route:GET /api/v1/gone"], proofs: [{ type: "xunit", file: "apps/server/tests/Weir.Api.Tests/Missing.cs", name: "n" }] }),
      row("bare", { proofs: [] }),
      row("broken", { severity: "huge" }),
      row("closed", { issue: 9, proofs: [] }),
    ],
  });

  const { code, err } = await command(root, ["--open-issues", "5"]);

  assert.equal(code, 1);
  for (const expected of [
    "(6) the register is malformed (1):",
    '(1) 1 item(s) in the code have no row.',
    "job:processing.thing.do.v1  (apps/server/src/Weir.Core/Jobs/Kinds.cs:1)",
    "(2) 1 row(s) cover something the code no longer has",
    "things -> route:GET /api/v1/gone",
    "(3) 1 proof(s) name a file or test that does not exist",
    "(4) 1 row(s) with neither a proof nor an issue",
    "(5) the gaps:",
    "row closed names issue #9, which is not open",
  ]) {
    assert.ok(err.includes(expected), `${expected}\n${err}`);
  }
});

test("a long list of faults shows the first dozen and counts the rest", async () => {
  const files = Object.fromEntries(Array.from({ length: 15 }, (_, index) => [`apps/server/src/Weir.Core/Jobs/K${index}.cs`, `public const string K = "processing.thing${String.fromCharCode(97 + index)}.do.v1";\n`]));
  const { err } = await command(repository({ rows: accounted, files }));
  assert.match(err, /\(1\) 15 item\(s\)/);
  assert.match(err, /\.\.\. and 3 more/);
});

test("--stubs prints a row to fill in for each item the register has no row for", async () => {
  const root = repository({
    rows: accounted,
    files: {
      "apps/server/src/Weir.Core/Jobs/Kinds.cs": 'public const string K = "processing.thing.do.v1";\n',
      "apps/tray/Weir.Tray/TrayMenu.cs": "enum TrayMenuItem\n{\n    Open,\n}\n",
    },
  });
  const { code, out } = await command(root, ["--stubs"]);
  assert.equal(code, 0);
  const stubs = JSON.parse(out);
  assert.deepEqual(stubs.map((stub) => [stub.id, stub.area, stub.kind, stub.covers]), [
    ["services.new.processing-thing-do-v1", "services", "job", ["job:processing.thing.do.v1"]],
    ["tray.new.open", "tray", "tray", ["tray:Open"]],
  ]);
  assert.equal(stubs[0].behaviour, "<one plain sentence about processing.thing.do.v1>");
});

test("--json prints the totals, and --list-gaps the gaps with the high ones first", async () => {
  const root = repository({
    rows: [
      row("low-gap", { covers: ["route:GET /api/v1/things"], severity: "low", issue: 8 }),
      row("high-gap", { severity: "high", touches: "disk", required: "real", issue: 7, proofs: [] }),
      row("medium-gap", { proofs: [], issue: 7 }),
    ],
  });

  const json = JSON.parse((await command(root, ["--json"])).out);
  assert.deepEqual([json.rows.rows, json.rows.gaps, json.highGaps, json.untracked, json.inventory], [3, 3, 1, 0, { route: 1 }]);

  const listed = (await command(root, ["--list-gaps"])).out.split("\n");
  assert.equal(listed.length, 3);
  assert.match(listed[0], /^high {3}disk {6}#7 {5}high-gap {2}Behaviour of high-gap$/);
  assert.match(listed[1], /^medium none {6}#7 {5}medium-gap/);
  assert.match(listed[2], /^low {4}none {6}#8 {5}low-gap/);
  const untracked = (await command(repository({ rows: [row("loose", { covers: ["route:GET /api/v1/things"], proofs: [], issue: undefined })] }), ["--list-gaps"])).out;
  assert.match(untracked, /^medium none {6}- {6}loose/);
});

test("--report writes the statistics where it is told, or to the usual place, and the job summary gets them too", async () => {
  const root = repository({ rows: accounted });
  const summary = path.join(root, "step-summary.md");

  const named = await command(root, ["--report", path.join(root, "out", "stats.md")], { env: { GITHUB_STEP_SUMMARY: summary } });
  assert.match(named.out, /^wrote .*stats\.md$/m);
  assert.match(readFileSync(path.join(root, "out", "stats.md"), "utf8"), /### Coverage register/);
  assert.match(readFileSync(summary, "utf8"), /### Coverage register/);

  const usual = await command(root, ["--report"]);
  assert.match(usual.out, /^wrote .*artifacts[\\/]coverage-register\.md$/m);
  assert.ok(existsSync(path.join(root, "artifacts", "coverage-register.md")));
  const flagNext = await command(root, ["--report", "--strict"]);
  assert.match(flagNext.out, /artifacts[\\/]coverage-register\.md/);
});

test("--strict fails on a gap, and on a real-fs claim the check cannot verify; without it they are reported", async () => {
  const root = repository({
    rows: [row("things", { covers: ["route:GET /api/v1/things"], severity: "high", touches: "disk", required: "real", proofs: [{ type: "real-fs", file: "apps/server/tests/Weir.Api.Tests/ThingTests.cs", name: "Gets_the_things" }] })],
  });

  const lenient = await command(root);
  assert.equal(lenient.code, 0);
  assert.match(lenient.out, /1 real-fs or container-smoke claim\(s\) the check cannot verify \(they count as inproc; only --strict fails on them\)/);
  assert.match(lenient.out, /\(gaps do not fail yet; node scripts\/check-coverage-register\.mjs --strict fails on them\)/);

  const strict = await command(root, ["--strict"]);
  assert.equal(strict.code, 1);
  assert.match(strict.err, /1 real-fs or container-smoke claim\(s\) the check cannot verify \(--strict\)/);
  assert.match(strict.err, /1 gaps \(1 high\): 0 of 1 rows reach their required proof level; 1 gaps are not on an issue yet \(--strict\)/);
});

test("--gh asks GitHub which coverage issues are open, and --base with no register behind it says there is nothing to compare", async () => {
  const root = repository({ rows: [row("things", { covers: ["route:GET /api/v1/things"], issue: 4, proofs: [] })] });
  const asked = [];
  const open = await command(root, ["--gh", "--base", "origin/not-here"], { askGh: (folder) => (asked.push(folder), new Set([4])) });
  assert.equal(open.code, 0);
  assert.deepEqual(asked, [root]);
  assert.match(open.out, /every issue named is open/);
  assert.match(open.out, /No register on the base branch to compare with/);

  const closed = await command(root, ["--gh"], { askGh: () => new Set() });
  assert.equal(closed.code, 1);
  assert.match(closed.err, /row things names issue #4, which is not open/);
});

test("the open issues come from gh, and its failure says what it printed", () => {
  const calls = [];
  const fine = (command, args, options) => (calls.push([command, args.slice(0, 3), options.cwd]), { status: 0, stdout: '[{"number":4},{"number":9}]' });
  assert.deepEqual([...openIssuesFromGh("/repo", fine)], [4, 9]);
  assert.deepEqual(calls, [["gh", ["issue", "list", "--label"], "/repo"]]);
  assert.throws(() => openIssuesFromGh("/repo", () => ({ status: 1, stderr: " not logged in\n" })), /^Error: gh could not list the open coverage issues: not logged in$/);
  assert.throws(() => openIssuesFromGh("/repo", () => ({ status: null, stderr: "", error: new Error("spawn gh ENOENT") })), /: spawn gh ENOENT$/);
  assert.throws(() => openIssuesFromGh("/repo", () => ({ status: 1 })), /open coverage issues: $/);
});

test("a base that has a register, but not a readable one, is no base", async () => {
  const root = repository({ rows: accounted });
  const git = (...args) => spawnSync("git", ["-c", "user.name=t", "-c", "user.email=t@example.test", ...args], { cwd: root, encoding: "utf8" });
  git("init", "-q");
  writeFileSync(path.join(root, REGISTER), "{ not json");
  git("add", "-A");
  git("commit", "-q", "-m", "x");
  writeFileSync(path.join(root, REGISTER), JSON.stringify(register(accounted)));

  const { code, out } = await command(root, ["--base", "HEAD"]);

  assert.equal(code, 0);
  assert.match(out, /No register on the base branch to compare with/);
});

test("run as a command, a repository without a register exits 1", () => {
  const script = fileURLToPath(new URL("./check-coverage-register.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script, "--root", repository({})], { encoding: "utf8" });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /coverage-register\.json is missing\./);
});
