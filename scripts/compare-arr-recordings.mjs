#!/usr/bin/env node
// Fails when a fresh recording of a real Sonarr, Radarr or download client differs from the one checked in (#1013, #1014, #1015).
//
// The recorders (apps/server/tools/Weir.ArrRecorder and Weir.ClientRecorder) write <folder>/<app>/<version>/<state>.json. The checked-in recordings are
// apps/server/tests/fixtures/recorded/<app>/<version>/<state>.json. Two runs never agree byte for byte: every recording carries the
// time it was made, and the apps number their rows, size their files and stamp their records afresh. So this compares what a
// replay depends on: the same states, the same requests, the same statuses and the same members with the same values, once these are
// set aside:
//
//   * timestamps (ISO or HTTP dates), and any text that is a GUID, a long hex string, a container id or a four-part version number;
//   * the value of a member that is an id, a size, a count or a duration (the member must still be there, with the same type);
//   * the members that describe a title on a metadata site and change without the app changing (ratings, images, popularity...);
//   * the numbers in a path (`/queue/12`) and in a query (`seriesId=3`).
//
// The version folder may differ: that is how a newer release shows up, and then the replies are compared across the two versions. When
// several versions of an app are checked in (qBittorrent 4.x and 5.x), a fresh recording is compared with the one of the same major version.
//
// Usage: node scripts/compare-arr-recordings.mjs <fresh folder> [<checked-in folder>] [--apps sonarr,radarr]   (the apps default to Sonarr and Radarr)
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = fileURLToPath(new URL("..", import.meta.url));
export const checkedIn = path.join(repositoryRoot, "apps", "server", "tests", "fixtures", "recorded");
export const apps = ["sonarr", "radarr"];

/** Every app with a recording: the Arr apps and the five download clients. */
export const allApps = [...apps, "qbittorrent", "transmission", "deluge", "sabnzbd", "nzbget"];

/** Members whose value is the app's own bookkeeping: still required, never compared. */
const VOLATILE_MEMBER = /^(id|.*Id|.*Ids|size|sizeleft|timeleft|sizeOnDisk|.*Count|.*Bytes|runtime|.*Size|.*Seconds|.*Minutes|.*Date|.*Time|added|.*At|age|ageHours|ageMinutes|duration|freeSpace|totalSpace|.*listen_port)$/;

/** Members that describe a title on a metadata site, and move without the app moving. */
const METADATA_MEMBER = new Set([
  "ratings", "images", "popularity", "lastInfoSync", "remotePoster", "statistics", "genres", "certification", "ended", "nextAiring",
  "previousAiring", "year", "network", "airTime", "originalLanguage", "alternateTitles", "seasons", "tags", "sortTitle", "cleanTitle",
  "titleSlug", "firstAired", "lastAired", "youTubeTrailerId", "studio", "website", "imdbId", "tmdbId", "tvdbId", "tvMazeId", "tvRageId",
  "collection", "inCinemas", "physicalRelease", "digitalRelease", "releaseDate", "secondaryYear", "secondaryYearSourceId", "overview",
  "description", "traceId",
]);

const TIMESTAMP = /\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?|[A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} GMT/g;
const GUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;
const LONG_HEX = /\b[0-9a-f]{32,}\b/gi;
const VERSION = /\b\d+\.\d+\.\d+\.\d+\b/g;
const CONTAINER_ID = /\b[0-9a-f]{12}\b/g;

export function maskText(text) {
  return text.replace(TIMESTAMP, "<time>").replace(GUID, "<guid>").replace(LONG_HEX, "<hex>").replace(VERSION, "<version>").replace(CONTAINER_ID, "<hex>");
}

export function maskPath(target) {
  return maskText(target).replace(/\/\d+(?=\/|\?|$)/g, "/<id>").replace(/=\d+(?=&|$)/g, "=<n>");
}

/** What a command's answer says about where it is in its life: it depends on how fast the app was, so it is never compared. */
const COMMAND_PROGRESS = new Set(["status", "started", "ended", "stateChangeTime", "queued", "duration", "result", "message", "exception"]);

/** The value as the comparison sees it: masked text, volatile members reduced to their type, metadata members dropped. */
export function normalise(value, member = "") {
  if (Array.isArray(value)) return value.map((item) => normalise(item, member));
  if (value !== null && typeof value === "object") {
    const out = {};
    const isCommand = typeof value.commandName === "string";
    for (const key of Object.keys(value).sort()) {
      if (METADATA_MEMBER.has(key) || (isCommand && COMMAND_PROGRESS.has(key))) continue;
      out[key] = normalise(value[key], key);
    }
    return out;
  }
  if (VOLATILE_MEMBER.test(member)) return `<${typeof value}>`;
  if (typeof value === "string") return maskText(value);
  return value;
}

/** A `location` header names the command or resource the app made: its number is the app's own. */
function maskLocation(headers) {
  return typeof headers.location === "string" ? { ...headers, location: maskPath(headers.location) } : headers;
}

export function normaliseExchange(exchange) {
  return {
    request: {
      method: exchange.request.method,
      path: maskPath(exchange.request.path),
      headers: normalise(exchange.request.headers ?? {}),
      body: normalise(exchange.request.body ?? null),
    },
    reply: {
      status: exchange.reply.status,
      headers: maskLocation(normalise(exchange.reply.headers ?? {})),
      body: normalise(exchange.reply.body ?? null),
      bodyText: exchange.reply.bodyText === undefined ? null : maskText(exchange.reply.bodyText),
    },
  };
}

function firstDifference(a, b, where) {
  if (JSON.stringify(a) === JSON.stringify(b)) return null;
  if (a && b && typeof a === "object" && typeof b === "object" && Array.isArray(a) === Array.isArray(b)) {
    const keys = Array.isArray(a) ? [...Array(Math.max(a.length, b.length)).keys()] : [...new Set([...Object.keys(a), ...Object.keys(b)])].sort();
    for (const key of keys) {
      const inner = firstDifference(a[key], b[key], `${where}.${key}`);
      if (inner) return inner;
    }
  }
  return `${where}: ${JSON.stringify(a)?.slice(0, 120)} became ${JSON.stringify(b)?.slice(0, 120)}`;
}

function versionFolders(appFolder) {
  return readdirSync(appFolder).filter((name) => statSync(path.join(appFolder, name)).isDirectory());
}

/** The version folder of a fresh recording: a run records one version of an app. */
function onlyVersionFolder(appFolder) {
  const folders = versionFolders(appFolder);
  return folders.length === 1 ? path.join(appFolder, folders[0]) : null;
}

/** The checked-in version folder to compare a fresh one with: the same version, the only one, or the one of the same major version. */
function matchingVersionFolder(appFolder, version) {
  const folders = versionFolders(appFolder);
  const major = (name) => name.split(".")[0];
  const found = folders.includes(version) ? [version] : folders.length === 1 ? folders : folders.filter((name) => major(name) === major(version));
  return found.length === 1 ? path.join(appFolder, found[0]) : null;
}

/** What differs between the recordings of one app in two folders, one sentence each; empty when they agree. */
export function problems(app, freshRoot, recordedRoot) {
  const out = [];
  const freshApp = path.join(freshRoot, app);
  const recordedApp = path.join(recordedRoot, app);
  if (!existsSync(freshApp)) return [`${app}: the recorder wrote nothing.`];
  if (!existsSync(recordedApp)) return [`${app}: nothing is checked in under ${recordedApp}.`];
  const fresh = onlyVersionFolder(freshApp);
  const recorded = fresh && matchingVersionFolder(recordedApp, path.basename(fresh));
  if (!fresh || !recorded) return [`${app}: expected one fresh version folder and one checked-in version of the same release line.`];
  const states = (folder) => readdirSync(folder).filter((name) => name.endsWith(".json")).map((name) => name.slice(0, -5)).sort();
  const freshStates = states(fresh);
  const recordedStates = states(recorded);
  for (const state of recordedStates) if (!freshStates.includes(state)) out.push(`${app}/${state}: recorded before, not recorded now.`);
  for (const state of freshStates) if (!recordedStates.includes(state)) out.push(`${app}/${state}: recorded now, not checked in.`);
  for (const state of freshStates.filter((name) => recordedStates.includes(name))) {
    const read = (folder) => JSON.parse(readFileSync(path.join(folder, `${state}.json`), "utf8"));
    const now = read(fresh);
    const before = read(recorded);
    if (now.exchanges.length !== before.exchanges.length) {
      out.push(`${app}/${state}: ${before.exchanges.length} exchange(s) before, ${now.exchanges.length} now.`);
      continue;
    }
    const difference = firstDifference(before.exchanges.map(normaliseExchange), now.exchanges.map(normaliseExchange), "exchanges");
    if (difference) out.push(`${app}/${state}: ${difference}`);
  }
  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const args = process.argv.slice(2);
  const named = args.includes("--apps") ? args.splice(args.indexOf("--apps"), 2)[1]?.split(",") : null;
  const [fresh, recorded = checkedIn] = args;
  if (!fresh || (named && !named.every((app) => allApps.includes(app)))) {
    console.error("Usage: node scripts/compare-arr-recordings.mjs <fresh folder> [<checked-in folder>] [--apps " + allApps.join(",") + "]");
    process.exit(2);
  }
  const found = (named ?? apps).flatMap((app) => problems(app, path.resolve(fresh), path.resolve(recorded)));
  if (found.length > 0) {
    console.error("The replies recorded now differ from the ones checked in (#1013, #1014, #1015):");
    for (const problem of found) console.error("  " + problem);
    process.exit(1);
  }
  console.log("Every fresh recording matches the one checked in, once times, ids and sizes are set aside.");
}
