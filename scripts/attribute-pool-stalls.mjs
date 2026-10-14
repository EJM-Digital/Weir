#!/usr/bin/env node
// Lays the thread pool stalls a test run reported beside the tests that were running at the time.
//
// Every test class in a test assembly runs at once on one process-wide thread pool, so a test that holds pool threads
// (a barrier or a blocking wait on a pool thread, a sleep, a process wait) delays every other test and the server code
// under test. The infrastructure tests can report when the pool was slow to begin work: set WEIR_POOL_SAMPLE to a file
// (see apps/server/README.md) and run the tests with a trx logger. This reads both and prints, for each stall, the tests
// that overlapped it, longest first. A stall of two seconds or more is a bug in the tests listed first.
//
// Usage: node scripts/attribute-pool-stalls.mjs <run.trx> <pool.csv> [minimum stall in ms, default 1500]
import fs from "node:fs";

const [trxPath, csvPath, minimumMs = "1500"] = process.argv.slice(2);
if (!trxPath || !csvPath) {
  console.error(
    "Usage: node scripts/attribute-pool-stalls.mjs <run.trx> <pool.csv> [minimum stall in ms]",
  );
  process.exit(2);
}

const attributes = (tag) =>
  Object.fromEntries(
    [...tag.matchAll(/(\w+)="([^"]*)"/g)].map(([, name, value]) => [
      name,
      value,
    ]),
  );

const tests = [
  ...fs.readFileSync(trxPath, "utf8").matchAll(/<UnitTestResult\b[^>]*>/g),
]
  .map(([tag]) => attributes(tag))
  .map((a) => ({
    name: a.testName,
    start: Date.parse(a.startTime),
    end: Date.parse(a.endTime),
  }))
  .filter((test) => test.start && test.end);

const stalls = fs
  .readFileSync(csvPath, "utf8")
  .split("\n")
  .filter((line) => line.trim())
  .map((line) => {
    const [start, end, waitedMs, threads, pending, oversleep] = line.split(",");
    return {
      start: Date.parse(start),
      end: Date.parse(end),
      waitedMs: Number(waitedMs),
      threads,
      pending,
      oversleep,
    };
  })
  .filter((stall) => stall.waitedMs >= Number(minimumMs));

if (stalls.length === 0) {
  console.log(`No stall of ${minimumMs} ms or more.`);
}

for (const stall of stalls) {
  console.log(
    `\nSTALL ${new Date(stall.start).toISOString()} ${stall.waitedMs} ms (pool threads ${stall.threads}, pending ${stall.pending}, the sampler itself woke ${stall.oversleep} ms late)`,
  );
  const overlapping = tests
    .filter((test) => test.start < stall.end && test.end > stall.start)
    .map((test) => ({ ...test, duration: test.end - test.start }))
    .sort((a, b) => b.duration - a.duration);
  for (const test of overlapping.slice(0, 14)) {
    console.log(`  ${String(test.duration).padStart(6)} ms  ${test.name}`);
  }
  console.log(`  (${overlapping.length} tests overlapped)`);
}
