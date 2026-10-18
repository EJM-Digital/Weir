// The browser scenario of the weir-browser shard, judged against the stand-in for a fresh Weir and a browser (scenarios-ci/fake-fresh-world.mjs),
// and the browser's own waiting, against a stand-in for Playwright.
import assert from "node:assert/strict";
import { test } from "node:test";

import { Browser } from "./scenarios-ci/browser.mjs";
import { FakeFreshWorld } from "./scenarios-ci/fake-fresh-world.mjs";
import { runScenarios } from "./scenarios-ci/run.mjs";
import { SHARDS } from "./scenarios-ci/shards.mjs";

async function run(behaviour) {
  const world = new FakeFreshWorld(behaviour);
  const [result] = await runScenarios({ ids: ["first-visit-account"], registry: SHARDS["weir-browser"].scenarios, world: world.asWorld() });
  return { result, world };
}

test("a first visit lands on account creation, a new browser on sign-in once there is an account, and the account signs in", async () => {
  const { result, world } = await run();
  assert.equal(result.status, "passed", result.detail);
  assert.match(result.detail, /account creation, then \(after the account existed\) sign-in; neither page said a session expired; the account signed in as admin/);
  assert.deepEqual(world.visits, ["/", "/"]);
});

test("a first visit that lands on sign-in fails the scenario, naming the page it did land on", async () => {
  const { result } = await run({ landsOnSignInFirst: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /lands on account creation \(the page has: login-username, app-root\)/);
});

test("a new browser that is sent to account creation after the account exists fails the scenario", async () => {
  const { result } = await run({ landsOnCreationAlways: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /lands on sign-in \(the page has: setup-username, app-root\)/);
});

test("a page that says the session expired fails the scenario", async () => {
  const { result } = await run({ saysSessionExpired: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /never says a session expired/);
});

test("an account that cannot sign in fails the scenario", async () => {
  const { result } = await run({ refusesSignIn: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /signing in/);
});

test("Weir that does not know who signed in fails the scenario", async () => {
  const { result } = await run({ knowsWrongUser: true });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /the new account signs in and Weir knows who it is/);
});

test("a warning in the log from the start of the stack fails the scenario, although it did what it should", async () => {
  const { result } = await run({ warnsAtStart: "The data folder is not writable" });
  assert.equal(result.status, "failed");
  assert.match(result.detail, /not writable/);
});

test("the shard starts with no account and a browser, and runs only the browser scenario", () => {
  const shard = SHARDS["weir-browser"];
  assert.deepEqual([shard.account, shard.browser], [false, true]);
  assert.deepEqual(Object.keys(shard.scenarios), ["first-visit-account"]);
});

// --- the browser --------------------------------------------------------------------------------------------------------

function playwrightStub(pages) {
  const log = { contexts: 0, contextsClosed: 0, browserClosed: 0, urls: [] };
  const launched = {
    newContext: async () => {
      log.contexts += 1;
      return {
        newPage: async () => {
          let reads = 0;
          return {
            goto: async (url, options) => void log.urls.push([url, options.waitUntil]),
            evaluate: async () => pages[Math.min(reads++, pages.length - 1)],
          };
        },
        close: async () => void (log.contextsClosed += 1),
      };
    },
    close: async () => void (log.browserClosed += 1),
  };
  return { launched, log };
}

const fakeClock = () => {
  let now = 0;
  return { now: () => now, sleep: async (ms) => void (now += ms) };
};

test("a visit is a brand-new context, closed afterwards, and waits for the page to land on creation or sign-in", async () => {
  const { launched, log } = playwrightStub([{ testIds: [], text: "" }, { testIds: ["spinner"], text: "Loading" }, { testIds: ["login-username"], text: "Sign in" }]);
  const seen = await new Browser(launched, "http://localhost:9347", fakeClock()).visit("/");
  assert.deepEqual(seen, { testIds: ["login-username"], text: "Sign in" });
  assert.deepEqual(log, { contexts: 1, contextsClosed: 1, browserClosed: 0, urls: [["http://localhost:9347/", "load"]] });
});

test("a page that never lands is returned as it stands after the ceiling, for the scenario to judge", async () => {
  const { launched, log } = playwrightStub([{ testIds: ["error-banner"], text: "Something broke" }]);
  const seen = await new Browser(launched, "http://localhost:9347", fakeClock()).visit("/");
  assert.deepEqual(seen, { testIds: ["error-banner"], text: "Something broke" });
  assert.equal(log.contextsClosed, 1);
});

test("closing the browser closes the one Playwright launched", async () => {
  const { launched, log } = playwrightStub([]);
  await new Browser(launched, "http://localhost:9347").close();
  assert.equal(log.browserClosed, 1);
});
