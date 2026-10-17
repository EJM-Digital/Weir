// Where each scenario of the VM catalogue runs once the VM is gone (docs/exec-plans/active/PLAN-SCENARIOS-IN-CI.md), read from homes.json.
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HOMES = ["linux", "windows", "not-portable"];
const STATES = ["to-port", "ported", "green-twice"];

export function loadHomes() {
  return JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), "homes.json"), "utf8"));
}

/** The scenario ids in the VM catalogue's text, in its order. */
export function catalogIds(catalogText) {
  return [...catalogText.matchAll(/^\s+Id = '([a-z-]+)'/gm)].map((match) => match[1]);
}

/** What is wrong with the manifest against the catalogue, in words; nothing when every scenario has exactly one home with a reason. */
export function problemsWithHomes(homes, catalog) {
  const problems = [];
  const seen = new Set();
  for (const scenario of homes.scenarios) {
    const { id, home, shard, state, why } = scenario;
    if (seen.has(id)) problems.push(`${id}: listed more than once.`);
    seen.add(id);
    if (!catalog.includes(id)) problems.push(`${id}: not in the catalogue of scenarios/Weir.Scenarios.Catalog.ps1.`);
    if (!HOMES.includes(home)) problems.push(`${id}: home "${home}" is none of ${HOMES.join(", ")}.`);
    if (!STATES.includes(state)) problems.push(`${id}: state "${state}" is none of ${STATES.join(", ")}.`);
    problems.push(...problemsWithPasses(scenario));
    if (!why || why.trim().length < 10) problems.push(`${id}: ${home} needs a reason (why).`);
    if (home === "not-portable") {
      if (shard) problems.push(`${id}: not-portable must have no shard (it has ${shard}).`);
    } else if (!homes.shards[shard]) {
      problems.push(`${id}: shard ${shard} is not one of the manifest's shards.`);
    } else if (homes.shards[shard].home !== home) {
      problems.push(`${id}: it is ${home} but shard ${shard} is ${homes.shards[shard].home}.`);
    }
  }
  for (const id of catalog) {
    if (!seen.has(id)) problems.push(`${id}: in the catalogue but has no home.`);
  }
  return problems;
}

/** A scenario that has not been ported has passed nowhere; one that is green twice has passed on main, in two runs, on different commits. */
function problemsWithPasses({ id, state, passedInCi = [] }) {
  const problems = [];
  if (state === "to-port" && passedInCi.length > 0) problems.push(`${id}: it is to-port but lists runs it passed in.`);
  for (const pass of passedInCi) {
    if (!Number.isInteger(pass.run) || !/^[0-9a-f]{40}$/.test(pass.commit ?? "") || !pass.ref) problems.push(`${id}: a pass needs its run number, its full commit and its ref.`);
  }
  if (state === "green-twice") {
    const commits = new Set(passedInCi.filter((pass) => pass.ref === "main").map((pass) => pass.commit));
    if (commits.size < 2) problems.push(`${id}: green-twice needs passes on main on two different commits (it has ${commits.size}).`);
  }
  return problems;
}

export function scenariosOfShard(homes, shard) {
  return homes.scenarios.filter((scenario) => scenario.shard === shard);
}

/** The Linux shards that have at least one scenario ported, which is what the workflow's matrix lists. */
export function linuxShards(homes) {
  return Object.keys(homes.shards).filter(
    (shard) => homes.shards[shard].home === "linux" && scenariosOfShard(homes, shard).some((scenario) => scenario.state !== "to-port"),
  );
}
