#!/usr/bin/env node
// Runs the scenarios of one shard. A Linux shard runs against the image under test, started the way the Docker install page tells people to
// (the shipped compose.yaml, on its named volume), with a first account made as an operator makes it. A Windows shard runs on a clean
// GitHub-hosted runner against the Setup this commit built, which it installs, uses and uninstalls for real. Each scenario makes what it needs,
// is judged on what it shows and on what the logs gained, and one that fails does not stop the next.
//
// Where every scenario of the VM catalogue runs, and what is left to port: docs/exec-plans/active/PLAN-SCENARIOS-IN-CI.md.
//
// Usage: node scripts/scenarios-in-ci.mjs <linux shard> --candidate <local image> [--only <id,id>] [--logs <folder>]
//        node scripts/scenarios-in-ci.mjs <windows shard> --setup <Weir-win-Setup.exe> [--version <version>] [--only <id,id>] [--logs <folder>]
import { appendFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { parseArgs } from "node:util";
import { randomBytes } from "node:crypto";

import { createFirstAccount } from "./docker-smoke/smoke-data.mjs";
import { ORIGIN, Stack } from "./docker-smoke/stack.mjs";
import { WeirClient } from "./docker-smoke/weir-client.mjs";
import { Browser } from "./scenarios-ci/browser.mjs";
import { ContainerBox } from "./scenarios-ci/container-box.mjs";
import { catalogIds, linuxShards, loadHomes, problemsWithHomes, scenariosOfShard, windowsShards } from "./scenarios-ci/homes.mjs";
import { exitCode, runScenarios, summaryMarkdown } from "./scenarios-ci/run.mjs";
import { SHARDS } from "./scenarios-ci/shards.mjs";
import { WindowsMachine, whyNotAThrowawayMachine } from "./scenarios-ci/windows-machine.mjs";
import { expectedVersion, runWindowsShard } from "./scenarios-ci/windows-shard.mjs";

const CATALOG = "scripts/scenarios/Weir.Scenarios.Catalog.ps1";

const USAGE = [
  "node scripts/scenarios-in-ci.mjs <linux shard> --candidate <image> [--only <id,id>] [--logs <folder>]",
  "node scripts/scenarios-in-ci.mjs <windows shard> --setup <Weir-win-Setup.exe> [--version <version>] [--only <id,id>] [--logs <folder>]",
].join("\n       ");

function usage(message) {
  console.error(`${message}\nUsage: ${USAGE}`);
  process.exit(2);
}

function chooseScenarios(homes, shard, only) {
  const ported = scenariosOfShard(homes, shard).filter((scenario) => scenario.state !== "to-port").map((scenario) => scenario.id);
  if (!only) return ported;
  const chosen = only.split(",").map((id) => id.trim());
  const unknown = chosen.filter((id) => !ported.includes(id));
  if (unknown.length > 0) usage(`${unknown.join(", ")} ${unknown.length > 1 ? "are" : "is"} not a ported scenario of ${shard} (${ported.join(", ")}).`);
  return chosen;
}

/** The ported scenarios of a Linux shard, run against the image in Compose. */
async function runLinuxShard({ shard, ids, candidate, logFolder }) {
  const scratch = mkdtempSync(join(tmpdir(), "weir-scenarios-ci-"));
  const stack = new Stack(logFolder, scratch);
  const client = new WeirClient(ORIGIN);
  const { account, browser: wantsBrowser, beside, scenarios } = SHARDS[shard];
  const started = beside?.start();
  let results = [];
  let browser;
  stack.removeEverything();
  try {
    for (const image of started?.pull ?? []) stack.pull(image);
    stack.up(candidate, "never", null, beside ? { files: beside.files, environment: started.environment, services: beside.services } : undefined);
    stack.waitHealthy();
    const world = { weir: client, box: new ContainerBox(stack), runId: randomBytes(3).toString("hex"), createAccount: () => createFirstAccount({ client, stack }) };
    if (account) await world.createAccount();
    if (beside) world.deluno = await beside.setUp(world, started);
    if (wantsBrowser) world.browser = browser = await Browser.launch(ORIGIN);
    results = await runScenarios({ ids, registry: scenarios, world });
  } catch (error) {
    console.error(`::error::${shard}: ${error.message}`);
    console.error(`::group::Container log\n${stack.logs()}\n::endgroup::`);
  } finally {
    await browser?.close();
    stack.keepLogs();
    stack.removeEverything();
    rmSync(scratch, { recursive: true, force: true });
  }
  return results;
}

/** The ported scenarios of a Windows shard, run on this machine, which must be a clean GitHub-hosted runner. */
async function runWindowsShardHere({ shard, ids, setup, version, logFolder }) {
  const refusal = whyNotAThrowawayMachine(process.env, process.platform);
  if (refusal) usage(`${shard} installs and uninstalls Weir for real, and ${refusal}.`);
  const props = readFileSync("apps/server/Directory.Build.props", "utf8");
  try {
    return await runWindowsShard({
      ids,
      machine: new WindowsMachine(),
      setupPath: resolve(setup),
      version: expectedVersion({ given: version ?? process.env.WEIR_BUILD_VERSION, props }),
      logFolder,
    });
  } catch (error) {
    console.error(`::error::${shard}: ${error.message}`);
    return [];
  }
}

async function main() {
  const { values, positionals } = parseArgs({
    allowPositionals: true,
    options: {
      candidate: { type: "string" },
      setup: { type: "string" },
      version: { type: "string" },
      only: { type: "string" },
      logs: { type: "string", default: "artifacts/scenarios-in-ci" },
    },
  });
  const [shard] = positionals;
  const homes = loadHomes();
  const broken = problemsWithHomes(homes, catalogIds(readFileSync(CATALOG, "utf8")));
  if (broken.length > 0) usage(`The scenario homes (scripts/scenarios-ci/homes.json) are wrong:\n${broken.join("\n")}`);
  const linux = linuxShards(homes).includes(shard);
  const windows = windowsShards(homes).includes(shard);
  if (linux && !values.candidate) usage(`${shard} runs against an image, so --candidate is needed.`);
  if (windows && !values.setup) usage(`${shard} runs against a Setup, so --setup is needed.`);
  if (!linux && !windows) usage(`The shard must be one of ${[...linuxShards(homes), ...windowsShards(homes)].join(", ")}.`);
  const ids = chooseScenarios(homes, shard, values.only);
  const logFolder = join(values.logs, shard);

  const subject = linux ? { label: "Image under test", value: values.candidate } : { label: "Setup under test", value: values.setup };
  const results = linux
    ? await runLinuxShard({ shard, ids, candidate: values.candidate, logFolder })
    : await runWindowsShardHere({ shard, ids, setup: values.setup, version: values.version, logFolder });

  const summary = summaryMarkdown(results, { shard, ...subject });
  console.log(summary);
  mkdirSync(logFolder, { recursive: true });
  writeFileSync(join(logFolder, "results.json"), JSON.stringify({ shard, [linux ? "candidate" : "setup"]: subject.value, results }, null, 2));
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
  for (const result of results.filter((entry) => entry.status !== "passed")) console.error(`::error::${result.id}: ${result.detail}`);
  process.exitCode = exitCode(results);
}

await main();
