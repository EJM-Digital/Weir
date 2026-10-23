#!/usr/bin/env node
// Which mutation targets a run of the Mutation workflow (.github/workflows/mutation.yml) mutates, from its `targets` input:
//
//   all (or empty)      every target; what the weekly run does
//   a, b, c             just those targets, by the ids in .github/mutation-targets.json
//   changed:<sha>       the targets whose own files changed between <sha> and the checked-out commit: the files they
//                       mutate, the tests that kill their mutants and their mutation setup (scripts/release/mutation-inputs.mjs)
//
//   REQUESTED="changed:abc1234" node scripts/resolve-mutation-targets.mjs
//
// The result goes to $GITHUB_OUTPUT as `matrix`, `targets` and `any` (the same outputs as scripts/select-mutation-targets.mjs)
// and to the log.
import { execFileSync } from "node:child_process";
import { appendFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { changedTargets } from "./release/mutation-inputs.mjs";
import { loadTargets, outputsOf } from "./select-mutation-targets.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const SHA = /^[0-9a-f]{7,40}$/;

const gitChangedSince = (sha) =>
  execFileSync("git", ["diff", "--name-only", "--no-renames", "-z", sha, "HEAD", "--"], { encoding: "utf8", cwd: root, maxBuffer: 256 * 1024 * 1024 }).split("\0").filter(Boolean);

/** The targets a `targets` input asks for. `changedSince(sha)` lists the paths that differ from that commit. */
export function resolveTargets(requested, targets, { changedSince = gitChangedSince, rootFolder = root } = {}) {
  const value = String(requested ?? "").trim();
  if (value === "" || value === "all") return targets;
  if (value.startsWith("changed:")) {
    const sha = value.slice("changed:".length).trim();
    if (!SHA.test(sha)) throw new Error(`"${value}" is not changed:<commit sha>; give the commit to compare with as a hex sha.`);
    const touched = new Set(changedTargets(changedSince(sha), rootFolder, targets).map((item) => item.target.target));
    return targets.filter((target) => touched.has(target.target));
  }
  const names = value.split(",").map((name) => name.trim()).filter(Boolean);
  const unknown = names.filter((name) => !targets.some((target) => target.target === name));
  if (unknown.length > 0) {
    throw new Error(`Unknown mutation target${unknown.length === 1 ? "" : "s"}: ${unknown.join(", ")}. The targets are: ${targets.map((target) => target.target).join(", ")}.`);
  }
  return targets.filter((target) => names.includes(target.target));
}

function main() {
  try {
    const selected = resolveTargets(process.env.REQUESTED, loadTargets(root));
    const outputs = outputsOf(selected);
    console.log(selected.length ? `Mutation targets to run: ${outputs.targets}` : "No mutation target has changed; nothing to run.");
    if (process.env.GITHUB_OUTPUT) {
      appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(outputs).map(([name, value]) => `${name}=${value}\n`).join(""));
    }
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
