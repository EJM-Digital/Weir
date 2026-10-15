// What the smoke reads of owners, modes and secrets from the container and from the folders it shares with this machine.
import { lstatSync, readdirSync } from "node:fs";
import { join } from "node:path";

/** A secret the entrypoint generates: 48 random bytes, base64url without padding. */
export const GENERATED_SECRET = /^[A-Za-z0-9_-]{64}$/;

/** The real uid and gid of a process, from its /proc/<pid>/status. */
export function processIds(status) {
  const ids = (label) => {
    const match = new RegExp(String.raw`^${label}:\s+(\d+)`, "m").exec(status);
    if (!match) throw new Error(`/proc status has no ${label} line:\n${status}`);
    return Number(match[1]);
  };
  return { uid: ids("Uid"), gid: ids("Gid") };
}

/**
 * Lines of `stat -c '%u:%g:%a:%n'`: the owner, group, permission bits (as the octal digits `stat` prints) and path of each.
 * @returns {{uid: number, gid: number, mode: string, path: string}[]}
 */
export function parseStat(output) {
  return output
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => {
      const match = /^(\d+):(\d+):([0-7]+):(.+)$/.exec(line);
      if (!match) throw new Error(`not a stat line: ${line}`);
      return { uid: Number(match[1]), gid: Number(match[2]), mode: match[3], path: match[4] };
    });
}

/** The paths under `folder` on this machine (the folder itself first) whose owner or group is not the given one. */
export function notOwnedBy(folder, { uid, gid }) {
  const wrong = [];
  const visit = (path) => {
    const info = lstatSync(path);
    if (info.uid !== uid || info.gid !== gid) wrong.push(`${path} (${info.uid}:${info.gid})`);
    if (info.isDirectory()) for (const entry of readdirSync(path)) visit(join(path, entry));
  };
  visit(folder);
  return wrong;
}

/** The ids the scenarios give the account the server runs as: neither is the image's 1000, nor likely to be this machine's user. */
export const REMAPPED = { uid: 2001, gid: 2002 };

/** The WEIR_PUID and WEIR_PGID that make the server run as `ids`. */
export const asEnvironment = ({ uid, gid }) => ({ WEIR_PUID: String(uid), WEIR_PGID: String(gid) });
