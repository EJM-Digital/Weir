// What `Weir recover` prints, as the documentation (docs-site/docs/guides/security.md) tells a person to expect. Each check throws an
// assertion error that names what was missing and shows what the command printed, so a failing smoke step reads as a sentence.
import assert from "node:assert/strict";

/** What a run of the command left, as the text kept in a recording file. */
export function recordingText(result) {
  return `exit code: ${result.status}\n--- stdout ---\n${result.stdout}\n--- stderr ---\n${result.stderr}`;
}

/** `recover --help`: exits 0 and describes the three options. */
export function expectHelp(result) {
  assert.equal(result.status, 0, `recover --help did not exit 0.\n${recordingText(result)}`);
  for (const wanted of ["usage: Weir recover", "--username USERNAME", "--password PASSWORD", "--list"]) {
    assert.ok(result.stdout.includes(wanted), `recover --help does not mention "${wanted}".\n${recordingText(result)}`);
  }
}

/** `recover --list`: exits 0 and shows the account as an active admin. */
export function expectListed(result, username) {
  assert.equal(result.status, 0, `recover --list did not exit 0.\n${recordingText(result)}`);
  assert.match(
    result.stdout,
    new RegExp(`^\\s+${username}\\s+role=admin\\s+active\\s*$`, "m"),
    `recover --list does not show ${username} as an active admin.\n${recordingText(result)}`,
  );
}

/** The words that say how many signed-in sessions the reset ended. */
export function sessionsEndedWords(count) {
  return count === 1 ? "1 signed-in session was ended" : `${count} signed-in sessions were ended`;
}

/** A reset by `--username` and `--password`: exits 0, says what it did, and warns that the password stays in the shell history. */
export function expectReset(result, { username, sessionsEnded }) {
  assert.equal(result.status, 0, `recover did not exit 0.\n${recordingText(result)}`);
  assert.ok(result.stdout.includes(`Password reset for '${username}'.`), `recover does not say the password was reset.\n${recordingText(result)}`);
  assert.ok(result.stdout.includes("The account is active and has the admin role."), `recover does not say the account is an active admin.\n${recordingText(result)}`);
  assert.match(
    result.stdout,
    new RegExp(`^${sessionsEndedWords(sessionsEnded)}\\W+sign in again with the new password\\.\\s*$`, "m"),
    `recover does not say that ${sessionsEnded} signed-in session(s) were ended.\n${recordingText(result)}`,
  );
  assert.ok(result.stderr.includes("--password stays in your shell history"), `recover does not warn that --password stays in the shell history.\n${recordingText(result)}`);
}

/** Pointed at a folder Weir never used: exits 2 and says so in a sentence, with no stack trace. */
export function expectNoDatabase(result) {
  assert.equal(result.status, 2, `recover in a folder Weir never used did not exit 2.\n${recordingText(result)}`);
  assert.ok(result.stderr.includes("There is no Weir database at"), `recover does not say there is no database.\n${recordingText(result)}`);
  assert.ok(result.stderr.includes("set WEIR_HOME to that folder"), `recover does not say how to point it at Weir's data.\n${recordingText(result)}`);
  assert.doesNotMatch(result.stderr, /Unhandled exception|^\s+at /m, `recover printed a stack trace.\n${recordingText(result)}`);
}
