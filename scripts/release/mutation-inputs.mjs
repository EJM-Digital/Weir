// What a mutation target's score depends on, so a release can carry a passing result forward while the target's own
// inputs are unchanged (docs/ci-cost.md). A target's inputs are:
//
//   mutated files  the files its Stryker config's `mutate` list names, and the config itself, which holds the list.
//   its tests      the tests that kill its mutants, as the config defines them. A Stryker.NET config names its test
//                  projects (`test-projects`; else the project the run starts in): every file in those folders counts,
//                  and so does every test-support project they reference and the shared test sources. A StrykerJS config
//                  runs a command: `vitest run <paths>` names its test folders or files exactly; `vitest related <files>`
//                  finds tests by import graph, which is not known here, so every test file and the test setup under
//                  apps/web/src count (the conservative choice).
//   its setup      the target's entry in .github/mutation-targets.json, the Mutation workflow, the score check, and the
//                  build and package settings its tests are built with.
//
// Nothing else counts: a change to another target's files does not touch this target.
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";

import { configOf, loadTargets, patternToRegExp } from "../select-mutation-targets.mjs";

const WEB = "apps/web/";
const SHARED_SETUP = [".github/workflows/mutation.yml", "scripts/check-mutation-floors.mjs"];
const WEB_SETUP = [`${WEB}package.json`, `${WEB}package-lock.json`, `${WEB}vite.config.ts`, `${WEB}tsconfig.json`, `${WEB}tsconfig.node.json`];
const DOTNET_BUILD_FILES = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "global.json"];
const WEB_TEST_FILE = /^apps\/web\/src\/(?:test\/.*|.*\.test\.[^/]+)$/;

const posix = (file) => String(file).replaceAll("\\", "/").replace(/^\.\//, "");

function readConfig(target, root) {
  const config = JSON.parse(readFileSync(path.join(root, configOf(target).file), "utf8"));
  return config["stryker-config"] ?? config;
}

function referencedProjects(projectFile, root) {
  const text = readFileSync(path.join(root, projectFile), "utf8");
  return [...text.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)].map((match) => path.posix.join(path.posix.dirname(projectFile), posix(match[1])));
}

/** The folders of a .NET target's tests: its test projects, the test-support projects they reference (never shipped code under src/), and the shared test sources. */
function dotnetTestFolders(target, config, root) {
  const listed = (config["test-projects"] ?? []).map((project) => path.posix.join(target.directory, posix(project)));
  const folders = new Set([target.directory, ...listed.map((project) => path.posix.dirname(project))]);
  const underTest = path.posix.basename(posix(config.project ?? ""), ".csproj");
  const ownProject = path.posix.join(target.directory, `${path.posix.basename(target.directory)}.csproj`);
  const queue = listed.length > 0 ? [...listed] : [ownProject];
  const seen = new Set(queue);
  while (queue.length > 0) {
    const project = queue.shift();
    if (!existsSync(path.join(root, project))) continue;
    for (const reference of referencedProjects(project, root)) {
      if (seen.has(reference)) continue;
      seen.add(reference);
      if (/(^|\/)src\//.test(reference) || path.posix.basename(reference, ".csproj") === underTest) continue;
      folders.add(path.posix.dirname(reference));
      queue.push(reference);
    }
  }
  folders.add(path.posix.join(path.posix.dirname(target.directory), "Shared"));
  return [...folders].map((folder) => `${folder}/`);
}

/** The test paths of `vitest run <paths>`, or null when the command finds its tests another way. */
function vitestRunPaths(command) {
  const words = String(command ?? "").trim().split(/\s+/);
  const at = words.findIndex((word, index) => word === "vitest" && words[index + 1] === "run");
  if (at < 0) return null;
  return words.slice(at + 2).filter((word) => !word.startsWith("-")).map((word) => posix(path.posix.join(WEB, word)).replace(/\/$/, ""));
}

function webTests(config) {
  const paths = vitestRunPaths(config.commandRunner?.command);
  if (!paths) return (changed) => WEB_TEST_FILE.test(changed);
  return (changed) => changed === `${WEB}src/test/setup.ts` || paths.some((entry) => changed === entry || changed.startsWith(`${entry}/`));
}

/** The inputs of one target: { mutated: RegExp[], tests(path), setup: string[] }, read from the checkout at `root`. */
export function inputsOf(target, root) {
  const { file, prefix } = configOf(target);
  const config = readConfig(target, root);
  const mutated = (config.mutate ?? []).filter((pattern) => !pattern.startsWith("!")).map((pattern) => patternToRegExp(pattern, prefix));
  const setup = [file, ...SHARED_SETUP];
  if (target.kind === "web") {
    setup.push(...WEB_SETUP);
    return { mutated, tests: webTests(config), setup };
  }
  setup.push(...DOTNET_BUILD_FILES.map((name) => `${posix(target.restore)}/${name}`));
  const folders = dotnetTestFolders(target, config, root);
  return { mutated, tests: (changed) => folders.some((folder) => changed.startsWith(folder)), setup };
}

/** The changed paths that alter the target's score, each with why, in plain words. */
export function changesToTarget(target, changedPaths, root) {
  const inputs = inputsOf(target, root);
  const reasons = [];
  for (const raw of changedPaths) {
    const changed = posix(raw);
    if (inputs.mutated.some((matcher) => matcher.test(changed))) reasons.push(`${changed} (a file it mutates)`);
    else if (inputs.tests(changed)) reasons.push(`${changed} (a test it runs)`);
    else if (inputs.setup.includes(changed)) reasons.push(`${changed} (its mutation setup)`);
  }
  return reasons;
}

/** Whether the target's own entry in .github/mutation-targets.json differs between two commits (or cannot be read). */
export function entryChanged(target, base, head, git) {
  const entryAt = (commit) => {
    const list = JSON.parse(git(["show", `${commit}:.github/mutation-targets.json`]));
    return JSON.stringify(list.find((entry) => entry.target === target.target) ?? null);
  };
  try {
    return entryAt(base) !== entryAt(head);
  } catch {
    return true;
  }
}

/** The targets (in the targets file's order) with something changed in the paths, each with the reasons. */
export function changedTargets(changedPathList, root, targets = loadTargets(root)) {
  return targets.map((target) => ({ target, reasons: changesToTarget(target, changedPathList, root) })).filter((item) => item.reasons.length > 0);
}
