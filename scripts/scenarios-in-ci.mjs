#!/usr/bin/env node
// Runs the scenarios of one shard. A Linux shard runs against the image under test, started the way the Docker install page tells people to
// (the shipped compose.yaml, on its named volume), with a first account made as an operator makes it. A Windows shard runs on a clean
// GitHub-hosted runner against the Setup this commit built, which it installs, uses and uninstalls for real. Each scenario makes what it needs,
// is judged on what it shows and on what the logs gained, and one that fails does not stop the next.
//
// Where every scenario of the VM catalogue runs, and what is left to port: docs/exec-plans/active/PLAN-SCENARIOS-IN-CI.md.
//
// The windows-update shard installs the newest published release instead and updates it to the build under test through the tray's own update
// code, from a release feed made of that build's packages (--feed), so it is also given the release and its Setup.
//
// Usage: node scripts/scenarios-in-ci.mjs <linux shard> --candidate <local image> [--only <id,id>] [--logs <folder>]
//        node scripts/scenarios-in-ci.mjs <windows shard> --setup <Weir-win-Setup.exe> [--version <version>] [--only <id,id>] [--logs <folder>]
//        node scripts/scenarios-in-ci.mjs windows-update --feed <folder> --previous-setup <Weir-win-Setup.exe> --previous-feed <releases.win.json>
//          --previous-version <version> --version <version> [--only <id,id>] [--logs <folder>]
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { pathToFileURL } from "node:url";

import { catalogIds, linuxShards, loadHomes, problemsWithHomes, scenariosOfShard, windowsShards } from "./scenarios-ci/homes.mjs";
import { exitCode, summaryMarkdown } from "./scenarios-ci/run.mjs";
import { runLinuxShard, runWindowsShardHere } from "./scenarios-ci/shard-runners.mjs";
import { whyNotAThrowawayMachine } from "./scenarios-ci/windows-machine.mjs";

const CATALOG = "scripts/scenarios/Weir.Scenarios.Catalog.ps1";

const USAGE = [
  "node scripts/scenarios-in-ci.mjs <linux shard> --candidate <image> [--only <id,id>] [--logs <folder>]",
  "node scripts/scenarios-in-ci.mjs <windows shard> --setup <Weir-win-Setup.exe> [--version <version>] [--only <id,id>] [--logs <folder>]",
  "node scripts/scenarios-in-ci.mjs windows-update --feed <folder> --previous-setup <Weir-win-Setup.exe> --previous-feed <releases.win.json> --previous-version <version> --version <version> [--only <id,id>] [--logs <folder>]",
].join("\n       ");

/** What is wrong with the command line, to be printed with the usage. */
export class UsageError extends Error {}

const usage = (message) => {
  throw new UsageError(message);
};

export function chooseScenarios(homes, shard, only) {
  const ported = scenariosOfShard(homes, shard).filter((scenario) => scenario.state !== "to-port").map((scenario) => scenario.id);
  if (!only) return ported;
  const chosen = only.split(",").map((id) => id.trim());
  const unknown = chosen.filter((id) => !ported.includes(id));
  if (unknown.length > 0) usage(`${unknown.join(", ")} ${unknown.length > 1 ? "are" : "is"} not a ported scenario of ${shard} (${ported.join(", ")}).`);
  return chosen;
}

/**
 * Runs the shard named in `argv` and returns the exit code. `shards` starts the real thing (the image in Compose, Setup on a runner) and is
 * what a test replaces; `env` and `platform` say whether this is a machine a Windows shard may install Weir on.
 */
export async function main(argv, { env = process.env, platform = process.platform, shards = { runLinuxShard, runWindowsShardHere }, homes = loadHomes(), log = console.log, error = console.error } = {}) {
  try {
    return await run(argv, { env, platform, shards, homes, log, error });
  } catch (failure) {
    if (!(failure instanceof UsageError)) throw failure;
    error(`${failure.message}
Usage: ${USAGE}`);
    return 2;
  }
}

async function run(argv, { env, platform, shards, homes, log, error }) {
  const { values, positionals } = parseArgs({
    args: argv,
    allowPositionals: true,
    options: {
      candidate: { type: "string" },
      setup: { type: "string" },
      feed: { type: "string" },
      "previous-setup": { type: "string" },
      "previous-feed": { type: "string" },
      "previous-version": { type: "string" },
      version: { type: "string" },
      only: { type: "string" },
      logs: { type: "string", default: "artifacts/scenarios-in-ci" },
    },
  });
  const [shard] = positionals;
  const broken = problemsWithHomes(homes, catalogIds(readFileSync(CATALOG, "utf8")));
  if (broken.length > 0) usage(`The scenario homes (scripts/scenarios-ci/homes.json) are wrong:\n${broken.join("\n")}`);
  const linux = linuxShards(homes).includes(shard);
  const windows = windowsShards(homes).includes(shard);
  if (linux && !values.candidate) usage(`${shard} runs against an image, so --candidate is needed.`);
  const updating = shard === "windows-update";
  if (windows && !updating && !values.setup) usage(`${shard} runs against a Setup, so --setup is needed.`);
  if (updating) {
    const missing = ["feed", "previous-setup", "previous-feed", "previous-version", "version"].filter((name) => !values[name]);
    if (missing.length > 0) usage(`${shard} updates a release to the build under test, so it needs ${missing.map((name) => `--${name}`).join(", ")}.`);
  }
  if (!linux && !windows) usage(`The shard must be one of ${[...linuxShards(homes), ...windowsShards(homes)].join(", ")}.`);
  const ids = chooseScenarios(homes, shard, values.only);
  if (windows) {
    const refusal = whyNotAThrowawayMachine(env, platform);
    if (refusal) usage(`${shard} installs and uninstalls Weir for real, and ${refusal}.`);
  }
  const logFolder = join(values.logs, shard);

  const subject = linux
    ? { label: "Image under test", value: values.candidate }
    : updating
      ? { label: "Feed of the build under test", value: values.feed }
      : { label: "Setup under test", value: values.setup };
  const update = updating
    ? { feedFolder: resolve(values.feed), previousSetup: resolve(values["previous-setup"]), previousFeed: resolve(values["previous-feed"]), previousVersion: values["previous-version"].replace(/^v/, "") }
    : undefined;
  const { results, collectFailed } = linux
    ? await shards.runLinuxShard({ shard, ids, candidate: values.candidate, logFolder })
    : { results: await shards.runWindowsShardHere({ shard, ids, setup: values.setup, version: values.version, logFolder, update }), collectFailed: false };

  const summary = summaryMarkdown(results, { shard, ...subject });
  log(summary);
  mkdirSync(logFolder, { recursive: true });
  writeFileSync(join(logFolder, "results.json"), JSON.stringify({ shard, [linux ? "candidate" : "setup"]: subject.value, results }, null, 2));
  if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, summary);
  for (const result of results.filter((entry) => entry.status !== "passed")) error(`::error::${result.id}: ${result.detail}`);
  return collectFailed ? 1 : exitCode(results);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  // Anything a scenario left running (a server, a relay, a call to a share that hangs) must not hold the job after its results are written.
  process.exit(await main(process.argv.slice(2)));
}
