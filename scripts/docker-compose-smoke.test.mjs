// node --test scripts/docker-compose-smoke.test.mjs
// The parts of scripts/docker-compose-smoke.mjs that decide things without Docker: which image is the previous release, which schema
// revision the build under test ends on, what the server's own log says about the setup code and the upgrade, and how a session cookie
// is read, the override laid over compose.yaml, and how owners, modes and secrets are read. The Docker steps themselves run in CI (ci-packaging.yml, release.yml).
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";

import { headRevision, readRevision, revisionNumber, usernamesIn } from "./docker-smoke/database.mjs";
import { upgradeOutcome, parseSetupCode } from "./docker-smoke/server-log.mjs";
import { previousRelease } from "./docker-smoke/previous-image.mjs";
import { sessionCookie } from "./docker-smoke/weir-client.mjs";
import { composeOverride } from "./docker-smoke/override.mjs";
import { GENERATED_SECRET, asEnvironment, notOwnedBy, parseStat, processIds } from "./docker-smoke/ownership.mjs";
import { pollUntil } from "./docker-smoke/poll.mjs";
import { shellQuote } from "./docker-smoke/shell.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const schemaMigrator = readFileSync(join(repository, "apps/server/src/Weir.Infrastructure/Sqlite/SchemaMigrator.cs"), "utf8");

test("the build under test ends on the last migration SchemaMigrator lists", () => {
  const head = headRevision(schemaMigrator);
  assert.match(head, /^\d{4}_[a-z0-9_]+$/);
  const numbers = [...schemaMigrator.matchAll(/new\((\d+), "(\d{4}_[a-z0-9_]+)"/g)].map((match) => Number(match[1]));
  assert.equal(numbers.at(-1), Math.max(...numbers), "the list is in order, so the last entry is the head");
  assert.equal(revisionNumber(head), head.slice(0, 4));
});

test("a source with no migrations has no head revision", () => {
  assert.throws(() => headRevision("public sealed class SchemaMigrator { }"), /no migration/i);
});

test("the setup code is read from the line Weir logs when it has no account yet", () => {
  const line =
    "Weir has no account yet. To create one from another device, enter this setup code: K7QM-4TZB. It is also in /data/weir/setup-code.";
  assert.equal(parseSetupCode(`info: starting\n${line}\ninfo: listening`), "K7QM-4TZB");
  assert.equal(parseSetupCode("info: listening"), null);
});

test("an upgrade is told apart from a start on a database that was already current", () => {
  assert.deepEqual(upgradeOutcome("Upgraded database to schema revision=0082_x path=/data/weir/data/weir.sqlite3"), {
    migrated: true,
    revision: "0082_x",
  });
  assert.deepEqual(upgradeOutcome("Opened database at schema revision=0082_x path=/data/weir/data/weir.sqlite3"), {
    migrated: false,
    revision: "0082_x",
  });
  assert.equal(upgradeOutcome("Created database schema revision=0082_x path=/data"), null);
});

test("the previous release of a version is the newest older one, and with no version it is the newest published", () => {
  const tags = ["v1.0.0-rc.9", "v1.0.0-rc.18", "v1.0.0-rc.10"];
  assert.deepEqual(previousRelease(tags, "1.0.0-rc.19"), { version: "1.0.0-rc.18", image: "ghcr.io/jampat000/weir:1.0.0-rc.18" });
  assert.deepEqual(previousRelease(tags, "1.0.0-rc.10"), { version: "1.0.0-rc.9", image: "ghcr.io/jampat000/weir:1.0.0-rc.9" });
  assert.deepEqual(previousRelease(tags, ""), { version: "1.0.0-rc.18", image: "ghcr.io/jampat000/weir:1.0.0-rc.18" });
});

test("the first release has no previous release to upgrade from", () => {
  assert.equal(previousRelease([], "1.0.0-rc.1"), null);
  assert.equal(previousRelease([], ""), null);
  assert.equal(previousRelease(["v1.0.0-rc.1"], "1.0.0-rc.1"), null);
});

test("the session cookie is the name and value Weir sets, without its attributes", () => {
  const header = "weir_session=abc123; HttpOnly; Max-Age=7776000; Path=/; SameSite=Lax";
  assert.equal(sessionCookie([header]), "weir_session=abc123");
  assert.equal(sessionCookie([]), null);
});

test("a database's recorded revision and accounts are read from the file", () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-docker-smoke-"));
  const path = join(folder, "weir.sqlite3");
  const database = new DatabaseSync(path);
  database.exec("CREATE TABLE schema_version (revision TEXT NOT NULL); INSERT INTO schema_version VALUES ('0080_handback_speaker');");
  database.exec("CREATE TABLE users (id INTEGER PRIMARY KEY, username VARCHAR(64) NOT NULL); INSERT INTO users (username) VALUES ('composer');");
  database.close();
  assert.equal(readRevision(path), "0080_handback_speaker");
  assert.deepEqual(usernamesIn(path), ["composer"]);
});

test("an override names the variables, folders and platform, and nothing it was not given", () => {
  assert.equal(composeOverride(), "services:\n  weir:\n");
  assert.equal(
    composeOverride({ environment: { WEIR_PUID: "2001", WEIR_NOTE: 'say "hi": #1' }, binds: ["/tmp/media:/media"], platform: "linux/arm64" }),
    [
      "services:",
      "  weir:",
      '    platform: "linux/arm64"',
      "    environment:",
      '      "WEIR_PUID": "2001"',
      '      "WEIR_NOTE": "say \\"hi\\": #1"',
      "    volumes:",
      '      - "/tmp/media:/media"',
      "",
    ].join("\n"),
  );
});

test("ids become the variables the entrypoint reads", () => {
  assert.deepEqual(asEnvironment({ uid: 2001, gid: 2002 }), { WEIR_PUID: "2001", WEIR_PGID: "2002" });
});

test("a shell word keeps what it holds, quotes included", () => {
  assert.equal(shellQuote("a b"), "'a b'");
  assert.equal(shellQuote("it's"), "'it'\\''s'");
});

test("the ids of a process are read from its status", () => {
  const status = "Name:\tWeir\nUmask:\t0022\nUid:\t2001\t2001\t2001\t2001\nGid:\t2002\t2002\t2002\t2002\nGroups:\t2002\n";
  assert.deepEqual(processIds(status), { uid: 2001, gid: 2002 });
  assert.throws(() => processIds("Name:\tWeir\n"), /no Uid line/);
});

test("stat lines give the owner, group, mode and path of each file", () => {
  assert.deepEqual(parseStat("2001:2002:600:/data/weir/session.secret\n1000:1000:2775:/media/out dir\n"), [
    { uid: 2001, gid: 2002, mode: "600", path: "/data/weir/session.secret" },
    { uid: 1000, gid: 1000, mode: "2775", path: "/media/out dir" },
  ]);
  assert.deepEqual(parseStat(""), []);
  assert.throws(() => parseStat("stat: cannot stat '/x': No such file or directory"), /not a stat line/);
});

test("a generated secret is 64 URL-safe characters", () => {
  assert.match("A1_-".repeat(16), GENERATED_SECRET);
  assert.doesNotMatch("A".repeat(63), GENERATED_SECRET);
  assert.doesNotMatch("A".repeat(65), GENERATED_SECRET);
  assert.doesNotMatch("+".repeat(64), GENERATED_SECRET);
  assert.doesNotMatch(`${"A".repeat(63)}=`, GENERATED_SECRET);
});

test("a folder is checked down to its last file for an owner that is not the one asked for", () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-owner-"));
  mkdirSync(join(folder, "data"));
  writeFileSync(join(folder, "data", "weir.sqlite3"), "x");
  const { uid, gid } = statSync(folder);
  assert.deepEqual(notOwnedBy(folder, { uid, gid }), []);
  assert.deepEqual(notOwnedBy(folder, { uid: uid + 1, gid }), [
    `${folder} (${uid}:${gid})`,
    `${join(folder, "data")} (${uid}:${gid})`,
    `${join(folder, "data", "weir.sqlite3")} (${uid}:${gid})`,
  ]);
});

/** A clock that only moves when something sleeps, so a wait needs no real time. */
function fakeClock() {
  let now = 0;
  const slept = [];
  return {
    now: () => now,
    sleep: async (ms) => {
      slept.push(ms);
      now += ms;
    },
    slept,
  };
}

test("polling returns the first answer and waits only between asks", async () => {
  const clock = fakeClock();
  let asked = 0;
  const found = await pollUntil(async () => (++asked === 3 ? "done" : undefined), { what: "the job", ceilingMs: 10_000, intervalMs: 250, clock });
  assert.equal(found, "done");
  assert.deepEqual(clock.slept, [250, 250]);
});

test("polling gives up at its ceiling and says what it waited for", async () => {
  const clock = fakeClock();
  await assert.rejects(pollUntil(async () => undefined, { what: "the job", ceilingMs: 1_000, intervalMs: 250, clock }), /the job did not happen within 1 s/);
  assert.deepEqual(clock.slept, [250, 250, 250, 250]);
});

test("a polled check that throws ends the wait with its own error", async () => {
  const check = async () => {
    throw new Error("the job failed");
  };
  await assert.rejects(pollUntil(check, { what: "the job", ceilingMs: 1_000, intervalMs: 1, clock: fakeClock() }), /the job failed/);
});

test("an override can replace the shipped pull policy", () => {
  assert.equal(composeOverride({ pullPolicy: "never" }), 'services:\n  weir:\n    pull_policy: "never"\n');
});
