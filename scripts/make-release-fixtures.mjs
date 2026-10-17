#!/usr/bin/env node
// Makes the fixtures of a published Weir build: its own database after a short scripted session, and the configuration backup it makes.
//
//   node scripts/make-release-fixtures.mjs v1.0.0-rc.18 [v1.0.0-rc.17 ...]
//
// For each release tag it
//   1. checks that tag out in a scratch clone (the repository's own checkout is never touched) and builds its server, the build that
//      tag's release was made from;
//   2. runs the contract suite's `ReleaseFixtureSession` against that build (WEIR_CONTRACT_SERVER names its Weir.dll), which drives it
//      through its API with the real ffmpeg: an alert, a rule set, direct play devices, a workflow a fake media manager feeds (one file
//      finished and imported, one refused), a workflow of its own with a scanned library, and the backup the build makes;
//   3. scrubs what it left (sessions, this PC's name and the user's folder, in every text column) and refuses to write a file in which
//      any of them survives;
//   4. writes apps/server/tests/fixtures/databases/<version>.db.gz and
//      apps/server/tests/fixtures/recorded/backups/<version>/configuration-bundle.json, and a line in databases/manifest.json.
//
// Needs git, dotnet 10, node 22 or later (node:sqlite), ffmpeg and ffprobe on PATH (or WEIR_CONTRACT_REAL_FFMPEG_DIR).
// Run it on a workstation, not in CI: the fixtures are checked in, and `check-release-fixtures.mjs` holds them fresh.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { gzipSync } from "node:zlib";

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
 * is doubled, and with forward slashes), then this PC's name. The account's name is changed only in a path (Users, then the name), never on its own, because it is also an ordinary word.
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
  list.push([new RegExp(`(Users)([\\\\/]+)${escapeRegExp(values.userName)}(?![\\w-])`, "gi"), (_, users, separators) => `${users}${separators}${placeholders.user}`]);
  return list;
}

export function scrubText(text, rules) {
  return rules.reduce((current, [pattern, replacement]) => current.replace(pattern, typeof replacement === "function" ? replacement : () => replacement), text);
}

/** What a text still holds of this PC after scrubbing: its name, or a path in the account's folder. */
export function leftovers(text, values) {
  const found = [];
  if (values.hostname.length > 2 && text.toLowerCase().includes(values.hostname.toLowerCase())) found.push("the PC's name");
  if (new RegExp(`Users[\\\\/]+${escapeRegExp(values.userName)}(?![\\w-])`, "i").test(text)) found.push("the account's folder");
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

function versionOf(tag) {
  const match = /^v(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$/.exec(tag);
  if (!match) throw new Error(`"${tag}" is not a release tag such as v1.0.0-rc.18.`);
  return match[1];
}

function build(tag, version, scratch) {
  const source = path.join(scratch, "source");
  run("git", ["clone", "--quiet", "--shared", "--no-checkout", repositoryRoot, source]);
  run("git", ["-C", source, "checkout", "--quiet", "--detach", tag]);
  const env = { ...process.env, MSBUILDDISABLENODEREUSE: "1" };
  const host = path.join(source, "apps", "server", "src", "Weir.Host");
  run("dotnet", ["build", host, "-c", "Release", "-m:4", `-p:WeirVersion=${version}`], { env });
  const dll = path.join(host, "bin", "Release", "net10.0", "Weir.dll");
  if (!existsSync(dll)) throw new Error(`The build of ${tag} left no ${dll}.`);
  return dll;
}

function session(dll, scratch) {
  const raw = path.join(scratch, "session", "weir.sqlite3");
  const bundle = path.join(scratch, "session", "configuration-bundle.json");
  const env = {
    ...process.env,
    MSBUILDDISABLENODEREUSE: "1",
    WEIR_CONTRACT_SERVER: dll,
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

/** One release: builds it, runs the session against it, scrubs and writes its fixtures. */
export function makeFixtures(tag) {
  const version = versionOf(tag);
  const scratch = mkdtempSync(path.join(os.tmpdir(), "weir-fixture-"));
  try {
    const commit = execFileSync("git", ["-C", repositoryRoot, "rev-list", "-n", "1", tag], { encoding: "utf8" }).trim();
    const dll = build(tag, version, scratch);
    const { raw, bundle } = session(dll, scratch);

    const values = privateValues();
    scrubDatabase(raw, values);
    const survivors = findLeftovers(raw, values);
    if (survivors.length > 0) throw new Error(`The database of ${tag} still holds something private after scrubbing: ${survivors.join('; ')}.`);
    const scrubbedBundle = scrubText(readFileSync(bundle, "utf8"), replacements(values));
    if (leftovers(scrubbedBundle, values).length > 0) throw new Error(`The backup of ${tag} still holds a private value after scrubbing.`);

    const description = describeDatabase(raw);
    mkdirSync(databasesFolder, { recursive: true });
    const compressed = gzipSync(readFileSync(raw), { level: 9 });
    writeFileSync(path.join(databasesFolder, `${version}.db.gz`), compressed);
    const folder = path.join(backupsFolder, version);
    mkdirSync(folder, { recursive: true });
    writeFileSync(path.join(folder, "configuration-bundle.json"), scrubbedBundle);

    const manifest = existsSync(manifestFile) ? JSON.parse(readFileSync(manifestFile, "utf8")) : { databases: [] };
    const entry = {
      version,
      tag,
      commit,
      schema_revision: description.revision,
      sha256: createHash("sha256").update(compressed).digest("hex"),
      rows: description.rows,
    };
    manifest.databases = [...manifest.databases.filter((existing) => existing.version !== version), entry];
    writeFileSync(manifestFile, `${JSON.stringify(manifest, null, 2)}\n`);
    console.log(`${version}: schema ${description.revision}, ${compressed.length} bytes, ${Object.keys(description.rows).length} tables with rows`);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const tags = process.argv.slice(2);
  if (tags.length === 0) {
    console.error("Usage: node scripts/make-release-fixtures.mjs <release tag> [<release tag> ...]   (for example v1.0.0-rc.18)");
    process.exit(2);
  }
  for (const tag of tags) makeFixtures(tag);
}
