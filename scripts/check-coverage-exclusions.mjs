#!/usr/bin/env node
// The coverage exclusions (#1060): every way the coverage figures leave code out is listed in
// docs/coverage/exclusions.json with a kind and a reason, so "100%" never hides code nobody decided to leave out.
//
//   node scripts/check-coverage-exclusions.mjs [--root <repository folder>]
//
// What counts as a way of leaving code out:
//   - an entry in the collectors' settings: Exclude, ExcludeByAttribute and ExcludeByFile in the two coverlet run settings,
//     the attribute and source exclusions in the out-of-process settings, and `coverage.exclude` in the web app's Vite config;
//   - a marker in the code itself: [ExcludeFromCodeCoverage] in the server and tray sources, and a v8, istanbul or c8
//     "ignore" comment in the web sources.
//
// The check fails when something is left out and not listed, when a listed exclusion no longer exists, when an entry has no
// reason (or one that says nothing), when its kind is not one of the kinds below, and when a platform-only entry does not
// name the system and the test that run it.
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const EXCLUSIONS_FILE = "docs/coverage/exclusions.json";

/** The kinds an exclusion can have. A kind says what sort of reason is acceptable. */
export const KINDS = {
  generated: "Code a tool wrote, which no one maintains by hand.",
  "not-production": "Test projects, stand-in programs and other code that does not ship.",
  "platform-only": "Code only one operating system can run, that no unit test leg runs either: `coveredBy` names the leg whose system it needs and `provedBy` the test or scenario that runs it there.",
  unreachable: "A defensive branch that cannot be reached, whatever the input, with the reason it cannot.",
  "no-code": "Files with no executable statement, such as type declarations.",
  policy: "A setting that switches a marker on; each use of the marker is listed on its own.",
};

export const LEGS = ["linux", "windows"];

const CONFIG_FILES = [
  "apps/server/coverage.runsettings",
  "apps/tray/coverage.runsettings",
  "apps/server/coverage.out-of-process.xml",
  "apps/web/vite.config.ts",
];

const CSHARP_SOURCE = /^apps\/(?:server\/src|tray\/Weir\.Tray)\/.+\.cs$/;
const WEB_SOURCE = /^apps\/web\/src\/.+\.(?:ts|tsx)$/;
const WEB_TEST_SOURCE = /(?:\.test\.(?:ts|tsx)$|^apps\/web\/src\/test\/|\.d\.ts$|^apps\/web\/src\/lib\/api\/generated\/)/;

const CSHARP_MARKER = /[[,]\s*(?:assembly:\s*)?(?:System\.Diagnostics\.CodeAnalysis\.)?ExcludeFromCodeCoverage(?:Attribute)?\s*[\](,]/g;
const WEB_MARKER = /(?:v8|istanbul|c8)\s+ignore\s+(?!stop)/g;

const PLACEHOLDERS = /^(?:todo|tbd|n\/a|na|none|-+|\.+|later|misc|various|because|reason)\.?$/i;

/** What is wrong with a reason, or undefined when it says something. */
export function reasonProblem(reason) {
  if (typeof reason !== "string") return "has no reason";
  const text = reason.trim();
  if (text.length === 0) return "has no reason";
  if (PLACEHOLDERS.test(text)) return `has a reason that says nothing ("${text}")`;
  if (text.length < 20 || !/\s/.test(text)) return "has a reason too short to say why the code is left out";
  return undefined;
}

/** The entries of an exclusions file, or the problems with them. */
export function readExclusions(text) {
  let file;
  try {
    file = JSON.parse(text);
  } catch (error) {
    return { entries: [], problems: [`${EXCLUSIONS_FILE} is not valid JSON: ${error.message}`] };
  }
  const entries = Array.isArray(file.exclusions) ? file.exclusions : undefined;
  if (!entries) return { entries: [], problems: [`${EXCLUSIONS_FILE} needs an "exclusions" list.`] };
  const problems = [];
  const ids = new Set();
  for (const [index, entry] of entries.entries()) {
    const name = typeof entry?.id === "string" && entry.id ? `Exclusion "${entry.id}"` : `Exclusion number ${index + 1}`;
    if (typeof entry?.id !== "string" || !entry.id) problems.push(`${name} has no id.`);
    else if (ids.has(entry.id)) problems.push(`${name} is listed twice.`);
    else ids.add(entry.id);
    if (typeof entry?.file !== "string" || !entry.file) problems.push(`${name} does not say which file it is in.`);
    if (typeof entry?.marker !== "string" || !entry.marker) problems.push(`${name} does not say what it leaves out (marker).`);
    if (!Object.hasOwn(KINDS, entry?.kind)) problems.push(`${name} has the kind "${entry?.kind}"; use one of: ${Object.keys(KINDS).join(", ")}.`);
    const bad = reasonProblem(entry?.reason);
    if (bad) problems.push(`${name} ${bad}.`);
    if (entry?.kind === "platform-only") {
      if (!LEGS.includes(entry.coveredBy)) {
        problems.push(`${name} is platform-only, so it has to name the system that runs it (coveredBy: ${LEGS.join(" or ")}).`);
      }
      if (typeof entry.provedBy !== "string" || entry.provedBy.trim().length === 0) {
        problems.push(`${name} is platform-only, so it has to name the test or scenario that runs it there (provedBy).`);
      }
    }
    if (entry?.count !== undefined && !(Number.isInteger(entry.count) && entry.count > 0)) {
      problems.push(`${name} has a count that is not a whole number above zero.`);
    }
  }
  return { entries, problems };
}

function tokens(text) {
  return text
    .split(",")
    .map((token) => token.trim())
    .filter(Boolean);
}

function xmlTexts(text, tag) {
  return [...text.matchAll(new RegExp(`<${tag}>([^<]*)</${tag}>`, "g"))].map((match) => match[1].trim()).filter(Boolean);
}

/** What a collector's settings file leaves out, one item per excluded name. */
export function configItems(file, text) {
  const uncommented = text.replace(/<!--[\s\S]*?-->/g, "");
  if (file.endsWith(".runsettings")) {
    return ["Exclude", "ExcludeByAttribute", "ExcludeByFile"].flatMap((tag) => xmlTexts(uncommented, tag).flatMap(tokens));
  }
  if (file.endsWith(".xml")) {
    // The out-of-process settings list excluded attributes and excluded sources, each in an <Exclude> block.
    return [...uncommented.matchAll(/<Exclude>([\s\S]*?)<\/Exclude>/g)].flatMap((block) =>
      [...xmlTexts(block[1], "Attribute"), ...xmlTexts(block[1], "Source")],
    );
  }
  const block = text.match(/coverage:\s*\{[\s\S]*?\bexclude:\s*\[([\s\S]*?)\]/);
  return block ? [...block[1].matchAll(/"([^"]+)"/g)].map((match) => match[1]) : [];
}

/** The exclusion markers written in a source file, as many items as there are markers. */
export function sourceItems(file, text) {
  if (CSHARP_SOURCE.test(file)) return [...text.matchAll(CSHARP_MARKER)].map(() => "ExcludeFromCodeCoverage");
  if (WEB_SOURCE.test(file) && !WEB_TEST_SOURCE.test(file)) return [...text.matchAll(WEB_MARKER)].map(() => "v8 ignore");
  return [];
}

/** Every item the collectors leave out, as `{ file, marker, found }` with the number of times it appears. */
export function collectItems(files, read) {
  const counts = new Map();
  const add = (file, marker) => {
    const key = `${file}\u0000${marker}`;
    counts.set(key, { file, marker, found: (counts.get(key)?.found ?? 0) + 1 });
  };
  for (const file of files) {
    if (CONFIG_FILES.includes(file)) for (const marker of configItems(file, read(file))) add(file, marker);
    else if (CSHARP_SOURCE.test(file) || WEB_SOURCE.test(file)) for (const marker of sourceItems(file, read(file))) add(file, marker);
  }
  return [...counts.values()];
}

/** The problems between the entries and what the code leaves out. */
export function compare(entries, items, fileExists = () => true) {
  const problems = [];
  const listed = new Map();
  for (const entry of entries) {
    if (typeof entry?.file !== "string" || typeof entry?.marker !== "string") continue;
    const key = `${entry.file}\u0000${entry.marker}`;
    listed.set(key, (listed.get(key) ?? 0) + (entry.count ?? 1));
    if (!fileExists(entry.file)) problems.push(`Exclusion "${entry.id}" is for ${entry.file}, which is not in the repository.`);
  }
  const foundByKey = new Map(items.map((item) => [`${item.file}\u0000${item.marker}`, item]));
  for (const item of items) {
    const count = listed.get(`${item.file}\u0000${item.marker}`) ?? 0;
    if (count === 0) {
      problems.push(
        `${item.file} leaves out "${item.marker}" and ${EXCLUSIONS_FILE} does not say why. Add an entry with a kind and a reason, or remove the exclusion.`,
      );
    } else if (count !== item.found) {
      problems.push(`${item.file} has ${item.found} of "${item.marker}" and ${EXCLUSIONS_FILE} lists ${count}; list each one with its own reason.`);
    }
  }
  for (const entry of entries) {
    if (typeof entry?.file !== "string" || typeof entry?.marker !== "string") continue;
    if (fileExists(entry.file) && !foundByKey.has(`${entry.file}\u0000${entry.marker}`)) {
      problems.push(`Exclusion "${entry.id}" lists "${entry.marker}" in ${entry.file}, but the file no longer leaves that out. Remove the entry.`);
    }
  }
  return problems;
}

function trackedFiles(root) {
  return execFileSync("git", ["ls-files", "-z", "--", "apps"], { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 })
    .split("\0")
    .filter(Boolean);
}

/** Checks the repository under `root`; returns the problems and a one-line summary. */
export function run(root, files = trackedFiles(root)) {
  const read = (file) => readFileSync(path.join(root, file), "utf8");
  const exclusionsPath = path.join(root, EXCLUSIONS_FILE);
  if (!existsSync(exclusionsPath)) return { problems: [`${EXCLUSIONS_FILE} is missing.`], summary: "" };
  const { entries, problems } = readExclusions(readFileSync(exclusionsPath, "utf8"));
  const items = collectItems(files, read);
  problems.push(...compare(entries, items, (file) => existsSync(path.join(root, file))));
  const byKind = Object.keys(KINDS)
    .map((kind) => `${entries.filter((entry) => entry.kind === kind).length} ${kind}`)
    .join(", ");
  return { problems, summary: `${entries.length} coverage exclusions (${byKind}), each with a reason.` };
}

function main() {
  const rootArgument = process.argv.indexOf("--root");
  const root = rootArgument >= 0 ? path.resolve(process.argv[rootArgument + 1]) : path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const { problems, summary } = run(root);
  if (problems.length) {
    for (const problem of problems) console.error(`::error::${problem}`);
    process.exit(1);
  }
  console.log(summary);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
