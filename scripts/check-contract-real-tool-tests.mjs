#!/usr/bin/env node
// Fails a contract suite leg whose real-tool scenarios did not all run. The scenarios marked [RealFfmpegFact],
// [RealFfmpegTheory], [RealMkvmergeFact] or [RealFfmpegWindowsFact] (apps/server/tests/Weir.Contract.Tests) skip themselves where
// ffmpeg, ffprobe or mkvmerge cannot be found, and a skip reads as a pass in the leg's result. WEIR_REQUIRE_REAL_TOOLS=1 turns
// that skip into a failure inside the scenario; this reads the TRX files too, so a leg that stops reporting a scenario, or
// reports it skipped, cannot go green either.
//
// The scenarios are read from the test source, so a new one is expected the moment it is written. The one allowed skip is the
// Windows-only scenario off Windows. minimumScenarios is what each area holds today: raise it when a scenario is added, so a
// scenario whose mark is removed cannot go unnoticed.
//
//   node scripts/check-contract-real-tool-tests.mjs <area> <folder with the *.trx files> <linux|windows> [title]
//
// The text goes to $GITHUB_STEP_SUMMARY when that is set, and to the log either way; the exit code is 1 on a failure.
import { appendFileSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { findTrx, readTrxResults } from "./summarize-test-results.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const contractProject = path.join(root, "apps/server/tests/Weir.Contract.Tests");

/** How many real-tool scenarios each contract area holds. */
export const minimumScenarios = { libraries: 26, processing: 3 };

/** The reason RealFfmpegWindowsFactAttribute gives off Windows. */
export const windowsOnlySkipReason = "Only Windows refuses to replace a file another program has open; elsewhere the swap goes ahead.";

const MARK = /\[(RealFfmpegFact|RealFfmpegTheory|RealMkvmergeFact|RealFfmpegWindowsFact)(?:\([^)]*\))?\]\s*(?:\[[^\]]*\]\s*)*(?:public|internal)\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(/g;
const CLASS = /\bclass\s+(\w+)/g;
const AREA = /\[ContractArea\("([^"]+)"\)\]/g;

/** Every real-tool scenario a source file holds: `{ area, name, windowsOnly }`, the name being what the test runner reports. */
export function scenariosIn(source, file = "a source file") {
  const namespace = (source.match(/^namespace\s+([\w.]+)\s*;/m) ?? [])[1];
  if (!namespace) throw new Error(`${file} has real-tool scenarios but no file-scoped namespace.`);
  const classes = [...source.matchAll(CLASS)].map((match) => ({ name: match[1], at: match.index }));
  const areas = [...source.matchAll(AREA)].map((match) => ({ area: match[1], at: match.index }));
  return [...source.matchAll(MARK)].map((match) => {
    const owner = classes.filter((candidate) => candidate.at < match.index).at(-1);
    if (!owner) throw new Error(`${file}: the scenario ${match[2]} is not inside a class.`);
    const area = areas.filter((candidate) => candidate.at < owner.at).at(-1);
    if (!area) throw new Error(`${file}: the class ${owner.name} holds real-tool scenarios but is not marked [ContractArea].`);
    return { area: area.area, name: `${namespace}.${owner.name}.${match[2]}`, windowsOnly: match[1] === "RealFfmpegWindowsFact" };
  });
}

function sourceFiles(folder) {
  return readdirSync(folder, { withFileTypes: true }).flatMap((entry) => {
    if (entry.name === "bin" || entry.name === "obj") return [];
    const full = path.join(folder, entry.name);
    if (entry.isDirectory()) return sourceFiles(full);
    return entry.name.endsWith(".cs") ? [full] : [];
  });
}

/** Every real-tool scenario in the contract project, wherever it is. */
export function allScenarios(project = contractProject) {
  return sourceFiles(project).flatMap((file) => {
    const source = readFileSync(file, "utf8");
    return /\[Real(?:Ffmpeg|Mkvmerge)\w*(?:Fact|Theory)\b/.test(source) ? scenariosIn(source, path.relative(project, file)) : [];
  });
}

const isReported = (scenario) => (result) => result.name === scenario.name || result.name.startsWith(`${scenario.name}(`);

/** Judges the results of one area's leg: `{ failures, summary }`, with no failures when every real-tool scenario of the area ran. */
export function checkContractRealToolTests(results, scenarios, area, platform) {
  const expected = scenarios.filter((scenario) => scenario.area === area);
  const failures = [];
  const minimum = minimumScenarios[area] ?? 0;
  if (expected.length < minimum) {
    failures.push(`Only ${expected.length} real-tool scenarios are marked in the ${area} area; it holds at least ${minimum}.`);
  }

  let passed = 0;
  let failed = 0;
  let skipped = 0;
  for (const scenario of expected) {
    const reported = results.filter(isReported(scenario));
    if (reported.length === 0) {
      failures.push(`${scenario.name} was not reported.`);
      continue;
    }
    for (const result of reported) {
      if (result.outcome === "Passed") passed++;
      else if (result.outcome === "Failed") failed++;
      else {
        skipped++;
        const allowed = scenario.windowsOnly && platform !== "windows" && result.message === windowsOnlySkipReason;
        if (!allowed) failures.push(`${result.name} was skipped: ${result.message || "no reason was given"}`);
      }
    }
  }
  return { failures, summary: `${passed} passed, ${failed} failed, ${skipped} skipped, of ${expected.length} real-tool scenarios in the ${area} area.` };
}

function main() {
  const [area, folder, platform, title = `Real-tool scenarios (${area})`] = process.argv.slice(2);
  if (!area || !folder || (platform !== "linux" && platform !== "windows")) {
    console.error("Usage: check-contract-real-tool-tests.mjs <area> <folder with the *.trx files> <linux|windows> [title]");
    return 2;
  }

  const files = findTrx(folder);
  const results = files.flatMap((file) => readTrxResults(readFileSync(file, "utf8")));
  const { failures, summary } =
    files.length === 0
      ? { failures: ["No test result files were written."], summary: "No test result files were written." }
      : checkContractRealToolTests(results, allScenarios(), area, platform);
  const lines = [`### ${title}`, "", summary];
  if (failures.length) lines.push("", "The real-tool scenarios did not all run:", "", ...failures.map((failure) => `- ${failure}`));
  const text = `${lines.join("\n")}\n`;
  console.log(text);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${text}\n`);
  return failures.length ? 1 : 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.exitCode = main();
}
