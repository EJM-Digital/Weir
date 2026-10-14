#!/usr/bin/env node
// Writes a Markdown summary of test results for a CI job's summary page.
//
//   node scripts/summarize-test-results.mjs trx <folder> [title]       every *.trx under <folder> (dotnet test)
//   node scripts/summarize-test-results.mjs playwright <file> [title]  a Playwright JSON report
//
// The text goes to $GITHUB_STEP_SUMMARY when that is set, and to the log either way. This never fails the step:
// the test step already carries the verdict, and a summary that cannot be read must not hide a real failure.
import { appendFileSync, existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const MAX_FAILURES = 25;

function attributes(tag) {
  const result = {};
  for (const match of tag.matchAll(/([A-Za-z:]+)="([^"]*)"/g)) result[match[1]] = match[2];
  return result;
}

function decode(text) {
  return text
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&");
}

/** Every result in a TRX file: `{ name, outcome, message }`, the message being the error or the reason a test was skipped. */
export function readTrxResults(text) {
  const results = [];
  for (const match of text.matchAll(/<UnitTestResult\b[^>]*?\/>|<UnitTestResult\b[^>]*>[\s\S]*?<\/UnitTestResult>/g)) {
    const result = attributes(match[0].slice(0, match[0].indexOf(">") + 1));
    const message = (match[0].match(/<Message>([\s\S]*?)<\/Message>/) ?? [])[1] ?? "";
    results.push({ name: decode(result.testName ?? "(unnamed test)"), outcome: result.outcome, message: decode(message).trim() });
  }
  return results;
}

export function readTrx(text) {
  const counters = attributes((text.match(/<Counters\b[^>]*>/) ?? [""])[0]);
  const results = readTrxResults(text);
  const failures = results
    .filter((result) => result.outcome === "Failed")
    .map((result) => ({ name: result.name, message: result.message.split(/\r?\n/)[0].slice(0, 300) }));
  const number = (name) => Number(counters[name] ?? 0);
  // dotnet test counts a skipped test in the total but leaves the notExecuted counter at 0, so the results say how many skipped.
  const skipped = results.filter((result) => result.outcome === "NotExecuted").length;
  return { total: number("total"), passed: number("passed"), failed: number("failed"), skipped, failures };
}

export function findTrx(folder) {
  const found = [];
  const walk = (current) => {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      if (entry.name === "node_modules" || entry.name === ".git") continue;
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (entry.name.toLowerCase().endsWith(".trx")) found.push(full);
    }
  };
  if (existsSync(folder) && statSync(folder).isDirectory()) walk(folder);
  return found.sort();
}

export function summarizeTrx(folder, title = "Backend tests") {
  const files = findTrx(folder);
  if (files.length === 0) return `### ${title}\n\nNo test result files were written.\n`;
  const rows = [];
  const failures = [];
  const totals = { total: 0, passed: 0, failed: 0, skipped: 0 };
  for (const file of files) {
    const run = readTrx(readFileSync(file, "utf8"));
    // <project>/TestResults/<file>.trx
    const project = path.basename(path.dirname(path.dirname(file)));
    rows.push(`| ${project} | ${run.total} | ${run.passed} | ${run.failed} | ${run.skipped} |`);
    for (const key of Object.keys(totals)) totals[key] += run[key];
    for (const failure of run.failures) failures.push({ project, ...failure });
  }
  const lines = [
    `### ${title}`,
    "",
    `${totals.passed} passed, ${totals.failed} failed, ${totals.skipped} skipped, of ${totals.total} tests in ${files.length} project(s).`,
    "",
    "| Project | Tests | Passed | Failed | Skipped |",
    "|---|---:|---:|---:|---:|",
    ...rows,
  ];
  if (failures.length) {
    lines.push("", "Failed tests:", "");
    for (const failure of failures.slice(0, MAX_FAILURES)) {
      lines.push(`- \`${failure.project}\`: ${failure.name}${failure.message ? ` - ${failure.message}` : ""}`);
    }
    if (failures.length > MAX_FAILURES) lines.push(`- ... and ${failures.length - MAX_FAILURES} more (see the TRX artifact).`);
  }
  return `${lines.join("\n")}\n`;
}

export function summarizePlaywright(file, title = "Browser smoke tests") {
  if (!existsSync(file)) return `### ${title}\n\nNo Playwright result file was written.\n`;
  let report;
  try {
    report = JSON.parse(readFileSync(file, "utf8"));
  } catch {
    return `### ${title}\n\nThe Playwright result file could not be read.\n`;
  }
  const failed = [];
  const flaky = [];
  const walk = (suite, trail) => {
    const here = suite.title && !suite.title.endsWith(".ts") && !suite.title.endsWith(".tsx") ? [...trail, suite.title] : trail;
    for (const spec of suite.specs ?? []) {
      const name = [...here, spec.title].join(" > ");
      for (const test of spec.tests ?? []) {
        const label = `${name} [${test.projectName ?? "default"}]`;
        if (test.status === "unexpected") failed.push({ label, file: spec.file });
        else if (test.status === "flaky") flaky.push({ label, file: spec.file });
      }
    }
    for (const child of suite.suites ?? []) walk(child, here);
  };
  for (const suite of report.suites ?? []) walk(suite, []);
  const stats = report.stats ?? {};
  const seconds = Math.round((stats.duration ?? 0) / 1000);
  const lines = [
    `### ${title}`,
    "",
    `${stats.expected ?? 0} passed, ${stats.unexpected ?? 0} failed, ${stats.flaky ?? 0} flaky, ${stats.skipped ?? 0} skipped in ${seconds} s.`,
  ];
  if (failed.length) {
    lines.push("", "Failed:", "");
    for (const item of failed.slice(0, MAX_FAILURES)) lines.push(`- ${item.label} (${item.file})`);
    if (failed.length > MAX_FAILURES) lines.push(`- ... and ${failed.length - MAX_FAILURES} more.`);
  }
  if (flaky.length) {
    // Every one is listed, never cut short: a test that needed its retry is a defect to fix, not a pass to forget.
    lines.push("", "Flaky (passed on retry):", "");
    for (const item of flaky) lines.push(`- ${item.label} (${item.file})`);
  }
  return `${lines.join("\n")}\n`;
}

function main() {
  const [kind, target, title] = process.argv.slice(2);
  let text;
  if (kind === "trx" && target) text = summarizeTrx(target, title);
  else if (kind === "playwright" && target) text = summarizePlaywright(target, title);
  else {
    console.error("Usage: summarize-test-results.mjs trx <folder> [title] | playwright <file> [title]");
    return;
  }
  console.log(text);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${text}\n`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    console.error(`Could not summarize test results: ${error.message}`);
  }
}
