#!/usr/bin/env node
// Fails when a fresh recording of the real Deluno and the real Weir talking (the with-deluno scenario shard, scripts/scenarios-ci/with-deluno)
// differs in kind from the one checked in.
//
// The shard writes <folder>/<Deluno version>/real-<state>.json. The checked-in ones are
// apps/server/tests/fixtures/recorded/deluno-side/<version>/real-<state>.json. Two runs never agree word for word: the hand-off list has other ids and
// times, the apps number and stamp things afresh, and how many times Deluno asks about a hand-off depends on how fast the run was. So this
// compares the kinds of exchange a replay depends on, not the exchanges one by one:
//
//   * for each state, the set of kinds of exchange: who called whom, the method, the path with its ids set aside, and the status;
//   * for each kind, the shape of the request and of the reply: every member and the type of its value, and the words of the members that
//     carry a decision (status, outcome, disposition, failureClass, delivery, code, error, event, enqueued) as written. The state a status
//     poll reports is not one of them: which state Deluno finds depends on how fast the run was.
//
// A newer Deluno shows up as a different version folder, and its kinds are compared with the checked-in ones all the same.
//
// Usage: node scripts/compare-deluno-recordings.mjs <fresh folder> [<checked-in folder>]
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { maskPath } from "./compare-arr-recordings.mjs";

const repositoryRoot = fileURLToPath(new URL("..", import.meta.url));
export const checkedIn = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded", "deluno-side");
const STATE_PREFIX = "real-";

/** Members whose words are the decision the exchange carries; the other strings are reduced to their type. */
const DECISION_MEMBER = new Set(["status", "outcome", "disposition", "failureClass", "delivery", "code", "error", "event", "enqueued", "eventType", "mediaType", "product", "source"]);

/** The shape of a value: members and types, with a decision member's words kept. */
export function shapeOf(value, member = "") {
  if (Array.isArray(value)) {
    const shapes = [...new Set(value.map((item) => JSON.stringify(shapeOf(item, member))))].sort();
    return shapes.map((shape) => JSON.parse(shape));
  }
  if (value !== null && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, shapeOf(value[key], key)]));
  }
  if (typeof value === "string" && DECISION_MEMBER.has(member)) return value;
  if (typeof value === "boolean" && DECISION_MEMBER.has(member)) return value;
  return value === null ? "null" : typeof value;
}

/** The kind of an exchange: who called whom, how, where, and what the status was. */
export function kindOf(exchange) {
  const request = exchange.request ?? {};
  const reply = exchange.reply ?? {};
  return JSON.stringify({
    method: request.method,
    path: maskPath(String(request.path ?? "").replace(/\/[0-9a-f]{20,}(?=\/|\?|$)/g, "/<id>")),
    status: reply.status,
    request: shapeOf(request.body ?? null),
    reply: reply.body !== undefined ? shapeOf(reply.body) : reply.bodyText !== undefined ? "text" : "null",
  });
}

/** The kinds of exchange in a recording, each once. */
export function kindsOf(recording) {
  return [...new Set(recording.exchanges.map(kindOf))].sort();
}

function recordingsIn(root) {
  const found = new Map();
  if (!existsSync(root)) return found;
  for (const version of readdirSync(root).sort()) {
    const folder = path.join(root, version);
    if (!statSync(folder).isDirectory()) continue;
    for (const file of readdirSync(folder).sort()) {
      if (file.startsWith(STATE_PREFIX) && file.endsWith(".json")) found.set(file.slice(0, -5), JSON.parse(readFileSync(path.join(folder, file), "utf8")));
    }
  }
  return found;
}

/** What differs between a fresh set of recordings and the checked-in one, one sentence each; empty when they agree. */
export function problems(freshRoot, recordedRoot = checkedIn) {
  const fresh = recordingsIn(freshRoot);
  const recorded = recordingsIn(recordedRoot);
  if (fresh.size === 0) return [`deluno: the scenarios wrote no ${STATE_PREFIX}* recordings under ${freshRoot}.`];
  if (recorded.size === 0) return [`deluno: nothing is checked in under ${recordedRoot}.`];
  const out = [];
  for (const state of recorded.keys()) if (!fresh.has(state)) out.push(`${state}: recorded before, not recorded now.`);
  for (const state of fresh.keys()) if (!recorded.has(state)) out.push(`${state}: recorded now, not checked in.`);
  for (const [state, now] of fresh) {
    const before = recorded.get(state);
    if (!before) continue;
    const [nowKinds, beforeKinds] = [kindsOf(now), kindsOf(before)];
    for (const kind of beforeKinds) if (!nowKinds.includes(kind)) out.push(`${state}: this was said before and is not now: ${kind.slice(0, 240)}`);
    for (const kind of nowKinds) if (!beforeKinds.includes(kind)) out.push(`${state}: this is said now and was not before: ${kind.slice(0, 240)}`);
  }
  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const [fresh, recorded = checkedIn] = process.argv.slice(2);
  if (!fresh) {
    console.error("Usage: node scripts/compare-deluno-recordings.mjs <fresh folder> [<checked-in folder>]");
    process.exit(2);
  }
  const found = problems(fresh, recorded);
  if (found.length > 0) {
    console.error(`A fresh recording of Deluno and Weir differs from the one checked in:\n${found.map((line) => `  ${line}`).join("\n")}`);
    process.exit(1);
  }
  console.log("A fresh recording of Deluno and Weir says what the one checked in says.");
}
