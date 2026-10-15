// The copy of its data a Weir that updates the database saves first (docs/install-docker.md, "Upgrade"): it must exist in the data
// volume, be the database as the old version left it, and come with the two secrets that open the saved connection keys.
import assert from "node:assert/strict";
import { basename, dirname, join } from "node:path";

import { readRevision, revisionNumber, usernamesIn } from "./database.mjs";

/**
 * @param {object} world
 * @param {import("./weir-client.mjs").WeirClient} world.client Signed in to the updated stack.
 * @param {{exec(command: string): string, copyOut(containerPath: string, hostFolder: string): void}} world.stack
 * @param {string} world.revisionBefore The revision the database recorded before the update.
 * @param {string} world.username An account that existed before the update.
 * @param {string} world.scratch An empty folder on this machine.
 */
export async function expectPreUpdateCopy({ client, stack, revisionBefore, username, scratch }) {
  const overview = await client.overview();
  const copy = overview.last_update_backup;
  assert.ok(copy, "the update changed the database but Weir reports no copy saved before it");
  assert.equal(copy.in_data_folder, true, "the copy is not inside the data volume, so it would not survive the container");
  assert.equal(copy.to_version, overview.version.split("+")[0], "the copy names a different version than the one now running");

  const name = basename(copy.path);
  assert.ok(name.startsWith(`weir-${revisionNumber(revisionBefore)}-to-`), `the copy ${name} is not named for the revision it came from`);

  const stem = name.replace(/\.db$/, "");
  const beside = stack.exec(`ls ${dirname(copy.path)}`).split(/\s+/);
  for (const secret of ["session.secret", "credentials.secret"]) {
    assert.ok(beside.includes(`${stem}.${secret}`), `the copy of the ${secret} file is missing beside ${name}`);
  }

  stack.copyOut(copy.path, scratch);
  const saved = join(scratch, name);
  assert.equal(readRevision(saved), revisionBefore, "the copy is not the database as it was before the update");
  assert.ok(usernamesIn(saved).includes(username), "the copy does not hold the account that existed before the update");
}
