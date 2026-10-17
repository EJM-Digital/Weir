// node --test scripts/make-release-fixtures.test.mjs
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";

import { allText, describeDatabase, leftovers, privateValues, replacements, scrubDatabase, scrubText } from "./make-release-fixtures.mjs";

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
