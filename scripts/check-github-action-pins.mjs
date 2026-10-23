#!/usr/bin/env node
// Every `uses:` in .github/workflows must name a full 40-character commit SHA and say which release that SHA is
// in a trailing comment (`uses: actions/checkout@<sha> # v7.0.1`). A tag can be moved to different code after
// review; a SHA cannot. The comment is what lets a person (and Dependabot) read the pin.
//
// A local reusable workflow or action (`./...`) is part of the same commit, so the commit itself pins it.
//
// Usage: node scripts/check-github-action-pins.mjs
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

/** The unpinned `uses:` lines of every workflow under `root`, and how many workflows were read. */
export function pinFailures(root) {
  const workflowDir = path.join(root, ".github", "workflows");
  const workflowFiles = readdirSync(workflowDir)
    .filter((name) => /\.ya?ml$/i.test(name))
    .map((name) => `.github/workflows/${name}`);
  const failures = [];

  for (const relative of workflowFiles) {
    const lines = readFileSync(path.resolve(root, relative), "utf8").split(/\r?\n/);
    lines.forEach((line, index) => {
      if (line.trimStart().startsWith("#")) return;
      const match = line.match(/\buses:\s*([^\s#]+)(?:\s*#\s*(\S*))?/);
      if (!match) return;
      const reference = match[1];
      if (reference.startsWith("./")) return;
      const where = `${relative}:${index + 1}: ${reference}`;
      const at = reference.lastIndexOf("@");
      if (at < 1 || !/^[0-9a-f]{40}$/i.test(reference.slice(at + 1))) {
        failures.push(`${where} (not a full commit SHA)`);
      } else if (!/^v\d+(\.\d+)*$/.test(match[2] ?? "")) {
        failures.push(`${where} (no "# vX.Y.Z" comment naming the release)`);
      }
    });
  }
  return { failures, workflows: workflowFiles.length };
}

/** Prints the result for the repository at `root`; returns the exit code. */
export function main(root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."), out = { log: console.log, error: console.error }) {
  const { failures, workflows } = pinFailures(root);
  if (failures.length) {
    out.error("GitHub Actions must be pinned to a full commit SHA with a release comment:");
    for (const failure of failures) out.error(`- ${failure}`);
    return 1;
  }
  out.log(`All GitHub Actions in ${workflows} workflow(s) are pinned to full commit SHAs.`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) process.exitCode = main();
