// The two stories the smoke tells about a stack started from the shipped compose.yaml.
import assert from "node:assert/strict";
import { join } from "node:path";

import { readRevision } from "./database.mjs";
import { expectPreUpdateCopy } from "./pre-update-copy.mjs";
import { upgradeOutcome } from "./server-log.mjs";
import { createData, expectKept } from "./smoke-data.mjs";

const DATABASE_FOLDER = "/data/weir/data";
const DATABASE_FILE = "weir.sqlite3";

/** PLAT-14: the account, workflow and setting survive a restart, and a down followed by an up that keeps the volume. */
export async function restartKeepsData({ stack, client, candidate }) {
  stack.up(candidate, "never");
  stack.waitHealthy();
  const kept = await createData({ client, stack });
  await expectKept({ client, kept, sessionKept: true });

  stack.restart();
  stack.waitHealthy();
  await expectKept({ client, kept, sessionKept: true });

  stack.down();
  stack.up(candidate, "never");
  stack.waitHealthy();
  await expectKept({ client, kept, sessionKept: true });
}

/**
 * PLAT-16: a volume the previous release filled is opened by the build under test with everything in it, the database is brought to
 * the build's schema when it was older, and the copy saved first is the old database.
 * @param {string} world.headRevision The revision the build under test records.
 * @param {{version: string, image: string}} world.previous
 * @param {string} world.scratch An empty folder on this machine.
 */
export async function upgradeKeepsData({ stack, client, candidate, previous, headRevision, scratch }) {
  stack.pull(previous.image);
  stack.up(previous.image, "always");
  stack.waitHealthy();
  const kept = await createData({ client, stack });
  await expectKept({ client, kept, sessionKept: true });

  // A stopped container still has its volume: the database is read from it as the previous release left it.
  stack.stop();
  const beforeFolder = join(scratch, "before");
  stack.copyOut(`${DATABASE_FOLDER}/.`, beforeFolder);
  const revisionBefore = readRevision(join(beforeFolder, DATABASE_FILE));
  stack.down();

  stack.up(candidate, "never");
  stack.waitHealthy();
  const migrates = revisionBefore !== headRevision;
  const outcome = upgradeOutcome(stack.logs());
  assert.ok(outcome, "the log of the updated start does not say how it opened the database");
  assert.equal(outcome.migrated, migrates, `the database was at ${revisionBefore} and the build ends on ${headRevision}, but the start ${outcome.migrated ? "updated" : "did not update"} it`);
  assert.equal(outcome.revision, headRevision);

  await expectKept({ client, kept, sessionKept: false });
  if (migrates) {
    await expectPreUpdateCopy({ client, stack, revisionBefore, username: kept.username, scratch: join(scratch, "copy") });
  }
  return { from: previous.version, revisionBefore, migrated: migrates };
}
