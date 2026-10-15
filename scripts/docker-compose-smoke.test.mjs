// node --test scripts/docker-compose-smoke.test.mjs
// The parts of scripts/docker-compose-smoke.mjs that decide things without Docker: which image is the previous release, which schema
// revision the build under test ends on, what the server's own log says about the setup code and the upgrade, and how a session cookie
// is read. The Docker steps themselves run in CI (ci-packaging.yml, release.yml).
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";

import { headRevision, readRevision, revisionNumber, usernamesIn } from "./docker-smoke/database.mjs";
import { upgradeOutcome, parseSetupCode } from "./docker-smoke/server-log.mjs";
import { previousRelease } from "./docker-smoke/previous-image.mjs";
import { sessionCookie } from "./docker-smoke/weir-client.mjs";

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
