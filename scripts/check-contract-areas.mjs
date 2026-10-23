#!/usr/bin/env node
// Fails when the contract suite's area list and its test classes disagree.
//
// CI runs one leg per name in apps/server/tests/Weir.Contract.Tests/areas.json with `--filter "Area=<name>"`.
// `dotnet test` passes when a filter matches nothing, so a misspelt or empty area would be a leg that always
// goes green. This check makes that impossible: every listed area has at least one [ContractArea("<name>")]
// class, and every area a class names is listed. Each area runs on Linux and on Windows, the platform people install on (#995):
// the workflow must keep both platforms in its matrix, run the Windows one on a Windows runner, and tell the real-tool check which
// platform a leg is on.
//
// Usage: node scripts/check-contract-areas.mjs
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { rootOf } from "./repository-root.mjs";

const PROJECT = "apps/server/tests/Weir.Contract.Tests";
const ATTRIBUTE = /\[ContractArea\("([^"]+)"\)\]/g;

export function areasUsedIn(source) {
  return [...source.matchAll(ATTRIBUTE)].map((match) => match[1]);
}

export function compareAreas(listed, used) {
  const problems = [];
  const duplicates = listed.filter((name, index) => listed.indexOf(name) !== index);
  for (const name of new Set(duplicates)) problems.push(`areas.json lists "${name}" more than once.`);
  for (const name of listed) {
    if (!used.has(name)) problems.push(`areas.json lists "${name}", but no test class is marked [ContractArea("${name}")].`);
  }
  for (const [name, example] of used) {
    if (!listed.includes(name)) problems.push(`${example} is in area "${name}", which areas.json does not list, so CI would never run it.`);
  }
  return problems;
}

/**
 * What is wrong with the platforms the contract suite runs on, one sentence each; empty when every area runs on both.
 * ci-contract.yml takes the platforms as an input and ci.yml calls it once for each, so Windows can be a job of its own.
 */
export function platformProblems(workflow, caller) {
  const problems = [];
  if (!/^\s+platform:\s*\$\{\{ fromJSON\(inputs\.platforms\) \}\}\s*$/m.test(workflow)) {
    problems.push("ci-contract.yml must take its matrix's `platform:` from `fromJSON(inputs.platforms)`, so ci.yml can run Windows as a job of its own.");
  }
  if (caller !== undefined) {
    for (const platform of ["linux", "windows"]) {
      if (!caller.includes(`platforms: '["${platform}"]'`)) {
        problems.push(`ci.yml must call ci-contract.yml with \`platforms: '["${platform}"]'\`, so every contract area runs on ${platform === "windows" ? "Windows" : "Linux"}.`);
      }
    }
  }
  if (!/runs-on:.*windows-latest/.test(workflow)) {
    problems.push("ci-contract.yml must run the windows platform on windows-latest.");
  }
  if (!/check-contract-real-tool-tests\.mjs[^\n]*"\$PLATFORM"/.test(workflow)) {
    problems.push(`ci-contract.yml must pass the leg's platform ("$PLATFORM") to check-contract-real-tool-tests.mjs, not a fixed one.`);
  }
  return problems;
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

function main() {
  const root = rootOf(import.meta.url);
  const project = path.join(root, PROJECT);
  const listed = JSON.parse(readFileSync(path.join(project, "areas.json"), "utf8")).areas.map((area) => area.name);
  const used = new Map();
  for (const file of sourceFiles(project)) {
    const relative = path.relative(root, file).split(path.sep).join("/");
    for (const name of areasUsedIn(readFileSync(file, "utf8"))) {
      if (!used.has(name)) used.set(name, relative);
    }
  }
  const workflow = (name) => readFileSync(path.join(root, ".github", "workflows", name), "utf8");
  const problems = [...compareAreas(listed, used), ...platformProblems(workflow("ci-contract.yml"), workflow("ci.yml"))];
  if (problems.length > 0) {
    console.error(problems.join("\n"));
    process.exit(1);
  }
  console.log(`Contract areas match their test classes, and each runs on Linux and Windows: ${listed.join(", ")}.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main();
