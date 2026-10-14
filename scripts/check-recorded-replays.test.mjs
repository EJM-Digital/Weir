// node --test scripts/check-recorded-replays.test.mjs
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";

import { problems, sourceText, statesIn } from "./check-recorded-replays.mjs";

const excuse = { reason: "Weir never calls it.", route: "/processors/handoffs" };

test("a state a test names, and a state with an excuse, are both fine", () => {
  assert.deepEqual(problems(["handoff-single-file", "reply-handoffs-listed"], 'Request("handoff-single-file")', "// nothing", { "reply-handoffs-listed": excuse }), []);
});

test("a state nothing replays and nothing explains is named", () => {
  assert.deepEqual(problems(["status-ask-queued"], 'Request("capabilities-ask")', "", {}), [
    "status-ask-queued is recorded but no contract test replays it, and deluno-side-no-weir-behaviour.json does not say why not.",
  ]);
});

test("an excuse for a state that is replayed, or not recorded, is stale", () => {
  assert.deepEqual(problems(["capabilities-ask"], 'Request("capabilities-ask")', "", { "capabilities-ask": excuse, "gone-state": excuse }), [
    "capabilities-ask is replayed by a contract test, so it must come out of deluno-side-no-weir-behaviour.json.",
    "deluno-side-no-weir-behaviour.json lists gone-state, which is not recorded.",
  ]);
});

test("an excuse needs its reason and its route", () => {
  assert.deepEqual(problems(["reply-handoffs-listed"], "", "", { "reply-handoffs-listed": { reason: "Weir never calls it." } }), [
    'reply-handoffs-listed: its entry in deluno-side-no-weir-behaviour.json needs a "reason" and the "route" Weir does not call.',
  ]);
});

test("a route Weir's server code now mentions means the recording must be replayed", () => {
  const found = problems(["reply-handoffs-listed"], "", 'client.Get("/api/integrations/processors/handoffs")', { "reply-handoffs-listed": excuse });
  assert.deepEqual(found, [
    'reply-handoffs-listed is listed as having no Weir behaviour, but Weir\'s server code now mentions "/processors/handoffs". Replay the recording.',
  ]);
});

test("states are the file names of every version folder, and source text skips build output", () => {
  const root = mkdtempSync(path.join(tmpdir(), "recorded-replays-"));
  try {
    mkdirSync(path.join(root, "1.0.0-rc.1"));
    mkdirSync(path.join(root, "1.0.0-rc.2"));
    mkdirSync(path.join(root, "obj"));
    writeFileSync(path.join(root, "1.0.0-rc.1", "a-state.json"), "{}");
    writeFileSync(path.join(root, "1.0.0-rc.2", "b-state.json"), "{}");
    writeFileSync(path.join(root, "MANIFEST.sha256"), "");
    writeFileSync(path.join(root, "Real.cs"), "class Real;");
    writeFileSync(path.join(root, "obj", "Generated.cs"), "class Generated;");
    assert.deepEqual(statesIn(root), ["a-state", "b-state"]);
    assert.match(sourceText(root), /class Real;/);
    assert.doesNotMatch(sourceText(root), /Generated/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
