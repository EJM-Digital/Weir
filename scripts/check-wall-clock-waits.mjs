#!/usr/bin/env node
// Fails when a test or a test script waits for the wall clock: a sleep, a delay or a guessed timeout. A test that does
// that proves the thing it waits for happened within the time the author guessed, and is intermittent on any machine
// slower than theirs. Wait for a signal (an event, a state the page or the API shows), move a fake clock, or poll with a
// ceiling that is named as a hang guard.
//
// What is looked for, and where:
//
//   csharp   Task.Delay, Thread.Sleep and Playwright's WaitForTimeoutAsync in the server's Api and contract tests, its test tools, the browser tests and the tray's tests
//   web      a bare `new Promise(... setTimeout ...)` sleep, a clock that keeps running (`shouldAdvanceTime`), and a waitFor or
//            findBy `timeout:` over 1000 ms with no comment naming it a hang guard, in the web app's tests
//   script   Start-Sleep in the PowerShell scripts (the VM scenarios, the package smoke, the packaging scripts)
//   node     a setTimeout sleep in a Node script's own test
//
// A wait that has to stay is listed in scripts/wall-clock-waits-allowed.json with the file, the kind, how many of them there
// are and why each is not a guess: nearly always a poll that ends the moment its condition holds and gives up at a ceiling. An
// entry that no longer matches (the wait is gone, or there are fewer) fails too, so the list only shrinks.
//
// Usage: node scripts/check-wall-clock-waits.mjs
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

// Which files are looked at, and for what. A new test project is added here.
const WATCHED = [
  { kind: "csharp", files: /^apps\/(?:server\/tests\/Weir\.E2E\.Tests|tray\/Weir\.Tray\.Tests)\/.+\.cs$/ },
  { kind: "web", files: /^apps\/web\/src\/(?:.+\.test\.tsx?|test\/.+|.+-(?:test-server|test-fixtures|test-support)\.tsx?)$/ },
  { kind: "csharp", files: /^apps\/server\/tests\/Weir\.Api\.Tests\/.+\.cs$/ },
  { kind: "script", files: /^(?:scripts|packaging)\/.+\.ps1$/ },
  { kind: "csharp", files: /^apps\/server\/tests\/(?:Weir\.(?:Api|Contract)\.Tests|Weir\.Contract\.FakeTools|Weir\.TestChild)\/.+\.cs$/ },
  // This check's own test holds the waits it looks for, as the text it is tried on.
  { kind: "node", files: /^scripts\/(?!check-wall-clock-waits\.test\.mjs$).+\.test\.mjs$/ },
];

const LONGEST_UNNAMED_WAIT_MS = 1000;

// Comments are blanked out, with the line count kept, so a word in a comment is never a finding. A PowerShell `#` starts a
// comment only at the start of a line or after a space, and a C# or JavaScript `//` is not one inside a string.
function withoutComments(kind, source) {
  if (kind === "script") {
    return source.replace(/<#[\s\S]*?#>/g, (block) => block.replace(/[^\n]/g, " ")).replace(/(^|\s)#[^\n]*/g, "$1");
  }
  return source
    .replace(/\/\*[\s\S]*?\*\//g, (block) => block.replace(/[^\n]/g, " "))
    .replace(/(^|[^:"'`\\])\/\/[^\n]*/g, "$1");
}

const PATTERNS = {
  csharp: [["delay", /\b(?:Task\.Delay|Thread\.Sleep|WaitForTimeoutAsync)\s*\(/g]],
  web: [
    ["sleep", /new Promise(?:<[^>]*>)?\(\s*\(?\s*(\w+)\s*\)?\s*=>\s*(?:window\.)?setTimeout\(\s*\1\b/g],
    ["running-clock", /\bshouldAdvanceTime\b/g],
  ],
  script: [["sleep", /\bStart-Sleep\b/g]],
  node: [["sleep", /new Promise\(\s*\(?\s*(\w+)\s*\)?\s*=>\s*setTimeout\(\s*\1\b/g]],
};

function lineOf(source, index) {
  return source.slice(0, index).split("\n").length;
}

/** Every wait for the wall clock in one file: { kind, line, text }. `original` keeps the comments, which can name a hang guard. */
export function findWaits(kind, source) {
  const code = withoutComments(kind, source);
  const found = [];
  for (const [name, pattern] of PATTERNS[kind]) {
    for (const match of code.matchAll(pattern)) {
      found.push({ kind: name, line: lineOf(code, match.index), text: match[0].trim() });
    }
  }
  if (kind === "web") {
    const lines = source.split("\n");
    code.split("\n").forEach((line, index) => {
      for (const match of line.matchAll(/\btimeout:\s*(\d[\d_]*)/g)) {
        const ms = Number(match[1].replace(/_/g, ""));
        const near = lines.slice(Math.max(0, index - 3), index + 1).join("\n");
        if (ms > LONGEST_UNNAMED_WAIT_MS && !/hang guard/i.test(near)) {
          found.push({ kind: "long-wait", line: index + 1, text: match[0] });
        }
      }
    });
  }
  return found.sort((a, b) => a.line - b.line);
}

/**
 * Judges the waits found against the allow-list: { findings, stale }. `found` is a map of file to its waits; `allowed` is the
 * list's entries ({ file, kind, count, reason }).
 */
export function judge(found, allowed) {
  const findings = [];
  const stale = [];
  const counted = new Map();
  for (const [file, waits] of found) {
    const byKind = Map.groupBy(waits, (wait) => wait.kind);
    for (const [kind, ofKind] of byKind) {
      const entry = allowed.find((item) => item.file === file && item.kind === kind);
      counted.set(`${file}|${kind}`, ofKind.length);
      if (!entry) {
        for (const wait of ofKind) findings.push(`${file}:${wait.line}: ${describe(wait)}`);
      } else if (ofKind.length > entry.count) {
        for (const wait of ofKind.slice(entry.count)) findings.push(`${file}:${wait.line}: ${describe(wait)} (the list allows ${entry.count} of this kind in the file)`);
      }
    }
  }
  for (const entry of allowed) {
    const seen = counted.get(`${entry.file}|${entry.kind}`) ?? 0;
    if (seen !== entry.count) {
      stale.push(`${entry.file}: the list allows ${entry.count} ${entry.kind} wait(s) and the file has ${seen}; correct the entry (the list only shrinks).`);
    }
    if (!entry.reason || entry.reason.trim().length < 20) {
      stale.push(`${entry.file}: the ${entry.kind} entry gives no reason.`);
    }
  }
  return { findings, stale };
}

const ADVICE = {
  delay: "waits for the wall clock; wait for a signal, move a fake clock, or poll with a ceiling (and list the poll in scripts/wall-clock-waits-allowed.json).",
  sleep: "sleeps for the wall clock; wait for a signal, move a fake clock, or poll with a ceiling (and list the poll in scripts/wall-clock-waits-allowed.json).",
  "running-clock": "lets the fake clock run on with real time, so what a test sees depends on how long a render takes; advance it by hand.",
  "long-wait": "waits over a second on a guess; use a fake clock, or name the ceiling a hang guard in a comment beside it.",
};

function describe(wait) {
  return `\`${wait.text}\` ${ADVICE[wait.kind]}`;
}

function main() {
  const tracked = execFileSync("git", ["ls-files", "apps", "scripts", "packaging"], { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 })
    .split(/\r?\n/)
    .filter(Boolean);
  const found = new Map();
  let checked = 0;
  for (const file of tracked) {
    const watched = WATCHED.find((item) => item.files.test(file));
    if (!watched) continue;
    checked += 1;
    const waits = findWaits(watched.kind, readFileSync(path.resolve(root, file), "utf8").replace(/\r\n/g, "\n"));
    if (waits.length) found.set(file, waits);
  }
  const allowed = JSON.parse(readFileSync(path.join(root, "scripts", "wall-clock-waits-allowed.json"), "utf8")).allowed;
  const { findings, stale } = judge(found, allowed);

  if (findings.length || stale.length) {
    console.error("A test waits for the wall clock:");
    for (const item of [...findings, ...stale]) console.error(`- ${item}`);
    process.exit(1);
  }
  console.log(`No test waits for the wall clock beyond the ${allowed.length} polls listed with their reasons (${checked} files checked).`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main();
}
