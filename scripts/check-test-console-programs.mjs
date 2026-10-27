#!/usr/bin/env node
// Fails when a test source under apps/ starts a system console program (ping, timeout, ...) as a stand-in.
//
// A console program can be handed a console window when its parent is killed, and on a desktop where Windows
// Terminal is the default terminal that opens a window titled after the program (#806, #821). A test that needs a
// slow or long-lived process uses a program of our own instead: apps/server/tests/Weir.TestChild, or
// apps/tray/Weir.Tray.StandInServer, which is a Windows-subsystem program that can never have a console.
//
// Only string literals are read, so a variable called `ping`, a URL ending in /ping or a comment that names the
// program is fine.
//
// Usage: node scripts/check-test-console-programs.mjs
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { rootOf } from "./repository-root.mjs";

// Test projects are named *.Tests or live under a tests folder; the two stand-in projects are test-only too.
const TEST_SOURCE_PATH = /^apps\/(?:.*\/)?(?:tests|[^/]+\.Tests|Weir\.TestChild|Weir\.Tray\.StandInServer)\/.+\.cs$/;

// Comments and character literals are matched so they are skipped; the last two alternatives are string literals.
const TOKEN = /\/\/[^\n]*|\/\*[\s\S]*?\*\/|'(?:[^'\\\n]|\\.)+'|@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"/g;

// A program name at the start of a literal, or after a path separator or a space, so `/admin/ping` does not match.
const CONSOLE_PROGRAM_PATTERNS = [
  /(?:^|[\\\s])(?:ping|tracert|nslookup|waitfor)(?:\.exe)?(?:\s|$)/i,
  /(?:^|[\\\s])(?:timeout|choice)\.exe(?:\s|$)/i,
  /(?:^|\s)\/[ck]\s+(?:timeout|choice)(?:\s|$)/i,
  /(?:^|\s)(?:timeout|choice)\s+\/[a-z]/i,
];

export function isTestSource(relativePath) {
  return TEST_SOURCE_PATH.test(relativePath);
}

export function findConsoleStandIns(source) {
  const findings = [];
  for (const match of source.matchAll(TOKEN)) {
    const token = match[0];
    if (!token.endsWith('"') || !token.includes('"')) continue;
    const content = token.replace(/^@?"/, "").slice(0, -1);
    if (CONSOLE_PROGRAM_PATTERNS.some((pattern) => pattern.test(content))) {
      const line = source.slice(0, match.index).split("\n").length;
      findings.push({ line, text: token });
    }
  }
  return findings;
}

function main() {
  const root = rootOf(import.meta.url);
  const files = execFileSync("git", ["ls-files", "apps"], { cwd: root, encoding: "utf8" })
    .split(/\r?\n/)
    .filter(isTestSource);
  const failures = files.flatMap((relative) =>
    findConsoleStandIns(readFileSync(path.resolve(root, relative), "utf8")).map(
      ({ line, text }) => `${relative}:${line}: ${text}`,
    ),
  );

  if (failures.length) {
    console.error("A test must not start a system console program; use Weir.TestChild or Weir.Tray.StandInServer instead:");
    for (const failure of failures) console.error(`- ${failure}`);
    process.exit(1);
  }
  console.log(`No test source starts a system console program (${files.length} files checked).`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main();
}
