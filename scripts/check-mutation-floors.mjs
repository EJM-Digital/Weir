#!/usr/bin/env node
// The mutation ratchet (#977): fails when a target's mutation score is below its floor in
// docs/coverage/mutation-floors.json, and writes the survivors (the changes no test noticed) to a CI job's summary page.
//
//   node scripts/check-mutation-floors.mjs --floors docs/coverage/mutation-floors.json \
//     --reports <folder> [--base-floors <floors file as it is on the base branch>] [--out <folder>]
//   (--only <target,target> checks just those targets, for a pull request that mutated only the ones it touches)
//   node scripts/check-mutation-floors.mjs --floors <file> --floors-only [--base-floors <file>]
//
// The second form reads no reports: it checks that the floors file is usable and that no floor was lowered without its
// entry, which is what a pull request can be held to without running Stryker.
//
// <folder> holds one folder per target named "mutation-" and the target's id (the name of its artifact), each with
// the `mutation-report.json` Stryker.NET or StrykerJS wrote (the mutation-testing-elements report format both share).
// A target whose report is missing fails, so a matrix job that wrote nothing is never read as "nothing to check".
//
// A floor only goes up. One that is lower than on the base branch fails unless the file carries a `lowered` entry for it
// that names the old and the new figure and says why; that entry belongs in its own pull request.
//
// With --out, the survivors of every target are written there as survivors.md (the full list, one cluster per file) and
// survivors.json, to be uploaded with the reports. The text goes to $GITHUB_STEP_SUMMARY when that is set, and to the log
// either way.
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

/** A job summary may not pass 1 MiB; the full list goes to the artifact, the summary keeps to this much of it. */
const SUMMARY_BUDGET = 700_000;
const SOURCE_SNIPPET = 90;
const TESTS_NAMED = 2;
/**
 * More timeouts than this share of the judged changes means the time limit is too short, not that the tests are strong:
 * a deadlock a mutation causes ends in a timeout that is a real catch, but not for most of a target's changes.
 */
const TIMEOUT_SHARE = 0.5;
const ANNOTATIONS = 10;

const SURVIVED = "Survived";
const NO_COVERAGE = "NoCoverage";

/** What the floor of one target is called in messages. */
function label(target) {
  return `"${target}"`;
}

export function readFloors(text) {
  let file;
  try {
    file = JSON.parse(text);
  } catch (error) {
    throw new Error(`The floors file is not valid JSON: ${error.message}`);
  }
  const problems = [];
  const targets = Object.keys(file.floors ?? {});
  if (targets.length === 0) problems.push('"floors" must name at least one target.');
  for (const target of targets) {
    const score = file.floors[target]?.score;
    if (typeof score !== "number" || !(score >= 0 && score <= 100)) {
      problems.push(`"floors.${target}.score" must be a percentage from 0 to 100.`);
    }
    const run = file.measuredFrom?.runs?.[target];
    if (!Number.isInteger(run) || run <= 0) {
      problems.push(`"measuredFrom.runs.${target}" must be the id of the CI run the starting floor was measured from.`);
    }
  }
  if (problems.length) throw new Error(`The floors file is not usable:\n- ${problems.join("\n- ")}`);
  return file;
}

/** A floor lower than the base branch's needs its own `lowered` entry, with the old and new figure and a reason. */
export function loweredFloorProblems(floors, base) {
  const problems = [];
  const notes = [];
  for (const target of Object.keys(floors.floors)) {
    const was = base.floors?.[target]?.score;
    const now = floors.floors[target].score;
    if (typeof was !== "number") continue;
    if (now > was) notes.push(`${label(target)} floor raised from ${was}% to ${now}%.`);
    if (now >= was) continue;
    const entry = floors.lowered?.[target];
    const reason = typeof entry?.reason === "string" ? entry.reason.trim() : "";
    if (entry && entry.from === was && entry.to === now && reason) {
      notes.push(`${label(target)} floor LOWERED from ${was}% to ${now}%: ${reason}`);
      continue;
    }
    problems.push(
      `${label(target)} floor was lowered from ${was}% to ${now}%. A floor only goes down in a pull request of its own, ` +
        `with the reason written under "lowered" in docs/coverage/mutation-floors.json: "${target}": { "from": ${was}, "to": ${now}, "reason": "..." }.`,
    );
  }
  return { problems, notes };
}

/**
 * The counts of a report's mutants by outcome, and the score: the share of the mutants that could be judged (not
 * ignored, not failing to compile, not failing to run) that a test caught, a test timing out counting as a catch.
 */
export function countMutants(report) {
  const counts = { killed: 0, timeout: 0, survived: 0, noCoverage: 0, other: 0 };
  for (const file of Object.values(report.files ?? {})) {
    for (const mutant of file.mutants ?? []) {
      if (mutant.status === "Killed") counts.killed += 1;
      else if (mutant.status === "Timeout") counts.timeout += 1;
      else if (mutant.status === SURVIVED) counts.survived += 1;
      else if (mutant.status === NO_COVERAGE) counts.noCoverage += 1;
      else counts.other += 1;
    }
  }
  const detected = counts.killed + counts.timeout;
  const judged = detected + counts.survived + counts.noCoverage;
  return { ...counts, detected, judged };
}

/** The score rounded down to one decimal, which is how floors are written: 71.74 is 71.7, never 71.8. */
export function scoreTenth(counts) {
  return counts.judged === 0 ? 100 : Math.floor((counts.detected * 1000) / counts.judged) / 10;
}

/**
 * The repository path of a report's file name: Stryker.NET writes the runner's absolute path, StrykerJS a path from
 * apps/web, where it ran.
 */
export function repoPath(file) {
  const name = file.replace(/\\/g, "/");
  const at = name.lastIndexOf("/apps/");
  if (at >= 0) return name.slice(at + 1);
  return /^([A-Za-z]:)?\//.test(name) ? name : `apps/web/${name}`;
}

/** The text of `source` between two 1-based line and column positions of a report. */
export function snippetOf(source, location) {
  const lines = source.split(/\r?\n/);
  const { start, end } = location;
  if (start.line === end.line) return lines[start.line - 1]?.slice(start.column - 1, end.column - 1) ?? "";
  const first = lines[start.line - 1]?.slice(start.column - 1) ?? "";
  return `${first} ...`;
}

function oneLine(text) {
  const flat = text.replace(/\s+/g, " ").trim();
  return flat.length > SOURCE_SNIPPET ? `${flat.slice(0, SOURCE_SNIPPET - 3)}...` : flat;
}

/** Every mutant no test caught, with the code it changed and the tests that ran it without noticing. */
export function survivorsOf(report) {
  const testNames = new Map();
  for (const testFile of Object.values(report.testFiles ?? {})) {
    for (const test of testFile.tests ?? []) testNames.set(test.id, test.name);
  }
  const survivors = [];
  for (const [file, content] of Object.entries(report.files ?? {})) {
    for (const mutant of content.mutants ?? []) {
      if (mutant.status !== SURVIVED && mutant.status !== NO_COVERAGE) continue;
      const covering = (mutant.coveredBy ?? []).map((id) => testNames.get(id) ?? id);
      survivors.push({
        file: repoPath(file),
        line: mutant.location.start.line,
        column: mutant.location.start.column,
        mutator: mutant.mutatorName,
        original: oneLine(snippetOf(content.source ?? "", mutant.location)),
        replacement: oneLine(mutant.replacement ?? ""),
        covered: mutant.status === SURVIVED,
        tests: covering,
      });
    }
  }
  return survivors.sort((a, b) => a.file.localeCompare(b.file) || a.line - b.line || a.column - b.column);
}

/** The survivors grouped by file, the file with the most first: each is one cluster, one test issue. */
export function clustersOf(survivors) {
  const byFile = new Map();
  for (const survivor of survivors) {
    const cluster = byFile.get(survivor.file) ?? { file: survivor.file, survivors: [], mutators: new Map() };
    cluster.survivors.push(survivor);
    cluster.mutators.set(survivor.mutator, (cluster.mutators.get(survivor.mutator) ?? 0) + 1);
    byFile.set(survivor.file, cluster);
  }
  return [...byFile.values()].sort((a, b) => b.survivors.length - a.survivors.length || a.file.localeCompare(b.file));
}

function topMutators(cluster) {
  return [...cluster.mutators.entries()]
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .slice(0, 3)
    .map(([name, count]) => `${name} (${count})`)
    .join(", ");
}

function cell(text) {
  return text.replace(/\|/g, "\\|").replace(/`/g, "'");
}

function testsCell(survivor) {
  if (!survivor.covered) return "none run this line";
  if (survivor.tests.length === 0) return "the related tests ran";
  const named = survivor.tests.slice(0, TESTS_NAMED).map((name) => cell(name.split(".").slice(-2).join(".")));
  return survivor.tests.length > TESTS_NAMED ? `${named.join("; ")}; and ${survivor.tests.length - TESTS_NAMED} more` : named.join("; ");
}

function survivorRow(survivor) {
  return `| ${survivor.line} | ${cell(survivor.mutator)} | \`${cell(survivor.original)}\` to \`${cell(survivor.replacement)}\` | ${testsCell(survivor)} |`;
}

const SURVIVOR_HEADER = ["| Line | Mutator | Change | Tests that ran it |", "|---:|---|---|---|"];

/** The whole list of survivors of one target, a collapsed block per file. */
export function renderClusters(clusters) {
  const lines = [];
  for (const cluster of clusters) {
    lines.push(`<details><summary>${cell(cluster.file)}: ${cluster.survivors.length}</summary>`, "", ...SURVIVOR_HEADER);
    for (const survivor of cluster.survivors) lines.push(survivorRow(survivor));
    lines.push("", "</details>", "");
  }
  return lines.join("\n");
}

/** Judges each target's counts against its floor. `results` maps a target to { counts } or { missing: "why" }. */
export function evaluate(floors, results) {
  const rows = [];
  const problems = [];
  for (const target of Object.keys(floors.floors)) {
    const result = results[target];
    const floor = floors.floors[target].score;
    if (!result || result.missing) {
      problems.push(`${label(target)}: ${result?.missing ?? "no report."}`);
      rows.push({ target, floor, status: "no report" });
      continue;
    }
    const actual = scoreTenth(result.counts);
    const row = { target, floor, actual, counts: result.counts, headroom: Math.round((actual - floor) * 10) / 10, status: "ok" };
    if (actual < floor) {
      row.status = "below floor";
      problems.push(
        `${label(target)} mutation score is ${actual}% (${result.counts.detected} of ${result.counts.judged} changes caught), below its floor of ${floor}%. ` +
          `A change to the code that no test noticed is listed below; add the test that fails on it. The floor is lowered only in a pull request of its own.`,
      );
    }
    rows.push(row);
  }
  return { rows, problems };
}

export function renderSummary({ rows, notes, problems, floors }) {
  const lines = ["### Mutation testing", ""];
  const runs = [...new Set(Object.values(floors.measuredFrom.runs))];
  lines.push(
    `Floors are in docs/coverage/mutation-floors.json, measured from CI run${runs.length === 1 ? "" : "s"} ${runs.join(", ")}. ` +
      "A score below its floor fails this check; a score above it can be raised in the pull request that earned it. " +
      "The score is the share of changes to the code (a flipped condition, a dropped statement, a swapped operator) that made a test fail.",
    "",
    "| Target | Score | Floor | Caught | Survived | Not run by any test | |",
    "|---|---:|---:|---:|---:|---:|---|",
  );
  for (const row of rows) {
    if (!row.counts) {
      lines.push(`| ${row.target} | - | ${row.floor}% | - | - | - | NO REPORT |`);
      continue;
    }
    const verdict = row.status === "ok" ? (row.headroom >= 0.1 ? "ok, floor can rise" : "ok") : "BELOW FLOOR";
    lines.push(`| ${row.target} | ${row.actual}% | ${row.floor}% | ${row.counts.detected} of ${row.counts.judged} | ${row.counts.survived} | ${row.counts.noCoverage} | ${verdict} |`);
  }
  for (const note of notes) lines.push("", `- ${note}`);
  for (const problem of problems) lines.push("", `**${problem}**`);
  return `${lines.join("\n")}\n`;
}

/** The survivors of every target for the job summary: the clusters first, then as much of the list as fits. */
export function renderSurvivorSummary(targets) {
  const lines = [];
  let budget = SUMMARY_BUDGET;
  for (const { target, survivors } of targets) {
    if (survivors.length === 0) continue;
    const clusters = clustersOf(survivors);
    lines.push(`### Survivors: ${target}`, "", `${survivors.length} changes no test noticed, in ${clusters.length} files.`, "", "| File | Survivors | Most common mutators |", "|---|---:|---|");
    for (const cluster of clusters) lines.push(`| ${cell(cluster.file)} | ${cluster.survivors.length} | ${topMutators(cluster)} |`);
    lines.push("");
    const detail = renderClusters(clusters);
    if (detail.length <= budget) {
      lines.push(detail);
      budget -= detail.length;
    } else {
      lines.push(`The line-by-line list for ${target} is too long for this page; it is in survivors.md in the mutation-survivors artifact.`, "");
    }
  }
  return lines.join("\n");
}

function readJson(file) {
  return JSON.parse(readFileSync(file, "utf8"));
}

/** Reads one target's report; returns its counts and survivors, or { missing } saying which file was not there. */
export function loadTarget(target, folder) {
  const file = path.join(folder, `mutation-${target}`, "mutation-report.json");
  if (!existsSync(file)) {
    return { result: { missing: `${file} was not written, so there is nothing to compare with the floor. Look at the target's job for why.` }, survivors: [] };
  }
  const report = readJson(file);
  const counts = countMutants(report);
  if (counts.judged === 0) return { result: { missing: `${file} holds no mutants that could be judged; the run mutated nothing.` }, survivors: [] };
  if (counts.timeout > counts.judged * TIMEOUT_SHARE) {
    return {
      result: { missing: `${counts.timeout} of ${counts.judged} changes ended by timing out, which scores them as caught. Raise the target's time limit (Stryker's timeoutFactor or timeoutMS) before trusting the score.` },
      survivors: [],
    };
  }
  return { result: { counts }, survivors: survivorsOf(report) };
}

/** The floors file read and, when there is a base to compare with, held to the rule that a floor only goes up. */
export function checkFloorsFile({ floorsFile, baseFloorsFile }) {
  const floors = readFloors(readFileSync(floorsFile, "utf8"));
  if (!baseFloorsFile || !existsSync(baseFloorsFile)) return { floors, problems: [], notes: [] };
  return { floors, ...loweredFloorProblems(floors, readJson(baseFloorsFile)) };
}

/** The floors of only the named targets, for a run that mutated only those; an unknown name is a mistake, not "nothing to check". */
export function onlyTargets(floors, only) {
  const unknown = only.filter((target) => !floors.floors[target]);
  if (unknown.length) throw new Error(`--only names ${unknown.join(", ")}, which ${unknown.length === 1 ? "has" : "have"} no floor in the floors file.`);
  return { ...floors, floors: Object.fromEntries(only.map((target) => [target, floors.floors[target]])) };
}

export function run({ floorsFile, baseFloorsFile, reportsFolder, only }) {
  const checked = checkFloorsFile({ floorsFile, baseFloorsFile });
  const { problems: loweredProblems, notes } = checked;
  const floors = only ? onlyTargets(checked.floors, only) : checked.floors;
  const results = {};
  const targets = [];
  for (const target of Object.keys(floors.floors)) {
    const loaded = loadTarget(target, reportsFolder);
    results[target] = loaded.result;
    targets.push({ target, survivors: loaded.survivors });
  }
  const { rows, problems } = evaluate(floors, results);
  problems.push(...loweredProblems);
  return { text: renderSummary({ rows, notes, problems, floors }), survivorText: renderSurvivorSummary(targets), problems, rows, targets };
}

/** The survivors of the targets that are below their floor, to name in the log and as annotations. */
export function failingSurvivors(rows, targets) {
  const failing = new Set(rows.filter((row) => row.status === "below floor").map((row) => row.target));
  return targets.filter((item) => failing.has(item.target));
}

export function parseArguments(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index];
    if (name === "--floors-only") {
      options.floorsOnly = true;
      index -= 1;
      continue;
    }
    const value = argv[index + 1];
    if (value === undefined) throw new Error(`${name} needs a value.`);
    if (name === "--floors") options.floorsFile = value;
    else if (name === "--base-floors") options.baseFloorsFile = value;
    else if (name === "--reports") options.reportsFolder = value;
    else if (name === "--out") options.outFolder = value;
    else if (name === "--only") options.only = value.split(",").map((target) => target.trim()).filter(Boolean);
    else throw new Error(`Unknown option ${name}.`);
  }
  if (!options.floorsFile) throw new Error("--floors <file> is required.");
  if (!options.reportsFolder && !options.floorsOnly) throw new Error("--reports <folder> is required, or --floors-only.");
  return options;
}

/** Checks the reports (or only the floors file) named in `argv`; returns the exit code. */
export function main(argv = process.argv.slice(2), { env = process.env, log = console.log, error = console.error } = {}) {
  let options;
  let result;
  try {
    options = parseArguments(argv);
    if (options.floorsOnly) {
      const { problems, notes } = checkFloorsFile(options);
      for (const note of notes) log(note);
      for (const problem of problems) error(`::error::${problem}`);
      return problems.length ? 1 : 0;
    }
    result = run(options);
  } catch (failure) {
    error(`::error::${failure.message}`);
    return 1;
  }
  log(result.text);
  log(result.survivorText);
  if (options.outFolder) {
    mkdirSync(options.outFolder, { recursive: true });
    writeFileSync(path.join(options.outFolder, "survivors.md"), `${result.survivorText}\n`);
    writeFileSync(
      path.join(options.outFolder, "survivors.json"),
      `${JSON.stringify(Object.fromEntries(result.targets.map((item) => [item.target, item.survivors])), null, 2)}\n`,
    );
  }
  if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, `${result.text}\n${result.survivorText}\n`);
  if (result.problems.length) {
    for (const problem of result.problems) error(`::error::${problem}`);
    let annotated = 0;
    for (const { target, survivors } of failingSurvivors(result.rows, result.targets)) {
      for (const survivor of survivors) {
        const where = `${target}: ${survivor.file}:${survivor.line} ${survivor.mutator}, \`${survivor.original}\` to \`${survivor.replacement}\``;
        if (annotated < ANNOTATIONS) error(`::error::Survived ${where}`);
        else error(`Survived ${where}`);
        annotated += 1;
      }
    }
    return 1;
  }
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) process.exitCode = main();
