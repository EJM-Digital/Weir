import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, describe, test } from "node:test";
import { fileURLToPath } from "node:url";

import { buildInventory } from "./coverage/inventory.mjs";
import { TestIndex } from "./coverage/testindex.mjs";
import { evaluate, generatedSection, isGap, proofLevel, requiredFor, validateShape } from "./check-coverage-register.mjs";

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

/** A C# test class: a real temp folder and real file work, or a fake file system, as the test needs. */
const csTest = (body, setup = "") => `public sealed class PlacementTests\n{\n${setup}    [Fact]\n    public void Places_the_file()\n    {\n${body}    }\n}\n`;
const REAL_BODY = '        using var dir = new TempDirectory();\n        File.WriteAllText(Path.Join(dir.Path, "a.mkv"), "x");\n';
const PLACEMENT = "apps/server/tests/Weir.Infrastructure.Tests/PlacementTests.cs";
const realFsProof = [{ type: "real-fs", file: PLACEMENT, name: "Places_the_file" }];
const diskRow = (extra = {}) => ({ area: "file-lifecycle", severity: "high", touches: "disk", required: "real", proofs: realFsProof, ...extra });

describe("real-fs proofs", () => {
  const index = (body, setup) => new TestIndex(fixture({ [PLACEMENT]: csTest(body, setup) }));

  test("a test that makes a real temp folder and works on its files is real for a disk row", () => {
    const idx = index(REAL_BODY);
    assert.equal(idx.realFs(PLACEMENT, "Places_the_file").real, true);
    assert.equal(proofLevel(realFsProof[0], diskRow(), idx), 2);
    assert.equal(isGap(diskRow(), idx), false);
  });

  test("it is real for an install row too, and a database file counts as a real store", () => {
    const idx = index("        using var db = new JobsTestDatabase();\n        using var connection = new SqliteConnection(db.ConnectionString);\n");
    assert.equal(proofLevel(realFsProof[0], diskRow({ touches: "install" }), idx), 2);
  });

  test("a folder made in the class setup counts for every test in the class", () => {
    const idx = index('        File.Delete(Path.Join(_dir.Path, "a.mkv"));\n', "    private readonly TempDirectory _dir = new();\n");
    assert.equal(idx.realFs(PLACEMENT, "PlacementTests").real, true);
  });

  test("a fake file system or an in-memory database is refused", () => {
    for (const fake of ["var files = new FakeSwapFileSystem();", "ISwapFileSystem files = null!;", 'var c = new SqliteConnection("Data Source=:memory:");', "var s = new InMemoryRedownloadTracker();"]) {
      const idx = index(`${REAL_BODY}        ${fake}\n`);
      const verdict = idx.realFs(PLACEMENT, "Places_the_file");
      assert.equal(verdict.real, false, fake);
      assert.match(verdict.why, /fake or in-memory/);
      assert.equal(proofLevel(realFsProof[0], diskRow(), idx), 1);
    }
  });

  test("a test that makes no temp folder or does no file work is refused", () => {
    assert.match(index('        File.WriteAllText("a.mkv", "x");\n').realFs(PLACEMENT, "Places_the_file").why, /creates no real temp folder/);
    assert.match(index("        using var dir = new TempDirectory();\n        Assert.True(true);\n").realFs(PLACEMENT, "Places_the_file").why, /no file or database work/);
  });

  test("a class or a whole file is real only when every test in it is", () => {
    const fake = csTest('        var files = new FakeSwapFileSystem();\n        File.Exists("x");\n').replace("PlacementTests", "OtherTests").replace("Places_the_file", "Fakes_the_files");
    const idx = new TestIndex(fixture({ [PLACEMENT]: csTest(REAL_BODY) + fake }));
    assert.equal(idx.realFs(PLACEMENT, "PlacementTests").real, false, "the fake sits in the same file, so the class name judges every test the file holds");
    assert.equal(idx.realFs(PLACEMENT, "Places_the_file").real, true);
    assert.equal(idx.realFs(PLACEMENT, "*").real, false);
  });

  test("it does not count for a row that touches no disk or install, nor for a vm row", () => {
    const idx = index(REAL_BODY);
    assert.equal(proofLevel(realFsProof[0], diskRow({ touches: "other-app" }), idx), 1);
    assert.equal(proofLevel(realFsProof[0], diskRow({ vm: true }), idx), 1);
  });

  test("a claim the check cannot verify counts as inproc and is reported, without failing the run", () => {
    const reg = register({ gapCeiling: 1 });
    reg.rows[0] = { ...reg.rows[0], severity: "high", touches: "disk", required: "real", proofs: realFsProof };
    const result = check(fixture({ [PLACEMENT]: csTest('        File.WriteAllText("a.mkv", "x");\n') }), reg);
    assert.deepEqual(result.missing, []);
    assert.equal(result.refused.length, 1);
    assert.match(result.refused[0].why, /creates no real temp folder/);
    assert.equal(result.totals.realFs.refused, 1);
    assert.equal(result.totals.gaps.length, 1);
  });

  test("a real-fs proof must name a C# test of the server or the tray", () => {
    const reg = register();
    reg.rows[7].proofs = [{ type: "real-fs", file: "apps/web/src/pages/thing-page.test.tsx", name: "opens the thing" }];
    assert.match(check(fixture(), reg).missing[0].why, /real-fs proof must be a C# test of the server or the tray/);
    const browser = "apps/server/tests/Weir.E2E.Tests/ThingTests.cs";
    reg.rows[7].proofs = [{ type: "real-fs", file: browser, name: "Places_the_file" }];
    assert.match(check(fixture({ [browser]: csTest(REAL_BODY) }), reg).missing[0].why, /real-fs proof must be a C# test of the server or the tray/);
  });

  test("the totals count claims, counted proofs and proofs without effect", () => {
    const reg = register({ gapCeiling: 1 });
    reg.rows[0] = { ...reg.rows[0], severity: "high", touches: "disk", required: "real", proofs: realFsProof };
    reg.rows[1] = { ...reg.rows[1], severity: "high", touches: "disk", required: "real", vm: true, proofs: realFsProof };
    const result = check(fixture({ [PLACEMENT]: csTest(REAL_BODY) }), reg);
    assert.deepEqual({ ...result.totals.realFs }, { claims: 2, counted: 1, refused: 0, noEffect: 1, vmRows: 1, vmRowGaps: 1 });
    assert.match(generatedSection(reg, buildInventory(fixture()), result.totals), /Real-fs proofs: \*\*2\*\* claimed; \*\*1\*\* verified/);
  });
});

describe("container-smoke proofs", () => {
  const WORKFLOW = ".github/workflows/ci-packaging.yml";
  const STEPS =
    "jobs:\n  docker-smoke:\n    steps:\n" +
    "      - name: Start Weir Docker candidate\n        run: docker compose -f compose.yaml up -d\n" +
    "      - name: Trivy scan of the Docker candidate\n        run: trivy image weir:candidate\n" +
    "      - name: Cleanup\n        run: docker rm -f weir\n";
  const dockerRow = (name, extra = {}) => ({ area: "platform", severity: "high", touches: "install", required: "real", proofs: [{ type: "container-smoke", file: WORKFLOW, name }], ...extra });
  const index = () => new TestIndex(fixture({ [WORKFLOW]: STEPS }));

  test("a step that starts the shipped container is real for an install row", () => {
    const idx = index();
    assert.equal(idx.containerSmoke(WORKFLOW, "Start Weir Docker candidate").real, true);
    const row = dockerRow("Start Weir Docker candidate");
    assert.equal(proofLevel(row.proofs[0], row, idx), 2);
    assert.equal(isGap(row, idx), false);
  });

  test("a step that does not start the container is refused and counts as inproc", () => {
    const idx = index();
    const verdict = idx.containerSmoke(WORKFLOW, "Trivy scan of the Docker candidate");
    assert.equal(verdict.real, false);
    assert.match(verdict.why, /does not start the container image/);
    const row = dockerRow("Trivy scan of the Docker candidate");
    assert.equal(proofLevel(row.proofs[0], row, idx), 1);
  });

  test("a step that is not in the workflow is named", () => {
    const reg = register();
    reg.rows[0] = { ...reg.rows[0], area: "api", severity: "high", touches: "install", required: "real", proofs: [{ type: "container-smoke", file: WORKFLOW, name: "A step nobody wrote" }] };
    const result = check(fixture({ [WORKFLOW]: STEPS }), reg);
    assert.match(result.missing[0].why, /no test, group or class "A step nobody wrote"/);
  });

  test("a script that starts the container is judged as a whole", () => {
    const script = "scripts/smoke-docker-compose.mjs";
    const idx = new TestIndex(fixture({ [script]: 'spawn("docker", ["compose", "up", "-d"]);\n// docker compose -f compose.yaml up -d\n' }));
    assert.equal(idx.containerSmoke(script, "*").real, true);
    assert.equal(new TestIndex(fixture({ [script]: "console.log('nothing started');\n" })).containerSmoke(script, "*").real, false);
  });

  test("it must name a packaging workflow or a container script", () => {
    const reg = register();
    reg.rows[0].proofs = [{ type: "container-smoke", file: "apps/server/tests/Weir.Api.Tests/ThingTests.cs", name: "Gets_the_things" }];
    assert.match(check(fixture(), reg).missing[0].why, /container-smoke proof must be a packaging workflow or a container script/);
  });

  test("it does not count for a vm row or a row that touches no disk or install, and the totals count it", () => {
    const idx = index();
    assert.equal(proofLevel(dockerRow("Start Weir Docker candidate").proofs[0], dockerRow("Start Weir Docker candidate", { vm: true }), idx), 1);
    assert.equal(proofLevel(dockerRow("Start Weir Docker candidate").proofs[0], dockerRow("Start Weir Docker candidate", { touches: "other-app" }), idx), 1);
    const reg = register({ gapCeiling: 0 });
    reg.rows[0] = { ...reg.rows[0], area: "api", severity: "high", touches: "install", required: "real", proofs: dockerRow("Start Weir Docker candidate").proofs };
    reg.rows[1] = { ...reg.rows[1], area: "api", severity: "high", touches: "install", required: "real", proofs: dockerRow("Trivy scan of the Docker candidate").proofs };
    reg.gapCeiling = 1;
    const result = check(fixture({ [WORKFLOW]: STEPS }), reg);
    assert.deepEqual({ ...result.totals.container }, { claims: 2, counted: 1, refused: 1, noEffect: 0 });
    assert.equal(result.refused.length, 1);
  });
});

describe("vm rows", () => {
  const index = { stubInfo: () => ({ stubbed: false, decided: true }), realFs: () => ({ real: true, why: "" }) };
  const vmRow = (extra = {}) => ({ area: "tray", severity: "high", touches: "install", required: "real", vm: true, ...extra });

  test("a vm row reaches real by a scenario alone", () => {
    assert.equal(proofLevel({ type: "scenario" }, vmRow(), index), 2);
    for (const type of ["real-fs", "recording-replay", "e2e", "xunit", "contract"]) assert.equal(proofLevel({ type }, vmRow(), index), 1, type);
    assert.equal(isGap(vmRow({ proofs: [{ type: "real-fs" }] }), index), true);
    assert.equal(isGap(vmRow({ proofs: [{ type: "real-fs" }, { type: "scenario" }] }), index), false);
  });

  test("a vm row that needs only inproc is proven by any proof", () => {
    assert.equal(isGap(vmRow({ severity: "medium", required: "inproc", proofs: [{ type: "xunit" }] }), index), false);
  });

  test("vm may only be true, and only on a row that touches disk or install", () => {
    const reg = register();
    reg.rows[0].vm = false;
    reg.rows[1].vm = true;
    const problems = validateShape(reg).join("\n");
    assert.match(problems, /vm may only be true/);
    assert.match(problems, /vm is for a row that touches disk or install, not none/);
    delete reg.rows[0].vm;
    reg.rows[1].touches = "disk";
    assert.deepEqual(validateShape(reg), []);
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
