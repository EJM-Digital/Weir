// The Linux shards: which scenarios each runs, and how its stack is made ready before them.
//   account: a first account exists before the scenarios begin (the ones that need a signed-in Weir); a shard without one starts them on a Weir
//            nobody has signed in to.
//   browser: a Chromium is started for them, as `world.browser`.
import { firstVisitAccount } from "./first-visit-account.mjs";
import { SCENARIOS as WEIR_ONLY } from "./weir-only.mjs";

// A scenario is its function, or { run, logs: "since-start" } when the logs are judged from the start of the stack, not from the scenario's
// own start: the scenario is what signs in, so there is nothing to read before it.
export const SHARDS = {
  "weir-only": { account: true, browser: false, scenarios: WEIR_ONLY },
  "weir-browser": { account: false, browser: true, scenarios: { "first-visit-account": { run: firstVisitAccount, logs: "since-start" } } },
};

/** Every scenario with code, by catalogue id. */
export const allScenarios = () => Object.assign({}, ...Object.values(SHARDS).map((shard) => shard.scenarios));
