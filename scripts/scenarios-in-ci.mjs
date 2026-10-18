#!/usr/bin/env node
// Runs the scenarios of one Linux shard against the image under test, started the way the Docker install page tells people to (the shipped
// compose.yaml, on its named volume), with a first account made as an operator makes it. Each scenario makes what it needs, is judged on what it
// shows and on what the logs gained, and one that fails does not stop the next.
//
// Where every scenario of the VM catalogue runs, and what is left to port: docs/exec-plans/active/PLAN-SCENARIOS-IN-CI.md.
//
// Usage: node scripts/scenarios-in-ci.mjs <shard> --candidate <local image> [--only <id,id>] [--logs <folder>]
import { appendFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { parseArgs } from "node:util";
import { randomBytes } from "node:crypto";

import { createFirstAccount } from "./docker-smoke/smoke-data.mjs";
import { ORIGIN, Stack } from "./docker-smoke/stack.mjs";
import { WeirClient } from "./docker-smoke/weir-client.mjs";
import { Browser } from "./scenarios-ci/browser.mjs";
import { ContainerBox } from "./scenarios-ci/container-box.mjs";
import { catalogIds, linuxShards, loadHomes, problemsWithHomes, scenariosOfShard } from "./scenarios-ci/homes.mjs";
import { exitCode, runScenarios, summaryMarkdown } from "./scenarios-ci/run.mjs";
import { SHARDS } from "./scenarios-ci/shards.mjs";

const CATALOG = "scripts/scenarios/Weir.Scenarios.Catalog.ps1";

function usage(message) {
  console.error(`${message}\nUsage: node scripts/scenarios-in-ci.mjs <shard> --candidate <image> [--only <id,id>] [--logs <folder>]`);
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

async function main() {
  const { values, positionals } = parseArgs({
    allowPositionals: true,
    options: { candidate: { type: "string" }, only: { type: "string" }, logs: { type: "string", default: "artifacts/scenarios-in-ci" } },
  });
  const [shard] = positionals;
  const homes = loadHomes();
  const broken = problemsWithHomes(homes, catalogIds(readFileSync(CATALOG, "utf8")));
  if (broken.length > 0) usage(`The scenario homes (scripts/scenarios-ci/homes.json) are wrong:\n${broken.join("\n")}`);
  if (!linuxShards(homes).includes(shard) || !values.candidate) usage(`The shard must be one of ${linuxShards(homes).join(", ")}, and --candidate is needed.`);
  const ids = chooseScenarios(homes, shard, values.only);

  const scratch = mkdtempSync(join(tmpdir(), "weir-scenarios-ci-"));
  const logFolder = join(values.logs, shard);
  const stack = new Stack(logFolder, scratch);
  const client = new WeirClient(ORIGIN);
  const { account, browser: wantsBrowser, beside, scenarios } = SHARDS[shard];
  const started = beside?.start();
  let results = [];
  let collectFailed = false;
  let browser;
  stack.removeEverything();
  try {
    for (const image of started?.pull ?? []) stack.pull(image);
    stack.up(values.candidate, "never", null, beside ? { files: beside.files, environment: started.environment, services: beside.services } : undefined);
    stack.waitHealthy();
    const world = { weir: client, box: new ContainerBox(stack), runId: randomBytes(3).toString("hex"), createAccount: () => createFirstAccount({ client, stack }) };
    if (account) await world.createAccount();
    if (beside) world.deluno = await beside.setUp(world, started);
    if (wantsBrowser) world.browser = browser = await Browser.launch(ORIGIN);
    results = await runScenarios({ ids, registry: scenarios, world });
    if (beside?.collect) {
      const files = await beside.collect(world, logFolder);
      console.log(`Kept ${files.length} recording(s) of what passed between the applications:\n${files.join("\n")}`);
    }
  } catch (error) {
    collectFailed = results.length > 0;
    console.error(`::error::${shard}: ${error.message}`);
    console.error(`::group::Container log\n${stack.logs()}\n::endgroup::`);
  } finally {
    await browser?.close();
    stack.keepLogs();
    stack.removeEverything();
    rmSync(scratch, { recursive: true, force: true });
  }

  const summary = summaryMarkdown(results, { shard, candidate: values.candidate });
  console.log(summary);
  mkdirSync(logFolder, { recursive: true });
  writeFileSync(join(logFolder, "results.json"), JSON.stringify({ shard, candidate: values.candidate, results }, null, 2));
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
  for (const result of results.filter((entry) => entry.status !== "passed")) console.error(`::error::${result.id}: ${result.detail}`);
  process.exitCode = collectFailed ? 1 : exitCode(results);
}

await main();
