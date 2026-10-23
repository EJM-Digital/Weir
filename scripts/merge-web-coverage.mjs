#!/usr/bin/env node
// Merges the web app's coverage from every source into one report (#1060): vitest's own run (jsdom, v8) and the browser
// tests' run (the app built with WEIR_WEB_COVERAGE=1 and Istanbul-instrumented, driven by apps/server/tests/Weir.E2E.Tests
// in Chromium).
//
//   node scripts/merge-web-coverage.mjs --out <folder> [--root apps/web] [--parts] <input>...
//
// An <input> is a coverage-final.json (an Istanbul coverage map), a JSON file holding an array of them (what each browser
// test writes), or a folder, whose JSON files are read. Paths are matched by where they sit under --root, so a report
// written on another machine or in another checkout merges with this one.
//
// The browser tests' reports describe the compiled code and carry the source map back to the source, which is applied
// here. Inside a file, a statement, function or branch counts as the same thing in the two sources when it starts at the same
// source position (and, for a branch, is the same kind with the same number of arms), because the two tools do not agree
// on where one ends. One that only a source knows is kept, so a difference in how the tools see the code can lower the
// figure but never raise it.
//
// --parts writes only coverage-final.json, still in the compiled form: what a CI shard uploads, to be merged with the others.
// Otherwise it writes coverage-final.json, coverage-summary.json (what scripts/check-coverage-floors.mjs judges the `web`
// floor on) and lcov.info to <folder>.
import { reporting as webReporting } from "./merge-web-coverage-reports.mjs";
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const slash = (text) => text.replace(/\\/g, "/");

/** Where a file sits under the web app, or undefined for a file outside it. */
export function relativeKey(file, root) {
  const normal = slash(file);
  const rooted = slash(root).replace(/\/+$/, "");
  const index = normal.lastIndexOf(`${rooted}/`);
  if (index >= 0) return normal.slice(index + rooted.length + 1);
  const web = normal.lastIndexOf("/apps/web/");
  return web >= 0 ? normal.slice(web + "/apps/web/".length) : undefined;
}

const position = (point) => (point && point.line != null ? `${point.line}:${point.column}` : "-");
const span = (location) => (location ? `${position(location.start)}-${position(location.end)}` : "-");

/** What makes two statements, functions or branches the same thing. `exact` is for one tool's reports of one build; `loose` for the two tools. */
export const EXACT = {
  statement: span,
  fn: (fn) => span(fn.loc),
  branch: (branch) => `${branch.type}|${branch.locations.map(span).join(",")}`,
};
export const LOOSE = {
  statement: (location) => position(location.start),
  fn: (fn) => position(fn.loc.start),
  branch: (branch) => `${branch.type}|${position(branch.loc.start)}|${branch.locations.length}`,
};

/** Each thing's id by its key; things that share a key are listed in the order they occur. */
function indexOf(file, keys) {
  const index = { s: new Map(), f: new Map(), b: new Map() };
  const add = (map, key, id) => map.set(key, [...(map.get(key) ?? []), id]);
  for (const [id, location] of Object.entries(file.statementMap)) add(index.s, keys.statement(location), id);
  for (const [id, fn] of Object.entries(file.fnMap)) add(index.f, keys.fn(fn), id);
  for (const [id, branch] of Object.entries(file.branchMap)) add(index.b, keys.branch(branch), id);
  return index;
}

function nextId(map) {
  return String(Object.keys(map).reduce((highest, id) => Math.max(highest, Number(id)), -1) + 1);
}

/** Adds what `source` ran to `target` (both one file's coverage); returns how many of its entries `target` did not know. */
export function mergeFile(target, source, keys) {
  const index = indexOf(target, keys);
  let unmatched = 0;
  const idFor = (map, key, occurrences, create) => {
    const nth = occurrences.get(key) ?? 0;
    occurrences.set(key, nth + 1);
    const known = map.get(key)?.[nth];
    if (known !== undefined) return known;
    unmatched++;
    return create();
  };

  const seen = { s: new Map(), f: new Map(), b: new Map() };
  for (const [id, location] of Object.entries(source.statementMap)) {
    const into = idFor(index.s, keys.statement(location), seen.s, () => {
      const created = nextId(target.statementMap);
      target.statementMap[created] = location;
      target.s[created] = 0;
      return created;
    });
    target.s[into] += source.s[id];
  }
  for (const [id, fn] of Object.entries(source.fnMap)) {
    const into = idFor(index.f, keys.fn(fn), seen.f, () => {
      const created = nextId(target.fnMap);
      target.fnMap[created] = fn;
      target.f[created] = 0;
      return created;
    });
    target.f[into] += source.f[id];
  }
  for (const [id, branch] of Object.entries(source.branchMap)) {
    const into = idFor(index.b, keys.branch(branch), seen.b, () => {
      const created = nextId(target.branchMap);
      target.branchMap[created] = branch;
      target.b[created] = branch.locations.map(() => 0);
      return created;
    });
    source.b[id].forEach((count, arm) => {
      target.b[into][arm] += count;
    });
  }
  return unmatched;
}

function jsonFiles(input) {
  if (!statSync(input).isDirectory()) return [input];
  return readdirSync(input, { recursive: true })
    .map((name) => path.join(input, name))
    .filter((file) => file.endsWith(".json") && statSync(file).isFile());
}

function coverageMaps(file) {
  const content = JSON.parse(readFileSync(file, "utf8"));
  return Array.isArray(content) ? content : [content];
}

/** Merges every report in `inputs` into two Istanbul maps keyed by path under `root`: the instrumented build's (compiled form) and the others. */
export function readInputs(inputs, root) {
  const groups = { instrumented: {}, native: {} };
  for (const input of inputs) {
    for (const file of jsonFiles(input)) {
      for (const map of coverageMaps(file)) {
        for (const [name, coverage] of Object.entries(map)) {
          const key = relativeKey(name, root);
          if (key === undefined) continue;
          const group = groups[coverage.inputSourceMap ? "instrumented" : "native"];
          if (group[key] === undefined) {
            group[key] = structuredClone({ ...coverage, path: key });
          } else {
            mergeFile(group[key], coverage, EXACT);
          }
        }
      }
    }
  }
  return groups;
}

/** Lays the sources' coverage over the native one's; returns the merged map and how many entries the native report did not have. */
export function combine(native, sourced) {
  const merged = native;
  let unmatched = 0;
  for (const [key, coverage] of Object.entries(sourced)) {
    if (merged[key] === undefined) {
      merged[key] = coverage;
      unmatched += Object.keys(coverage.statementMap).length + Object.keys(coverage.fnMap).length + Object.keys(coverage.branchMap).length;
    } else {
      unmatched += mergeFile(merged[key], coverage, LOOSE);
    }
  }
  return { merged, unmatched };
}

export function parseArguments(argv) {
  const options = { root: "apps/web", inputs: [], parts: false };
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--out" || argument === "--root") {
      options[argument.slice(2)] = argv[++index];
    } else if (argument === "--parts") {
      options.parts = true;
    } else if (argument.startsWith("--")) {
      throw new Error(`Unknown option ${argument}.`);
    } else {
      options.inputs.push(argument);
    }
  }
  if (!options.out || options.inputs.length === 0) {
    throw new Error("Usage: merge-web-coverage.mjs --out <folder> [--root apps/web] [--parts] <input>...");
  }
  for (const input of options.inputs) {
    if (!existsSync(input)) throw new Error(`${input} does not exist, so there is no coverage to merge from it.`);
  }
  return options;
}

/** `reporting` is what needs the web app's coverage packages: moving the browser tests' coverage back to the source, and writing the reports. */
export async function main(argv = process.argv.slice(2), log = console.log, reporting = webReporting) {
  const { out, root, inputs, parts } = parseArguments(argv);
  const { instrumented, native } = readInputs(inputs, root);
  if (Object.keys(instrumented).length + Object.keys(native).length === 0) {
    throw new Error("The inputs held no coverage of any file under the web app.");
  }
  if (parts) {
    mkdirSync(out, { recursive: true });
    const absolute = path.resolve(root);
    const named = Object.fromEntries(Object.entries(instrumented).map(([key, coverage]) => [path.join(absolute, key), { ...coverage, path: path.join(absolute, key) }]));
    writeFileSync(path.join(out, "coverage-final.json"), JSON.stringify(named));
    log(`Merged the browser tests' coverage of ${Object.keys(instrumented).length} files into ${out}.`);
    return;
  }
  const { merged, unmatched } = combine(native, await reporting.toSource(instrumented, root, relativeKey));
  const { lines, branches } = reporting.writeReports(merged, root, out);
  log(`Merged web coverage: ${lines.pct}% of lines (${lines.covered}/${lines.total}), ${branches.pct}% of branches (${branches.covered}/${branches.total}).`);
  log(`${unmatched} statements, functions and branches from the browser tests did not match one in the unit tests' report and were added.`);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error.message);
    process.exit(1);
  });
}
