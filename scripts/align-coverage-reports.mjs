#!/usr/bin/env node
// Makes coverlet Cobertura files written on different runners name the same things the same way (#976).
//
//   node scripts/align-coverage-reports.mjs <folder> <checkout>
//
// ReportGenerator merges reports by the names they use, and two things in a coverlet report depend on where the
// tests ran, not on the code:
//
//   * the `<source>` folder is the runner's own checkout (/home/runner/work/Weir/Weir on Linux, D:\a\Weir\Weir on
//     Windows), and a class that spans several files, like the server's partial `Endpoints` class, comes out as
//     different files on each operating system. Every report is pointed at the one checkout the merge job has,
//     which also lets the Html report find the sources it shows.
//   * the compiler names a file-local type `<File>F<SHA-256 of the file's path>__Name`, so the same type has a
//     different name on each runner. The hash is replaced with zeros.
//
// Without both, merging the Linux and Windows reports counted a fifth of the Api's lines twice.
import { readFileSync, readdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const SOURCE = /<source>([^<]*)<\/source>/g;
const FILE_LOCAL_HASH = /(&gt;F)[0-9A-Fa-f]{64}(__)/g;

function slashed(value) {
  return value.replace(/\\/g, "/");
}

/** The checkout a report was written in: the part of its first source folder before `/apps/`, or undefined. */
export function checkoutOf(text) {
  const first = [...text.matchAll(SOURCE)][0];
  if (!first) return undefined;
  const folder = slashed(first[1]);
  const at = folder.indexOf("/apps/");
  return at > 0 ? folder.slice(0, at) : undefined;
}

export function align(text, checkout) {
  const from = checkoutOf(text);
  const to = slashed(checkout).replace(/\/+$/, "");
  const move = (value) => {
    const name = slashed(value);
    return from !== undefined && (name === from || name.startsWith(`${from}/`)) ? to + name.slice(from.length) : value;
  };
  return text
    .replace(SOURCE, (_, value) => `<source>${move(value)}</source>`)
    .replace(/filename="([^"]*)"/g, (_, value) => `filename="${move(value)}"`)
    .replace(FILE_LOCAL_HASH, (_, before, after) => `${before}${"0".repeat(64)}${after}`);
}

export function coberturaFiles(folder) {
  const found = [];
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    const full = path.join(folder, entry.name);
    if (entry.isDirectory()) found.push(...coberturaFiles(full));
    else if (entry.name === "coverage.cobertura.xml") found.push(full);
  }
  return found;
}

/** Aligns every coverage.cobertura.xml under a folder in place; returns how many files it looked at. */
export function alignFolder(folder, checkout) {
  const files = coberturaFiles(folder);
  for (const file of files) {
    const text = readFileSync(file, "utf8");
    const aligned = align(text, checkout);
    if (aligned !== text) writeFileSync(file, aligned);
  }
  return files.length;
}

function main() {
  const [folder, checkout] = process.argv.slice(2);
  if (!folder || !checkout) {
    console.error("Usage: align-coverage-reports.mjs <folder> <checkout>");
    process.exit(1);
  }
  console.log(`Aligned ${alignFolder(folder, checkout)} coverage file(s) under ${folder} with ${checkout}.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
