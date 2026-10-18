// Running the chosen scenarios of a shard one after the other, each judged on its own and on what the logs gained, and the verdict.
import { LogWatch } from "./log-watch.mjs";

// A hang guard: a scenario that has not ended by then is failed, saying which, instead of leaving the run to wait for ever.
const SCENARIO_CEILING_MS = 10 * 60_000;

async function within(running, id) {
  let timer;
  const hung = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`${id} was still running after ${SCENARIO_CEILING_MS / 60_000} minutes.`)), SCENARIO_CEILING_MS);
  });
  try {
    return await Promise.race([running, hung]);
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Runs the scenarios in order; one that fails does not stop the next, so one run reports every failure.
 * @param {string[]} ids
 * @param {Record<string, (world: object) => Promise<string>>} registry The scenarios that are ported, by id.
 * @param {{weir: object, watchLogs?: (world: object) => Promise<{finish(): Promise<void>}>}} world `watchLogs` replaces the default watch of System > Logs, for a shard whose first scenario begins before Weir is there.
 */
export async function runScenarios({ ids, registry, world }) {
  for (const id of ids) {
    if (!registry[id]) throw new Error(`${id} is not ported to CI yet, so it cannot be run there.`);
  }
  const results = [];
  for (const id of ids) {
    const began = Date.now();
    let outcome;
    try {
      const watch = await (world.watchLogs ? world.watchLogs(world) : LogWatch.begin(world.weir));
      world.logWatch = watch;
      const detail = await within(registry[id](world), id);
      await watch.finish();
      outcome = { status: "passed", detail: `${detail} The logs gained no unexpected warning or error.` };
    } catch (error) {
      outcome = { status: "failed", detail: error.message };
    }
    results.push({ id, ...outcome, seconds: Math.round((Date.now() - began) / 1000) });
    // Said as it happens, so a run that ends before the table is written still shows what each scenario did.
    console.log(`${id}: ${outcome.status} in ${results.at(-1).seconds} s${outcome.status === "passed" ? "" : `: ${outcome.detail}`}`);
  }
  return results;
}

/** 0 when something ran and all of it passed. */
export function exitCode(results) {
  return results.length > 0 && results.every((result) => result.status === "passed") ? 0 : 1;
}

/** The table the workflow shows on its summary page. */
export function summaryMarkdown(results, { shard, value, label = "Image under test" }) {
  const cell = (text) => text.replaceAll("|", "\\|").replaceAll(/\r?\n/g, " ");
  const rows = results.map((result) => `| \`${result.id}\` | ${result.status === "passed" ? "passed" : "FAILED"} | ${result.seconds} s | ${cell(result.detail)} |`);
  return [`### Scenarios: ${shard}`, "", `${label}: \`${value}\``, "", "| Scenario | Verdict | Time | What it showed |", "| --- | --- | --- | --- |", ...rows, ""].join("\n");
}
