#!/usr/bin/env node
// Fails when a recording of Deluno's own side is neither replayed by a contract test nor explained (#971).
//
// apps/server/tests/fixtures/recorded/deluno-side/ holds what Deluno recorded of its talk with Weir. A recording nothing replays proves
// nothing, so every state in it is named (as a quoted string) by a test under apps/server/tests/Weir.Contract.Tests, or is listed in
// apps/server/tests/fixtures/recorded/deluno-side-no-weir-behaviour.json with the reason, and the route Weir would have to call for the
// recording to matter. That route must not appear in Weir's server code: when Weir starts calling it, the recording has to be replayed.
// A recording Weir does have behaviour for, but whose replay is not written yet, is listed in
// apps/server/tests/fixtures/recorded/deluno-side-awaiting-replay.json with the open issue that will replay it. Such an entry comes out of
// the list when a test replays the recording, so the list only shrinks.
//
// The recordings of a real Sonarr, Radarr and the five download clients (apps/server/tests/fixtures/recorded/<app>, #1013, #1014, #1015) are held to the same
// rule: every state is named as a quoted string by a test under apps/server/tests (Infrastructure, Api or Contract), or covered by a test
// that lists a whole family of states with ArrRecording.States(app, "<prefix>") (the webhook-* events). Nothing excuses a state there: a
// recording of the real app that no test replays is a recording that proves nothing, so it is either replayed or not made.
//
// Usage: node scripts/check-recorded-replays.mjs
import { readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = fileURLToPath(new URL("..", import.meta.url));
const recordings = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded", "deluno-side");
const allowList = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded", "deluno-side-no-weir-behaviour.json");
const awaitingList = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded", "deluno-side-awaiting-replay.json");
const tests = path.join(repositoryRoot, "apps", "server", "tests", "Weir.Contract.Tests");
const serverCode = path.join(repositoryRoot, "apps", "server", "src");

const arrRecordings = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded");
const recordedApps = ["sonarr", "radarr", "qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget"];
const allTests = path.join(repositoryRoot, "apps", "server", "tests");

/**
 * What is wrong with the replay of the Sonarr, Radarr and download client recordings, one sentence each.
 * @param statesByApp app -> every recorded state
 * @param testSource the text of the server tests
 */
export function arrProblems(statesByApp, testSource) {
  const prefixes = [...testSource.matchAll(/\.States\(\s*\w+\s*,\s*"([^"]+)"\s*\)/g)].map((match) => match[1]);
  const out = [];
  for (const [app, states] of Object.entries(statesByApp)) {
    if (states.length === 0) out.push(`${app}: no recordings.`);
    for (const state of states) {
      const named = testSource.includes(`"${state}"`) || prefixes.some((prefix) => state.startsWith(prefix));
      if (!named) out.push(`${app}/${state} is recorded but no test replays it. Replay it in a test, or take the step out of the recorder.`);
    }
  }
  return out;
}

/** The states in the folder: file name without .json, by version folder. */
export function statesIn(root) {
  const states = [];
  for (const version of readdirSync(root).sort()) {
    const folder = path.join(root, version);
    if (!statSync(folder).isDirectory()) continue;
    for (const file of readdirSync(folder).sort()) if (file.endsWith(".json")) states.push(file.slice(0, -".json".length));
  }
  return states;
}

export function sourceText(folder) {
  let text = "";
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    if (entry.name === "bin" || entry.name === "obj") continue;
    const full = path.join(folder, entry.name);
    if (entry.isDirectory()) text += sourceText(full);
    else if (entry.name.endsWith(".cs")) text += readFileSync(full, "utf8") + "\n";
  }
  return text;
}

/**
 * What is wrong with the recordings' coverage, one sentence each.
 * @param states every recorded state
 * @param testSource the text of the contract tests
 * @param serverSource the text of the server code
 * @param excused state -> { reason, route } for recordings Weir has no behaviour for
 * @param awaiting state -> { reason, issue } for recordings Weir has behaviour for and no test replays yet
 */
export function problems(states, testSource, serverSource, excused, awaiting = {}) {
  const out = [];
  for (const state of states) {
    const replayed = testSource.includes(`"${state}"`);
    const excuse = excused[state];
    const pending = awaiting[state];
    if (replayed && pending) out.push(`${state} is replayed by a contract test, so it must come out of ${path.basename(awaitingList)}.`);
    if (pending && !(pending.reason && Number.isInteger(pending.issue) && pending.issue > 0)) {
      out.push(`${state}: its entry in ${path.basename(awaitingList)} needs a "reason" and the number of the "issue" that will replay it.`);
    }

    if (!replayed && !excuse && !pending) {
      out.push(`${state} is recorded but no contract test replays it, and neither ${path.basename(allowList)} nor ${path.basename(awaitingList)} says why not.`);
    }

    if (replayed && excuse) out.push(`${state} is replayed by a contract test, so it must come out of ${path.basename(allowList)}.`);
    if (excuse && !(excuse.reason && excuse.route)) out.push(`${state}: its entry in ${path.basename(allowList)} needs a "reason" and the "route" Weir does not call.`);
    if (excuse?.route && serverSource.includes(excuse.route)) {
      out.push(`${state} is listed as having no Weir behaviour, but Weir's server code now mentions "${excuse.route}". Replay the recording.`);
    }
  }

  for (const state of Object.keys(excused)) {
    if (!states.includes(state)) out.push(`${path.basename(allowList)} lists ${state}, which is not recorded.`);
  }

  for (const state of Object.keys(awaiting)) {
    if (!states.includes(state)) out.push(`${path.basename(awaitingList)} lists ${state}, which is not recorded.`);
  }

  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const found = problems(statesIn(recordings), sourceText(tests), sourceText(serverCode), JSON.parse(readFileSync(allowList, "utf8")), JSON.parse(readFileSync(awaitingList, "utf8")));
  const arrFound = arrProblems(
    Object.fromEntries(recordedApps.map((app) => [app, [...new Set(statesIn(path.join(arrRecordings, app)))]])),
    sourceText(allTests),
  );
  if (found.length > 0 || arrFound.length > 0) {
    if (found.length > 0) console.error("Deluno's recordings and the tests that replay them disagree (#971):");
    for (const problem of found) console.error("  " + problem);
    if (arrFound.length > 0) console.error("The Sonarr, Radarr and download client recordings and the tests that replay them disagree (#1013, #1014, #1015):");
    for (const problem of arrFound) console.error("  " + problem);
    process.exit(1);
  }
  console.log("Every recording of Deluno's own side is replayed by a contract test, explained, or waiting on an open issue, and every Sonarr, Radarr and download client recording is replayed.");
}
