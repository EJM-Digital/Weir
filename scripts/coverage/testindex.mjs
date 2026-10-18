// Finds every test in the repository and the names a coverage-register proof may cite.
//
// A proof in docs/testing/coverage-register.json is { type, file, name }. The check (scripts/check-coverage-register.mjs)
// asks this index whether `file` exists and `name` is a test, a test group or a class in it. No dependencies: plain node.
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";

const SKIP_DIRS = new Set(["bin", "obj", "node_modules", ".git", "TestResults", "dist", "playwright-report", "test-results"]);

export function walk(dir, accept, out = []) {
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue;
    const full = join(dir, entry);
    let info;
    try {
      info = statSync(full);
    } catch {
      continue;
    }
    if (info.isDirectory()) walk(full, accept, out);
    else if (accept(full)) out.push(full);
  }
  return out;
}

export const slash = (p) => p.split("\\").join("/");
export const read = (file) => readFileSync(file, "utf8").replace(/^\uFEFF/, "");

const E2E_TESTS = "apps/server/tests/Weir.E2E.Tests/";
const CONTRACT_TESTS = "apps/server/tests/Weir.Contract.Tests/";
/** A scenario proof: a golden-VM script, or a module of the scenarios that run in CI (scripts/scenarios-ci/), where each scenario is a quoted key of the module's registry. */
export const SCENARIO_FILE = /^scripts\/scenarios\/.+\.ps1$|^scripts\/scenarios-ci\/[a-z]+(?:-[a-z]+)*\.mjs$/;
/** The Windows package smokes: the package itself and the recovery command. */
export const PACKAGE_SMOKE_FILE = /^scripts\/smoke-windows-[a-z]+\.ps1$/;
const LIVE_AUDIT = "apps/server/tools/Weir.LiveAudit/";
/** A packaging script that starts the shipped container (and, below, the workflows whose steps do). */
const CONTAINER_SCRIPT = /(?:^|\/)scripts\/(?:[^/]*\/)*[^/]*(?:docker|compose|container)[^/]*\.(?:mjs|ps1|sh)$/;
export const CONTAINER_SMOKE_FILE = /^(?:\.github\/workflows\/[^/]+\.ya?ml|scripts\/(?:[^/]*\/)*[^/]*(?:docker|compose|container)[^/]*\.(?:mjs|ps1|sh))$/;

/** Every file that can hold a proof, as repo-relative paths. */
export function testFiles(root) {
  const files = [];
  const add = (dir, accept) => walk(join(root, dir), accept, files);
  add("apps/server/tests", (f) => f.endsWith(".cs") && !/[\\/]fixtures[\\/]/.test(f));
  add("apps/tray/Weir.Tray.Tests", (f) => f.endsWith(".cs"));
  add("apps/server/tools/Weir.LiveAudit", (f) => f.endsWith(".cs"));
  add("apps/web/src", (f) => /\.test\.tsx?$/.test(f));
  add("scripts", (f) => /\.test\.mjs$/.test(f) || /[\\/]scenarios[\\/].*\.ps1$/.test(f) || SCENARIO_FILE.test(slash(relative(root, f))) ||/scripts[\\/]smoke-windows-[a-z]+\.ps1$/.test(f) || CONTAINER_SCRIPT.test(slash(f)));
  add(".github/workflows", (f) => /\.ya?ml$/.test(f));
  return files.map((f) => slash(relative(root, f)));
}

const TS_TITLE = /\b(?:it|test|describe)(?:\.(?:each|skip|only|fixme|fails|concurrent|serial|describe|configure))*(?:\([^)]*\))?\(\s*(['"`])((?:\\.|(?!\1)[^\\\n])+)\1/g;
const CS_METHOD = /\[(?:[A-Za-z0-9]*Fact|[A-Za-z0-9]*Theory)\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:public\s+|internal\s+)?(?:static\s+)?(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)/g;
const CS_CLASS = /\b(?:class|record)\s+(\w+)/g;
const RECORDING = /\bArrRecording\b|\bRealStackSide\b|\bRecordedDeluno\b|\bDelunoSide\b|\bRecordedWorld\b|\bRecordedReplies\b|fixtures[\\/]+recorded/;

// real-fs: the test (or its class setup) creates a real temp folder or a real SQLite file, works on it with real file or database code,
// and neither names a fake or in-memory file system or store. What cannot be decided is refused.
const FS_CREATE =
  /Path\.GetTempPath\s*\(|CreateTempSubdirectory\s*\(|Path\.GetTempFileName\s*\(|\b(?:TempDirectory|TemporaryFolder|JobsTestDatabase|StoreFixture|MediaManagerFixture)\b|\bMigratedDatabaseTemplate\b|\b(?:WeirTestServer|WeirServer|SwapWorld|LibraryCleanWorld|LeasedJobScenario|LibrariesPartBFilm|Scenario)\.Start\w*\s*\(|\b(?:Server|Users|SeededServer|HandoffServer|ManagerServer)Fixture\b|\bSwapCrashRecoveryChecks\.RunAsync/;
const FS_WORK = /\b(?:File|Directory|FileInfo|DirectoryInfo)\.|\bnew\s+(?:FileInfo|DirectoryInfo)\b|CreateHardLink|SqliteConnection|SqliteDatabase|UnitOfWork|SchemaMigrator|\bMigrat\w*|\bSeedSql\.|\bStopForDatabaseAsync\b|\bRealMedia\.|\.(?:AddFilmAsync|FilesOnDisk|AssertNoSwapLeftovers)\(|\bSwapCrashRecoveryChecks\.RunAsync/;
const FS_FAKE =
  /ScriptedStateFileSystem|\bSwapScenario\b|\bI\w*FileSystem\b|\bIFolderProbe\b|\bIHardlinkInspector\b|\bISwapJournal\b|\bIOutputOwnership\w*\b|\bIRemuxPassFileFacts\b|\bIRemovedTrackStore\b|\bIPostSuccessCleanupData\b|\bITvSeasonFolderCleanup\b|\bIUpdateBackup\b|MockFileSystem|MemoryFileSystem|\bFake\w*(?:FileSystem|Storage|Repository|Database|Store|Journal|Probe|Disk|Volume)\b|\bInMemory\w*|\bNull\w*Repository\b|:memory:|Mode=Memory|new\s+Mock<|Substitute\.For</;

// container-smoke: a packaging step or script that starts the shipped container image (docker run, or docker compose up on the shipped file).
const CONTAINER_START = /\bdocker\s+(?:compose\s+(?:-\S+\s+\S+\s+)*up|run)\b/;

/** C# test files a real-fs claim may name: the server's and the tray's, never the browser tests. */
export const REAL_FS_FILE = /^apps\/(?:server\/tests\/(?!Weir\.E2E\.Tests\/)|tray\/Weir\.Tray\.Tests\/).+\.cs$/;

/** The names inside one test file: its tests, its groups and (C#) its classes. */
export function namesOf(file, text) {
  const names = new Set();
  if (file.endsWith(".cs")) {
    for (const m of text.matchAll(CS_METHOD)) names.add(m[1]);
    for (const m of text.matchAll(CS_CLASS)) names.add(m[1]);
  } else if (file.endsWith(".ps1")) {
    for (const m of text.matchAll(/^\s*function\s+([\w-]+)/gim)) names.add(m[1]);
    for (const m of text.matchAll(/\bId\s*=\s*'([a-z][a-z0-9-]*)'/g)) names.add(m[1]);
  } else if (SCENARIO_FILE.test(file) && file.endsWith(".mjs")) {
    for (const m of text.matchAll(/^\s+"([a-z][a-z0-9]*(?:-[a-z0-9]+)+)":\s/gm)) names.add(m[1]);
  } else if (/\.ya?ml$/.test(file)) {
    for (const m of text.matchAll(/^\s*-\s+name:\s*(.+?)\s*$/gm)) names.add(m[1].replace(/^["']|["']$/g, ""));
  } else {
    for (const m of text.matchAll(TS_TITLE)) names.add(m[2]);
  }
  return names;
}

/** Lower-case letters and digits only, so a title cited with other quotes, spaces or punctuation still finds itself. */
export const squash = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, "");

export class TestIndex {
  constructor(root) {
    this.root = root;
    this.files = new Map(); // rel -> { text, names:Set, squashed:Set, hay:string, header, bodies }
    for (const rel of testFiles(root)) {
      const text = read(join(root, rel));
      const names = namesOf(rel, text);
      this.files.set(rel, { text, names, squashed: new Set([...names].map(squash)), hay: squash(text), ...csBodies(rel, text) });
    }
    this.byName = new Map(); // exact name -> [files]  (C# methods, classes, scenario ids)
    for (const [rel, f] of this.files) {
      for (const n of f.names) {
        if (!this.byName.has(n)) this.byName.set(n, []);
        this.byName.get(n).push(rel);
      }
    }
  }

  has(file) {
    return this.files.has(file);
  }

  /** A cited name exists when it is a test, group or class of the file, or (for a long enough phrase) its squashed text is in the file. */
  hasName(file, name) {
    const f = this.files.get(file);
    if (!f) return false;
    if (name === "*") return true;
    if (f.names.has(name)) return true;
    const q = squash(name);
    if (q.length === 0) return false;
    if (f.squashed.has(q)) return true;
    // A test title cited as the start of a longer title, or the other way round, is the same test.
    if (q.length >= 14) {
      for (const s of f.squashed) if (s.startsWith(q) || (s.length >= 14 && q.startsWith(s))) return true;
    }
    return q.length >= 20 && f.hay.includes(q);
  }

  /**
   * Is the proof a step of a packaging workflow (or a packaging script) that starts the shipped container image?
   * Decided statically from the step's text: a named step is judged alone, a script or "*" as a whole. { real, why }.
   */
  containerSmoke(file, name) {
    const f = this.files.get(file);
    if (!f || !CONTAINER_SMOKE_FILE.test(file)) return { real: false, why: "not a packaging workflow or container script" };
    let text = f.text;
    if (/\.ya?ml$/.test(file) && name && name !== "*") {
      const q = squash(name);
      const step = f.text.split(/^(?=\s*-\s+name:)/m).find((s) => squash(/^\s*-\s+name:\s*(.+)/.exec(s)?.[1] ?? "") === q);
      if (step === undefined) return { real: false, why: `no step named "${name}" to judge` };
      text = step;
    }
    return CONTAINER_START.test(text) ? { real: true, why: "" } : { real: false, why: `${name || file} does not start the container image with docker run or docker compose up` };
  }

  /**
   * Is the test a real-fs test: it runs Weir's real code against real files in a real temp folder, or a real SQLite file?
   * Decided statically from the class setup (the text before its first test) plus the test's own body.
   * A named test is judged alone; a class name, a cited fragment or "*" is judged over every test it covers, and every one must qualify.
   * { real, why }: what cannot be decided is not real.
   */
  realFs(file, name) {
    const f = this.files.get(file);
    if (!f || !REAL_FS_FILE.test(file)) return { real: false, why: "not a C# test of the server or the tray" };
    const header = f.header ?? "";
    let scope;
    const body = name && name !== "*" ? f.bodies?.get(name) : undefined;
    if (body !== undefined) scope = [[name, body]];
    else {
      const all = [...(f.bodies ?? [])];
      if (!name || name === "*" || f.names.has(name)) scope = all;
      else {
        const q = squash(name);
        scope = all.filter(([n]) => squash(n).startsWith(q));
      }
    }
    if (scope.length === 0) return { real: false, why: "no test found to judge" };
    for (const [n, text] of scope) {
      const both = `${header}\n${text}`;
      if (FS_FAKE.test(both)) return { real: false, why: `${n} uses a fake or in-memory file system or store` };
      if (!FS_CREATE.test(both)) return { real: false, why: `${n} (or its class setup) creates no real temp folder or database file` };
      if (!FS_WORK.test(both)) return { real: false, why: `${n} does no file or database work that can be seen` };
    }
    return { real: true, why: "" };
  }

  /**
   * Is an e2e test's API stubbed? Decided from the test's text: stubbed when its body, or the part of its file before the first test
   * (fixtures and helpers it uses), intercepts requests. What cannot be decided (the test is not found, or the name is the whole
   * file) counts as stubbed and not decided: { stubbed, decided }.
   */
  stubInfo(file, name) {
    const f = this.files.get(file);
    if (!f || !file.endsWith(".cs")) return { stubbed: true, decided: false };
    // A whole file or a class is stubbed when any of its text intercepts requests, and decided either way.
    if (!name || name === "*" || (f.names.has(name) && !f.bodies?.has(name))) return { stubbed: STUB_MARKER.test(f.text), decided: true };
    let body = f.bodies?.get(name);
    if (body === undefined) {
      const q = squash(name);
      for (const [title, text] of f.bodies ?? []) {
        if (squash(title).startsWith(q)) {
          body = text;
          break;
        }
      }
    }
    if (body === undefined) return { stubbed: true, decided: false };
    return { stubbed: STUB_MARKER.test(body) || STUB_MARKER.test(f.header ?? ""), decided: true };
  }

  /**
   * Does the test replay a recorded real reply of another app (Deluno's own recordings)? A recording loaded in the class's setup
   * (before its first test) counts for every test in it; otherwise the test's own body must load one. With no name (or "*"), any
   * test of the file will do.
   */
  replaysRecording(file, name) {
    const f = this.files.get(file);
    if (!f || !file.endsWith(".cs")) return false;
    if (RECORDING.test(f.header ?? "")) return true;
    if (!name || name === "*") return RECORDING.test(f.text);
    const body = f.bodies?.get(name);
    if (body !== undefined) return RECORDING.test(body);
    const q = squash(name);
    for (const [n, text] of f.bodies ?? []) if (squash(n).startsWith(q) && RECORDING.test(text)) return true;
    return false;
  }
}

/** Page.RouteAsync and friends intercept the browser's requests, so the screen is not talking to the real server. */
const STUB_MARKER = /\bRouteAsync\(|\bFulfillAsync\(|\bRouteFromHARAsync\(/;

/** Splits a C# test file into the text before its first test and the body of each test. */
function csBodies(file, text) {
  if (!file.endsWith(".cs")) return {};
  const starts = [...text.matchAll(CS_METHOD)].map((m) => ({ name: m[1], at: m.index }));
  if (starts.length === 0) return { header: text, bodies: new Map() };
  const bodies = new Map();
  for (let i = 0; i < starts.length; i++) bodies.set(starts[i].name, text.slice(starts[i].at, i + 1 < starts.length ? starts[i + 1].at : text.length));
  return { header: text.slice(0, starts[0].at), bodies };
}

/**
 * The kind of proof a file is: xunit (the server's and the tray's unit, integration and API tests), contract (the contract suite),
 * e2e (Playwright for .NET against the real host), vitest, node (the repository scripts' tests), scenario (a golden-VM scenario),
 * recording-replay (a test that replays Deluno's recorded real reply), real-fs (a test the check verifies runs on a real temp folder or
 * SQLite file with no fake), package-smoke (the Windows package smokes and the live audit).
 */
export function proofTypeOf(file, index, name) {
  if (PACKAGE_SMOKE_FILE.test(file) || file.startsWith(LIVE_AUDIT)) return "package-smoke";
  if (SCENARIO_FILE.test(file)) return "scenario";
  if (file.startsWith(E2E_TESTS)) return "e2e";
  if (/\.test\.tsx?$/.test(file)) return "vitest";
  if (/\.test\.mjs$/.test(file)) return "node";
  if (file.startsWith(CONTRACT_TESTS) && index && index.replaysRecording(file, name)) return "recording-replay";
  if (index && index.realFs(file, name).real) return "real-fs";
  return file.startsWith(CONTRACT_TESTS) ? "contract" : "xunit";
}
