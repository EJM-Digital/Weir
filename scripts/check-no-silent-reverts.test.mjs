// node --test scripts/check-no-silent-reverts.test.mjs
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, describe, test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  WINDOW_COMMITS,
  WINDOW_DAYS,
  check,
  declaredReverts,
  describe as describeFindings,
  descriptionOf,
  parseHistory,
  parseRaw,
  pullRequestOf,
  undoneBy,
} from "./check-no-silent-reverts.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-no-silent-reverts.mjs");
const folders = [];
after(() => {
  for (const folder of folders) rmSync(folder, { recursive: true, force: true });
});

const DAY = 24 * 60 * 60 * 1000;
const START = Date.parse("2026-09-01T00:00:00Z");

/** A throwaway repository whose commits are made one minute apart from the start date, unless a test says otherwise. */
function makeRepo() {
  const root = mkdtempSync(path.join(tmpdir(), "weir-reverts-"));
  folders.push(root);
  const repo = { root, clock: START };
  git(repo, "init", "-q", "-b", "main");
  git(repo, "config", "user.name", "Test");
  git(repo, "config", "user.email", "test@example.com");
  git(repo, "config", "commit.gpgsign", "false");
  return repo;
}

function git(repo, ...args) {
  return execFileSync("git", args, { cwd: repo.root, encoding: "utf8" }).trim();
}

/** Commits `files` (path to content; null deletes the path) and returns the commit's id. */
function commit(repo, files, subject, { at, body = "" } = {}) {
  for (const [file, content] of Object.entries(files)) {
    const full = path.join(repo.root, file);
    if (content === null) {
      rmSync(full, { force: true });
    } else {
      mkdirSync(path.dirname(full), { recursive: true });
      writeFileSync(full, content);
    }
  }
  repo.clock += 60 * 1000;
  const date = new Date(at ?? repo.clock).toISOString();
  git(repo, "add", "-A");
  execFileSync("git", ["commit", "-q", "--allow-empty", "-m", subject, "-m", body], {
    cwd: repo.root,
    env: { ...process.env, GIT_AUTHOR_DATE: date, GIT_COMMITTER_DATE: date },
  });
  return git(repo, "rev-parse", "HEAD");
}

const subjects = (result) => result.findings.map(({ commit: found }) => found.subject);
const filesOf = (result) => result.findings.map(({ files }) => files);

describe("the shape of the failure that happened", () => {
  test("a stale branch that sets a file back to what an earlier pull request replaced is found", () => {
    const repo = makeRepo();
    commit(repo, { "src/signout.ts": "old\n", "src/other.ts": "same\n" }, "Start");
    commit(repo, { "src/signout.ts": "new\n" }, "Sign out: send the logout request (#1320)");
    const base = commit(repo, { "src/unrelated.ts": "x\n" }, "Something else (#1321)");
    const head = commit(repo, { "src/signout.ts": "old\n", "src/coverage.ts": "tests\n" }, "Coverage (#1323)");

    const result = check({ root: repo.root, base, head });
    assert.deepEqual(subjects(result), ["Sign out: send the logout request (#1320)"]);
    assert.deepEqual(filesOf(result), [["src/signout.ts"]]);
    assert.equal(result.filesChecked, 2);
  });

  test("a pull request branch cut before the earlier change is judged from where it left main", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    git(repo, "checkout", "-q", "-b", "stale");
    const forked = commit(repo, { "branch-only.txt": "b\n" }, "Work on the branch");
    git(repo, "checkout", "-q", "main");
    commit(repo, { "f.txt": "2\n" }, "Change f (#10)");
    commit(repo, { "g.txt": "g\n" }, "Change g (#11)");
    git(repo, "checkout", "-q", "stale");
    git(repo, "merge", "-q", "--no-edit", "main");
    const head = commit(repo, { "f.txt": "1\n" }, "Resolve the conflict, keeping the branch's f");

    const result = check({ root: repo.root, base: "main", head });
    assert.deepEqual(subjects(result), ["Change f (#10)"]);
    assert.notEqual(forked, head);
  });

  test("a branch that is stale but never touches the file is not an undo", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    git(repo, "checkout", "-q", "-b", "stale");
    const head = commit(repo, { "branch-only.txt": "b\n" }, "Work on the branch");
    git(repo, "checkout", "-q", "main");
    commit(repo, { "f.txt": "2\n" }, "Change f (#10)");

    assert.deepEqual(check({ root: repo.root, base: "main", head }).findings, []);
  });
});

describe("what counts as an undo", () => {
  test("a file left as it was before several commits undoes every one of them that changed it", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    commit(repo, { "f.txt": "2\n" }, "First (#1)");
    commit(repo, { "other.txt": "o\n" }, "Unrelated (#2)");
    const base = commit(repo, { "f.txt": "3\n" }, "Second (#3)");
    const head = commit(repo, { "f.txt": "1\n" }, "Back");

    assert.deepEqual(subjects(check({ root: repo.root, base, head })), ["Second (#3)", "First (#1)"]);
  });

  test("a file left at a later state undoes only the commits after it", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    commit(repo, { "f.txt": "2\n" }, "First (#1)");
    const base = commit(repo, { "f.txt": "3\n" }, "Second (#3)");
    const head = commit(repo, { "f.txt": "2\n" }, "Partly back");

    assert.deepEqual(subjects(check({ root: repo.root, base, head })), ["Second (#3)"]);
  });

  test("a file that went 1, 2, 1 and is left at 2 undoes the newest commit, not the older ones", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    commit(repo, { "f.txt": "2\n" }, "Up (#1)");
    const base = commit(repo, { "f.txt": "1\n" }, "Down (#2)");
    const head = commit(repo, { "f.txt": "2\n" }, "Up again");

    assert.deepEqual(subjects(check({ root: repo.root, base, head })), ["Down (#2)"]);
  });

  test("a file the base already has at the old version is not touched, so nothing is found", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    commit(repo, { "f.txt": "2\n" }, "Change (#1)");
    const base = commit(repo, { "f.txt": "1\n" }, "Revert (#2)");
    const head = commit(repo, { "g.txt": "g\n" }, "Other");

    const result = check({ root: repo.root, base, head });
    assert.deepEqual(result.findings, []);
    assert.equal(result.filesChecked, 1);
  });

  test("a file changed to a version it never had is not an undo", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    const base = commit(repo, { "f.txt": "2\n" }, "Change (#1)");
    const head = commit(repo, { "f.txt": "3\n" }, "Change again");

    assert.deepEqual(check({ root: repo.root, base, head }).findings, []);
  });

  test("new files are not an undo", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    const base = commit(repo, { "f.txt": "2\n" }, "Change (#1)");
    const head = commit(repo, { "new.txt": "n\n" }, "Add a file");

    assert.deepEqual(check({ root: repo.root, base, head }).findings, []);
  });

  test("a file the change deletes is not an undo of the commit that created it", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    commit(repo, { "helper.ts": "h\n" }, "Add a helper (#1)");
    const base = commit(repo, { "helper.ts": "h2\n" }, "Change the helper (#2)");
    const head = commit(repo, { "helper.ts": null }, "Remove the helper");

    assert.deepEqual(check({ root: repo.root, base, head }).findings, []);
  });

  test("a file the change brings back is an undo of the commit that deleted it", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n", "old.ts": "keep\n" }, "Start");
    const base = commit(repo, { "old.ts": null }, "Remove old.ts (#7)");
    const head = commit(repo, { "old.ts": "keep\n" }, "Bring it back");

    const result = check({ root: repo.root, base, head });
    assert.deepEqual(subjects(result), ["Remove old.ts (#7)"]);
    assert.deepEqual(filesOf(result), [["old.ts"]]);
  });

  test("a file moved back to where it was is an undo of the move", () => {
    const repo = makeRepo();
    commit(repo, { "src/a.ts": "content\n", "z.txt": "z\n" }, "Start");
    const base = commit(repo, { "src/a.ts": null, "lib/a.ts": "content\n" }, "Move a.ts to lib (#5)");
    const head = commit(repo, { "src/a.ts": "content\n", "lib/a.ts": null }, "Move it back");

    const result = check({ root: repo.root, base, head });
    assert.deepEqual(subjects(result), ["Move a.ts to lib (#5)"]);
    assert.deepEqual(filesOf(result), [["src/a.ts"]]);
  });

  test("one commit undone through several files is listed once with all of them", () => {
    const repo = makeRepo();
    commit(repo, { "a.txt": "a1\n", "b.txt": "b1\n" }, "Start");
    const base = commit(repo, { "a.txt": "a2\n", "b.txt": "b2\n" }, "Change both (#4)");
    const head = commit(repo, { "a.txt": "a1\n", "b.txt": "b1\n" }, "Back");

    const result = check({ root: repo.root, base, head });
    assert.equal(result.findings.length, 1);
    assert.deepEqual(filesOf(result), [["a.txt", "b.txt"]]);
  });

  test("a merge commit on main is read against its first parent", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    git(repo, "checkout", "-q", "-b", "feature");
    commit(repo, { "f.txt": "2\n" }, "Feature work");
    git(repo, "checkout", "-q", "main");
    commit(repo, { "other.txt": "o\n" }, "Main work");
    git(repo, "merge", "-q", "--no-ff", "-m", "Merge the feature (#9)", "feature");
    const base = git(repo, "rev-parse", "HEAD");
    const head = commit(repo, { "f.txt": "1\n" }, "Back");

    assert.deepEqual(subjects(check({ root: repo.root, base, head })), ["Merge the feature (#9)"]);
  });
});

describe("a deliberate revert", () => {
  function reverted() {
    const repo = makeRepo();
    commit(repo, { "a.txt": "a1\n", "b.txt": "b1\n" }, "Start");
    commit(repo, { "a.txt": "a2\n" }, "Change a (#21)");
    const base = commit(repo, { "b.txt": "b2\n" }, "Change b (#22)");
    return { repo, base };
  }

  test("passes when the pull request description names each undone pull request", () => {
    const { repo, base } = reverted();
    const head = commit(repo, { "a.txt": "a1\n", "b.txt": "b1\n" }, "Back out both");

    assert.equal(check({ root: repo.root, base, head }).findings.length, 2);
    assert.deepEqual(check({ root: repo.root, base, head, revertsText: "Reverts #21\nReverts #22" }).findings, []);
  });

  test("passes when commit messages name them, in the line GitHub's Revert button writes or in a body", () => {
    const { repo, base } = reverted();
    commit(repo, { "a.txt": "a1\n" }, "Revert a", { body: "Reverts some-owner/some-repo#21" });
    const head = commit(repo, { "b.txt": "b1\n" }, "Revert b", { body: "Some words.\nreverts #22." });

    assert.deepEqual(check({ root: repo.root, base, head }).findings, []);
  });

  test("still fails for a pull request that is not named", () => {
    const { repo, base } = reverted();
    const head = commit(repo, { "a.txt": "a1\n", "b.txt": "b1\n" }, "Back out both");

    const result = check({ root: repo.root, base, head, revertsText: "Reverts #21. Also see #22 and Revert #22" });
    assert.deepEqual(subjects(result), ["Change b (#22)"]);
  });

  test("a commit without a pull request number is named by its commit id", () => {
    const repo = makeRepo();
    commit(repo, { "a.txt": "a1\n" }, "Start");
    const direct = commit(repo, { "a.txt": "a2\n" }, "A direct commit");
    const head = commit(repo, { "a.txt": "a1\n" }, "Back");

    const result = check({ root: repo.root, base: direct, head });
    assert.deepEqual(subjects(result), ["A direct commit"]);
    assert.deepEqual(check({ root: repo.root, base: direct, head, revertsText: `Reverts ${direct.slice(0, 9)}` }).findings, []);
    assert.equal(check({ root: repo.root, base: direct, head, revertsText: "Reverts #1 and reverts abcdef0" }).findings.length, 1);
  });
});

describe("the window", () => {
  test("a commit older than the window is not looked at", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start", { at: START });
    commit(repo, { "f.txt": "2\n" }, "Old change (#1)", { at: START + 10 * DAY });
    const base = commit(repo, { "g.txt": "g\n" }, "Recent (#2)", { at: START + 10 * DAY + WINDOW_DAYS * DAY + 60000 });
    const head = commit(repo, { "f.txt": "1\n" }, "Back", { at: START + 40 * DAY });

    assert.deepEqual(check({ root: repo.root, base, head }).findings, []);
    assert.equal(check({ root: repo.root, base, head, windowDays: 40 }).findings.length, 1);
  });

  test("only the newest commits up to the limit are looked at", () => {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    commit(repo, { "f.txt": "2\n" }, "Change (#1)");
    commit(repo, { "a.txt": "a\n" }, "Filler (#2)");
    const base = commit(repo, { "b.txt": "b\n" }, "Filler (#3)");
    const head = commit(repo, { "f.txt": "1\n" }, "Back");

    assert.deepEqual(check({ root: repo.root, base, head, windowCommits: 2 }).findings, []);
    const result = check({ root: repo.root, base, head, windowCommits: 3 });
    assert.equal(result.findings.length, 1);
    assert.equal(result.commitsChecked, 3);
  });

  test("the window is long enough for the longest branch of the last 251 pull requests, five times over", () => {
    assert.ok(WINDOW_DAYS >= 5 * 3.8);
    assert.ok(WINDOW_COMMITS >= 5 * 3.8 * 16);
  });
});

describe("the pieces", () => {
  test("parseRaw reads blobs before and after, with no blob for an absent file, and skips other lines", () => {
    const added = "a".repeat(40);
    const text = [`:000000 100644 ${"0".repeat(40)} ${added} A\tnew file.txt`, `:100644 000000 ${added} ${"0".repeat(40)} D\tgone.txt`, "", "not a change"].join("\n");
    assert.deepEqual(parseRaw(text), [
      { path: "new file.txt", before: null, after: added },
      { path: "gone.txt", before: added, after: null },
    ]);
  });

  test("parseHistory reads each commit's id, subject and changes", () => {
    const blob = "b".repeat(40);
    const text = `\x01${"1".repeat(40)}\x02First (#1)\x03\n\n:100644 100644 ${blob} ${"c".repeat(40)} M\ta.txt\n\x01${"2".repeat(40)}\x02Second\x03\n`;
    const history = parseHistory(text);
    assert.deepEqual(
      history.map(({ sha, subject, changes }) => [sha, subject, [...changes.keys()]]),
      [
        ["1".repeat(40), "First (#1)", ["a.txt"]],
        ["2".repeat(40), "Second", []],
      ],
    );
    assert.deepEqual(parseHistory(""), []);
  });

  test("pullRequestOf reads the number a squash merge puts at the end of the subject", () => {
    assert.equal(pullRequestOf("Coverage (#1060): Infrastructure to 100% (#1323)"), "1323");
    assert.equal(pullRequestOf("Fix (#12) "), "12");
    assert.equal(pullRequestOf("Fix #12 in the middle"), null);
    assert.equal(pullRequestOf("Coverage (#1060): no number at the end"), null);
  });

  test("declaredReverts reads numbers and commit ids, and not other words", () => {
    const text = "Reverts #5, reverts owner/repo#6, REVERTS abcdef1234. Does not revert #7. Reverts abc.";
    const { numbers, shas } = declaredReverts(text);
    assert.deepEqual([...numbers].sort(), ["5", "6"]);
    assert.deepEqual(shas, ["abcdef1234"]);
  });

  test("undoneBy ignores a file that was deleted", () => {
    const history = [{ sha: "x", subject: "s", changes: new Map([["f", { before: "1", after: null }]]) }];
    assert.deepEqual(undoneBy(history, "f", null), []);
    assert.deepEqual(undoneBy(history, "f", "1"), history);
  });

  test("descriptionOf reads a pull request's description, and an empty one for anything else", () => {
    const folder = mkdtempSync(path.join(tmpdir(), "weir-reverts-event-"));
    folders.push(folder);
    const write = (name, payload) => {
      const file = path.join(folder, name);
      writeFileSync(file, JSON.stringify(payload));
      return file;
    };
    assert.equal(descriptionOf(write("pr.json", { pull_request: { body: "Reverts #3" } })), "Reverts #3");
    assert.equal(descriptionOf(write("empty.json", { pull_request: { body: null } })), "");
    assert.equal(descriptionOf(write("push.json", { ref: "refs/heads/main" })), "");
    assert.equal(descriptionOf(undefined), "");
  });

  test("describe lists each undone change with its files in plain words", () => {
    const sha = "1234567890abcdef".repeat(2);
    const text = describeFindings([
      { commit: { sha, subject: "Sign out (#1320)" }, files: ["a.ts", "b.ts"] },
      { commit: { sha: "f".repeat(40), subject: "A direct commit" }, files: ["c.ts"] },
    ]);
    assert.equal(
      text,
      [
        "This change puts files back to a version that an earlier merged change replaced:",
        "",
        "  #1320 Sign out (12345678): 2 files",
        "    a.ts",
        "    b.ts",
        "  A direct commit (ffffffff): 1 file",
        "    c.ts",
        "",
        "If a merge conflict was resolved by taking this branch's side, merge main in again and keep main's version of each file.",
        'If undoing a change is intended, write "Reverts #<number>" for each one in the pull request description or in a commit message.',
      ].join("\n"),
    );
  });
});

describe("the command", () => {
  const run = (repo, args, env = {}) =>
    spawnSync(process.execPath, [script, "--root", repo.root, ...args], { encoding: "utf8", env: { ...process.env, GITHUB_EVENT_PATH: "", ...env } });

  function undone() {
    const repo = makeRepo();
    commit(repo, { "f.txt": "1\n" }, "Start");
    const base = commit(repo, { "f.txt": "2\n" }, "Change f (#10)");
    const head = commit(repo, { "f.txt": "1\n" }, "Back");
    return { repo, base, head };
  }

  test("exits 1 and says what was undone and what to do", () => {
    const { repo, base, head } = undone();
    const result = run(repo, ["--base", base, "--head", head]);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /puts files back to a version that an earlier merged change replaced/);
    assert.match(result.stderr, /#10 Change f \(.{8}\): 1 file\n {4}f\.txt/);
    assert.match(result.stderr, /Reverts #<number>/);
  });

  test("passes when the pull request description in the event payload names the pull request", () => {
    const { repo, base, head } = undone();
    const event = path.join(repo.root, "..", `event-${path.basename(repo.root)}.json`);
    writeFileSync(event, JSON.stringify({ pull_request: { body: "Reverts #10 because it broke sign-in." } }));
    folders.push(event);

    for (const result of [run(repo, ["--base", base, "--head", head, "--event", event]), run(repo, ["--base", base, "--head", head], { GITHUB_EVENT_PATH: event })]) {
      assert.equal(result.status, 0, result.stderr);
      assert.match(result.stdout, /No merged change is silently undone \(1 changed files checked against \d+ recent commits\)\./);
    }
  });

  test("passes a change that touches nothing", () => {
    const { repo, head } = undone();
    const clean = run(repo, ["--base", head, "--head", head]);
    assert.equal(clean.status, 0);
    assert.match(clean.stdout, /0 changed files/);
  });

  test("asks for both refs when one is missing", () => {
    const { repo, base } = undone();
    for (const args of [[], ["--base", base], ["--head", base]]) {
      const result = run(repo, args);
      assert.equal(result.status, 1);
      assert.match(result.stderr, /Say which change to check/);
    }
  });

  test("says which refs it could not compare, in one line", () => {
    const { repo, base } = undone();
    const result = run(repo, ["--base", base, "--head", "no-such-ref"]);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /^Could not compare no-such-ref with [0-9a-f]{40}: .*\n$/);
  });

  test("says so when the event payload cannot be read", () => {
    const { repo, base, head } = undone();
    const result = run(repo, ["--base", base, "--head", head, "--event", path.join(repo.root, "missing.json")]);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /^Could not compare /);
  });
});
