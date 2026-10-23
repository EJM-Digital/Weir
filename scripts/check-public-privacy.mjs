#!/usr/bin/env node
// Fails when something private shows up in a file the public reads: the README, the user docs, the release notes,
// the changelog and the built web bundle. Weir's repository is public, so a home address, a machine name, a NAS path
// or an email that slipped into an example would be public too.
//
// What it looks for (broad shapes, never a list of the real values):
//   - private IPv4 addresses (10.x, 172.16-31.x, 192.168.x). Documentation ranges (192.0.2.x, 198.51.100.x,
//     203.0.113.x) are not private, so examples use those. Network ranges written as CIDR with a zero host
//     (10.0.0.0/8) are allowed, because they describe a range rather than a machine.
//   - Windows default machine names, WIN- followed by eight or more letters and digits
//   - UNC paths such as \\name\share. \\<host>\share is a placeholder and is allowed.
//   - email addresses, except noreply ones and the example domains
//   - api_key= and apikey values that are not placeholders
//   - a few known private words, held only as SHA-256 hashes in scripts/public-privacy-hashes.json, so this file
//     and that one never contain them. Every word in a file is hashed and compared.
//
// A failure names the file, the line and the kind of match. It never prints the match itself, because CI logs of a
// public repository are public.
//
// Usage:
//   node scripts/check-public-privacy.mjs           the public files; exit 1 on a match
//   node scripts/check-public-privacy.mjs --audit   every tracked text file, grouped by kind; always exit 0
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

export const KINDS = {
  privateIp: "private IP address",
  machineName: "Windows machine name",
  uncPath: "UNC or NAS path",
  email: "email address",
  apiKey: "API key value",
  knownLiteral: "known private literal (hashed)",
};

const PRIVATE_IPV4 =
  /(?<![\d.])(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3})(?![\d]|\.\d)(?<cidr>\/\d{1,2})?/g;
const MACHINE_NAME = /\bWIN-[A-Z0-9]{8,}\b/g;
const UNC_PATH = /(?<![\\\w:.])\\\\(?![<{$%])(?<name>[A-Za-z0-9_$.-]{2,})\\/g;
// The same path inside a C# or JSON string, where each backslash is written twice.
const UNC_ESCAPED = /(?<![\\\w:.])\\\\\\\\(?![<{$%])(?<name>[A-Za-z0-9_$.-]{2,})\\\\/g;
const EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/g;
const API_KEY = /\b(?:api[_-]?key|apikey)\b["']?\s*[=:]\s*["']?(?<value>[A-Za-z0-9_\-+/]{12,})/gi;

const HARMLESS_EMAIL_DOMAINS = /(?:^|\.)(?:example\.(?:com|org|net)|users\.noreply\.github\.com|noreply\.github\.com)$/i;
const PLACEHOLDER_WORDS = /^(?:your|my|the|example|sample|placeholder|changeme|redacted|secret|token|apikey|api[_-]?key|xxx+|\.{3}|\*+|<.*>|\$.*|%.*|\{.*\})/i;

const hashList = JSON.parse(readFileSync(path.join(repositoryRoot, "scripts", "public-privacy-hashes.json"), "utf8")).sha256;

export function sha256(text) {
  return createHash("sha256").update(text).digest("hex");
}

function lineOf(text, index) {
  let line = 1;
  for (let i = 0; i < index; i += 1) if (text.charCodeAt(i) === 10) line += 1;
  return line;
}

function isPlaceholderKey(value) {
  if (PLACEHOLDER_WORDS.test(value)) return true;
  if (/^(.)\1+$/.test(value)) return true;
  // A real key mixes letters and digits; a word like "apikey_header" or "api_key_name" does not.
  return !(/\d/.test(value) && /[A-Za-z]/.test(value));
}

// Words and the dash-joined runs inside them ("a-b-c" gives a-b-c, a-b, b-c, a, b, c), plus every "n.n.n." prefix
// of a dotted number. Each is hashed and looked up.
function candidates(text) {
  const found = new Map();
  const add = (token, index) => {
    if (!found.has(token)) found.set(token, index);
  };
  for (const match of text.matchAll(/[A-Za-z0-9]+(?:[-_][A-Za-z0-9]+)*/g)) {
    const parts = match[0].toLowerCase().split(/[-_]/);
    for (let start = 0; start < parts.length; start += 1) {
      for (let end = start + 1; end <= Math.min(parts.length, start + 4); end += 1) {
        add(parts.slice(start, end).join("-"), match.index);
        if (match[0].includes("_")) add(parts.slice(start, end).join("_"), match.index);
      }
    }
  }
  for (const match of text.matchAll(/(?<![\d.])\d{1,3}\.\d{1,3}\.\d{1,3}\./g)) add(match[0], match.index);
  return found;
}

/** Every private-looking match in `text`, as { kind, line }. Never includes the matched text. */
export function scanText(text, hashes = hashList) {
  const hits = [];
  const hit = (kind, index) => hits.push({ kind, line: lineOf(text, index) });

  for (const m of text.matchAll(PRIVATE_IPV4)) {
    const isNetworkRange = m.groups?.cidr && m[0].replace(m.groups.cidr, "").endsWith(".0");
    if (!isNetworkRange) hit("privateIp", m.index);
  }
  for (const m of text.matchAll(MACHINE_NAME)) hit("machineName", m.index);
  for (const m of [...text.matchAll(UNC_PATH), ...text.matchAll(UNC_ESCAPED)]) {
    if (!/^u[0-9a-f]{4}$/i.test(m.groups.name)) hit("uncPath", m.index);
  }
  for (const m of text.matchAll(EMAIL)) {
    const address = m[0];
    const [local, domain] = address.split("@");
    if (/^(?:noreply|no-reply)$/i.test(local) || HARMLESS_EMAIL_DOMAINS.test(domain)) continue;
    hit("email", m.index);
  }
  for (const m of text.matchAll(API_KEY)) {
    if (!isPlaceholderKey(m.groups.value)) hit("apiKey", m.index);
  }
  if (hashes.length > 0) {
    const wanted = new Set(hashes);
    for (const [token, index] of candidates(text)) {
      if (wanted.has(sha256(token))) hit("knownLiteral", index);
    }
  }
  return hits.sort((a, b) => a.line - b.line);
}

const BINARY_EXTENSIONS = /\.(?:png|jpe?g|gif|webp|ico|icns|svgz|woff2?|ttf|otf|eot|zip|gz|7z|exe|dll|so|dylib|mp4|mkv|pdf|nupkg|snk|bin|wasm|lockb|ico)$/i;
const PUBLIC_DOC_EXTENSIONS = /\.(?:md|mdx|json|txt|yml|yaml)$/i;

function walk(dir, accept, out = []) {
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "node_modules" || entry.name === ".docusaurus" || entry.name === "build") continue;
      walk(full, accept, out);
    } else if (accept(full)) out.push(full);
  }
  return out;
}

function underAny(root, file, folders) {
  const relative = path.relative(root, file).split(path.sep).join("/");
  return folders.some((folder) => relative === folder || relative.startsWith(`${folder}/`));
}

/** The files a visitor can read: relative paths from the repository root. */
export function publicFiles(root = repositoryRoot) {
  const files = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (entry.isFile() && /\.md$/i.test(entry.name)) files.push(path.join(root, entry.name));
  }
  const skipped = ["docs/archive", "docs/exec-plans"];
  files.push(...walk(path.join(root, "docs"), (f) => /\.md$/i.test(f) && !underAny(root, f, skipped)));
  files.push(...walk(path.join(root, "docs-site", "docs"), (f) => PUBLIC_DOC_EXTENSIONS.test(f)));
  files.push(...walk(path.join(root, "docker"), (f) => /\.md$/i.test(f)));
  files.push(...walk(path.join(root, "apps", "web", "dist"), (f) => !BINARY_EXTENSIONS.test(f)));
  return files;
}

function isText(buffer) {
  return !buffer.subarray(0, 8000).includes(0);
}

function scanFile(file) {
  const buffer = readFileSync(file);
  if (!isText(buffer)) return [];
  return scanText(buffer.toString("utf8"));
}

function relative(root, file) {
  return path.relative(root, file).split(path.sep).join("/");
}

/** Scans the files a visitor can read under `root`; returns the exit code. */
export function runCheck(root = repositoryRoot, out = { log: console.log, error: console.error }) {
  const files = publicFiles(root);
  let failures = 0;
  for (const file of files) {
    for (const { kind, line } of scanFile(file)) {
      failures += 1;
      out.error(`${relative(root, file)}:${line}  ${KINDS[kind]}`);
    }
  }
  if (!existsSync(path.join(root, "apps", "web", "dist"))) {
    out.log("The web bundle is not built here, so it was not checked.");
  }
  if (failures > 0) {
    out.error(
      `\n${failures} private-looking value(s) in public files. Use a placeholder such as <host>, <nas> or <your-pc>, ` +
        "and documentation addresses (192.0.2.x, 198.51.100.x, 203.0.113.x). The match is not printed on purpose.",
    );
    return 1;
  }
  out.log(`No private values in ${files.length} public files.`);
  return 0;
}

/** Lists every private-looking match in the files the repository tracks, by kind. */
export function runAudit(root = repositoryRoot, out = { log: console.log }) {
  const tracked = execFileSync("git", ["ls-files", "-z"], { cwd: root, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 })
    .split("\0")
    .filter((file) => file && !BINARY_EXTENSIONS.test(file));
  const byKind = new Map();
  for (const file of tracked) {
    const full = path.join(root, file);
    if (!existsSync(full) || !statSync(full).isFile()) continue;
    for (const { kind, line } of scanFile(full)) {
      if (!byKind.has(kind)) byKind.set(kind, []);
      byKind.get(kind).push(`${file}:${line}`);
    }
  }
  for (const kind of Object.keys(KINDS)) {
    const where = byKind.get(kind) ?? [];
    out.log(`\n${KINDS[kind]}: ${where.length}`);
    for (const place of where) out.log(`  ${place}`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (process.argv.includes("--audit")) runAudit();
  else process.exitCode = runCheck();
}
