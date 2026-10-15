// PLAT-20: the lock-out recovery as a person runs it from the Docker page: `docker exec -u weir -e WEIR_HOME=/data/weir weir
// /opt/weir/Weir recover` in the running container (docs-site/docs/guides/security.md).
import assert from "node:assert/strict";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { expectHelp, expectListed, expectNoDatabase, expectReset, recordingText } from "./recover-output.mjs";
import { createData, expectKept } from "./smoke-data.mjs";

const RECOVER = "/opt/weir/Weir recover";
// A folder Weir never used, outside the data volume.
const EMPTY_HOME = "/tmp/weir-never-used";

/**
 * The account made through the API is locked out: the documented command resets its password in the running container, the forgotten
 * password is refused, the new one signs in (also after the container is restarted), the session from before is ended and the
 * workflow and setting are as they were. What the command printed is kept in `recording`.
 * @param {string} world.recording A folder on this machine for what the command printed.
 */
export async function recoverResetsPassword({ stack, client, candidate, recording }) {
  stack.up(candidate, "never");
  stack.waitHealthy();
  const kept = await createData({ client, stack });
  await expectKept({ client, kept, sessionKept: true });
  const sessionsEnded = (await client.activeSessions()).length;
  assert.ok(sessionsEnded >= 1, "no session is signed in, so there is nothing for the reset to end");

  mkdirSync(recording, { recursive: true });
  const keep = (name, result) => writeFileSync(join(recording, name), recordingText(result));
  const recover = (args, home = "/data/weir") => stack.execResult(`${RECOVER} ${args}`, { WEIR_HOME: home });

  const help = recover("--help");
  keep("help.txt", help);
  expectHelp(help);

  const list = recover("--list");
  keep("list.txt", list);
  expectListed(list, kept.username);

  const newPassword = `Rc-${kept.password.slice(3)}`;
  const reset = recover(`--username ${kept.username} --password ${newPassword}`);
  keep("recover.txt", reset);
  expectReset(reset, { username: kept.username, sessionsEnded });

  assert.equal(await client.signedInAs(), null, "the session from before the reset is still accepted");
  assert.equal(await client.loginStatus(kept.username, kept.password), 401, "the forgotten password was not refused with 401");
  const renewed = { ...kept, password: newPassword };
  await expectKept({ client, kept: renewed, sessionKept: false });
  const recorded = (await client.activity("auth.password_changed")).filter((entry) => entry.title === "Password recovered from the server");
  assert.equal(recorded.length, 1, "Activity does not hold exactly one entry that the password was recovered from the server");

  stack.restart();
  stack.waitHealthy();
  await expectKept({ client, kept: renewed, sessionKept: false });

  const elsewhere = recover(`--password ${newPassword}`, EMPTY_HOME);
  keep("no-database.txt", elsewhere);
  expectNoDatabase(elsewhere);
  const left = stack.exec(`find ${EMPTY_HOME} -name '*.sqlite3'`).trim();
  assert.equal(left, "", `recover in a folder Weir never used left a database behind: ${left}`);
  return { sessionsEnded };
}
