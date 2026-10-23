#!/usr/bin/env node
// Splits the browser (E2E) tests over CI jobs. All of them share one server and run one test at a time, so a
// single job takes as long as every test added to it; a job per shard keeps each well under its timeout.
//
// The classes and their test counts are read from the test source, so a new class is in a shard the moment it
// is written and nothing is listed by hand. A class goes to the lightest shard so far, heaviest class first,
// which is deterministic and balanced to within one class; a test takes about the same time as any other.
// The number of shards follows from the number of tests (TESTS_PER_SHARD), so CI adds a shard before a job
// nears its timeout.
//
//   node scripts/e2e-shards.mjs --matrix        the shard numbers as a JSON list, for the job's matrix
//   node scripts/e2e-shards.mjs --shard <n>     the `dotnet test --filter` for shard n (1-based)
//   node scripts/e2e-shards.mjs --summary       the classes and test count of every shard, for the log
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { rootOf } from "./repository-root.mjs";

const PROJECT = "apps/server/tests/Weir.E2E.Tests";

/** About four minutes of tests, so a job with its build and browser install is well under ten. */
export const TESTS_PER_SHARD = 40;

const NAMESPACE = /^namespace\s+([\w.]+)\s*;/m;
const TEST_CLASS = /\bclass\s+(\w+)\s*(?:\([^)]*\))?\s*:\s*E2ETestBase\b/g;
const TEST = /\[E2EFact\b/g;

/** The browser test classes in one source file, with the tests each holds: [{ name, tests }] (names are full). */
export function classesIn(source, file = "a source file") {
  const namespace = (source.match(NAMESPACE) ?? [])[1];
  const declared = [...source.matchAll(TEST_CLASS)];
  const marked = [...source.matchAll(TEST)];
  if (marked.length > 0 && declared.length === 0) {
    throw new Error(`${file} has [E2EFact] tests but no class deriving from E2ETestBase, so no shard would run them.`);
  }
  if (declared.length > 0 && !namespace) throw new Error(`${file} has no file-scoped namespace.`);
  return declared.map((match, index) => {
    const end = declared[index + 1]?.index ?? source.length;
    const tests = marked.filter((test) => test.index >= match.index && test.index < end).length;
    return { name: `${namespace}.${match[1]}`, tests };
  });
}

/** Test classes spread over partial files add up; a class with no tests is left out. */
export function allClasses(sources) {
  const byName = new Map();
  for (const [file, source] of sources) {
    for (const { name, tests } of classesIn(source, file)) byName.set(name, (byName.get(name) ?? 0) + tests);
  }
  return [...byName].filter(([, tests]) => tests > 0).map(([name, tests]) => ({ name, tests }));
}

export function shardCountFor(classes) {
  const tests = classes.reduce((sum, entry) => sum + entry.tests, 0);
  return Math.max(1, Math.ceil(tests / TESTS_PER_SHARD));
}

/** The classes of every shard, heaviest class first onto the lightest shard so far. */
export function assignShards(classes, count = shardCountFor(classes)) {
  const shards = Array.from({ length: count }, () => ({ classes: [], tests: 0 }));
  const heaviestFirst = [...classes].sort((a, b) => b.tests - a.tests || a.name.localeCompare(b.name));
  for (const entry of heaviestFirst) {
    const lightest = shards.reduce((best, shard) => (shard.tests < best.tests ? shard : best));
    lightest.classes.push(entry.name);
    lightest.tests += entry.tests;
  }
  return shards;
}

/** A vstest filter that matches exactly these classes (the trailing dot stops one name matching inside another). */
export function filterFor(classNames) {
  return classNames.map((name) => `FullyQualifiedName~${name}.`).join("|");
}

function sourceFiles(folder) {
  const files = [];
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    if (entry.name === "bin" || entry.name === "obj") continue;
    const full = path.join(folder, entry.name);
    if (entry.isDirectory()) files.push(...sourceFiles(full));
    else if (entry.name.endsWith(".cs")) files.push(full);
  }
  return files;
}

function main(argv) {
  const root = rootOf(import.meta.url, argv);
  const project = path.join(root, PROJECT);
  const classes = allClasses(sourceFiles(project).map((file) => [path.relative(root, file), readFileSync(file, "utf8")]));
  if (classes.length === 0) throw new Error(`No browser test classes found under ${PROJECT}.`);
  const shards = assignShards(classes);
  const [mode, value] = argv;
  if (mode === "--matrix") {
    console.log(JSON.stringify(shards.map((_, index) => index + 1)));
  } else if (mode === "--shard") {
    const shard = shards[Number(value) - 1];
    if (!shard) throw new Error(`There is no shard ${value}; there are ${shards.length}.`);
    console.log(filterFor(shard.classes));
  } else if (mode === "--summary") {
    shards.forEach((shard, index) => console.log(`Shard ${index + 1}: ${shard.tests} tests in ${shard.classes.length} classes`));
  } else {
    throw new Error("Usage: node scripts/e2e-shards.mjs --matrix | --shard <n> | --summary");
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}
