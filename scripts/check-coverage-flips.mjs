#!/usr/bin/env node
// Finds the places in the server's code that CI runs reached in some runs and not in others, so a coverage floor that turns red
// with no change to the code is caught the day a test starts depending on timing, not at release.
//
//   node scripts/check-coverage-flips.mjs <folder> [--current-tree <tree id of apps/server on main>]
//
// <folder> holds one subfolder per CI run (scripts/fetch-coverage-runs.mjs writes it), each with the run's two server
// coverage artifacts, `coverage-server-linux` and `coverage-server-windows`, as the `coverage` job in ci.yml downloads them.
// Each run is merged the way that job merges it: the reports are aligned (scripts/align-coverage-reports.mjs) and a line or branch
// counts as reached when any report of either system reached it, which is what ReportGenerator's union does.
//
// Runs are compared only with runs that tested identical server code. Each run folder carries a run.json with the tree id of
// apps/server at the commit the run tested (the fetch writes it), and the runs with the same id form a group. Within a group, every
// line and branch reached in some runs and not in others is listed. A run that is the only one of its code is left alone and named,
// because a difference between different code is a change, not a flip.
//
// A run that lacks either system's reports, or its run.json, is left out and named: with one system missing, a branch only the
// other system reaches (`if (OperatingSystem.IsWindows())`) would look like a flip. A point is compared only across the runs of its
// group that list it.
//
// With --current-tree, only flips in runs of that code fail the check: the workflow passes the tree id of apps/server on main, because
// a flip in code that has since changed is already fixed or gone. The flips of any other group are printed as "in code no longer on
// main, for information". The check stays offline: the id is given, not looked up.
//
// Exit 0: nothing flips in the code that counts, or no two runs tested it. Exit 1: something flips. Exit 2: the folder is missing or cannot be read.
import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { align, coberturaFiles } from "./align-coverage-reports.mjs";

/** The artifacts of the CI server legs; the folders of a run are named after them. */
export const LEGS = [
  { artifact: "coverage-server-linux", system: "Linux" },
  { artifact: "coverage-server-windows", system: "Windows" },
];

/** Where every report is pointed, so the two systems' files name the same sources. Only the part from /apps/ is kept. */
const CHECKOUT = "/checkout";

const TAG = /<Module[ >]|<File ([^>]*)>|<Name>([^<]*)<\/Name>|<(SequencePoint|BranchPoint) ([^>]*)>/g;
const ATTRIBUTE = /([A-Za-z]+)="([^"]*)"/g;

function attributes(text) {
  return Object.fromEntries([...text.matchAll(ATTRIBUTE)].map(([, name, value]) => [name, value]));
}

function decode(text) {
  return text.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, "&");
}

/** The shipped source file a report's path names (`apps/server/src/...`), or undefined for tests and generated code under obj/. */
export function sourceOf(fullPath) {
  const name = fullPath.replace(/\\/g, "/");
  const relative = name.slice(name.indexOf("/apps/") + 1);
  return relative.startsWith("apps/server/src/") && !relative.includes("/obj/") ? relative : undefined;
}

/** Adds the lines and branches of one aligned OpenCover report to `points`, keyed by where they are; a point any report reached stays reached. */
export function addPoints(text, points = new Map()) {
  let files = new Map();
  let method = "";
  for (const [tag, fileAttributes, name, kind, pointAttributes] of text.matchAll(TAG)) {
    if (tag.startsWith("<Module")) {
      files = new Map();
    } else if (fileAttributes !== undefined) {
      const file = attributes(fileAttributes);
      files.set(file.uid, sourceOf(file.fullPath));
    } else if (name !== undefined) {
      method = decode(name);
    } else {
      const point = attributes(pointAttributes);
      const file = files.get(point.fileid);
      if (file === undefined) continue;
      const branch = kind === "BranchPoint";
      const key = branch ? [file, method, "branch", point.sl, point.offset, point.path].join("\t") : [file, method, "line", point.sl, point.sc].join("\t");
      const visited = Number(point.vc) > 0;
      const known = points.get(key);
      if (known === undefined) points.set(key, { file, method, branch, line: Number(point.sl), offset: point.offset, path: point.path, visited });
      else if (visited) known.visited = true;
    }
  }
  return points;
}

/** One run's merged points, and the systems that left no report. */
export function readRun(runFolder) {
  const points = new Map();
  const missing = [];
  for (const { artifact, system } of LEGS) {
    const folder = path.join(runFolder, artifact);
    const reports = existsSync(folder) ? coberturaFiles(folder).filter((file) => path.basename(file) === "coverage.opencover.xml") : [];
    if (reports.length === 0) missing.push(system);
    for (const report of reports) addPoints(align(readFileSync(report, "utf8"), CHECKOUT), points);
  }
  return { points, missing };
}

/** The file a fetched run carries, naming the commit it tested and the tree id of apps/server at that commit. */
export const RUN_RECORD = "run.json";

/** The code id a run's record holds, or undefined when there is no usable record. */
export function treeOfRun(runFolder) {
  const file = path.join(runFolder, RUN_RECORD);
  if (!existsSync(file)) return undefined;
  try {
    return JSON.parse(readFileSync(file, "utf8")).tree || undefined;
  } catch {
    return undefined;
  }
}

// Every key it orders is different, so there is no tie to settle.
const byOrder = (a, b) => (a < b ? -1 : 1);

/** Orders points by file, method, then line, lines before branches, offset and path (numbers padded so they sort as numbers). */
const orderKey = (point) => [point.file, point.method, ...[point.line, point.branch ? 1 : 0, point.offset, point.path].map((value) => String(value ?? 0).padStart(9, "0"))].join("\t");

/**
 * Compares the runs under `folder`, only against runs that tested the same server code (the same tree id in their record).
 * Returns the groups compared (tree, runs, flips: the points reached in some of the group's runs and not others), the runs left
 * alone because no other run tested their code, and the runs left out and why.
 */
export function compareRuns(folder) {
  const names = readdirSync(folder, { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => entry.name);
  // Run ids are the same length, so sorting them as text puts the oldest first.
  names.sort(byOrder);
  const left = [];
  const alone = [];
  const byTree = new Map();
  const trees = new Map(names.map((run) => [run, treeOfRun(path.join(folder, run))]));
  const sharing = (tree) => [...trees.values()].filter((other) => other === tree).length;
  for (const run of names) {
    const tree = trees.get(run);
    if (tree === undefined) {
      left.push({ run, reason: `no record of its code (${RUN_RECORD})` });
      continue;
    }
    // The only run of its code needs no reading.
    if (sharing(tree) === 1) {
      alone.push({ run, tree });
      continue;
    }
    const { points, missing } = readRun(path.join(folder, run));
    if (missing.length > 0) {
      left.push({ run, reason: `no ${missing.join(" or ")} report` });
      continue;
    }
    const group = byTree.get(tree) ?? { tree, runs: [], tally: new Map() };
    byTree.set(tree, group);
    group.runs.push(run);
    for (const [key, point] of points) {
      const entry = group.tally.get(key) ?? { ...point, present: 0, hit: 0, missed: [] };
      entry.present += 1;
      if (point.visited) entry.hit += 1;
      else entry.missed.push(run);
      group.tally.set(key, entry);
    }
  }
  const groups = [];
  for (const { tree, runs, tally } of byTree.values()) {
    if (runs.length < 2) {
      alone.push({ run: runs[0], tree });
      continue;
    }
    const flips = [...tally.values()].filter((entry) => entry.hit > 0 && entry.missed.length > 0).sort((a, b) => byOrder(orderKey(a), orderKey(b)));
    groups.push({ tree, runs, flips });
  }
  return { groups, alone, left };
}

const SHOWN_RUNS = 5;
const shortTree = (tree) => tree.slice(0, 12);

function describe(point) {
  const where = point.branch ? `branch at line ${point.line} (offset ${point.offset}, path ${point.path})` : `line ${point.line}`;
  const shown = point.missed.slice(0, SHOWN_RUNS).join(", ");
  const more = point.missed.length > SHOWN_RUNS ? ` and ${point.missed.length - SHOWN_RUNS} more` : "";
  return `${where}: reached in ${point.hit} of ${point.present} runs, not in ${shown}${more}`;
}

/** What was left alone and left out, for the end of every message. */
function notes({ alone, left }) {
  const out = [];
  if (alone.length > 0) out.push(`Left alone, because no other run tested the same server code: ${alone.map(({ run, tree }) => `${run} (code ${shortTree(tree)})`).join(", ")}.`);
  if (left.length > 0) out.push(`Left out: ${left.map(({ run, reason }) => `${run} (${reason})`).join(", ")}.`);
  return out;
}

const groupsOf = (groups) => groups.map(({ tree, runs }) => `${shortTree(tree)} (${runs.length} runs)`).join(", ");

/** The groups with flips, split into those in the code that counts (`current`, or every group when none is given) and the rest. */
function judged(result, current) {
  const flipping = result.groups.filter((group) => group.flips.length > 0);
  const counts = (group) => current === undefined || group.tree === current;
  return { failing: flipping.filter(counts), information: flipping.filter((group) => !counts(group)), judging: result.groups.filter(counts) };
}

/** The listing of one group's flips: its code and runs, then the places by file and method. */
function groupLines(group) {
  const out = ["", `Server code ${shortTree(group.tree)}, ${group.runs.length} runs (${group.runs.join(", ")})`];
  let file;
  let method;
  for (const point of group.flips) {
    if (point.file !== file) {
      file = point.file;
      method = undefined;
      out.push("", file);
    }
    if (point.method !== method) {
      method = point.method;
      out.push(`  ${method}`);
    }
    out.push(`    ${describe(point)}`);
  }
  return out;
}

/** The flips of code no longer on main, which are shown and do not fail the check. */
function informationLines(information) {
  if (information.length === 0) return [];
  return ["", "In code no longer on main, for information (this does not fail the check):", ...information.flatMap(groupLines)];
}

/** The message for a result of `compareRuns`; only flips in the code `current` names (every group's, when none is given) are what the check fails for. */
export function formatFlips(result, current) {
  const { failing, information } = judged(result, current);
  const total = failing.reduce((sum, group) => sum + group.flips.length, 0);
  const spots = failing.reduce((sum, group) => sum + new Set(group.flips.map((point) => `${point.file}\t${point.method}`)).size, 0);
  const out = [
    `Coverage is not steady: ${total} line(s) and branch(es) in ${spots} place(s) in the server were reached in some CI runs and not in others that tested the same server code (compared within ${result.groups.length} group(s) of runs: ${groupsOf(result.groups)}).`,
    ...failing.flatMap(groupLines),
    "",
    "What to do: each of these is code that a test reaches only when timing, or the way data arrives in pieces, goes one way, so the coverage check can turn red with no change to the code.",
    "Write a test that reaches each one every time: make the test produce both outcomes on purpose (hand the code its input in the pieces you choose, hold a race open with a signal) rather than waiting, and add no delay.",
    "The runs named above are the ones that missed it; their test logs show which tests ran.",
    ...informationLines(information),
  ];
  const rest = notes(result);
  if (rest.length > 0) out.push("", ...rest);
  return out.join("\n");
}

/** The message when nothing in the code that counts flips. */
function formatSteady(result, current) {
  const { information, judging } = judged(result, current);
  const out = [
    judging.length === 0
      ? `Nothing to compare: no two runs tested ${current === undefined ? "the same server code" : "the server code now on main"}.`
      : `Coverage is steady: no line or branch in the server was reached in only some of the runs that tested the same server code (${groupsOf(judging)}).`,
    ...informationLines(information),
  ];
  const rest = notes(result);
  if (rest.length > 0) out.push("", ...rest);
  return out.join("\n");
}

/** Prints the verdict for `argv` (the folder of runs, and `--current-tree <id>`); returns the exit code. */
export function main(argv, { log = console.log, error = console.error } = {}) {
  const option = argv.indexOf("--current-tree");
  const current = option < 0 ? undefined : argv[option + 1];
  const valueAt = option < 0 ? -1 : option + 1;
  const [folder] = argv.filter((argument, index) => !argument.startsWith("--") && index !== valueAt);
  if (!folder || (option >= 0 && !current)) {
    error("Usage: check-coverage-flips.mjs <folder of runs> [--current-tree <tree id of apps/server on main>]");
    return 2;
  }
  if (!existsSync(folder)) {
    error(`There is no folder ${folder}. Download the runs first: node scripts/fetch-coverage-runs.mjs ${folder}`);
    return 2;
  }
  const result = compareRuns(folder);
  if (judged(result, current).failing.length > 0) {
    log(formatFlips(result, current));
    return 1;
  }
  log(formatSteady(result, current));
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (failure) {
    console.error(failure.message);
    process.exitCode = 2;
  }
}
