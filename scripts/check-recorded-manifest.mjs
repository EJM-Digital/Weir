// Fails when a recording of Deluno's own side of its talk with Weir differs from the checksum list that came with it (#971).
//
// apps/server/tests/fixtures/recorded/deluno-side/ is a byte-for-byte copy of Deluno's tests/fixtures/recorded/deluno/ at the commit its
// README names: what the real Deluno sends Weir, what Weir sends it, and Deluno's own replies to the calls Weir makes. Weir's contract tests
// replay it, so a recording must never change silently, and one is never edited here: it is copied again from Deluno, with Deluno's
// MANIFEST.sha256, which lists a SHA-256 for every file:
//
//   <64 hex characters>  <version>/<state>.json
//
// This is Deluno's scripts/check-recorded-manifest.mjs, ported to the paths Weir needs. A file's hash is taken over its text with line
// endings read as LF, so a Windows checkout (CRLF) and a Linux one agree. The lines are sorted by name and the file ends with a newline.
//
// Usage: node scripts/check-recorded-manifest.mjs [--root <folder>] [--manifest <file>]
import { createHash } from "node:crypto";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = fileURLToPath(new URL("..", import.meta.url));
export const defaultRoot = join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded", "deluno-side");
export const manifestName = "MANIFEST.sha256";

export function hashOf(file) {
  const text = readFileSync(file, "utf8").replace(/^﻿/, "").replace(/\r\n/g, "\n");
  return createHash("sha256").update(text, "utf8").digest("hex");
}

function* recordings(root) {
  for (const version of readdirSync(root).sort()) {
    const folder = join(root, version);
    if (!statSync(folder).isDirectory()) continue;
    for (const file of readdirSync(folder).sort()) if (file.endsWith(".json")) yield { name: `${version}/${file}`, path: join(folder, file) };
  }
}

/** The manifest this folder would need: one line per recording, sorted by name, ending with a newline. */
export function currentManifest(root) {
  const lines = [...recordings(root)].map(({ name, path }) => ({ name, line: `${hashOf(path)}  ${name}` }));
  lines.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  return lines.map((entry) => entry.line).join("\n") + "\n";
}

/** What differs between the folder and the manifest, one sentence each; empty when they agree. */
export function problems(root, manifestPath) {
  if (!existsSync(manifestPath)) return [`${manifestPath} is missing. Copy it from Deluno with the recordings.`];
  const read = (text) =>
    new Map(
      text
        .split(/\r?\n/)
        .filter((line) => line.trim())
        .map((line) => {
          const match = /^([0-9a-f]{64}) {2}(\S.*)$/.exec(line);
          return match ? [match[2], match[1]] : [line, null];
        }),
    );
  const listed = read(readFileSync(manifestPath, "utf8"));
  const found = read(currentManifest(root));
  const out = [];
  for (const [name, hash] of found) {
    if (!listed.has(name)) out.push(`${name} is not in the manifest (a new recording).`);
    else if (listed.get(name) !== hash) out.push(`${name} changed: the manifest holds ${listed.get(name)?.slice(0, 12)}, the file is ${hash.slice(0, 12)}.`);
  }
  for (const [name, hash] of listed) {
    if (hash === null) out.push(`The manifest has a line that is not "<sha256>  <version>/<state>.json": ${name}`);
    else if (!found.has(name)) out.push(`${name} is in the manifest but the file is gone.`);
  }
  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const args = process.argv.slice(2);
  const option = (name, fallback) => (args.includes(name) ? resolve(args[args.indexOf(name) + 1]) : fallback);
  const root = option("--root", defaultRoot);
  const manifest = option("--manifest", join(root, manifestName));
  const found = problems(root, manifest);
  if (found.length > 0) {
    console.error("Deluno's recordings and their manifest disagree (#971):");
    for (const problem of found) console.error("  " + problem);
    console.error("A recording here is a byte-for-byte copy of Deluno's and is never edited. Copy the folder and its manifest again from Deluno (see the README beside them).");
    process.exit(1);
  }
  console.log(`Every recording under ${relative(process.cwd(), root) || "."} matches its manifest.`);
}
