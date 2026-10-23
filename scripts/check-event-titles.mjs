#!/usr/bin/env node
// Fails when the server defines an Activity event type the web app has no title for.
//
// Without a title the System > Logs page falls back to the raw name ("workflow sync notice"). The server's
// constants are the contract, so this reads them from source and compares them to the titles in
// apps/web/src/lib/activity/event-labels.ts.
//
// Usage: node scripts/check-event-titles.mjs
import { readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { rootOf } from "./repository-root.mjs";

const SERVER_SOURCES = [
  "apps/server/src/Weir.Core/Activity/ActivityEventTypes.cs",
  "apps/server/src/Weir.Core/MediaManagers/LibraryFileChangeRules.cs",
];
const TITLES = "apps/web/src/lib/activity/event-labels.ts";

const EVENT_TYPE = /const string \w+ = "([a-z_]+(?:\.[a-z_]+)+)";/g;
const TITLE_ENTRY = /^\s*"([a-z_]+(?:\.[a-z_]+)+)":\s*"/gm;

export function eventTypesIn(source) {
  return [...source.matchAll(EVENT_TYPE)].map((match) => match[1]);
}

export function titledTypesIn(source) {
  return new Set([...source.matchAll(TITLE_ENTRY)].map((match) => match[1]));
}

export function untitled(eventTypes, titled) {
  return eventTypes.filter((type) => !titled.has(type));
}

function main() {
  const root = rootOf(import.meta.url);
  const eventTypes = SERVER_SOURCES.flatMap((file) => eventTypesIn(readFileSync(path.join(root, file), "utf8")));
  if (eventTypes.length === 0) {
    console.error("No event types were found in the server sources; this check's pattern is out of date.");
    process.exit(1);
  }
  const missing = untitled(eventTypes, titledTypesIn(readFileSync(path.join(root, TITLES), "utf8")));
  if (missing.length > 0) {
    console.error(`These event types have no title in ${TITLES}, so the log would show their raw names:\n  ${missing.join("\n  ")}`);
    process.exit(1);
  }
  console.log(`Every one of the server's ${eventTypes.length} event types has a title.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main();
