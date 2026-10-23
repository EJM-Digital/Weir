// node --test scripts/make-release-fixtures.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";

import { allText, describeDatabase, findLeftovers, leftovers, main, makeFixtures, privateValues, replacements, scrubDatabase, scrubText } from "./make-release-fixtures.mjs";

const values = privateValues({ hostname: "WORKBENCH-7", userName: "jdoe", tempRoot: "C:\\Users\\jdoe\\AppData\\Local\\Temp" });
const rules = replacements(values);

test("the temp folder is scrubbed as a path, as a path inside JSON, and with forward slashes", () => {
  const asPath = "C:\\Users\\jdoe\\AppData\\Local\\Temp\\weir_files_a\\watched";
  const inJson = "C:\\\\Users\\\\jdoe\\\\AppData\\\\Local\\\\Temp\\\\weir_files_a\\\\watched";
  const forward = "C:/Users/jdoe/AppData/Local/Temp/weir_files_a/watched";

  assert.equal(scrubText(asPath, rules), "C:\\Fixture\\weir_files_a\\watched");
  assert.equal(scrubText(inJson, rules), "C:\\\\Fixture\\\\weir_files_a\\\\watched");
  assert.equal(scrubText(forward, rules), "C:/Fixture/weir_files_a/watched");
});

test("a path in the account's folder outside the temp folder keeps its shape and loses the name", () => {
  assert.equal(
    scrubText("C:\\Users\\jdoe\\AppData\\Local\\Tools\\ffmpeg.exe and C:\\\\Users\\\\jdoe\\\\x", rules),
    "C:\\Users\\fixture-user\\AppData\\Local\\Tools\\ffmpeg.exe and C:\\\\Users\\\\fixture-user\\\\x",
  );
});

test("the PC's name is scrubbed in any letter case, and ordinary words are left alone", () => {
  assert.equal(scrubText("Weir on workbench-7 and WORKBENCH-7: the user jdoe wrote it", rules), "Weir on FIXTURE-PC and FIXTURE-PC: the user jdoe wrote it");
});

test("what is left of this PC after scrubbing is named, and a clean text has none", () => {
  const dirty = "C:\\Users\\jdoe\\Documents and WORKBENCH-7";

  assert.deepEqual(leftovers(dirty, values), ["the PC's name", "the account's folder"]);
  assert.deepEqual(leftovers(scrubText("C:\\Users\\jdoe\\AppData\\Local\\Temp\\x on WORKBENCH-7", rules), values), []);
  assert.deepEqual(leftovers("a user wrote this", values), []);
});

test("a database is scrubbed in every text column, its sessions end, and it stays one plain file", () => {
  const folder = mkdtempSync(path.join(os.tmpdir(), "weir-fixture-test-"));
  try {
    const file = path.join(folder, "weir.sqlite3");
    const database = new DatabaseSync(file);
    database.exec("PRAGMA journal_mode = WAL");
    database.exec("CREATE TABLE schema_version (revision TEXT NOT NULL)");
    database.exec("INSERT INTO schema_version VALUES ('0001_test')");
    database.exec("CREATE TABLE user_sessions (token TEXT)");
    database.exec("INSERT INTO user_sessions VALUES ('secret-token')");
    database.exec("CREATE TABLE activity_events (id INTEGER PRIMARY KEY, title TEXT, detail JSON, size INTEGER)");
    database.prepare("INSERT INTO activity_events (title, detail, size) VALUES (?, ?, ?)").run(
      "Finished on WORKBENCH-7",
      '{"path":"C:\\\\Users\\\\jdoe\\\\AppData\\\\Local\\\\Temp\\\\x\\\\film.mkv"}',
      42,
    );
    database.close();

    const { changed } = scrubDatabase(file, values);

    assert.equal(changed, 2);
    const text = allText(file);
    assert.deepEqual(leftovers(text, values), []);
    assert.match(text, /Finished on FIXTURE-PC/);
    assert.match(text, /C:\\\\Fixture\\\\x\\\\film\.mkv/);
    assert.doesNotMatch(text, /secret-token/);
    const described = describeDatabase(file);
    assert.equal(described.revision, "0001_test");
    assert.deepEqual(described.rows, { activity_events: 1, schema_version: 1 });
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

/** A session database like the one a release leaves, holding `title` in an activity row. */
function sessionDatabase(file, title) {
  const database = new DatabaseSync(file);
  database.exec("CREATE TABLE schema_version (revision TEXT NOT NULL)");
  database.exec("INSERT INTO schema_version VALUES ('0042_test')");
  database.exec("CREATE TABLE user_sessions (token TEXT)");
  database.exec("CREATE TABLE activity_events (id INTEGER PRIMARY KEY, title TEXT, size INTEGER)");
  database.prepare("INSERT INTO activity_events (title, size) VALUES (?, 7)").run(title);
  database.close();
}

test("the places something private is left in a database are named by table and column, and a clean one has none", () => {
  const folder = mkdtempSync(path.join(os.tmpdir(), "weir-fixture-test-"));
  try {
    const file = path.join(folder, "weir.sqlite3");
    sessionDatabase(file, "Finished on WORKBENCH-7 in C:\\Users\\jdoe\\Videos");
    assert.deepEqual(findLeftovers(file, values), ["activity_events.title: the PC's name", "activity_events.title: the account's folder"]);
    scrubDatabase(file, values);
    assert.deepEqual(findLeftovers(file, values), []);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

/** The folders and the stand-ins for docker and dotnet that makeFixtures is given, over a scratch folder. */
function withFixtureRun(title, bundle, callback) {
  const root = mkdtempSync(path.join(os.tmpdir(), "weir-fixture-run-"));
  try {
    const folders = { databases: path.join(root, "databases"), backups: path.join(root, "backups"), manifest: path.join(root, "databases", "manifest.json") };
    const steps = {
      values,
      folders,
      fetch: (version) => ({ executable: `${version}/Weir`, digest: "sha256:abc" }),
      runSession: (executable, scratch) => {
        const raw = path.join(scratch, "weir.sqlite3");
        sessionDatabase(raw, title);
        const file = path.join(scratch, "bundle.json");
        writeFileSync(file, bundle);
        return { raw, bundle: file };
      },
    };
    callback({ root, folders, steps });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("a release is fetched, run, scrubbed and written as a compressed database, a backup and a manifest line", () => {
  withFixtureRun("Finished on WORKBENCH-7", '{"host":"workbench-7"}', ({ folders, steps }) => {
    makeFixtures("2.0.0", steps);
    const entry = JSON.parse(readFileSync(folders.manifest, "utf8")).databases[0];
    assert.deepEqual({ version: entry.version, image_digest: entry.image_digest, schema_revision: entry.schema_revision, rows: entry.rows }, { version: "2.0.0", image_digest: "sha256:abc", schema_revision: "0042_test", rows: { activity_events: 1, schema_version: 1 } });
    const compressed = readFileSync(path.join(folders.databases, "2.0.0.db.gz"));
    assert.equal(entry.sha256, createHash("sha256").update(compressed).digest("hex"));
    assert.equal(readFileSync(path.join(folders.backups, "2.0.0", "configuration-bundle.json"), "utf8"), '{"host":"FIXTURE-PC"}');
    makeFixtures("2.0.1", steps);
    makeFixtures("2.0.0", steps);
    assert.deepEqual(JSON.parse(readFileSync(folders.manifest, "utf8")).databases.map((item) => item.version), ["2.0.1", "2.0.0"]);
  });
});

test("a database that still holds something private after scrubbing is refused, naming the table and column", () => {
  withFixtureRun("Finished", "{}", ({ steps }) => {
    const runSession = (executable, scratch) => {
      const session = steps.runSession(executable, scratch);
      const database = new DatabaseSync(session.raw);
      database.exec("CREATE TABLE notes (data BLOB)");
      database.prepare("INSERT INTO notes VALUES (?)").run("kept on WORKBENCH-7");
      database.close();
      return session;
    };
    assert.throws(() => makeFixtures("2.0.0", { ...steps, runSession }), /of 2\.0\.0 still holds something private after scrubbing: notes\.data: the PC's name\./);
  });
});

test("a backup that still holds a private value after scrubbing is refused", () => {
  // An account named like the stand-in the scrub writes cannot be told from the scrubbed text.
  withFixtureRun("Finished", "C:\\Users\\fixture-user\\x", ({ steps }) => {
    const hidden = { ...steps, values: { hostname: "WORKBENCH-7", userName: "fixture-user", tempRoot: "Z:\\other" } };
    assert.throws(() => makeFixtures("2.0.0", hidden), /The backup of 2\.0\.0 still holds a private value/);
  });
});

test("the command makes each version it is given, and refuses none, or one that is not a version", () => {
  const made = [];
  const errors = [];
  assert.equal(main(["1.0.0", "1.0.0-rc.18"], { make: (version) => made.push(version), error: (line) => errors.push(line) }), 0);
  assert.deepEqual(made, ["1.0.0", "1.0.0-rc.18"]);
  assert.equal(main([], { make: () => assert.fail("none"), error: (line) => errors.push(line) }), 2);
  assert.equal(main(["latest"], { make: () => assert.fail("none"), error: (line) => errors.push(line) }), 2);
  assert.equal(errors.length, 2);
  assert.match(errors[0], /Usage: node scripts\/make-release-fixtures\.mjs <release version>/);
  assert.equal(spawnSync(process.execPath, [fileURLToPath(new URL("./make-release-fixtures.mjs", import.meta.url))], { encoding: "utf8" }).status, 2);
});

test("a Linux home folder is scrubbed like a Windows account folder", () => {
  const linux = replacements(privateValues({ hostname: "runner-vm-3", userName: "runner", tempRoot: "/tmp/fixture-session" }));

  assert.equal(scrubText("/home/runner/tools/ffmpeg and /tmp/fixture-session/weir_files_a", linux), "/home/fixture-user/tools/ffmpeg and C:/Fixture/weir_files_a");
  assert.deepEqual(leftovers("/home/runner/tools", privateValues({ hostname: "runner-vm-3", userName: "runner", tempRoot: "/tmp" })), ["the account's folder"]);
});
