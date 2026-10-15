// The account, workflow and setting a smoke creates through Weir's API, and the check that a later start of the stack still has them.
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";

import { parseSetupCode } from "./server-log.mjs";

const USERNAME = "composer";
const WORKFLOW_FOLDER = "/data/weir/smoke";
// A value the settings screen would never default to, so a database that came back with its defaults cannot pass for one that kept it.
const LOG_RETENTION_DAYS = 17;
const KEPT_FIELDS = ["id", "name", "media_type", "watched_folder", "work_folder", "output_folder"];

const pick = (library) => Object.fromEntries(KEPT_FIELDS.map((field) => [field, library[field]]));

/**
 * Creates the first account, a workflow and a saved setting in the running stack, the way an operator does from another device:
 * the setup code comes from Weir's log. Returns what was created, for `expectKept`.
 * @param {{client: import("./weir-client.mjs").WeirClient, stack: {exec(command: string): string, logs(): string}}} world
 */
export async function createData({ client, stack }) {
  const status = await client.bootstrapStatus();
  assert.equal(status.bootstrap_allowed, true, "a new volume must offer to create the first account");
  const setupCode = status.requires_setup_code ? parseSetupCode(stack.logs()) : null;
  assert.ok(!status.requires_setup_code || setupCode, "Weir asked for a setup code but its log does not carry one");

  const password = `Pw-${randomBytes(18).toString("hex")}`;
  await client.bootstrap(USERNAME, password, setupCode);

  stack.exec(`mkdir -p ${WORKFLOW_FOLDER}/watched ${WORKFLOW_FOLDER}/work ${WORKFLOW_FOLDER}/output`);
  const created = await client.createLibrary({
    name: "Compose smoke movies",
    media_type: "movie",
    watched_folder: `${WORKFLOW_FOLDER}/watched`,
    work_folder: `${WORKFLOW_FOLDER}/work`,
    output_folder: `${WORKFLOW_FOLDER}/output`,
  });

  const settings = await client.suiteSettings();
  await client.saveSuiteSettings({ app_timezone: settings.app_timezone, log_retention_days: LOG_RETENTION_DAYS });

  return { username: USERNAME, password, library: pick(created) };
}

/**
 * Checks the running stack still holds what `createData` made: the first account cannot be created again, the password signs in (and
 * the session from before the stop still works when `sessionKept`), and the workflow and setting are as they were saved.
 */
export async function expectKept({ client, kept, sessionKept }) {
  const status = await client.bootstrapStatus();
  assert.equal(status.bootstrap_allowed, false, "the account is gone: Weir offers to create the first one again");
  assert.equal(status.reason, "admin_already_exists");

  if (sessionKept) {
    assert.equal(await client.signedInAs(), kept.username, "the session from before the restart is no longer accepted");
  }
  client.forgetSession();
  assert.equal(await client.signedInAs(), null, "a client with no session must be signed out");
  await client.login(kept.username, kept.password);
  assert.equal(await client.signedInAs(), kept.username, "the account does not sign in with its password");

  const libraries = await client.libraries();
  const found = libraries.find((library) => library.id === kept.library.id);
  assert.ok(found, `the workflow "${kept.library.name}" is gone`);
  assert.deepEqual(pick(found), kept.library, "the workflow changed");

  assert.equal((await client.suiteSettings()).log_retention_days, LOG_RETENTION_DAYS, "the saved setting went back to its default");
}
