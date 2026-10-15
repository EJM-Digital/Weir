// What Weir's own log says, which is how an operator learns the setup code and whether a start updated the database.

/** The one-time setup code Weir logs while it has no account, or null when the log does not carry it. */
export function parseSetupCode(log) {
  return /enter this setup code:\s*([A-Z0-9]{4}-[A-Z0-9]{4})/.exec(log)?.[1] ?? null;
}

/**
 * How the last start opened the database: `migrated` when it brought an older database up to `revision`, not when it found the
 * database already there. Null when the log says neither (a start that created the database, or one that never got that far).
 */
export function upgradeOutcome(log) {
  const found = [...log.matchAll(/(Upgraded database to|Opened database at) schema revision=(\S+)/g)].at(-1);
  return found ? { migrated: found[1].startsWith("Upgraded"), revision: found[2] } : null;
}
