#!/usr/bin/env node
// Makes the fixtures of a published Weir build: its own database after a short scripted session, and the configuration backup it makes.
//
//   node scripts/make-release-fixtures.mjs 1.0.0-rc.18 [1.0.0-rc.17 ...]
//
// For each release version it
//   1. pulls the image that release published (this repository's image, tagged with the version), records its digest, and copies the Weir program out of
//      it: the very bytes people run, not a build of the source;
//   2. runs the contract suite's `ReleaseFixtureSession` against that program (WEIR_CONTRACT_SERVER names it), which drives it
//      through its API with the real ffmpeg: an alert, a rule set, direct play devices, a workflow a fake media manager feeds (one file
//      finished and imported, one refused), a workflow of its own with a scanned library, and the backup the program makes;
//   3. scrubs what it left (sessions, this machine's name and the user's folder, in every text column) and refuses to write a file in which
//      any of them survives;
//   4. writes apps/server/tests/fixtures/databases/<version>.db.gz and
//      apps/server/tests/fixtures/recorded/backups/<version>/configuration-bundle.json, and a line in databases/manifest.json.
//
// Needs docker, dotnet 10, node 22 or later (node:sqlite), and ffmpeg and ffprobe on PATH (or WEIR_CONTRACT_REAL_FFMPEG_DIR). The image is
// Linux, so run it on Linux: the "Release fixtures" workflow does, and uploads what it made. The fixtures are checked in, and
// `check-release-fixtures.mjs` holds them fresh.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { gzipSync } from "node:zlib";

import { releaseImage } from "./docker-smoke/previous-image.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const databasesFolder = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "databases");
export const backupsFolder = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded", "backups");
export const manifestFile = path.join(databasesFolder, "manifest.json");

/** A private value that must not survive in a fixture, and what stands in for it. */
export const placeholders = { machine: "FIXTURE-PC", user: "fixture-user", root: "C:\\Fixture\\" };

/** The values of this PC that a fixture must not carry: its name, the account's folder name and the temp folder of the session. */
export function privateValues({ hostname = os.hostname(), userName = os.userInfo().username, tempRoot = os.tmpdir() } = {}) {
  return { hostname, userName, tempRoot };
}

const escapeRegExp = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** A pattern for a literal text, in any letter case. */
const literal = (text) => new RegExp(escapeRegExp(text), "gi");

/**
 * The replacements that scrub a text: this PC's temp folder in the three ways it is written (as a path, inside JSON where every backslash
 * is doubled, and with forward slashes), then this PC's name. The account's name is changed only in a path (Users or home, then the name), never on its own, because it is also an ordinary word.
 */
export function replacements(values) {
  const backslashes = values.tempRoot.replace(/[\\/]+$/, "").replaceAll("/", "\\");
  const spellings = [
    { text: backslashes.replaceAll("\\", "\\\\"), separator: "\\\\" },
    { text: backslashes, separator: "\\" },
    { text: backslashes.replaceAll("\\", "/"), separator: "/" },
  ];
  const list = spellings.map(({ text, separator }) => [
    new RegExp(`${escapeRegExp(text)}(?:${escapeRegExp(separator)})?`, "gi"),
    placeholders.root.replaceAll("\\", separator),
  ]);
  list.push([literal(values.hostname), placeholders.machine]);
  // Any other path in the account's folder (where ffmpeg was installed, say), whichever way its separators are written.
  list.push([new RegExp(`(Users|home)([\\\\/]+)${escapeRegExp(values.userName)}(?![\\w-])`, "gi"), (_, users, separators) => `${users}${separators}${placeholders.user}`]);
  return list;
}

export function scrubText(text, rules) {
  return rules.reduce((current, [pattern, replacement]) => current.replace(pattern, typeof replacement === "function" ? replacement : () => replacement), text);
}

/** What a text still holds of this PC after scrubbing: its name, or a path in the account's folder. */
export function leftovers(text, values) {
  const found = [];
  if (values.hostname.length > 2 && text.toLowerCase().includes(values.hostname.toLowerCase())) found.push("the PC's name");
  if (new RegExp(`(?:Users|home)[\\\\/]+${escapeRegExp(values.userName)}(?![\\w-])`, "i").test(text)) found.push("the account's folder");
  return found;
}

const quote = (name) => `"${name.replace(/"/g, '""')}"`;

/** Scrubs a database file in place: ends every session, rewrites private values in every text column, and leaves it a single plain file. */
export function scrubDatabase(file, values) {
  const rules = replacements(values);
  const database = new DatabaseSync(file);
  try {
    database.exec("PRAGMA foreign_keys = OFF");
    database.exec("PRAGMA journal_mode = DELETE");
    database.exec("DELETE FROM user_sessions");
    const tables = database.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").all().map((row) => row.name);
    let changed = 0;
    for (const table of tables) {
      const columns = database.prepare(`PRAGMA table_info(${quote(table)})`).all().filter((column) => /CHAR|TEXT|CLOB|JSON|^$/i.test(column.type) || column.type === "");
      for (const column of columns) {
        const rows = database.prepare(`SELECT rowid AS id, ${quote(column.name)} AS value FROM ${quote(table)} WHERE typeof(${quote(column.name)}) = 'text'`).all();
        const update = database.prepare(`UPDATE ${quote(table)} SET ${quote(column.name)} = ? WHERE rowid = ?`);
        for (const row of rows) {
          const scrubbed = scrubText(row.value, rules);
          if (scrubbed !== row.value) {
            update.run(scrubbed, row.id);
            changed += 1;
          }
        }
      }
    }
    database.exec("VACUUM");
    return { changed, tables };
  } finally {
    database.close();
  }
}

/** Where in the database something private is left: each table and column that holds it, and what kind. */
export function findLeftovers(file, values) {
  const database = new DatabaseSync(file, { readOnly: true });
  try {
    const found = new Set();
    const tables = database.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").all().map((row) => row.name);
    for (const table of tables) {
      const select = database.prepare(`SELECT * FROM ${quote(table)}`);
      select.setReadBigInts(true);
      for (const row of select.all()) {
        for (const [column, value] of Object.entries(row)) {
          if (typeof value === "string") {
            for (const kind of leftovers(value, values)) found.add(`${table}.${column}: ${kind}`);
          }
        }
      }
    }
    return [...found];
  } finally {
    database.close();
  }
}

/** Every text in the database, joined. */
export function allText(file) {
  const database = new DatabaseSync(file, { readOnly: true });
  try {
    const parts = [];
    const tables = database.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").all().map((row) => row.name);
    for (const table of tables) {
      const select = database.prepare(`SELECT * FROM ${quote(table)}`);
      select.setReadBigInts(true);
      for (const row of select.all()) {
        for (const value of Object.values(row)) {
          if (typeof value === "string") parts.push(value);
        }
      }
    }
    return parts.join("\n");
  } finally {
    database.close();
  }
}

/** What a fixture's manifest line records about it. */
export function describeDatabase(file) {
  const database = new DatabaseSync(file, { readOnly: true });
  try {
    const revision = database.prepare("SELECT revision FROM schema_version").get().revision;
    const rows = {};
    for (const { name } of database.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").all()) {
      const { count } = database.prepare(`SELECT COUNT(*) AS count FROM ${quote(name)}`).get();
      if (count > 0) rows[name] = count;
    }
    return { revision, rows };
  } finally {
    database.close();
  }
}

const run = (program, args, options = {}) => execFileSync(program, args, { stdio: "inherit", ...options });

/** The image a release published. */
export const imageOf = releaseImage;

/** Pulls the release's image and copies its program out: the program's path, and the image's digest. */
function fetchRelease(version, scratch) {
  const image = imageOf(version);
  run("docker", ["pull", "--quiet", "--platform", "linux/amd64", image]);
  const digest = execFileSync("docker", ["inspect", "--format", "{{index .RepoDigests 0}}", image], { encoding: "utf8" }).trim().split("@")[1];
  if (!/^sha256:[0-9a-f]{64}$/.test(digest ?? "")) throw new Error(`Docker gave no digest for ${image}.`);
  const container = execFileSync("docker", ["create", "--platform", "linux/amd64", image], { encoding: "utf8" }).trim();
  try {
    const program = path.join(scratch, "release");
    run("docker", ["cp", `${container}:/opt/weir/.`, program]);
    const executable = path.join(program, "Weir");
    if (!existsSync(executable)) throw new Error(`${image} holds no /opt/weir/Weir.`);
    return { executable, digest };
  } finally {
    execFileSync("docker", ["rm", "--force", container], { stdio: "ignore" });
  }
}

function session(executable, scratch) {
  const raw = path.join(scratch, "session", "weir.sqlite3");
  const bundle = path.join(scratch, "session", "configuration-bundle.json");
  const env = {
    ...process.env,
    MSBUILDDISABLENODEREUSE: "1",
    WEIR_CONTRACT_SERVER: executable,
    WEIR_FIXTURE_DATABASE_OUT: raw,
    WEIR_FIXTURE_BACKUP_OUT: bundle,
  };
  run(
    "dotnet",
    ["test", path.join(repositoryRoot, "apps", "server", "tests", "Weir.Contract.Tests"), "-m:4", "--filter", "FullyQualifiedName~ReleaseFixtureSession"],
    { env },
  );
  if (!existsSync(raw) || !existsSync(bundle)) throw new Error("The session left no database or backup; it was skipped or failed.");
  return { raw, bundle };
}

/**
 * One release: pulls it, runs the session against it, scrubs and writes its fixtures.
 * `fetch` and `runSession` are the docker and dotnet steps, and `folders` where the fixtures are written; a test replaces them.
 */
export function makeFixtures(
  version,
  { fetch = fetchRelease, runSession = session, values = privateValues(), folders = { databases: databasesFolder, backups: backupsFolder, manifest: manifestFile } } = {},
) {
  const scratch = mkdtempSync(path.join(os.tmpdir(), "weir-fixture-"));
  try {
    const { executable, digest } = fetch(version, scratch);
    const { raw, bundle } = runSession(executable, scratch);

    scrubDatabase(raw, values);
    const survivors = findLeftovers(raw, values);
    if (survivors.length > 0) throw new Error(`The database of ${version} still holds something private after scrubbing: ${survivors.join('; ')}.`);
    const scrubbedBundle = scrubText(readFileSync(bundle, "utf8"), replacements(values));
    if (leftovers(scrubbedBundle, values).length > 0) throw new Error(`The backup of ${version} still holds a private value after scrubbing.`);

    const description = describeDatabase(raw);
    mkdirSync(folders.databases, { recursive: true });
    const compressed = gzipSync(readFileSync(raw), { level: 9 });
    writeFileSync(path.join(folders.databases, `${version}.db.gz`), compressed);
    const folder = path.join(folders.backups, version);
    mkdirSync(folder, { recursive: true });
    writeFileSync(path.join(folder, "configuration-bundle.json"), scrubbedBundle);

    const manifest = existsSync(folders.manifest) ? JSON.parse(readFileSync(folders.manifest, "utf8")) : { databases: [] };
    const entry = {
      version,
      image_digest: digest,
      schema_revision: description.revision,
      sha256: createHash("sha256").update(compressed).digest("hex"),
      rows: description.rows,
    };
    manifest.databases = [...manifest.databases.filter((existing) => existing.version !== version), entry];
    writeFileSync(folders.manifest, `${JSON.stringify(manifest, null, 2)}\n`);
    console.log(`${version}: schema ${description.revision}, ${compressed.length} bytes, ${Object.keys(description.rows).length} tables with rows`);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

/** Makes the fixtures of each version named; returns the exit code (2 when no version, or one that is not a version, was given). */
export function main(versions, { make = makeFixtures, error = console.error } = {}) {
  if (versions.length === 0 || versions.some((version) => !/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version))) {
    error("Usage: node scripts/make-release-fixtures.mjs <release version> [<release version> ...]   (for example 1.0.0-rc.18)");
    return 2;
  }
  for (const version of versions) make(version);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) process.exitCode = main(process.argv.slice(2));
