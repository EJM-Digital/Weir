import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, describe, test } from "node:test";
import { fileURLToPath } from "node:url";

import { buildInventory } from "./coverage/inventory.mjs";
import { TestIndex } from "./coverage/testindex.mjs";
import { evaluate, isGap, proofLevel, requiredFor, validateShape } from "./check-coverage-register.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-coverage-register.mjs");
const made = [];
after(() => {
  for (const folder of made) rmSync(folder, { recursive: true, force: true });
});

/** A small repository tree with one of everything the inventory reads, and the register that accounts for all of it. */
const TREE = {
  "apps/server/src/Weir.Api/Endpoints/ThingEndpoints.cs": 'endpoints.MapV1("GET", "/things", handlers.ListAsync);\n',
  "apps/server/src/Weir.Core/Jobs/ThingKinds.cs": 'public const string ThingKind = "processing.thing.do.v1";\n',
  "apps/server/src/Weir.Infrastructure/ThingWorker.cs": "public sealed class ThingWorker : BackgroundService { }\n",
  "apps/server/src/Weir.Infrastructure/ThingTask.cs": 'public sealed class ThingTask : IPeriodicTask\n{\n    public string Name => "thing-task";\n}\n',
  "apps/server/src/Weir.Core/Configuration/Options.cs": 'var level = runtime.Get("WEIR_THING_LEVEL");\n',
  "apps/server/src/Weir.Core/Settings/SuiteSettings.cs": "public sealed record SuiteSettingsRecord\n{\n    public string Title { get; init; } = string.Empty;\n    public long Id { get; init; }\n}\n",
  "apps/server/src/Weir.Core/Activity/ActivityEventTypes.cs": 'public const string Happened = "thing.happened";\n',
  "apps/web/src/app/router.tsx": 'const router = createBrowserRouter([\n  { path: "/login", lazy: async () => ({}) },\n]);\n',
  "apps/web/src/pages/thing-page.tsx": '<button data-testid="thing-go" onClick={go}>Go</button>\n<p data-testid="thing-note">Note</p>\n',
  "apps/tray/Weir.Tray/TrayMenu.cs": "enum TrayMenuItem\n{\n    Open,\n    Quit,\n}\n",
  "scripts/scenarios/Weir.Scenarios.Catalog.ps1": "$ScenarioCatalog = @(\n    [pscustomobject]@{\n        Id = 'one'; Phase = 'Fresh'; Required = $true\n    }\n)\n",
  "apps/server/tests/Weir.Api.Tests/ThingTests.cs": "public sealed class ThingTests\n{\n    [Fact]\n    public void Gets_the_things()\n    {\n    }\n}\n",
  "apps/web/src/pages/thing-page.test.tsx": 'it("opens the thing", () => {});\n',
};

const row = (id, covers, proofs = [{ type: "xunit", file: "apps/server/tests/Weir.Api.Tests/ThingTests.cs", name: "Gets_the_things" }], extra = {}) => ({
  id,
  area: "api",
  behaviour: `Behaviour of ${id}`,
  kind: "API route",
  severity: "medium",
  touches: "none",
  required: "inproc",
  proofs,
  covers,
  ...extra,
});

const COVERS = [
  ["route:GET /api/v1/things"],
  ["job:processing.thing.do.v1"],
  ["service:ThingWorker"],
  ["task:thing-task"],
  ["setting:env:WEIR_THING_LEVEL"],
  ["setting:SuiteSettingsRecord.Title"],
  ["event:thing.happened"],
  ["web:/login"],
  ["action:apps/web/src/pages/thing-page.tsx#thing-go"],
  ["tray:Open", "tray:Quit"],
  ["scenario:one"],
];

function register(overrides = {}) {
  const rows = COVERS.map((covers, i) => row(`row.${i}`, covers));
  const base = { version: 1, gapCeiling: 0, areas: { api: { code: "R", name: "API" } }, rows };
  return { ...base, ...overrides };
}

function fixture(files = {}) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-coverage-"));
  made.push(root);
  for (const [file, text] of Object.entries({ ...TREE, ...files })) {
    if (text === null) continue;
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), text);
  }
  return root;
}

function check(root, reg, options = {}) {
  return evaluate({ register: reg, inventory: buildInventory(root), index: new TestIndex(root), ...options });
}

const keys = (result) => result.unmapped.map((item) => item.key);
const clean = (result) => {
  assert.deepEqual(result.shape, []);
  assert.deepEqual(keys(result), []);
  assert.deepEqual(result.stale, []);
  assert.deepEqual(result.missing, []);
  assert.deepEqual(result.issueProblems, []);
  assert.deepEqual(result.gapProblems, []);
};

describe("the inventory", () => {
  test("reads one of everything from the source", () => {
    const found = buildInventory(fixture()).map((item) => item.key);
    for (const key of COVERS.flat()) assert.ok(found.includes(key), `${key} should be in the inventory: ${found.join(", ")}`);
  });

  test("leaves out a bookkeeping field and a test id on something nobody operates", () => {
    const found = buildInventory(fixture()).map((item) => item.key);
    assert.ok(!found.some((key) => key.includes("Id") && key.startsWith("setting:SuiteSettingsRecord")));
    assert.ok(!found.includes("action:apps/web/src/pages/thing-page.tsx#thing-note"));
  });
});

describe("a register that accounts for everything", () => {
  test("passes", () => clean(check(fixture(), register())));
});

describe("(1) code the register does not account for", () => {
  const added = [
    ["a route", { "apps/server/src/Weir.Api/Endpoints/MoreEndpoints.cs": 'endpoints.MapV1("POST", "/things/{thing_id}/go", h);\n' }, "route:POST /api/v1/things/{thing_id}/go"],
    ["a route mapped on the builder", { "apps/server/src/Weir.Api/Endpoints/Direct.cs": 'endpoints.MapMethods("/health", GetAndHead, h);\n' }, "route:GET /health"],
    ["a job kind", { "apps/server/src/Weir.Core/Jobs/More.cs": 'public const string K = "processing.other.run.v2";\n' }, "job:processing.other.run.v2"],
    ["a hosted service", { "apps/server/src/Weir.Infrastructure/Other.cs": "public sealed class OtherService : IHostedService { }\n" }, "service:OtherService"],
    ["a periodic task", { "apps/server/src/Weir.Infrastructure/OtherTask.cs": 'public sealed class OtherTask : IPeriodicTask\n{\n    public string Name => "other-task";\n}\n' }, "task:other-task"],
    ["an environment variable", { "apps/server/src/Weir.Core/Configuration/More.cs": 'runtime.Get("WEIR_OTHER_SETTING");\n' }, "setting:env:WEIR_OTHER_SETTING"],
    ["a setting", { "apps/server/src/Weir.Core/Settings/SuiteSettings.cs": TREE["apps/server/src/Weir.Core/Settings/SuiteSettings.cs"].replace("public long Id", "public bool Extra { get; init; }\n    public long Id") }, "setting:SuiteSettingsRecord.Extra"],
    ["an event type", { "apps/server/src/Weir.Core/Activity/ActivityEventTypes.cs": TREE["apps/server/src/Weir.Core/Activity/ActivityEventTypes.cs"] + 'public const string Other = "thing.other_happened";\n' }, "event:thing.other_happened"],
    ["a web route", { "apps/web/src/app/router.tsx": 'const router = createBrowserRouter([\n  { path: "/login", lazy: a },\n  { path: "/extra", lazy: b },\n]);\n' }, "web:/extra"],
    ["a button", { "apps/web/src/pages/thing-page.tsx": TREE["apps/web/src/pages/thing-page.tsx"] + '<button data-testid="thing-stop">Stop</button>\n' }, "action:apps/web/src/pages/thing-page.tsx#thing-stop"],
    ["a tray menu item", { "apps/tray/Weir.Tray/TrayMenu.cs": "enum TrayMenuItem\n{\n    Open,\n    Quit,\n    Extra,\n}\n" }, "tray:Extra"],
    ["a scenario", { "scripts/scenarios/Weir.Scenarios.Catalog.ps1": TREE["scripts/scenarios/Weir.Scenarios.Catalog.ps1"] + "        Id = 'two'; Phase = 'Fresh'\n" }, "scenario:two"],
  ];
  for (const [what, files, key] of added) {
    test(`names ${what}: ${key}`, () => {
      assert.deepEqual(keys(check(fixture(files), register())), [key]);
    });
  }

  test("names a route whose row was deleted, with its method and path", () => {
    const reg = register();
    reg.rows = reg.rows.filter((r) => !r.covers.includes("route:GET /api/v1/things"));
    assert.deepEqual(keys(check(fixture(), reg)), ["route:GET /api/v1/things"]);
  });
});

describe("(2) a row for code that is gone", () => {
  test("names the row and the key", () => {
    const root = fixture({ "apps/server/src/Weir.Api/Endpoints/ThingEndpoints.cs": "" });
    const result = check(root, register());
    assert.deepEqual(result.stale, [{ row: "row.0", key: "route:GET /api/v1/things" }]);
    assert.deepEqual(keys(result), []);
  });
});

describe("(3) a proof that has gone", () => {
  const proof = (file, name, type = "xunit") => [{ type, file, name }];

  test("a renamed test is named", () => {
    const reg = register();
    reg.rows[0].proofs = proof("apps/server/tests/Weir.Api.Tests/ThingTests.cs", "Gets_the_things_renamed");
    const result = check(fixture(), reg);
    assert.equal(result.missing.length, 1);
    assert.match(result.missing[0].why, /no test, group or class "Gets_the_things_renamed"/);
    assert.equal(result.missing[0].row, "row.0");
  });

  test("a deleted file is named", () => {
    const result = check(fixture({ "apps/server/tests/Weir.Api.Tests/ThingTests.cs": null }), register());
    assert.ok(result.missing.length > 0);
    assert.match(result.missing[0].why, /does not exist/);
  });

  test("a vitest title is found by its words", () => {
    const reg = register();
    reg.rows[7].proofs = proof("apps/web/src/pages/thing-page.test.tsx", "opens the thing", "vitest");
    clean(check(fixture(), reg));
  });

  test("a type the file does not have is named", () => {
    const reg = register();
    reg.rows[0].proofs = proof("apps/server/tests/Weir.Api.Tests/ThingTests.cs", "Gets_the_things", "scenario");
    const result = check(fixture(), reg);
    assert.match(result.missing[0].why, /scenario proof must be a script in scripts\/scenarios/);
  });

  test("a recording-replay proof must load one of Deluno's recordings", () => {
    const reg = register();
    reg.rows[0].proofs = proof("apps/server/tests/Weir.Contract.Tests/ReplayTests.cs", "Replays", "recording-replay");
    const replaying = 'public sealed class ReplayTests\n{\n    [Fact]\n    public void Replays()\n    {\n        var r = DelunoSide.Reply("reply-x");\n    }\n}\n';
    clean(check(fixture({ "apps/server/tests/Weir.Contract.Tests/ReplayTests.cs": replaying }), reg));
    const handBuilt = replaying.replace('DelunoSide.Reply("reply-x")', 'new Reply("x")');
    assert.match(check(fixture({ "apps/server/tests/Weir.Contract.Tests/ReplayTests.cs": handBuilt }), reg).missing[0].why, /does not load one of Deluno's recordings/);
  });

  test("a partial proof is checked the same way", () => {
    const reg = register();
    reg.rows[0].partial = proof("apps/server/tests/Weir.Api.Tests/ThingTests.cs", "Gone");
    assert.equal(check(fixture(), reg).missing.length, 1);
  });
});

describe("(4) a row with neither a proof nor an issue", () => {
  test("is named", () => {
    const reg = register({ gapCeiling: 1 });
    reg.rows[0].proofs = [];
    const result = check(fixture(), reg);
    assert.equal(result.issueProblems.length, 1);
    assert.match(result.issueProblems[0], /row row\.0 has neither a proof nor an issue/);
  });

  test("a gap that names its issue passes", () => {
    const reg = register({ gapCeiling: 1 });
    reg.rows[0].proofs = [];
    reg.rows[0].issue = 975;
    clean(check(fixture(), reg, { openIssues: new Set([975]) }));
  });

  test("a row proven in part keeps its proofs, names the issue that will prove the rest, and is a gap", () => {
    const reg = register({ gapCeiling: 1 });
    reg.rows[0].issue = 975;
    const result = check(fixture(), reg, { openIssues: new Set([975]) });
    clean(result);
    assert.equal(result.totals.gaps.length, 1);
    assert.equal(result.totals.untracked.length, 0);
    assert.equal(result.totals.gaps[0].proofs.length, 1);
  });

  test("the same row is a gap that cannot be added without lifting the ceiling", () => {
    const reg = register();
    reg.rows[0].issue = 975;
    assert.match(check(fixture(), reg).gapProblems[0], /1 gaps, above the ceiling of 0/);
  });
});

describe("(5) the gaps", () => {
  const gapRegister = (ceiling) => {
    const reg = register({ gapCeiling: ceiling });
    reg.rows[0].proofs = [];
    reg.rows[0].issue = 975;
    return reg;
  };

  test("a gap above the ceiling fails", () => {
    const result = check(fixture(), gapRegister(0));
    assert.match(result.gapProblems[0], /1 gaps, above the ceiling of 0/);
  });

  test("a ceiling above the gaps fails, so the ceiling only goes down", () => {
    const result = check(fixture(), gapRegister(2));
    assert.match(result.gapProblems[0], /below the ceiling of 2: lower "gapCeiling" to 1/);
  });

  test("more gaps than the base branch fails", () => {
    const result = check(fixture(), gapRegister(1), { baseGaps: 0 });
    assert.match(result.gapProblems[0], /more than the 0 in the register on the base branch/);
  });

  test("no more gaps than the base branch passes", () => {
    clean(check(fixture(), gapRegister(1), { baseGaps: 1, openIssues: new Set([975]) }));
  });

  test("a gap that names a closed issue fails", () => {
    const result = check(fixture(), gapRegister(1), { openIssues: new Set([976]) });
    assert.match(result.gapProblems[0], /row row\.0 names issue #975, which is not open/);
  });

  test("a gap with proofs that fall short of the level counts, and may have no issue yet", () => {
    const reg = register({ gapCeiling: 1 });
    reg.rows[0].severity = "high";
    reg.rows[0].touches = "disk";
    reg.rows[0].required = "real";
    const result = check(fixture(), reg);
    assert.deepEqual(result.gapProblems, []);
    assert.equal(result.totals.gaps.length, 1);
    assert.equal(result.totals.untracked.length, 1);
  });
});

describe("the proof levels", () => {
  const index = { stubInfo: () => ({ stubbed: false, decided: true }) };
  const screen = { area: "web-shell", kind: "screen action" };

  test("a high row that touches another app, the disk or an install needs real proof", () => {
    assert.equal(requiredFor({ severity: "high", touches: "other-app" }), "real");
    assert.equal(requiredFor({ severity: "high", touches: "disk" }), "real");
    assert.equal(requiredFor({ severity: "high", touches: "install" }), "real");
    assert.equal(requiredFor({ severity: "high", touches: "none" }), "inproc");
    assert.equal(requiredFor({ severity: "medium", touches: "disk" }), "inproc");
  });

  test("a scenario and a replay are real; an xunit or contract test is not", () => {
    for (const type of ["scenario", "recording-replay"]) assert.equal(proofLevel({ type }, screen, index), 2);
    for (const type of ["xunit", "contract", "vitest", "node", "package-smoke"]) assert.equal(proofLevel({ type }, screen, index), 1);
  });

  test("an e2e test is real for a screen only when its requests are not intercepted", () => {
    assert.equal(proofLevel({ type: "e2e" }, screen, index), 2);
    assert.equal(proofLevel({ type: "e2e" }, screen, { stubInfo: () => ({ stubbed: true, decided: true }) }), 1);
    assert.equal(proofLevel({ type: "e2e" }, { area: "api", kind: "API route" }, index), 1);
  });

  test("a row is a gap when its best proof is below its level", () => {
    const row = { area: "web-shell", severity: "high", touches: "disk", proofs: [{ type: "vitest" }] };
    assert.equal(isGap(row, index), true);
    assert.equal(isGap({ ...row, proofs: [{ type: "vitest" }, { type: "e2e" }] }, index), false);
  });
});

describe("the register's shape", () => {
  test("a required level that does not follow from severity and touches is named", () => {
    const reg = register();
    reg.rows[0].severity = "high";
    reg.rows[0].touches = "disk";
    assert.match(validateShape(reg)[0], /required must be "real" for a high row that touches disk/);
  });

  test("an unknown proof type, a repeated id and an unknown area are named", () => {
    const reg = register();
    reg.rows[0].proofs = [{ type: "magic", file: "a", name: "b" }];
    reg.rows[1].id = reg.rows[0].id;
    reg.rows[2].area = "nowhere";
    const problems = validateShape(reg).join("\n");
    assert.match(problems, /proof type "magic"/);
    assert.match(problems, /id used twice/);
    assert.match(problems, /area "nowhere"/);
  });

  test("a register with no ceiling is malformed", () => {
    const reg = register();
    delete reg.gapCeiling;
    assert.match(validateShape(reg)[0], /gapCeiling/);
  });
});

describe("the command", () => {
  const run = (root, ...args) => spawnSync(process.execPath, [script, "--root", root, ...args], { encoding: "utf8" });
  const writeRegister = (root, reg) => {
    mkdirSync(path.join(root, "docs/testing"), { recursive: true });
    writeFileSync(path.join(root, "docs/testing/coverage-register.json"), JSON.stringify(reg));
  };

  test("passes on a register that accounts for everything", () => {
    const root = fixture();
    writeRegister(root, register());
    const result = run(root);
    assert.equal(result.status, 0, result.stderr);
  });

  test("fails naming the route that has no row", () => {
    const root = fixture({ "apps/server/src/Weir.Api/Endpoints/MoreEndpoints.cs": 'endpoints.MapV1("DELETE", "/things/{thing_id}", h);\n' });
    writeRegister(root, register());
    const result = run(root);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /route:DELETE \/api\/v1\/things\/\{thing_id\}/);
  });

  test("--strict fails on a gap that the plain run lets through", () => {
    const root = fixture();
    const reg = register({ gapCeiling: 1 });
    reg.rows[0].proofs = [];
    reg.rows[0].issue = 975;
    writeRegister(root, reg);
    assert.equal(run(root, "--open-issues", "975").status, 0);
    assert.equal(run(root, "--open-issues", "975", "--strict").status, 1);
  });

  test("--base fails when the register has more gaps than it had at that ref", () => {
    const root = fixture();
    writeRegister(root, register());
    const git = (...args) => spawnSync("git", ["-c", "user.name=t", "-c", "user.email=t@example.test", ...args], { cwd: root, encoding: "utf8" });
    git("init");
    git("add", "docs/testing/coverage-register.json");
    assert.equal(git("commit", "-m", "base").status, 0);
    const reg = register({ gapCeiling: 1 });
    reg.rows[0].proofs = [];
    reg.rows[0].issue = 975;
    writeRegister(root, reg);
    assert.equal(run(root, "--open-issues", "975").status, 0);
    const result = run(root, "--open-issues", "975", "--base", "HEAD");
    assert.equal(result.status, 1);
    assert.match(result.stderr, /more than the 0 in the register on the base branch/);
  });

  test("--stubs prints a row for what has none", () => {
    const root = fixture({ "apps/server/src/Weir.Api/Endpoints/MoreEndpoints.cs": 'endpoints.MapV1("GET", "/more", h);\n' });
    writeRegister(root, register());
    const stubs = JSON.parse(run(root, "--stubs").stdout);
    assert.deepEqual(stubs.map((s) => s.covers), [["route:GET /api/v1/more"]]);
  });
});
