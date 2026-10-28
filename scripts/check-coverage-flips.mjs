#!/usr/bin/env node
// Finds the places in the server's code that CI runs reached in some runs and not in others, so a coverage floor that turns red
// with no change to the code is caught the day a test starts depending on timing, not at release.
//
//   node scripts/check-coverage-flips.mjs <folder>
//
// <folder> holds one subfolder per CI run (scripts/fetch-coverage-runs.mjs writes it), each with the run's two server
// coverage artifacts, `coverage-server-linux` and `coverage-server-windows`, as the `coverage` job in ci.yml downloads them.
// Each run is merged the way that job merges it: the reports are aligned (scripts/align-coverage-reports.mjs) and a line or branch
// counts as reached when any report of either system reached it, which is what ReportGenerator's union does. Then the runs are
// laid side by side, and every line and branch that is reached in some runs and not in others is listed.
//
// A run that lacks either system's reports is left out and named: with one system missing, a branch only the other system
// reaches (`if (OperatingSystem.IsWindows())`) would look like a flip. A point is compared only across the runs that list it, so a
// change to the code between two runs does not count.
//
// This reads CI runs of different branches side by side, so while a floor is still being raised, a run of a branch that adds tests
// differs from one that does not; with the server floors at 100% (docs/coverage/floors.json) every difference is a run that missed
// something, which is what this is for.
//
// Exit 0: nothing flips, or fewer than two runs could be compared. Exit 1: something flips. Exit 2: the folder is missing or cannot be read.
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

// Every key it orders is different, so there is no tie to settle.
const byOrder = (a, b) => (a < b ? -1 : 1);

/** Orders points by file, method, then line, lines before branches, offset and path (numbers padded so they sort as numbers). */
const orderKey = (point) => [point.file, point.method, ...[point.line, point.branch ? 1 : 0, point.offset, point.path].map((value) => String(value ?? 0).padStart(9, "0"))].join("\t");

/** Compares every run under `folder`: which were compared, which were left out and why, and the points reached in some of them and not others. */
export function compareRuns(folder) {
  const names = readdirSync(folder, { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => entry.name);
  // Run ids are the same length, so sorting them as text puts the oldest first.
  names.sort(byOrder);
  const compared = [];
  const left = [];
  const tally = new Map();
  for (const run of names) {
    const { points, missing } = readRun(path.join(folder, run));
    if (missing.length > 0) {
      left.push({ run, missing });
      continue;
    }
    compared.push(run);
    for (const [key, point] of points) {
      const entry = tally.get(key) ?? { ...point, present: 0, hit: 0, missed: [] };
      entry.present += 1;
      if (point.visited) entry.hit += 1;
      else entry.missed.push(run);
      tally.set(key, entry);
    }
  }
  const flips = [...tally.values()]
    .filter((entry) => entry.hit > 0 && entry.missed.length > 0)
    .sort((a, b) => byOrder(orderKey(a), orderKey(b)));
  return { compared, left, flips };
}

const SHOWN_RUNS = 5;

function describe(point) {
  const where = point.branch ? `branch at line ${point.line} (offset ${point.offset}, path ${point.path})` : `line ${point.line}`;
  const shown = point.missed.slice(0, SHOWN_RUNS).join(", ");
  const more = point.missed.length > SHOWN_RUNS ? ` and ${point.missed.length - SHOWN_RUNS} more` : "";
  return `${where}: reached in ${point.hit} of ${point.present} runs, not in ${shown}${more}`;
}

/** The message for a result of `compareRuns` that has flips. */
export function formatFlips({ compared, left, flips }) {
  const spots = new Set(flips.map((point) => `${point.file}\t${point.method}`)).size;
  const out = [
    `Coverage is not steady: ${flips.length} line(s) and branch(es) in ${spots} place(s) in the server were reached in some CI runs and not in others (${compared.length} runs compared).`,
  ];
  let file;
  let method;
  for (const point of flips) {
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
  out.push(
    "",
    "What to do: each of these is code that a test reaches only when timing, or the way data arrives in pieces, goes one way, so the coverage check can turn red with no change to the code.",
    "Write a test that reaches each one every time: make the test produce both outcomes on purpose (hand the code its input in the pieces you choose, hold a race open with a signal) rather than waiting, and add no delay.",
    "The runs named above are the ones that missed it; their test logs show which tests ran.",
  );
  if (left.length > 0) out.push("", leftOut(left));
  return out.join("\n");
}

function leftOut(left) {
  return `Left out, because a system's coverage is missing from the run: ${left.map(({ run, missing }) => `${run} (no ${missing.join(" or ")} report)`).join(", ")}.`;
}

/** Prints the verdict for `argv` (the folder of runs); returns the exit code. */
export function main(argv, { log = console.log, error = console.error } = {}) {
  const [folder] = argv;
  if (!folder) {
    error("Usage: check-coverage-flips.mjs <folder of runs>");
    return 2;
  }
  if (!existsSync(folder)) {
    error(`There is no folder ${folder}. Download the runs first: node scripts/fetch-coverage-runs.mjs ${folder}`);
    return 2;
  }
  const result = compareRuns(folder);
  if (result.flips.length > 0) {
    log(formatFlips(result));
    return 1;
  }
  const left = result.left.length > 0 ? ` ${leftOut(result.left)}` : "";
  if (result.compared.length < 2) {
    log(`Nothing to compare: ${result.compared.length} run(s) have both the Linux and the Windows server coverage, and two are needed.${left}`);
    return 0;
  }
  log(`Coverage is steady: no line or branch in the server was reached in only some of ${result.compared.length} runs.${left}`);
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
