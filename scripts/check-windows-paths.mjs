#!/usr/bin/env node
// Keeps the `windows` path filter in .github/workflows/ci.yml honest (docs/ci-cost.md).
//
// A pull request runs the Windows contract legs and the Windows scenario shards only when it touches a path in that filter,
// so a Windows-only server file that the filter misses would change without either running. This check finds the server
// files that are Windows-only by what they contain or are called, and fails for each one the filter does not match:
//
//   * a file named *.Windows.cs, or with Windows, Firewall or Velopack in its name;
//   * a file that calls Win32 or COM (DllImport, LibraryImport, Microsoft.Win32), is marked [SupportedOSPlatform("windows")],
//     names Velopack, or shells out to netsh.
//
// Code that merely branches on OperatingSystem.IsWindows() is portable and is not listed here; the filter names the few such
// files that handle file locking and paths by hand, because those are where a Windows regression has come from.
//
// Usage: node scripts/check-windows-paths.mjs
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { rootOf } from "./repository-root.mjs";
import { patternToRegExp } from "./select-mutation-targets.mjs";

const SOURCE_FOLDERS = ["apps/server/src/", "apps/server/tools/"];
const WINDOWS_NAME = /(?:Windows|Firewall|Velopack)/;
const WINDOWS_CONTENT = /SupportedOSPlatform\("windows"\)|Microsoft\.Win32|\[DllImport|\[LibraryImport|Velopack|\bnetsh\b/;

/** The patterns of the `windows:` filter in ci.yml's paths-filter step. */
export function windowsFilter(ciText) {
  const lines = ciText.replace(/\r\n/g, "\n").split("\n");
  const start = lines.findIndex((line) => /^ {12}windows:\s*$/.test(line));
  if (start < 0) throw new Error("ci.yml has no `windows:` path filter.");
  const patterns = [];
  for (let i = start + 1; i < lines.length; i += 1) {
    const item = lines[i].match(/^ {14}- '([^']+)'\s*$/);
    if (item) patterns.push(item[1]);
    else if (!/^\s*#/.test(lines[i])) break;
  }
  if (patterns.length === 0) throw new Error("The `windows:` path filter in ci.yml lists nothing.");
  return patterns;
}

/** Whether a file is Windows-only by its name or its content. */
export function isWindowsOnly(file, content) {
  return WINDOWS_NAME.test(path.posix.basename(file)) || WINDOWS_CONTENT.test(content);
}

/** The Windows-only files among `files` that no pattern matches; `read(file)` returns a file's text. */
export function unmatchedWindowsFiles(files, read, patterns) {
  const matchers = patterns.map((pattern) => patternToRegExp(pattern));
  return files
    .filter((file) => SOURCE_FOLDERS.some((folder) => file.startsWith(folder)) && file.endsWith(".cs"))
    .filter((file) => isWindowsOnly(file, read(file)))
    .filter((file) => !matchers.some((matcher) => matcher.test(file)));
}

function main() {
  const root = rootOf(import.meta.url);
  const patterns = windowsFilter(readFileSync(path.join(root, ".github", "workflows", "ci.yml"), "utf8"));
  const files = execFileSync("git", ["ls-files", ...SOURCE_FOLDERS], { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 })
    .split("\n").map((line) => line.trim()).filter(Boolean);
  const missed = unmatchedWindowsFiles(files, (file) => readFileSync(path.join(root, file), "utf8"), patterns);
  if (missed.length > 0) {
    console.error("These server files are Windows-only, but the `windows:` filter in .github/workflows/ci.yml does not match them, so a pull request that changes one would skip the Windows legs:");
    for (const file of missed) console.error(`  - ${file}`);
    console.error("Add the file, or its folder, to that filter.");
    process.exit(1);
  }
  console.log(`Every Windows-only server file is matched by the ${patterns.length} patterns of the \`windows:\` path filter.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
