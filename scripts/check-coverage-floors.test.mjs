// node --test scripts/check-coverage-floors.test.mjs
// The coverage ratchet, read against the real reports ReportGenerator and vitest write (scripts/fixtures/coverage).
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  PROJECTS,
  evaluate,
  figuresFromSummary,
  figuresFromVitest,
  filesFromCobertura,
  filesFromVitest,
  floorTenth,
  leastCovered,
  loweredFloorProblems,
  parseArguments,
  readFloors,
  run,
  sourceName,
} from "./check-coverage-floors.mjs";

const fixtures = path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "coverage");
const readFixture = (...parts) => readFileSync(path.join(fixtures, ...parts), "utf8");

function floorsFile({ measuredFrom = { run: 123 }, floors = {}, lowered, base = 50 } = {}) {
  const all = {};
  for (const project of PROJECTS) all[project.id] = { line: base, branch: base, ...floors[project.id] };
  return { measuredFrom, floors: all, ...(lowered ? { lowered } : {}) };
}

const figure = (covered, total) => ({ covered, total });
const figures = (line, branch) => ({ line, branch });

test("a floor is the figure rounded down to one decimal", () => {
  assert.equal(floorTenth(figure(9174, 10000)), 91.7);
  assert.equal(floorTenth(figure(9169, 10000)), 91.6);
  assert.equal(floorTenth(figure(1, 3)), 33.3);
  assert.equal(floorTenth(figure(7, 7)), 100);
  assert.equal(floorTenth(figure(0, 0)), 100, "nothing to cover is fully covered");
});

test("a floors file must name the run it was measured from and a floor per project and measure", () => {
  assert.doesNotThrow(() => readFloors(JSON.stringify(floorsFile())));
  assert.throws(() => readFloors(JSON.stringify(floorsFile({ measuredFrom: {} }))), /measuredFrom\.run/);
  const missing = floorsFile();
  delete missing.floors.tray.branch;
  assert.throws(() => readFloors(JSON.stringify(missing)), /floors\.tray\.branch/);
  assert.throws(() => readFloors(JSON.stringify(floorsFile({ floors: { web: { line: 101 } } }))), /floors\.web\.line/);
  assert.throws(() => readFloors("{"), /not valid JSON/);
});

test("a figure at its floor passes", () => {
  const floors = floorsFile({ floors: { "server-core": { line: 91.7, branch: 82.1 } } });
  const result = evaluate(floors, { "server-core": figures(figure(15011, 16368), figure(7566, 9213)) });
  assert.deepEqual(result.problems, []);
  assert.equal(result.rows.find((row) => row.project.id === "server-core").status, "ok");
});

test("a figure below its floor fails and names the project and the measure", () => {
  const floors = floorsFile({ floors: { "server-core": { line: 91.8, branch: 82.1 } } });
  const result = evaluate(floors, { "server-core": figures(figure(15011, 16368), figure(7566, 9213)) });
  assert.equal(result.problems.length, 1);
  assert.match(result.problems[0], /Weir\.Core line coverage is 91\.7% \(15011 of 16368\), below its floor of 91\.8%/);
});

test("a figure that rounds down to just under its floor fails", () => {
  const floors = floorsFile({ floors: { web: { branch: 82.8 } } });
  const result = evaluate(floors, { web: figures(figure(100, 100), figure(7412, 8955)) });
  assert.match(result.problems.join("\n"), /Web app branch coverage is 82\.7%/);
});

test("both measures can fail at once, each named", () => {
  const result = evaluate(floorsFile({ floors: { tray: { line: 99, branch: 99 } } }), { tray: figures(figure(50, 100), figure(50, 100)) });
  assert.equal(result.problems.length, 2);
  assert.match(result.problems[0], /Tray line/);
  assert.match(result.problems[1], /Tray branch/);
});

test("a project that was not run is not judged", () => {
  const result = evaluate(floorsFile(), {});
  assert.deepEqual(result.problems, []);
  assert.ok(result.rows.every((row) => row.status === "not run"));
});

test("a report that should be there and is not fails", () => {
  const result = evaluate(floorsFile(), { tray: { missing: "Summary.json was not written" } });
  assert.deepEqual(result.problems, ["Tray: Summary.json was not written"]);
});

test("code with no branches is fully branch covered", () => {
  const result = evaluate(floorsFile({ floors: { tray: { line: 90, branch: 100 } } }), { tray: figures(figure(95, 100), figure(0, 0)) });
  assert.deepEqual(result.problems, []);
});

test("a floor lowered without its own entry fails, naming the project and the figures", () => {
  const base = floorsFile({ floors: { web: { line: 88.3, branch: 82.7 } } });
  const lowered = floorsFile({ floors: { web: { line: 88.3, branch: 80 } } });
  const { problems } = loweredFloorProblems(lowered, base);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /Web app branch floor was lowered from 82\.7% to 80%/);
  assert.match(problems[0], /pull request of its own/);
});

test("a floor lowered with an entry that names both figures and a reason passes, and says so", () => {
  const base = floorsFile({ floors: { web: { branch: 82.7 } } });
  const lowered = floorsFile({
    floors: { web: { branch: 80 } },
    lowered: { "web.branch": { from: 82.7, to: 80, reason: "The generated client moved into src; it is not ours to test." } },
  });
  const result = loweredFloorProblems(lowered, base);
  assert.deepEqual(result.problems, []);
  assert.match(result.notes.join("\n"), /Web app branch floor LOWERED from 82\.7% to 80%: The generated client/);
});

test("a lowered entry that does not match the change, or gives no reason, does not excuse it", () => {
  const base = floorsFile({ floors: { web: { branch: 82.7 } } });
  for (const entry of [{ from: 82.7, to: 79, reason: "x" }, { from: 90, to: 80, reason: "x" }, { from: 82.7, to: 80, reason: "  " }, { from: 82.7, to: 80 }]) {
    const lowered = floorsFile({ floors: { web: { branch: 80 } }, lowered: { "web.branch": entry } });
    assert.equal(loweredFloorProblems(lowered, base).problems.length, 1, JSON.stringify(entry));
  }
});

test("a floor that rises or stays is never a problem, and a rise is noted", () => {
  const base = floorsFile({ floors: { web: { line: 88.3 } } });
  const result = loweredFloorProblems(floorsFile({ floors: { web: { line: 89 } } }), base);
  assert.deepEqual(result.problems, []);
  assert.match(result.notes.join("\n"), /Web app line floor raised from 88\.3% to 89%/);
  assert.deepEqual(loweredFloorProblems(base, base), { problems: [], notes: [] });
});

test("ReportGenerator's summary gives each assembly's counts, and the host is the assembly named Weir", () => {
  const summary = JSON.parse(readFixture("server", "Summary.json"));
  const core = figuresFromSummary(summary, "Weir.Core");
  assert.deepEqual(core.line, { covered: 60, total: 109 });
  assert.deepEqual(core.branch, { covered: 45, total: 112 });
  assert.ok(figuresFromSummary(summary, "Weir").line.total > 0);
  assert.equal(figuresFromSummary(summary, "Weir.Missing"), undefined);
});

test("vitest's summary gives the whole app's lines and branches", () => {
  const summary = JSON.parse(readFixture("web", "coverage-summary.json"));
  assert.deepEqual(figuresFromVitest(summary), figures(figure(9588, 10850), figure(7412, 8955)));
  assert.equal(figuresFromVitest({ total: {} }), undefined);
});

test("file names read the same whichever folder or slash a tool wrote", () => {
  assert.equal(sourceName("/home/runner/work/Weir/Weir/apps/server/src/Weir.Host/Program.cs", "Weir.Host"), "Weir.Host/Program.cs");
  assert.equal(sourceName("C:\\a\\Weir\\apps\\server\\src\\Weir.Core\\Json\\WireConvert.cs", "Weir.Core"), "Weir.Core/Json/WireConvert.cs");
  assert.equal(sourceName("/home/runner/work/Weir/Weir/apps/tray/Weir.Tray/Debounce.cs", "Weir.Tray"), "Weir.Tray/Debounce.cs");
  assert.equal(sourceName("/home/runner/work/Weir/Weir/apps/web/src/lib/api/auth-api.ts", ""), "src/lib/api/auth-api.ts");
  assert.equal(sourceName("Auth/RateLimiting.cs", "Weir.Infrastructure"), "Weir.Infrastructure/Auth/RateLimiting.cs");
});

test("files are counted from each class's own lines, with every class of a file added up", () => {
  const files = filesFromCobertura(readFixture("server", "Cobertura.xml"));
  const api = files.filter((file) => file.assembly === "Weir.Api");
  assert.equal(api.length, 1, "three classes share ApiHttp.cs");
  const summary = JSON.parse(readFixture("server", "Summary.json"));
  const fromSummary = figuresFromSummary(summary, "Weir.Api");
  assert.deepEqual(api[0].line, fromSummary.line, "the report's own line count");
  assert.deepEqual(api[0].branch, fromSummary.branch, "the report's own branch count");
  const total = (assembly, measure) => files.filter((file) => file.assembly === assembly).reduce((sum, file) => sum + file[measure].covered, 0);
  for (const assembly of ["Weir", "Weir.Core", "Weir.Infrastructure"]) {
    assert.equal(total(assembly, "line"), figuresFromSummary(summary, assembly).line.covered, assembly);
    assert.equal(total(assembly, "branch"), figuresFromSummary(summary, assembly).branch.covered, assembly);
  }
});

test("the least covered files are the ones with the most lines and branches never run, ten at most", () => {
  const files = filesFromCobertura(readFixture("server", "Cobertura.xml")).filter((file) => file.assembly === "Weir.Infrastructure");
  const ranked = leastCovered(files, "Weir.Infrastructure");
  assert.ok(ranked.length > 0 && ranked.length <= 10);
  const missing = ranked.map((file) => file.missing);
  assert.deepEqual(missing, [...missing].sort((a, b) => b - a));
  assert.ok(ranked.every((file) => file.missing > 0), "a fully covered file is never a target");
  assert.ok(ranked.every((file) => file.name.startsWith("Weir.Infrastructure/")));

  const many = Array.from({ length: 14 }, (_, index) => ({ filename: `F${index}.cs`, line: figure(0, index + 1), branch: figure(0, 0) }));
  assert.equal(leastCovered(many, "X").length, 10);
  assert.equal(leastCovered(many, "X")[0].name, "X/F13.cs");
});

test("the web app's files come from the same summary file", () => {
  const files = filesFromVitest(JSON.parse(readFixture("web", "coverage-summary.json")));
  assert.equal(files.length, 5);
  const ranked = leastCovered(files, "");
  assert.equal(ranked[0].name, "src/lib/settings/settings-api.ts");
  assert.ok(ranked.some((file) => file.name === "src/components/activity/structured-detail-facts.tsx" && file.line.covered === 0));
});

// run() against report folders written to disk, the way CI lays them out.
function withFolders(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-coverage-"));
  try {
    const put = (name, ...parts) => {
      mkdirSync(path.join(root, name), { recursive: true });
      return (file) => writeFileSync(path.join(root, name, file), readFixture(...parts, file));
    };
    const folders = {
      server: path.join(root, "server"),
      tray: path.join(root, "tray"),
      web: path.join(root, "web"),
    };
    callback({ root, folders, put });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function floorsAt(root, content) {
  const file = path.join(root, "floors.json");
  writeFileSync(file, JSON.stringify(content));
  return file;
}

test("a run over real reports passes at the measured figures and lists the files to work on", () => {
  withFolders(({ root, folders, put }) => {
    for (const file of ["Summary.json", "Cobertura.xml"]) {
      put("server", "server")(file);
      put("tray", "tray")(file);
    }
    put("web", "web")("coverage-summary.json");
    const summary = JSON.parse(readFixture("server", "Summary.json"));
    const floors = floorsFile({
      base: 0,
      floors: {
        "server-core": { line: floorTenth(figuresFromSummary(summary, "Weir.Core").line), branch: floorTenth(figuresFromSummary(summary, "Weir.Core").branch) },
        web: { line: 88.3, branch: 82.7 },
      },
    });
    const result = run({ floorsFile: floorsAt(root, floors), folders });
    assert.deepEqual(result.problems, []);
    assert.match(result.text, /Test coverage/);
    assert.match(result.text, /Least covered in Server: Weir\.Core/);
    assert.match(result.text, /Weir\.Core\/Json\/WireConvert\.cs/);
  });
});

test("deleting a well-covered test is caught: the same reports against a higher floor fail, naming project and measure", () => {
  withFolders(({ root, folders, put }) => {
    put("web", "web")("coverage-summary.json");
    const result = run({ floorsFile: floorsAt(root, floorsFile({ floors: { web: { line: 88.4, branch: 82.7 } } })), folders: { web: folders.web } });
    assert.equal(result.problems.length, 1);
    assert.match(result.problems[0], /Web app line coverage is 88\.3%.*floor of 88\.4%/);
    assert.match(result.text, /BELOW FLOOR/);
  });
});

test("a folder that was asked for and holds no report fails", () => {
  withFolders(({ root, folders }) => {
    mkdirSync(folders.tray);
    const result = run({ floorsFile: floorsAt(root, floorsFile()), folders: { tray: folders.tray } });
    assert.equal(result.problems.length, 1);
    assert.match(result.problems[0], /^Tray: .*Summary\.json was not written/);
    assert.match(result.text, /NO REPORT/);
  });
});

test("a server report with no assembly for a project fails that project only", () => {
  withFolders(({ root, folders, put }) => {
    put("server", "server")("Summary.json");
    const summaryFile = path.join(folders.server, "Summary.json");
    const summary = JSON.parse(readFileSync(summaryFile, "utf8"));
    summary.coverage.assemblies = summary.coverage.assemblies.filter((assembly) => assembly.name !== "Weir.Api");
    writeFileSync(summaryFile, JSON.stringify(summary));
    const result = run({ floorsFile: floorsAt(root, floorsFile({ base: 0 })), folders: { server: folders.server } });
    assert.equal(result.problems.length, 1);
    assert.match(result.problems[0], /^Server: Weir\.Api: .*no assembly named Weir\.Api/);
  });
});

test("a project that was not asked for is shown as not run and does not fail", () => {
  withFolders(({ root }) => {
    const result = run({ floorsFile: floorsAt(root, floorsFile()), folders: {} });
    assert.deepEqual(result.problems, []);
    assert.match(result.text, /not run for this change/);
  });
});

test("a lowered floor is caught against the base file in a run", () => {
  withFolders(({ root, folders, put }) => {
    put("web", "web")("coverage-summary.json");
    const base = floorsAt(root, floorsFile({ floors: { web: { line: 88.3, branch: 82.7 } } }));
    const baseCopy = path.join(root, "base-floors.json");
    writeFileSync(baseCopy, readFileSync(base));
    const lowered = floorsAt(root, floorsFile({ floors: { web: { line: 80, branch: 82.7 } } }));
    const result = run({ floorsFile: lowered, baseFloorsFile: baseCopy, folders: { web: folders.web } });
    assert.equal(result.problems.length, 1);
    assert.match(result.problems[0], /Web app line floor was lowered from 88\.3% to 80%/);
  });
});

test("command line options are read, and anything else is refused", () => {
  assert.deepEqual(parseArguments(["--floors", "f.json", "--server", "s", "--web", "w"]), { floorsFile: "f.json", folders: { server: "s", web: "w" } });
  assert.equal(parseArguments(["--floors", "f.json", "--base-floors", "b.json"]).baseFloorsFile, "b.json");
  assert.throws(() => parseArguments(["--server", "s"]), /--floors/);
  assert.throws(() => parseArguments(["--floors", "f.json", "--nope", "x"]), /Unknown option/);
  assert.throws(() => parseArguments(["--floors"]), /needs a value/);
});
