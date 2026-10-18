// first-visit-account, ported from scripts/scenarios/Weir.Scenarios.Install.ps1: a first visit to a Weir with no account lands on account
// creation, and once the account exists a brand-new browser lands on sign-in; neither ever says a session expired. Chromium under Playwright
// takes the place of Edge, and each visit is a brand-new profile as the VM's were.
import assert from "node:assert/strict";

const SESSION_EXPIRED = /session expired/i;

export async function firstVisitAccount(world) {
  const first = await world.browser.visit("/");
  assert.ok(first.testIds.includes("setup-username"), `a first visit lands on account creation (the page has: ${first.testIds.join(", ")})`);
  assert.doesNotMatch(first.text, SESSION_EXPIRED, "the first visit never says a session expired");

  const account = await world.createAccount();

  const second = await world.browser.visit("/");
  assert.ok(second.testIds.includes("login-username"), `a new browser, once an account exists, lands on sign-in (the page has: ${second.testIds.join(", ")})`);
  assert.doesNotMatch(second.text, SESSION_EXPIRED, "the new browser is never told a session expired");

  world.weir.forgetSession();
  await world.weir.login(account.username, account.password);
  const { user } = await world.weir.get("/auth/me");
  assert.equal(user.username, account.username, `the new account signs in and Weir knows who it is (${user.username}, ${user.role})`);
  return `A new Chromium profile saw account creation, then (after the account existed) sign-in; neither page said a session expired; the account signed in as ${user.role}.`;
}
