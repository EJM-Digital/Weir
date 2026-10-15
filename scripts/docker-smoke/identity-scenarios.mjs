// What the entrypoint does as root before it hands over to the server (docker/entrypoint.sh), proved on the image with the shipped
// compose.yaml's own capabilities: who the server runs as, who owns the data folder and the two secrets, and what a refused start looks like.
import assert from "node:assert/strict";
import { readFileSync, statSync } from "node:fs";
import { join } from "node:path";

import { GENERATED_SECRET, REMAPPED, asEnvironment, notOwnedBy, parseStat, processIds } from "./ownership.mjs";
import { createData, expectKept } from "./smoke-data.mjs";

const HOME = "/data/weir";
const SECRETS = ["session.secret", "credentials.secret"].map((name) => `${HOME}/${name}`);

const count = (log, pattern) => [...log.matchAll(pattern)].length;

/** The secrets as the container holds them, after checking each is a generated one, kept private, and different from the other. */
function readSecrets(stack, owner) {
  const [session, credentials] = SECRETS.map((path) => stack.execAs("root", `cat ${path}`).trim());
  for (const secret of [session, credentials]) assert.match(secret, GENERATED_SECRET, "a secret is not 48 random bytes in base64url");
  assert.notEqual(session, credentials, "the two secrets are the same");
  const files = parseStat(stack.execAs("root", `stat -c '%u:%g:%a:%n' ${SECRETS.join(" ")}`));
  assert.deepEqual(
    files.map((file) => `${file.path} ${file.uid}:${file.gid} ${file.mode}`),
    SECRETS.map((path) => `${path} ${owner.uid}:${owner.gid} 600`),
    "the secrets are not private to the account the server runs as",
  );
  return { session, credentials };
}

/** The weir account has the ids that were asked for, the server runs as them, and the data folder holds nothing they do not own. */
function expectRunsAs(stack, ids) {
  assert.equal(stack.execAs("root", "id -u weir").trim(), String(ids.uid), "the weir account has another uid");
  assert.equal(stack.execAs("root", "id -g weir").trim(), String(ids.gid), "the weir account has another gid");
  assert.equal(stack.execAs("root", "cat /proc/1/comm").trim(), "Weir", "the container's first process is not the server");
  assert.deepEqual(processIds(stack.execAs("root", "cat /proc/1/status")), ids, "the server runs as other ids than were asked for");
  assert.equal(stack.execAs("root", `find ${HOME} \\( ! -uid ${ids.uid} -o ! -gid ${ids.gid} \\)`).trim(), "", "some of the data folder is owned by someone else");
}

/**
 * SET-E39, PLAT-15: WEIR_PUID and WEIR_PGID turn the weir account into the ids given, the server runs as them, everything in the data
 * volume and both secrets belong to them, and the secrets made on the first start are the ones used after a restart and after the
 * container is made again.
 */
export async function remapsAndKeepsSecrets({ stack, client, candidate }) {
  const layers = { environment: asEnvironment(REMAPPED) };
  stack.up(candidate, "never", layers);
  stack.waitHealthy();

  const first = stack.logs();
  assert.match(first, new RegExp(`Updating weir group id to ${REMAPPED.gid}`));
  assert.match(first, new RegExp(`Updating weir user id to ${REMAPPED.uid}`));
  assert.match(first, /generated WEIR_SESSION_SECRET at \/data\/weir\/session\.secret/);
  assert.match(first, /generated WEIR_CREDENTIALS_SECRET at \/data\/weir\/credentials\.secret/);
  expectRunsAs(stack, REMAPPED);
  const secrets = readSecrets(stack, REMAPPED);
  assert.equal(stack.exec(`cat ${SECRETS[0]}`).trim(), secrets.session, "the account the server runs as cannot read its own session secret");

  const kept = await createData({ client, stack });
  await expectKept({ client, kept, sessionKept: true });

  stack.restart();
  stack.waitHealthy();
  assert.deepEqual(readSecrets(stack, REMAPPED), secrets, "a restart made new secrets");
  assert.equal(count(stack.logs(), /generated WEIR_(?:SESSION|CREDENTIALS)_SECRET/g), 2, "a restart generated a secret again");
  await expectKept({ client, kept, sessionKept: true });

  stack.down();
  stack.up(candidate, "never", layers);
  stack.waitHealthy();
  const second = stack.logs();
  assert.match(second, new RegExp(`Updating weir user id to ${REMAPPED.uid}`), "the new container did not remap the account");
  assert.doesNotMatch(second, /generated WEIR_/, "a new container on the same volume made new secrets");
  assert.doesNotMatch(second, /fixing ownership/, "the data folder was already owned by the ids and should not be changed again");
  expectRunsAs(stack, REMAPPED);
  assert.deepEqual(readSecrets(stack, REMAPPED), secrets, "a new container on the same volume has other secrets");
  await expectKept({ client, kept, sessionKept: true });
}

/**
 * SET-E39, PLAT-15: a data folder bind-mounted from the host that Docker made as root is handed to the ids WEIR_PUID and WEIR_PGID name,
 * so the person who set them (here, the user running this smoke) can read the database and secrets from outside the container; and a
 * data folder the ids of an earlier start owned is handed to new ids, database included.
 * @param {string} world.dataFolder A folder that does not exist yet, in a folder this user owns.
 */
export async function takesOverTheDataFolder({ stack, client, candidate, dataFolder }) {
  const host = { uid: process.getuid(), gid: process.getgid() };
  assert.notEqual(host.uid, 0, "this smoke must run as an ordinary user, whose ids it gives the server");
  const bind = `${dataFolder}:${HOME}`;

  stack.up(candidate, "never", { environment: asEnvironment(host), binds: [bind] });
  stack.waitHealthy();
  assert.match(stack.logs(), new RegExp(`WEIR_HOME is owned by 0:0, not ${host.uid}:${host.gid}; fixing ownership`), "the folder Docker made was not handed over");
  expectRunsAs(stack, host);

  assert.deepEqual(notOwnedBy(dataFolder, host), [], "the host user does not own everything the server wrote");
  const secrets = readSecrets(stack, host);
  assert.equal(readFileSync(join(dataFolder, "session.secret"), "utf8").trim(), secrets.session, "the host user cannot read the session secret");
  const database = join(dataFolder, "data", "weir.sqlite3");
  assert.equal(readFileSync(database).subarray(0, 15).toString("latin1"), "SQLite format 3", "the database the host user can read is not a SQLite file");
  assert.ok(statSync(database).size > 0);

  const kept = await createData({ client, stack });
  await expectKept({ client, kept, sessionKept: true });
  stack.down();

  stack.up(candidate, "never", { environment: asEnvironment(REMAPPED), binds: [bind] });
  stack.waitHealthy();
  assert.match(stack.logs(), new RegExp(`WEIR_HOME is owned by ${host.uid}:${host.gid}, not ${REMAPPED.uid}:${REMAPPED.gid}; fixing ownership`), "new ids did not take the folder over");
  expectRunsAs(stack, REMAPPED);
  assert.deepEqual(readSecrets(stack, REMAPPED), secrets);
  await expectKept({ client, kept, sessionKept: true });
}

/** The starts the entrypoint refuses, and what it says; each ends before the server starts, with exit status 1. */
export const REFUSED_STARTS = [
  [{ WEIR_PUID: "0" }, /WEIR_PUID must not be 0: Weir refuses to run its server as root/],
  [{ WEIR_PGID: "0" }, /WEIR_PGID must not be 0: Weir refuses to run its server as root/],
  [{ PUID: "0" }, /WEIR_PUID must not be 0/],
  [{ WEIR_PUID: "abc" }, /WEIR_PUID must be a non-negative integer/],
  [{ WEIR_PGID: "-1" }, /WEIR_PGID must be a non-negative integer/],
  [{ WEIR_SESSION_SECRET: "too-short" }, /WEIR_SESSION_SECRET must be at least 32 characters/],
  [{ WEIR_CHOWN_OUTPUT: "maybe" }, /WEIR_CHOWN_OUTPUT must be one of: true\/false, 1\/0, yes\/no, on\/off/],
  [{ WEIR_DIR_MODE_OUTPUT: "999" }, /WEIR_DIR_MODE_OUTPUT must be an octal directory mode such as 775 or 2775/],
];

/** SET-E39, PLAT-15: the image refuses to run the server as root and refuses malformed settings, before anything starts. */
export function refusesBadSettings({ stack, candidate }) {
  for (const [environment, message] of REFUSED_STARTS) {
    const attempt = stack.startOnce(candidate, environment);
    const named = JSON.stringify(environment);
    assert.equal(attempt.status, 1, `${named} did not end the start with status 1 (status ${attempt.status})\n${attempt.output}`);
    assert.match(attempt.output, message, `${named} was refused without saying why`);
  }
}
