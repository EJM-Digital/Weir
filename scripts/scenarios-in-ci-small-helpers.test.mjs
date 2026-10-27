// node --test scripts/scenarios-in-ci-small-helpers.test.mjs
// The small helpers of the scenarios that run in CI, on the input their larger tests do not reach: a browser that is launched, a manifest that is
// wrong in every way, a request nobody answers, a log that cannot be read.
import assert from "node:assert/strict";
import { test } from "node:test";

import { Browser, SNAPSHOT } from "./scenarios-ci/browser.mjs";
import { catalogIds, problemsWithHomes } from "./scenarios-ci/homes.mjs";
import { ExchangeLog } from "./scenarios-ci/with-deluno/exchange-log.mjs";
import { weirFilesFor, whereItStopped } from "./scenarios-ci/with-deluno/handoffs.mjs";
import { send } from "./scenarios-ci/with-deluno/http.mjs";
import { nothingWasOwed } from "./scenarios-ci/with-deluno/outcomes.mjs";

test("the browser is launched from Playwright's Chromium, and the page snapshot lists each test id once with the page's text", async () => {
  const launched = [];
  const browser = await Browser.launch("http://127.0.0.1:9", async () => ({ chromium: { launch: async () => (launched.push("chromium"), { newContext: async () => ({}), close: async () => {} }) } }));
  assert.ok(browser instanceof Browser);
  assert.deepEqual(launched, ["chromium"]);

  const element = (id) => ({ getAttribute: () => id });
  globalThis.document = { querySelectorAll: () => [element("a"), element("b"), element("a")], body: { innerText: "Hello" } };
  try {
    assert.deepEqual(SNAPSHOT(), { testIds: ["a", "b"], text: "Hello" });
  } finally {
    delete globalThis.document;
  }
});

const scenario = (extra) => ({ id: "one", home: "linux", shard: "linux-a", state: "ported", why: "x", ...extra });

test("a manifest is refused for each way it can disagree with the catalogue", () => {
  const homes = {
    shards: { "linux-a": { home: "linux" }, "windows-a": { home: "windows" } },
    scenarios: [
      scenario({ id: "added", catalogue: false }),
      scenario({ id: "odd-home", home: "mars" }),
      scenario({ id: "odd-state", state: "dreamt" }),
      scenario({ id: "wrong-shard", shard: "windows-a" }),
      scenario({ id: "bad-pass", state: "green-twice", passedInCi: [{ run: "x", commit: "short" }] }),
      scenario({ id: "plain" }),
    ],
  };
  const problems = problemsWithHomes(homes, ["added", "odd-home", "odd-state", "wrong-shard", "bad-pass", "plain", "forgotten"]).join("\n");
  assert.match(problems, /added: marked as added to CI only, but it is in the catalogue/);
  assert.match(problems, /odd-home: home "mars" is none of linux, windows, not-portable/);
  assert.match(problems, /odd-state: state "dreamt" is none of to-port, ported, green-twice/);
  assert.match(problems, /wrong-shard: it is linux but shard windows-a is windows/);
  assert.match(problems, /bad-pass: a pass needs its run number, its full commit and its ref/);
  assert.match(problems, /forgotten: in the catalogue but has no home/);
  assert.deepEqual(catalogIds("  Id = 'one'\n  Id = 'two-b'\n"), ["one", "two-b"]);
});

test("a request nobody answers names the request and the reason: the error's code, else its message, else the failure's own", async (context) => {
  const failing = (error) => context.mock.method(globalThis, "fetch", async () => { throw error; });
  const ask = () => send("http://127.0.0.1:9", { method: "GET", path: "/x", what: "Deluno" });

  failing(Object.assign(new TypeError("fetch failed"), { cause: { code: "ECONNREFUSED" } }));
  await assert.rejects(ask(), /^Error: Deluno GET \/x did not get an answer: ECONNREFUSED/);
  failing(Object.assign(new TypeError("fetch failed"), { cause: { message: "socket hang up" } }));
  await assert.rejects(ask(), /did not get an answer: socket hang up/);
  failing(new TypeError("fetch failed"));
  await assert.rejects(ask(), /did not get an answer: fetch failed/);
});

test("the recording proxy's exchanges are listed oldest first, and a list that cannot be read says what it answered", async (context) => {
  const answer = (status, body) => context.mock.method(globalThis, "fetch", async () => new Response(body, { status }));
  const exchanges = [{ route: "to-deluno", id: 1 }, { route: "to-weir", id: 2 }, { route: "to-deluno", id: 3 }];
  answer(200, JSON.stringify({ exchanges }));
  const log = new ExchangeLog("http://127.0.0.1:9");
  assert.deepEqual((await log.where("to-deluno")).map((entry) => entry.id), [1, 3]);
  assert.deepEqual((await log.where("to-deluno", (entry) => entry.id > 1)).map((entry) => entry.id), [3]);

  answer(500, "down");
  await assert.rejects(log.all(), /The recording proxy's list answered 500: down/);
  answer(200, JSON.stringify({ exchanges: "not a list" }));
  await assert.rejects(log.all(), /list answered 200/);
});

test("where a film stopped is said even when the applications cannot be read, and a hand-off with no output has no files", async () => {
  const world = { weir: { get: async () => { throw new Error("Weir is down"); } } };
  assert.equal(await whereItStopped(world, { title: "T", release: "R" }), "(Where it stopped could not be read: Weir is down)");
  assert.deepEqual(weirFilesFor({}, [{ handback: { output_path: "/a/b" } }]), []);
});

test("a hand-off that was owed nothing needs every sign of it: no output, no attempt, the processor's own words, and no copy in Weir", () => {
  const outcome = { outcome: "not-imported", delivery: "given-up", attempts: 0, processorSaid: "no file was handed back, so there was nothing to tell it" };
  assert.equal(nothingWasOwed({}, outcome, []), true, "a hand-off with no output path at all");
  assert.equal(nothingWasOwed({ outputPath: "  " }, outcome, []), true);
  assert.equal(nothingWasOwed({ outputPath: "/out" }, outcome, []), false);
  assert.equal(nothingWasOwed({}, { ...outcome, processorSaid: undefined }, []), false);
  assert.equal(nothingWasOwed({}, outcome, [{}]), false);
});
