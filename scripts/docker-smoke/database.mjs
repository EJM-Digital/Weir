// What the smoke reads from Weir's SQLite files: the revision a database records, its accounts, and the revision a build ends on.
import { DatabaseSync } from "node:sqlite";

/** The revision the last migration in SchemaMigrator.cs's list leaves behind: what a database of this build records. */
export function headRevision(schemaMigratorSource) {
  const revisions = [...schemaMigratorSource.matchAll(/new\(\d+, "(\d{4}_[a-z0-9_]+)"/g)].map((match) => match[1]);
  if (revisions.length === 0) throw new Error("SchemaMigrator.cs lists no migration, so the build's schema revision cannot be told.");
  return revisions.at(-1);
}

/** The number in front of a revision's name ("0080" for "0080_handback_speaker"), as the pre-update copy's file name carries it. */
export function revisionNumber(revision) {
  const end = revision.indexOf("_");
  return end < 0 ? revision : revision.slice(0, end);
}

function withDatabase(path, read) {
  const database = new DatabaseSync(path, { readOnly: true });
  try {
    return read(database);
  } finally {
    database.close();
  }
}

/** The schema revision the database file records. */
export function readRevision(path) {
  return withDatabase(path, (database) => database.prepare("SELECT revision FROM schema_version").get().revision);
}

/** The user names of the accounts in the database file. */
export function usernamesIn(path) {
  return withDatabase(path, (database) =>
    database
      .prepare("SELECT username FROM users ORDER BY id")
      .all()
      .map((row) => row.username),
  );
}
