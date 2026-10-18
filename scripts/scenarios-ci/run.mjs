// Running the chosen scenarios of a shard one after the other, each judged on its own and on what the logs gained, and the verdict.
import { LogWatch } from "./log-watch.mjs";

/**
 * Runs the scenarios in order; one that fails does not stop the next, so one run reports every failure.
 * @param {string[]} ids
 * @param {Record<string, ((world: object) => Promise<string>) | {run: (world: object) => Promise<string>, logs?: "since-start", expectedLogs?: (world: object) => (row: object) => boolean}>} registry
 *   The scenarios that are ported, by id. A scenario that is signed out when it begins has its logs judged from the start of the stack. One that makes
 *   Weir fail on purpose says which warnings that is expected to log (`expectedLogs`); any other warning or error still fails it.
 * @param {{weir: object}} world
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
      const { run, logs, expectedLogs } = typeof registry[id] === "function" ? { run: registry[id] } : registry[id];
      const watch = logs === "since-start" ? LogWatch.sinceStart(world.weir) : await LogWatch.begin(world.weir);
      const detail = await run(world);
      const accepted = await watch.finish(expectedLogs?.(world));
      const logged = accepted.length === 0 ? "The logs gained no warning or error." : `The logs gained only what the scenario expected: ${accepted.map((row) => row.title).join(" | ")}.`;
      outcome = { status: "passed", detail: `${detail} ${logged}` };
    } catch (error) {
      outcome = { status: "failed", detail: error.message };
    }
    results.push({ id, ...outcome, seconds: Math.round((Date.now() - began) / 1000) });
  }
  return results;
}

/** 0 when something ran and all of it passed. */
export function exitCode(results) {
  return results.length > 0 && results.every((result) => result.status === "passed") ? 0 : 1;
}

/** The table the workflow shows on its summary page. */
export function summaryMarkdown(results, { shard, candidate }) {
  const cell = (text) => text.replaceAll("|", "\\|").replaceAll(/\r?\n/g, " ");
  const rows = results.map((result) => `| \`${result.id}\` | ${result.status === "passed" ? "passed" : "FAILED"} | ${result.seconds} s | ${cell(result.detail)} |`);
  return [`### Scenarios: ${shard}`, "", `Image under test: \`${candidate}\``, "", "| Scenario | Verdict | Time | What it showed |", "| --- | --- | --- | --- |", ...rows, ""].join("\n");
}
