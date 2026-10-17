// Running the chosen scenarios of a shard one after the other, each judged on its own and on what the logs gained, and the verdict.
import { LogWatch } from "./log-watch.mjs";

/**
 * Runs the scenarios in order; one that fails does not stop the next, so one run reports every failure.
 * @param {string[]} ids
 * @param {Record<string, (world: object) => Promise<string>>} registry The scenarios that are ported, by id.
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
      const watch = await LogWatch.begin(world.weir);
      const detail = await registry[id](world);
      await watch.finish();
      outcome = { status: "passed", detail: `${detail} The logs gained no warning or error.` };
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
