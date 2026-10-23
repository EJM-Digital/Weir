// What starts the real thing for a shard: the image under test in Docker Compose for a Linux shard, and Setup on a GitHub-hosted runner for a
// Windows one. It is the glue between scripts/scenarios-in-ci.mjs and the machine; the choices it is given are made, and tested, there.
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { randomBytes } from "node:crypto";

import { createFirstAccount } from "../docker-smoke/smoke-data.mjs";
import { ORIGIN, Stack } from "../docker-smoke/stack.mjs";
import { WeirClient } from "../docker-smoke/weir-client.mjs";
import { Browser } from "./browser.mjs";
import { ContainerBox } from "./container-box.mjs";
import { runScenarios } from "./run.mjs";
import { SHARDS } from "./shards.mjs";
import { WindowsMachine } from "./windows-machine.mjs";
import * as windowsFiles from "./windows-files.mjs";
import * as windowsInstall from "./windows-install.mjs";
import * as windowsTray from "./windows-tray.mjs";
import * as windowsUpdate from "./windows-update.mjs";
import { expectedVersion, runWindowsShard } from "./windows-shard.mjs";

/** The scenarios of each Windows shard, and the watch on the logs they are judged by. */
const WINDOWS_SHARDS = {
  "windows-install": windowsInstall,
  "windows-files": windowsFiles,
  "windows-tray": windowsTray,
  "windows-update": windowsUpdate,
};

/** The ported scenarios of a Linux shard, run against the image in Compose; `collectFailed` says the recordings of what they showed could not be kept although they ran. */
export async function runLinuxShard({ shard, ids, candidate, logFolder }) {
  const scratch = mkdtempSync(join(tmpdir(), "weir-scenarios-ci-"));
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
    stack.up(candidate, "never", null, beside ? { files: beside.files, environment: started.environment, services: beside.services } : undefined);
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
  return { results, collectFailed };
}

/** The ported scenarios of a Windows shard, run on this machine, which the caller has found to be a clean GitHub-hosted runner. */
export async function runWindowsShardHere({ shard, ids, setup, version, logFolder, update }) {
  const props = readFileSync("apps/server/Directory.Build.props", "utf8");
  try {
    return await runWindowsShard({
      ids,
      registry: WINDOWS_SHARDS[shard].SCENARIOS,
      watchLogs: WINDOWS_SHARDS[shard].watchLogs,
      machine: new WindowsMachine(),
      setupPath: setup ? resolve(setup) : null,
      update,
      version: expectedVersion({ given: version ?? process.env.WEIR_BUILD_VERSION, props }),
      logFolder,
    });
  } catch (error) {
    console.error(`::error::${shard}: ${error.message}`);
    return [];
  }
}
