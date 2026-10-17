#!/usr/bin/env node
// Combines a full scenario-suite run with the re-runs of the scenarios it failed, and only then sets the `golden-path`
// commit status (the owner, 17 Oct 2026).
//
// The rule: a full run that fails only on harness or timing problems, where the fix touches no file that ships
// (scripts/release/shipped-paths.mjs), still counts as the gate together with the failed scenarios re-run alone and
// passing on the same build (same version, same commit). The combined record lists the full run and every re-run. A fix
// to a file that ships still needs a full run.
//
// It reads the records Invoke-WeirScenarios.ps1 writes (scenario-<version>-<short sha>.md) and refuses, in plain words,
// unless all of these hold:
//   - the first record is a full run: every scenario ran, none was left out or left to another invocation;
//   - it did not pass everything (a run that passed has its own status and needs no combining);
//   - every other record is a run of chosen scenarios (-Scenarios), made for the same version and the same build commit;
//   - every scenario the full run did not pass was re-run, and the latest re-run of it passed;
//   - no file that ships differs between the commit the scenario scripts ran from in the full run and in each re-run.
//
// Usage:  node scripts/combine-golden-path-records.mjs <full-run record> <re-run record>... [--out <file>]
//           [--evidence-url <url>] [--repository <owner/name>] [--no-status]
// Give the re-runs in the order they ran: when one scenario was re-run more than once, the last re-run decides.
// Needs `gh` signed in with the right to write statuses, unless --no-status is given. Run it inside a clone that has the
// commits the records name (shipped files are compared with git).
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { isAncestor, sameShippedFiles } from "./release/shipped-paths.mjs";
import { REPOSITORY } from "./repository.mjs";
import { GOLDEN_PATH_CONTEXT } from "./verify-golden-path-for-release.mjs";

const SCRIPTS_COMMIT_FACT = "Scripts commit";
const NOT_RUN = ["not chosen", "not run yet"];
const STATUSES = ["passed", "failed", "not-applicable", ...NOT_RUN];
const ROW = new RegExp(`^\\| (\\d+) \\| (.+?) \\| (${STATUSES.join("|")}) \\| ([\\d.]+) \\| (.*) \\|$`);

// Reads one record: { name, version, shortSha, commit, scriptsCommit, subset, rows: [{ title, status, detail }] }.
export function parseRecord(name, text) {
  const heading = /^# Weir scenario suite (\S+), commit ([0-9a-f]{7,40})\b/m.exec(text);
  const facts = {};
  const rows = [];
  for (const line of text.split(/\r?\n/)) {
    const fact = /^- \*\*(.+?):\*\* (.*)$/.exec(line);
    if (fact) facts[fact[1]] = fact[2].trim();
    const row = ROW.exec(line);
    if (row) rows.push({ title: row[2], status: row[3], detail: row[5] });
  }
  return {
    name,
    version: heading?.[1] ?? null,
    shortSha: heading?.[2] ?? null,
    commit: /^[0-9a-f]{40}$/.test(facts.Commit ?? "") ? facts.Commit : null,
    scriptsCommit: facts[SCRIPTS_COMMIT_FACT] ?? null,
    subset: Object.hasOwn(facts, "Scenarios chosen"),
    rows,
  };
}

const build = (record) => `${record.version}+${record.shortSha}`;
const plural = (count, word) => `${count} ${word}${count === 1 ? "" : "s"}`;
const ranInThisRecord = (row) => !NOT_RUN.includes(row.status);

// Why the scenario scripts of two records cannot be taken as the same, or null when no file that ships differs between them.
function scriptsDiffer(full, rerun, git) {
  for (const record of [full, rerun]) {
    if (!record.scriptsCommit) return `${record.name} does not say which commit its scenario scripts ran from, so it cannot be compared (it was made before records carried that).`;
    if (!/^[0-9a-f]{40}$/.test(record.scriptsCommit)) return `${record.name} ran its scenario scripts from ${record.scriptsCommit}, which is not one committed state, so what ran cannot be compared.`;
    try {
      git(["cat-file", "-e", `${record.scriptsCommit}^{commit}`]);
    } catch {
      return `${record.name} ran from commit ${record.scriptsCommit}, which is not in this clone. Fetch it and run this again.`;
    }
  }
  const rerunIsOlder = rerun.scriptsCommit !== full.scriptsCommit && isAncestor(rerun.scriptsCommit, full.scriptsCommit, git);
  const [older, newer] = rerunIsOlder ? [rerun.scriptsCommit, full.scriptsCommit] : [full.scriptsCommit, rerun.scriptsCommit];
  const same = sameShippedFiles(older, newer, git);
  return same.ok ? null : `Between the scenario scripts of ${full.name} (${full.scriptsCommit}) and of ${rerun.name} (${rerun.scriptsCommit}), ${same.reason}. A change to a file that ships needs a full run.`;
}

// Judges the records. `full` and each of `reruns` are { name, text }; git(args) runs git in a clone that has the commits.
// Returns { ok: false, problems } or { ok: true, version, shortSha, commit, description, record }, where record is the
// text of the combined record.
export function combineRecords({ full, reruns, git }) {
  const first = parseRecord(full.name, full.text);
  const later = reruns.map((rerun) => parseRecord(rerun.name, rerun.text));

  const unreadable = [first, ...later].filter((record) => !record.version || !record.commit || record.rows.length === 0);
  if (unreadable.length > 0) {
    return { ok: false, problems: unreadable.map((record) => `${record.name} is not a scenario record: it has no version, build commit or scenario lines.`) };
  }

  const problems = [];
  const needed = first.rows.filter((row) => row.status !== "passed");
  const notRun = first.rows.filter((row) => !ranInThisRecord(row));
  if (first.subset) problems.push(`${first.name} is a run of chosen scenarios, not a full run. Give the full run first.`);
  else if (notRun.length > 0) problems.push(`${first.name} is not a complete full run: ${plural(notRun.length, "scenario")} did not run (${notRun.map((row) => row.title).join(", ")}).`);
  else if (needed.length === 0) problems.push(`${first.name} passed every scenario, so there is nothing to combine: its own result is the record.`);

  const sameBuild = [];
  for (const record of later) {
    if (!record.subset) problems.push(`${record.name} is a full run, not a re-run of chosen scenarios. Only the first record may be a full run.`);
    else if (record.version !== first.version || record.commit !== first.commit) {
      problems.push(`${record.name} was made for ${build(record)} (commit ${record.commit}), and ${first.name} for ${build(first)} (commit ${first.commit}): a different build, so it cannot be combined.`);
    } else sameBuild.push(record);
  }

  const titles = new Set(first.rows.map((row) => row.title));
  const latest = new Map();
  for (const record of sameBuild) {
    const strange = record.rows.filter((row) => !titles.has(row.title));
    if (strange.length > 0) problems.push(`${record.name} has scenarios the full run does not have (${strange.map((row) => row.title).join(", ")}), so they are not the same suite.`);
    for (const row of record.rows.filter(ranInThisRecord)) latest.set(row.title, { record, row });
  }

  if (!first.subset && notRun.length === 0) {
    const missing = needed.filter((row) => !latest.has(row.title));
    if (missing.length > 0) problems.push(`No re-run is given for ${plural(missing.length, "scenario")} the full run did not pass: ${missing.map((row) => `${row.title} (${row.status})`).join(", ")}.`);
    for (const row of needed) {
      const rerun = latest.get(row.title);
      if (rerun && rerun.row.status !== "passed") problems.push(`${row.title} did not pass when re-run alone: ${rerun.row.status} in ${rerun.record.name}.`);
    }
  }

  for (const record of sameBuild) {
    const reason = scriptsDiffer(first, record, git);
    if (reason) problems.push(reason);
  }
  if (problems.length > 0) return { ok: false, problems };

  const total = first.rows.length;
  const used = sameBuild.filter((record) => needed.some((row) => latest.get(row.title).record === record));
  return {
    ok: true,
    version: first.version,
    shortSha: first.shortSha,
    commit: first.commit,
    description: `Weir scenarios passed, combined from a full run and ${plural(used.length, "re-run")}: ${total} of ${total}, ${build(first)}`,
    record: renderCombined({ first, later: sameBuild, needed, latest, total }),
  };
}

function renderCombined({ first, later, needed, latest, total }) {
  const lines = [`# Weir scenario suite ${first.version}, commit ${first.shortSha}`, ""];
  lines.push(`- **Commit:** ${first.commit}`);
  lines.push("- **Combined:** yes. The full run failed some scenarios; each was re-run alone and passed on this same build, and no file that ships changed in the scenario scripts between the runs (the owner, 17 Oct 2026).");
  lines.push("- **Rehearsal:** no");
  lines.push(`- **Full run:** ${first.name} (scenario scripts from ${first.scriptsCommit})`);
  later.forEach((record, index) => lines.push(`- **Re-run ${index + 1}:** ${record.name} (scenario scripts from ${record.scriptsCommit})`));
  lines.push("", "| # | Scenario | Result | Result from | Saw |", "| --- | --- | --- | --- | --- |");
  first.rows.forEach((row, index) => {
    const rerun = needed.includes(row) ? latest.get(row.title) : null;
    const shown = rerun ? rerun.row : row;
    const from = rerun ? `re-run ${later.indexOf(rerun.record) + 1} (${rerun.record.name})` : `full run (${first.name})`;
    lines.push(`| ${index + 1} | ${row.title} | ${shown.status} | ${from} | ${shown.detail} |`);
  });
  lines.push("", `**${total} of ${total} scenarios passed, ${needed.length} of them on a re-run alone.**`);
  if (needed.length > 0) {
    lines.push("", "## What the full run did not pass");
    for (const row of needed) lines.push("", `### ${row.title}`, "", `- **Full run:** ${row.status}. ${row.detail}`, `- **Re-run:** passed. ${latest.get(row.title).row.detail}`);
  }
  return `${lines.join("\n")}\n`;
}

const USAGE = "Usage: node scripts/combine-golden-path-records.mjs <full-run record> <re-run record>... [--out <file>] [--evidence-url <url>] [--repository <owner/name>] [--no-status]";

// Runs the command line. Returns the exit code. Everything outside the judging is passed in so a test can see it.
export function run(argv, { git, setStatus, read = (path) => readFileSync(path, "utf8"), write = writeFileSync, log = console.log, error = console.error }) {
  const option = (flag) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : undefined);
  const withValue = ["--out", "--evidence-url", "--repository"];
  const paths = argv.filter((arg, index) => !arg.startsWith("--") && !withValue.includes(argv[index - 1]));
  if (paths.length < 2) {
    error(USAGE);
    return 2;
  }
  const result = combineRecords({
    full: { name: basename(paths[0]), text: read(paths[0]) },
    reruns: paths.slice(1).map((path) => ({ name: basename(path), text: read(path) })),
    git,
  });
  if (!result.ok) {
    error("Not combined. The golden-path status is left as it was.");
    for (const problem of result.problems) error(`  - ${problem}`);
    return 1;
  }
  const out = resolve(option("--out") ?? join(dirname(paths[0]), `scenario-${result.version}-${result.shortSha}-combined.md`));
  write(out, result.record);
  log(`Combined record: ${out}`);
  if (argv.includes("--no-status")) {
    log(`--no-status: the golden-path status is not set. It would read: ${result.description}`);
    return 0;
  }
  const repository = option("--repository") ?? REPOSITORY;
  setStatus({ repository, sha: result.commit, description: result.description, target: option("--evidence-url") ?? `https://github.com/${repository}/commit/${result.commit}` });
  log(`${GOLDEN_PATH_CONTEXT} status on ${result.shortSha}: success (${result.description})`);
  return 0;
}

const runGit = (args) => execFileSync("git", args, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] });

function setStatusWithGh({ repository, sha, description, target }) {
  execFileSync(
    "gh",
    ["api", `repos/${repository}/statuses/${sha}`, "-f", "state=success", "-f", `context=${GOLDEN_PATH_CONTEXT}`, "-f", `description=${description.slice(0, 140)}`, "-f", `target_url=${target}`],
    { stdio: ["ignore", "ignore", "inherit"] },
  );
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    process.exit(run(process.argv.slice(2), { git: runGit, setStatus: setStatusWithGh }));
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
}
