#!/usr/bin/env node
// Which mutation targets a change touches (#977): the Mutation workflow (.github/workflows/mutation.yml) runs every
// target weekly, but on a pull request only the targets whose mutated files the pull request changed.
//
//   node scripts/select-mutation-targets.mjs --all
//   node scripts/select-mutation-targets.mjs --changed <file with one changed path per line>
//
// The targets are listed in .github/mutation-targets.json. Each one has a Stryker config whose `mutate` list names the
// files it changes; a target is selected when a changed path matches one of them, or is the config itself. The result goes
// to $GITHUB_OUTPUT as `matrix` (the JSON a job matrix reads), `targets` (the comma-separated ids the floors check reads)
// and `any`, and to the log.
import { appendFileSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const defaultRoot = path.resolve(here, "..");

function escapeRegExp(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** A Stryker `mutate` pattern as the regular expression a repository-relative path is matched with. */
export function patternToRegExp(pattern, prefix = "") {
  const withoutSpan = pattern.replace(/\{\d+\.\.\d+\}$/, "");
  let source = "";
  for (let index = 0; index < withoutSpan.length; index += 1) {
    const rest = withoutSpan.slice(index);
    if (rest.startsWith("**/")) {
      source += "(?:.*/)?";
      index += 2;
    } else if (rest.startsWith("**")) {
      source += ".*";
      index += 1;
    } else if (rest[0] === "*") {
      source += "[^/]*";
    } else {
      source += escapeRegExp(rest[0]);
    }
  }
  return new RegExp(`^${escapeRegExp(prefix)}${source}$`);
}

export function loadTargets(root = defaultRoot) {
  return JSON.parse(readFileSync(path.join(root, ".github", "mutation-targets.json"), "utf8"));
}

/** The config of a target and the folder its mutate patterns are relative to, both repository-relative with forward slashes. */
export function configOf(target) {
  const file = path.posix.normalize(path.posix.join(target.directory, target.config));
  return { file, prefix: target.kind === "web" ? "apps/web/" : "" };
}

/** The `mutate` patterns of a target's Stryker config (Stryker.NET nests them under "stryker-config"). */
export function mutatePatterns(target, root = defaultRoot) {
  const config = JSON.parse(readFileSync(path.join(root, configOf(target).file), "utf8"));
  const patterns = (config["stryker-config"] ?? config).mutate;
  if (!Array.isArray(patterns) || patterns.length === 0) throw new Error(`${target.target}: its Stryker config lists nothing to mutate.`);
  return patterns.filter((pattern) => !pattern.startsWith("!"));
}

/** The targets a set of changed paths touches, in the order the targets file lists them. */
export function selectTargets(changed, targets, root = defaultRoot) {
  const paths = changed.map((file) => file.trim().replaceAll("\\", "/")).filter(Boolean);
  return targets.filter((target) => {
    const { file, prefix } = configOf(target);
    if (paths.includes(file)) return true;
    const matchers = mutatePatterns(target, root).map((pattern) => patternToRegExp(pattern, prefix));
    return paths.some((changedPath) => matchers.some((matcher) => matcher.test(changedPath)));
  });
}

export function outputsOf(selected) {
  return {
    matrix: JSON.stringify({ include: selected }),
    targets: selected.map((target) => target.target).join(","),
    any: String(selected.length > 0),
  };
}

export function parseArguments(argv) {
  if (argv.length === 1 && argv[0] === "--all") return { all: true };
  if (argv.length === 2 && argv[0] === "--changed") return { changedFile: argv[1] };
  throw new Error("Use --all, or --changed <file listing the changed paths>.");
}

function main() {
  try {
    const options = parseArguments(process.argv.slice(2));
    const targets = loadTargets();
    const selected = options.all ? targets : selectTargets(readFileSync(options.changedFile, "utf8").split("\n"), targets);
    const outputs = outputsOf(selected);
    console.log(selected.length ? `Mutation targets to run: ${outputs.targets}` : "No mutation target is touched by this change.");
    if (process.env.GITHUB_OUTPUT) {
      appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(outputs).map(([name, value]) => `${name}=${value}\n`).join(""));
    }
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
