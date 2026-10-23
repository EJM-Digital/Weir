#!/usr/bin/env node
// Fails when a Python file is tracked in the repository.
//
// Weir is .NET (server and tray) plus the web app's TypeScript, and so are its tests: the contract suite, the
// browser tests and the packaged live audit are .NET projects (#892). A Python file coming back would bring back a
// second toolchain on every machine and in CI, so this check refuses it. Tooling goes in Node (scripts/*.mjs) or
// PowerShell, or in a .NET project.
//
// Usage: node scripts/check-no-python.mjs
import { execFileSync } from "node:child_process";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { rootOf } from "./repository-root.mjs";

const PYTHON_FILE = /\.(?:py|pyw|pyi|pyx)$/i;

export function pythonFiles(trackedFiles) {
  return trackedFiles.filter((file) => PYTHON_FILE.test(file));
}

function main() {
  const root = rootOf(import.meta.url);
  const tracked = execFileSync("git", ["ls-files", "-z"], { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 })
    .split("\0")
    .filter(Boolean);
  const found = pythonFiles(tracked);
  if (found.length > 0) {
    console.error(
      `Weir has no Python. Remove these files, or write the tool in Node, PowerShell or .NET:\n${found.map((file) => `  ${file}`).join("\n")}`,
    );
    process.exit(1);
  }
  console.log("No Python files are tracked.");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main();
