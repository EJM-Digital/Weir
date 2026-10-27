#!/usr/bin/env node
// Fails when a change sets files back to a version that an earlier merged change replaced, without saying so.
//
// A squash-merged pull request once put the files of two earlier merged pull requests (#1320 and #1321) back to their
// old versions: its branch was stale, a merge conflict was resolved the wrong way, and CI stayed green because the old
// versions were consistent with each other. Nothing in the diff of that pull request looked like a revert, so nothing
// asked for a second look. This check looks.
//
// It takes the change as two refs. The change is everything `head` has that the common ancestor of the two does not (a
// pull request: origin/main and its head; a push to main: the commit's parent and the commit). For each file the change
// touches it reads the base's recent history of that file. When the change leaves the file exactly as it was just
// before one of those commits (a file a commit deleted and the change brings back counts), the change undoes that commit,
// and every later commit that touched the file with it. It fails unless the pull request description or a commit
// message of the change says `Reverts #<number>` for each one (GitHub's own Revert button writes that line).
//
// The window is 21 days and 400 commits of the base's first-parent history, whichever ends first. A branch only undoes
// a commit it was cut before, so the window has to cover how long a branch lives: of the last 251 merged pull requests
// the median lived 1.6 hours, the 99th percentile 2.0 days and the longest 3.8 days, and main takes about 16 commits a
// day. 21 days is five times the longest of them; 400 commits is the same margin on a busy fortnight.
//
// Usage: node scripts/check-no-silent-reverts.mjs --base <ref> --head <ref> [--event <event.json>] [--root <folder>]
//   --event  the GitHub event payload (default: $GITHUB_EVENT_PATH); a pull request's description is read from it.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { rootOf } from "./repository-root.mjs";

export const WINDOW_DAYS = 21;
export const WINDOW_COMMITS = 400;

const DAY_MS = 24 * 60 * 60 * 1000;
const RAW_LINE = /^:\d+ \d+ ([0-9a-f]+) ([0-9a-f]+) [A-Z]\d*\t(.+)$/;
const NO_BLOB = /^0+$/;

function git(root, args) {
  return execFileSync("git", ["-c", "core.quotepath=false", ...args], { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], maxBuffer: 1024 * 1024 * 1024 });
}

/** The files `git ... --raw` lists, each with the blob before and after (null: the file was absent). */
export function parseRaw(text) {
  const files = [];
  for (const line of text.split("\n")) {
    const match = RAW_LINE.exec(line);
    if (match) files.push({ path: match[3], before: NO_BLOB.test(match[1]) ? null : match[1], after: NO_BLOB.test(match[2]) ? null : match[2] });
  }
  return files;
}

/** The commits `git log --format=%x01%H%x02%s%x03 --raw` printed, newest first, each with a map of path to its change. */
export function parseHistory(text) {
  return text
    .split("\x01")
    .slice(1)
    .map((entry) => {
      const split = entry.indexOf("\x03");
      const [sha, subject] = entry.slice(0, split).split("\x02");
      const changes = new Map(parseRaw(entry.slice(split + 1)).map((file) => [file.path, file]));
      return { sha, subject, changes };
    });
}

/** The pull request number a squash-merged commit carries in its subject, or null. */
export function pullRequestOf(subject) {
  const match = /\(#(\d+)\)\s*$/.exec(subject);
  return match ? match[1] : null;
}

/** What a description or commit messages say they revert: pull request numbers and commit ids. */
export function declaredReverts(text) {
  const numbers = new Set([...text.matchAll(/\bReverts\s+(?:[\w.-]+\/[\w.-]+)?#(\d+)/gi)].map((match) => match[1]));
  const shas = [...text.matchAll(/\bReverts\s+([0-9a-f]{7,40})\b/gi)].map((match) => match[1].toLowerCase());
  return { numbers, shas };
}

function isDeclared(commit, declared) {
  const number = pullRequestOf(commit.subject);
  return number !== null ? declared.numbers.has(number) : declared.shas.some((sha) => commit.sha.startsWith(sha));
}

/**
 * The commits of `history` (newest first) that a file left at `headBlob` undoes: the newest commit after which the file
 * was not at that blob but before which it was, and every newer commit that changed the file, since going back past
 * that commit takes their changes with it. Empty when the file was never at that blob in the window.
 *
 * A file the change deletes never undoes anything: a deletion is plain in the diff, and a file that an earlier commit
 * created is routinely removed again (a helper that moved, a script that was replaced). Run over the last 45 commits of
 * main, counting deletions flagged five commits that were right to delete files.
 */
export function undoneBy(history, path, headBlob) {
  if (headBlob === null) return [];
  const touched = [];
  for (const commit of history) {
    const change = commit.changes.get(path);
    if (!change) continue;
    touched.push(commit);
    if (change.before === headBlob) return touched;
  }
  return [];
}

/** The changes of `head` since it left `base`, checked against the base's recent history; see the top of this file. */
export function check({ root, base, head, revertsText = "", windowDays = WINDOW_DAYS, windowCommits = WINDOW_COMMITS }) {
  const mergeBase = git(root, ["merge-base", base, head]).trim();
  const changed = parseRaw(git(root, ["diff", "--raw", "--no-renames", "--no-abbrev", mergeBase, head]));
  const since = new Date(Date.parse(git(root, ["log", "-1", "--format=%cI", mergeBase]).trim()) - windowDays * DAY_MS).toISOString();
  const history = parseHistory(
    git(root, ["log", "--first-parent", "-m", "--root", "--no-renames", "--raw", "--no-abbrev", "--format=%x01%H%x02%s%x03", `--since=${since}`, "-n", String(windowCommits), mergeBase]),
  );
  const messages = git(root, ["log", "--format=%B", `${mergeBase}..${head}`]);
  const declared = declaredReverts(`${revertsText}\n${messages}`);

  const undone = new Map();
  for (const file of changed) {
    for (const commit of undoneBy(history, file.path, file.after)) {
      if (!undone.has(commit.sha)) undone.set(commit.sha, { commit, files: [] });
      undone.get(commit.sha).files.push(file.path);
    }
  }
  const findings = [...undone.values()].filter(({ commit }) => !isDeclared(commit, declared));
  return { findings, filesChecked: changed.length, commitsChecked: history.length };
}

/** The pull request description in a GitHub event payload, or "" for any other event. */
export function descriptionOf(eventFile) {
  return eventFile ? (JSON.parse(readFileSync(eventFile, "utf8")).pull_request?.body ?? "") : "";
}

export function describe(findings) {
  const lines = ["This change puts files back to a version that an earlier merged change replaced:", ""];
  for (const { commit, files } of findings) {
    const number = pullRequestOf(commit.subject);
    const title = commit.subject.replace(/\s*\(#\d+\)\s*$/, "");
    lines.push(`  ${number ? `#${number} ` : ""}${title} (${commit.sha.slice(0, 8)}): ${files.length} ${files.length === 1 ? "file" : "files"}`);
    lines.push(...files.map((file) => `    ${file}`));
  }
  lines.push(
    "",
    "If a merge conflict was resolved by taking this branch's side, merge main in again and keep main's version of each file.",
    'If undoing a change is intended, write "Reverts #<number>" for each one in the pull request description or in a commit message.',
  );
  return lines.join("\n");
}

function option(argv, name) {
  const at = argv.indexOf(name);
  return at >= 0 ? argv[at + 1] : undefined;
}

function main() {
  const argv = process.argv.slice(2);
  const base = option(argv, "--base");
  const head = option(argv, "--head");
  if (!base || !head) {
    console.error("Say which change to check: node scripts/check-no-silent-reverts.mjs --base <ref> --head <ref>");
    process.exit(1);
  }
  let result;
  try {
    const revertsText = descriptionOf(option(argv, "--event") ?? process.env.GITHUB_EVENT_PATH);
    result = check({ root: rootOf(import.meta.url, argv), base, head, revertsText });
  } catch (error) {
    console.error(`Could not compare ${head} with ${base}: ${(error.stderr || error.message).trim().split("\n")[0]}`);
    process.exit(1);
  }
  if (result.findings.length > 0) {
    console.error(describe(result.findings));
    process.exit(1);
  }
  console.log(`No merged change is silently undone (${result.filesChecked} changed files checked against ${result.commitsChecked} recent commits).`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main();
