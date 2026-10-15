// node --test scripts/recover-output.test.mjs
// The wording checks the recover smokes (scripts/docker-smoke/recover.mjs, scripts/smoke-windows-recover.ps1) apply to what
// `Weir recover` prints, judged against what the packaged WeirServer.exe really printed (scripts/fixtures/recover/: its help, its
// account list, a reset and a run pointed at a folder Weir never used; the data folder's temporary path is the one edit). The Docker
// image's own output is kept by the smoke in CI and is not recorded here.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { expectHelp, expectListed, expectNoDatabase, expectReset, recordingText, sessionsEndedWords } from "./docker-smoke/recover-output.mjs";

const fixtures = join(dirname(fileURLToPath(import.meta.url)), "fixtures", "recover");

/** A recording in the form `recordingText` writes. */
function recorded(name) {
  const text = readFileSync(join(fixtures, `${name}.txt`), "utf8");
  const [, status, stdout, stderr] = /^exit code: (\d+)\n--- stdout ---\n([\s\S]*)\n--- stderr ---\n([\s\S]*)$/.exec(text);
  return { status: Number(status), stdout, stderr };
}

test("a recording reads back as the text it was written from", () => {
  for (const name of ["help", "list", "recover", "no-database"]) {
    const result = recorded(name);
    assert.equal(recordingText(result), readFileSync(join(fixtures, `${name}.txt`), "utf8"));
  }
});

test("the help, the list, a reset and a folder with no database are what the checks expect", () => {
  expectHelp(recorded("help"));
  expectListed(recorded("list"), "recover-smoke-admin");
  expectReset(recorded("recover"), { username: "recover-smoke-admin", sessionsEnded: 1 });
  expectNoDatabase(recorded("no-database"));
});

test("a reset that exits 0 and prints nothing is not a reset", () => {
  assert.throws(() => expectReset({ status: 0, stdout: "", stderr: "" }, { username: "recover-smoke-admin", sessionsEnded: 1 }), /does not say the password was reset/);
});

test("a reset that ended a different number of sessions is caught", () => {
  assert.throws(() => expectReset(recorded("recover"), { username: "recover-smoke-admin", sessionsEnded: 2 }), /2 signed-in session/);
});

test("a reset that does not warn about the shell history is caught", () => {
  assert.throws(() => expectReset({ ...recorded("recover"), stderr: "" }, { username: "recover-smoke-admin", sessionsEnded: 1 }), /shell history/);
});

test("the sessions that were ended are counted in the singular and the plural", () => {
  assert.equal(sessionsEndedWords(1), "1 signed-in session was ended");
  assert.equal(sessionsEndedWords(3), "3 signed-in sessions were ended");
});

test("an account that is not listed as an active admin is caught", () => {
  const inactive = recorded("list");
  assert.throws(() => expectListed({ ...inactive, stdout: inactive.stdout.replace("active", "INACTIVE") }, "recover-smoke-admin"), /active admin/);
});

test("a stack trace is not a plain answer for a folder with no database", () => {
  const trace = "Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 1: 'no such table: users'.\n   at Microsoft.Data.Sqlite.SqliteCommand.PrepareAndEnumerateStatements()";
  assert.throws(() => expectNoDatabase({ status: 127, stdout: "", stderr: trace }), /did not exit 2/);
  assert.throws(() => expectNoDatabase({ status: 2, stdout: "", stderr: `${trace}\nThere is no Weir database at x. set WEIR_HOME to that folder` }), /stack trace/);
});

test("help that leaves an option out is caught", () => {
  const help = recorded("help");
  assert.throws(() => expectHelp({ ...help, stdout: help.stdout.replaceAll("--list", "") }), /--list/);
});
