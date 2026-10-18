#!/usr/bin/env node
// The coverage ratchet (#976): fails when a project's measured line or branch coverage is below its floor in
// docs/coverage/floors.json, and writes the figures and the least-covered files to a CI job's summary page.
//
//   node scripts/check-coverage-floors.mjs --floors docs/coverage/floors.json \
//     [--base-floors <floors.json as it is on the base branch>] \
//     [--server <folder>] [--out-of-process <folder>] [--tray <folder>] [--web <folder>]
//
// <folder> for the server, the out-of-process server figure (#1081) and the tray is a ReportGenerator output folder
// (Summary.json and Cobertura.xml: the per-leg Cobertura files merged into one report); for the web app it is vitest's coverage folder
// (coverage-summary.json). A project whose folder is not passed was not due for this change and is shown as not
// run; a project whose folder is passed but holds no report fails, so a test run that wrote nothing is never read
// as "nothing to check".
//
// A floor only goes up. One that is lower than on the base branch fails unless the file carries a `lowered` entry
// for it that names the old and the new figure and says why; that entry belongs in its own pull request.
//
// The text goes to $GITHUB_STEP_SUMMARY when that is set, and to the log either way.
import { appendFileSync, existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

export const MEASURES = ["line", "branch"];

// `report` is the folder the project's figures come from; `assembly` is the name in a ReportGenerator summary;
// `folder` is the source folder a coverlet file name is relative to.
export const PROJECTS = [
  { id: "server-core", label: "Server: Weir.Core", report: "server", assembly: "Weir.Core", folder: "Weir.Core" },
  { id: "server-infrastructure", label: "Server: Weir.Infrastructure", report: "server", assembly: "Weir.Infrastructure", folder: "Weir.Infrastructure" },
  { id: "server-api", label: "Server: Weir.Api", report: "server", assembly: "Weir.Api", folder: "Weir.Api" },
  { id: "server-host", label: "Server: Weir.Host", report: "server", assembly: "Weir", folder: "Weir.Host" },
  // The server as the contract suite and the E2E smoke run it, measured by dotnet-coverage: a figure of its own, because
  // that tool counts lines and branches differently from coverlet.
  { id: "out-of-process-core", label: "Out of process: Weir.Core", report: "out-of-process", assembly: "Weir.Core", folder: "Weir.Core" },
  { id: "out-of-process-infrastructure", label: "Out of process: Weir.Infrastructure", report: "out-of-process", assembly: "Weir.Infrastructure", folder: "Weir.Infrastructure" },
  { id: "out-of-process-api", label: "Out of process: Weir.Api", report: "out-of-process", assembly: "Weir.Api", folder: "Weir.Api" },
  { id: "out-of-process-host", label: "Out of process: Weir.Host", report: "out-of-process", assembly: "Weir", folder: "Weir.Host" },
  { id: "tray", label: "Tray", report: "tray", assembly: "Weir", folder: "Weir.Tray" },
  { id: "web", label: "Web app", report: "web" },
];

const TEN = 10;

export function percent(figure) {
  return figure.total === 0 ? 100 : (figure.covered / figure.total) * 100;
}

/** The figure rounded down to one decimal, which is how floors are written: 91.74 is 91.7, never 91.8. */
export function floorTenth(figure) {
  return figure.total === 0 ? 100 : Math.floor((figure.covered * 1000) / figure.total) / 10;
}

export function readFloors(text) {
  let file;
  try {
    file = JSON.parse(text);
  } catch (error) {
    throw new Error(`The floors file is not valid JSON: ${error.message}`);
  }
  const problems = [];
  if (!Number.isInteger(file.measuredFrom?.run) || file.measuredFrom.run <= 0) {
    problems.push('"measuredFrom.run" must be the id of the CI run the starting floors were measured from.');
  }
  for (const project of PROJECTS) {
    for (const measure of MEASURES) {
      const value = file.floors?.[project.id]?.[measure];
      if (typeof value !== "number" || !(value >= 0 && value <= 100)) {
        problems.push(`"floors.${project.id}.${measure}" must be a percentage from 0 to 100.`);
      }
    }
  }
  if (problems.length) throw new Error(`The floors file is not usable:\n- ${problems.join("\n- ")}`);
  return file;
}

/** A floor lower than the base branch's needs its own `lowered` entry, with the old and new figure and a reason. */
export function loweredFloorProblems(floors, base) {
  const problems = [];
  const notes = [];
  for (const project of PROJECTS) {
    for (const measure of MEASURES) {
      const was = base.floors?.[project.id]?.[measure];
      const now = floors.floors[project.id][measure];
      if (typeof was !== "number") continue;
      if (now > was) notes.push(`${project.label} ${measure} floor raised from ${was}% to ${now}%.`);
      if (now >= was) continue;
      const key = `${project.id}.${measure}`;
      const entry = floors.lowered?.[key];
      const reason = typeof entry?.reason === "string" ? entry.reason.trim() : "";
      if (entry && entry.from === was && entry.to === now && reason) {
        notes.push(`${project.label} ${measure} floor LOWERED from ${was}% to ${now}%: ${reason}`);
        continue;
      }
      problems.push(
        `${project.label} ${measure} floor was lowered from ${was}% to ${now}%. A floor only goes down in a pull request of its own, ` +
          `with the reason written under "lowered" in docs/coverage/floors.json: "${key}": { "from": ${was}, "to": ${now}, "reason": "..." }.`,
      );
    }
  }
  return { problems, notes };
}

function figuresOf(lineCovered, lineTotal, branchCovered, branchTotal) {
  return { line: { covered: lineCovered, total: lineTotal }, branch: { covered: branchCovered, total: branchTotal } };
}

/** The figures of one assembly in a ReportGenerator JsonSummary, or undefined when it is not in the report. */
export function figuresFromSummary(summary, assemblyName) {
  const assembly = (summary.coverage?.assemblies ?? []).find((item) => item.name === assemblyName);
  if (!assembly) return undefined;
  return figuresOf(assembly.coveredlines, assembly.coverablelines, assembly.coveredbranches, assembly.totalbranches);
}

/** The whole-app figures of a vitest json-summary file. */
export function figuresFromVitest(summary) {
  const total = summary.total;
  if (!total?.lines || !total?.branches) return undefined;
  return figuresOf(total.lines.covered, total.lines.total, total.branches.covered, total.branches.total);
}

function slashed(file) {
  return file.replace(/\\/g, "/");
}

/** `Weir.Host/Program.cs` for either spelling of a server or tray file name coverlet writes. */
export function sourceName(file, folder) {
  const name = slashed(file);
  for (const root of ["/apps/server/src/", "/apps/tray/", "/apps/web/"]) {
    const at = name.lastIndexOf(root);
    if (at >= 0) return name.slice(at + root.length);
  }
  return /^([A-Za-z]:)?\//.test(name) ? name : `${folder}/${name}`;
}

function attribute(tag, name) {
  const match = tag.match(new RegExp(`\\b${name}="([^"]*)"`));
  return match ? match[1] : undefined;
}

/**
 * The covered and coverable lines and branches of every file in a Cobertura report, keyed by assembly and file.
 * Only a class's own `<lines>` block is counted: the lines repeated under each of its methods are the same lines.
 */
export function filesFromCobertura(text) {
  const files = new Map();
  for (const packageChunk of text.split('<package name="').slice(1)) {
    const assembly = packageChunk.slice(0, packageChunk.indexOf('"'));
    for (const classChunk of packageChunk.split("<class ").slice(1)) {
      const filename = attribute(classChunk.slice(0, classChunk.indexOf(">")), "filename");
      if (filename === undefined) continue;
      const afterMethods = classChunk.lastIndexOf("</methods>");
      const start = classChunk.indexOf("<lines>", Math.max(afterMethods, 0));
      if (start < 0) continue;
      const block = classChunk.slice(start, classChunk.indexOf("</lines>", start));
      const key = `${assembly}\u0000${filename}`;
      const file = files.get(key) ?? { assembly, filename, line: { covered: 0, total: 0 }, branch: { covered: 0, total: 0 } };
      for (const lineTag of block.match(/<line\b[^>]*>/g) ?? []) {
        file.line.total += 1;
        if (Number(attribute(lineTag, "hits")) > 0) file.line.covered += 1;
        const condition = (attribute(lineTag, "condition-coverage") ?? "").match(/\((\d+)\/(\d+)\)/);
        if (condition) {
          file.branch.covered += Number(condition[1]);
          file.branch.total += Number(condition[2]);
        }
      }
      files.set(key, file);
    }
  }
  return [...files.values()];
}

/** The files of a vitest json-summary file. */
export function filesFromVitest(summary) {
  return Object.entries(summary)
    .filter(([name]) => name !== "total")
    .map(([name, value]) => ({
      filename: name,
      line: { covered: value.lines.covered, total: value.lines.total },
      branch: { covered: value.branches.covered, total: value.branches.total },
    }));
}

/** The files with the most uncovered lines and branches first: where the next test pays back the most. */
export function leastCovered(files, folder, count = TEN) {
  return files
    .map((file) => ({ ...file, name: sourceName(file.filename, folder) }))
    .map((file) => ({ ...file, missing: file.line.total - file.line.covered + (file.branch.total - file.branch.covered) }))
    .filter((file) => file.missing > 0)
    .sort((a, b) => b.missing - a.missing || a.name.localeCompare(b.name))
    .slice(0, count);
}

/**
 * Judges the measured figures against the floors.
 * `figures` maps a project id to { line, branch } (each { covered, total }), to { missing: "why" } for a report that
 * should be there and is not, or has no entry for a project that was not run.
 */
export function evaluate(floors, figures) {
  const rows = [];
  const problems = [];
  for (const project of PROJECTS) {
    const measured = figures[project.id];
    if (!measured) {
      rows.push({ project, status: "not run" });
      continue;
    }
    if (measured.missing) {
      problems.push(`${project.label}: ${measured.missing}`);
      rows.push({ project, status: "no report" });
      continue;
    }
    const row = { project, status: "ok", measures: {} };
    for (const measure of MEASURES) {
      const floor = floors.floors[project.id][measure];
      const actual = floorTenth(measured[measure]);
      row.measures[measure] = { figure: measured[measure], actual, floor, headroom: Math.round((actual - floor) * 10) / 10 };
      if (actual < floor) {
        row.status = "below floor";
        problems.push(
          `${project.label} ${measure} coverage is ${actual}% (${measured[measure].covered} of ${measured[measure].total}), below its floor of ${floor}%. ` +
            `Add tests for the code that lost coverage; the floor is lowered only in a pull request of its own.`,
        );
      }
    }
    rows.push(row);
  }
  return { rows, problems };
}

function cell(measure) {
  return `${measure.actual}% (${measure.figure.covered}/${measure.figure.total})`;
}

function tableOf(rows) {
  const lines = ["| Project | Lines | Floor | Branches | Floor | |", "|---|---:|---:|---:|---:|---|"];
  for (const row of rows) {
    if (!row.measures) {
      lines.push(`| ${row.project.label} | - | - | - | - | ${row.status === "not run" ? "not run for this change" : "NO REPORT"} |`);
      continue;
    }
    const { line, branch } = row.measures;
    const verdict = row.status === "ok" ? (line.headroom >= 0.1 || branch.headroom >= 0.1 ? "ok, floor can rise" : "ok") : "BELOW FLOOR";
    lines.push(`| ${row.project.label} | ${cell(line)} | ${line.floor}% | ${cell(branch)} | ${branch.floor}% | ${verdict} |`);
  }
  return lines;
}

export function renderSummary({ rows, notes, problems, targets, floors }) {
  const outOfProcess = (row) => row.project.report === "out-of-process";
  const lines = ["### Test coverage", ""];
  lines.push(
    `Floors are in docs/coverage/floors.json, measured from CI run ${floors.measuredFrom.run}. A figure below its floor fails this check; a figure above it can be raised in the pull request that earned it.`,
    "",
    ...tableOf(rows.filter((row) => !outOfProcess(row))),
    "",
    "#### Out of process: the contract suite and the E2E smoke",
    "",
    "The server as those suites run it, as its own process, measured by dotnet-coverage. The two tools count lines and branches differently, so this figure is held to its own floors and is not added to the one above.",
    "",
    ...tableOf(rows.filter(outOfProcess)),
  );
  for (const note of notes) lines.push("", `- ${note}`);
  for (const problem of problems) lines.push("", `**${problem}**`);
  for (const target of targets) {
    if (target.files.length === 0) continue;
    lines.push("", `#### Least covered in ${target.project.label}`, "", "| File | Lines not run | Branches not run | Line coverage |", "|---|---:|---:|---:|");
    for (const file of target.files) {
      const uncoveredLines = file.line.total - file.line.covered;
      const uncoveredBranches = file.branch.total - file.branch.covered;
      lines.push(`| ${file.name} | ${uncoveredLines} | ${uncoveredBranches} | ${floorTenth(file.line)}% |`);
    }
  }
  return `${lines.join("\n")}\n`;
}

function readJson(file) {
  return JSON.parse(readFileSync(file, "utf8"));
}

// The server report is read once for its four projects.
const fileReports = new Map();
function coberturaFiles(file) {
  if (!fileReports.has(file)) fileReports.set(file, filesFromCobertura(readFileSync(file, "utf8")));
  return fileReports.get(file);
}

/** Reads one project's report folder; returns the figures, or { missing } saying which file was not there. */
export function loadFigures(project, folders) {
  const folder = folders[project.report];
  if (folder === undefined) return { figures: undefined };
  const summaryFile = path.join(folder, project.report === "web" ? "coverage-summary.json" : "Summary.json");
  if (!existsSync(summaryFile)) {
    return { figures: { missing: `${summaryFile} was not written, so there is nothing to compare with the floor. Look at the test step above for why.` }, files: [] };
  }
  const summary = readJson(summaryFile);
  if (project.report === "web") {
    const figures = figuresFromVitest(summary);
    return { figures: figures ?? { missing: `${summaryFile} has no totals.` }, files: filesFromVitest(summary) };
  }
  const figures = figuresFromSummary(summary, project.assembly);
  if (!figures) return { figures: { missing: `${summaryFile} has no assembly named ${project.assembly}; its tests produced no coverage.` }, files: [] };
  const coberturaFile = path.join(folder, "Cobertura.xml");
  const files = existsSync(coberturaFile) ? coberturaFiles(coberturaFile).filter((file) => file.assembly === project.assembly) : [];
  return { figures, files };
}

export function run({ floorsFile, baseFloorsFile, folders }) {
  const floors = readFloors(readFileSync(floorsFile, "utf8"));
  const figures = {};
  const targets = [];
  for (const project of PROJECTS) {
    const loaded = loadFigures(project, folders);
    if (!loaded.figures) continue;
    figures[project.id] = loaded.figures;
    const belowFull = !loaded.figures.missing && MEASURES.some((measure) => percent(loaded.figures[measure]) < 100);
    if (belowFull) targets.push({ project, files: leastCovered(loaded.files ?? [], project.folder) });
  }
  const { rows, problems } = evaluate(floors, figures);
  const notes = [];
  if (baseFloorsFile && existsSync(baseFloorsFile)) {
    const lowered = loweredFloorProblems(floors, readJson(baseFloorsFile));
    problems.push(...lowered.problems);
    notes.push(...lowered.notes);
  }
  return { text: renderSummary({ rows, notes, problems, targets, floors }), problems };
}

export function parseArguments(argv) {
  const options = { folders: {} };
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index];
    const value = argv[index + 1];
    if (value === undefined) throw new Error(`${name} needs a value.`);
    if (name === "--floors") options.floorsFile = value;
    else if (name === "--base-floors") options.baseFloorsFile = value;
    else if (["--server", "--out-of-process", "--tray", "--web"].includes(name)) options.folders[name.slice(2)] = value;
    else throw new Error(`Unknown option ${name}.`);
  }
  if (!options.floorsFile) throw new Error("--floors <file> is required.");
  return options;
}

function main() {
  let result;
  try {
    result = run(parseArguments(process.argv.slice(2)));
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
  console.log(result.text);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${result.text}\n`);
  if (result.problems.length) {
    for (const problem of result.problems) console.error(`::error::${problem}`);
    process.exit(1);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
