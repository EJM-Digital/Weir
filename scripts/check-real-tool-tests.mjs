#!/usr/bin/env node
// Fails a server test leg whose real-tool tests did not all run. The real-tool tests (RealFfmpegTests,
// MkvmergeRealTests and RemuxPassRealFfmpegTests, in Weir.Infrastructure.Tests) skip themselves when ffmpeg, ffprobe
// or mkvmerge cannot be found, and a skip reads as a pass in the job's result. WEIR_REQUIRE_REAL_TOOLS=1 turns that
// skip into a failure inside the tests; this reads the TRX files too, so a leg that stops reporting the tests, or
// reports them skipped, cannot go green either.
//
// The one allowed skip is the Windows-only named-pipe test off Windows.
//
//   node scripts/check-real-tool-tests.mjs <folder with the *.trx files> <linux|windows> [title]
//
// The text goes to $GITHUB_STEP_SUMMARY when that is set, and to the log either way; the exit code is 1 on a failure.
import { appendFileSync, readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { findTrx, readTrxResults } from "./summarize-test-results.mjs";

export const realToolTestClasses = [
  "Weir.Infrastructure.Tests.Media.RealFfmpegTests",
  "Weir.Infrastructure.Tests.Media.MkvmergeRealTests",
  "Weir.Infrastructure.Tests.Processing.RemuxPass.RemuxPassRealFfmpegTests",
];

/** How many real-tool tests the server tests hold; raise it when one is added, so a deleted one cannot go unnoticed. */
export const minimumRealToolTests = 31;

/** The reason RequiresFfmpegOnWindowsFactAttribute gives off Windows. */
export const windowsOnlySkipReason = "Windows named pipes are the mechanism this test blocks ffmpeg on.";

const isRealToolTest = (result) => realToolTestClasses.some((className) => result.name.startsWith(`${className}.`));

/** Judges the results of a leg: `{ failures, summary }`, with no failures when every real-tool test ran. */
export function checkRealToolTests(results, platform) {
  const tests = results.filter(isRealToolTest);
  const passed = tests.filter((test) => test.outcome === "Passed").length;
  const failed = tests.filter((test) => test.outcome === "Failed").length;
  const skipped = tests.filter((test) => test.outcome === "NotExecuted");
  const allowed = (test) => platform !== "windows" && test.message === windowsOnlySkipReason;

  const failures = [];
  if (tests.length < minimumRealToolTests) {
    failures.push(`Only ${tests.length} real-tool tests were reported; the server tests hold at least ${minimumRealToolTests}.`);
  }
  for (const test of skipped.filter((candidate) => !allowed(candidate))) {
    failures.push(`${test.name} was skipped: ${test.message || "no reason was given"}`);
  }
  const summary = `${passed} passed, ${failed} failed, ${skipped.length} skipped, of ${tests.length} real-tool tests.`;
  return { failures, summary };
}

function main() {
  const [folder, platform, title = `Real-tool tests (${platform})`] = process.argv.slice(2);
  if (!folder || (platform !== "linux" && platform !== "windows")) {
    console.error("Usage: check-real-tool-tests.mjs <folder with the *.trx files> <linux|windows> [title]");
    return 2;
  }

  const files = findTrx(folder);
  const results = files.flatMap((file) => readTrxResults(readFileSync(file, "utf8")));
  const { failures, summary } = checkRealToolTests(results, platform);
  const lines = [`### ${title}`, "", files.length === 0 ? "No test result files were written." : summary];
  if (failures.length) lines.push("", "The real-tool tests did not all run:", "", ...failures.map((failure) => `- ${failure}`));
  const text = `${lines.join("\n")}\n`;
  console.log(text);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${text}\n`);
  return failures.length ? 1 : 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.exitCode = main();
}
