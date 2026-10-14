// Everything Weir exposes or runs, read straight from the source: the inventory the coverage register must account for.
//
//   node scripts/coverage/inventory.mjs            print the count of every kind
//   node scripts/coverage/inventory.mjs --list     print every item (kind, key, where)
//   node scripts/coverage/inventory.mjs --json     print the whole inventory as JSON
//
// Kinds (each item has a `key`; a register row names the keys it covers in `covers`):
//   route     route:GET /api/v1/activity/recent   every MapV1, MapApi and MapMethods call in apps/server/src
//   job       job:processing.library.clean.v1     every job kind the server queues
//   service   service:ThreadPoolWatch             every hosted service (BackgroundService / IHostedService)
//   task      task:SessionCleanupTask             every periodic task (IPeriodicTask)
//   setting   setting:env:WEIR_LOG_LEVEL          every environment variable the server or the tray reads, and
//             setting:ProcessingLibraryRecord.MinFileSizeMb   every field of the stored settings models
//   event     event:library.file_cleaned          every Activity event type the server defines
//   webroute  web:/setup/rules/profiles           every route and redirect in apps/web/src/app/router.tsx and the setup areas
//   action    action:<file>#<test id>             every button, form, field or link in a web screen that carries a data-testid
//   tray      tray:Pause                          every tray menu item (TrayMenuItem)
//   scenario  scenario:fresh-install              every golden-VM scenario id (scripts/scenarios/Weir.Scenarios.Catalog.ps1)
//
// No dependencies: plain node.
import { existsSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { slash, walk } from "./testindex.mjs";

const readText = (file) => readFileSync(file, "utf8").replace(/^\uFEFF/, "");
const files = (root, dir, accept) => walk(join(root, dir), accept);
const rel = (root, file) => slash(relative(root, file));
const lineAt = (text, pos) => text.slice(0, pos).split("\n").length;
const isSource = (f) => f.endsWith(".cs");

// ------------------------------------------------------------------------------------------------------------- routes

const SERVER_SRC = "apps/server/src";

/** The text of the call's argument list from the opening parenthesis at `open`, and where it closes. */
function argumentsOf(text, open) {
  let depth = 0;
  let inString = false;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      if (c === "\\") i++;
      else if (c === '"') inString = false;
    } else if (c === '"') inString = true;
    else if (c === "(") depth++;
    else if (c === ")" && --depth === 0) return { text: text.slice(open + 1, i), end: i };
  }
  return { text: text.slice(open + 1), end: text.length };
}

const normalisePath = (path) => path.replace(/\{(\w+)(?::[^}]*)?\}/g, "{$1}");

export function routeItems(root) {
  const items = new Map();
  const add = (method, path, file, line) => {
    const key = `route:${method} ${normalisePath(path)}`;
    if (!items.has(key)) items.set(key, { kind: "route", key, label: `${method} ${normalisePath(path)}`, file, line });
  };
  for (const file of files(root, SERVER_SRC, isSource)) {
    const text = readText(file);
    const relFile = rel(root, file);
    // endpoints.MapV1("GET", "/path", handler) maps /api/v1/path; MapApi takes the whole template.
    for (const m of text.matchAll(/\.MapV1\(\s*"([A-Z]+)"\s*,\s*"([^"]+)"/g)) add(m[1], `/api/v1${m[2]}`, relFile, lineAt(text, m.index));
    for (const m of text.matchAll(/\.MapApi\(\s*"([A-Z]+)"\s*,\s*"([^"]+)"/g)) add(m[1], m[2], relFile, lineAt(text, m.index));
    // A route mapped straight on the builder answers GET and HEAD (or just GET); it is one route.
    for (const m of text.matchAll(/\.MapMethods\(\s*"([^"]+)"\s*,\s*(GetAndHead|\[\s*HttpMethods\.Get\s*\])/g)) add("GET", m[1], relFile, lineAt(text, m.index));
  }
  return [...items.values()];
}

// ------------------------------------------------------------------------------------------------------------ job kinds

export function jobItems(root) {
  const items = new Map();
  for (const file of files(root, SERVER_SRC, isSource)) {
    const text = readText(file);
    for (const m of text.matchAll(/"(processing\.[a-z0-9_]+(?:\.[a-z0-9_]+)*\.v\d+)"/g)) {
      const key = `job:${m[1]}`;
      if (!items.has(key)) items.set(key, { kind: "job", key, label: m[1], file: rel(root, file), line: lineAt(text, m.index) });
    }
  }
  return [...items.values()];
}

// ------------------------------------------------------------------------------------- hosted services and periodic tasks

/** The classes whose declaration (up to its body or its end) names one of `bases`. */
function classesImplementing(root, bases) {
  const found = new Map();
  for (const file of files(root, SERVER_SRC, isSource)) {
    const text = readText(file);
    for (const m of text.matchAll(/\bclass\s+(\w+)\b([^{;]*)[{;]/g)) {
      const header = m[2];
      const open = header.indexOf("(");
      const afterConstructor = open >= 0 ? header.slice(argumentsOf(header, open).end + 1) : header;
      const inheritance = afterConstructor.includes(":") ? afterConstructor.slice(afterConstructor.indexOf(":")) : "";
      if (bases.some((b) => new RegExp(`\\b${b}\\b`).test(inheritance)) && !found.has(m[1])) {
        found.set(m[1], { file: rel(root, file), line: lineAt(text, m.index) });
      }
    }
  }
  return found;
}

export function serviceItems(root) {
  return [...classesImplementing(root, ["BackgroundService", "IHostedService"])].map(([name, where]) => ({
    kind: "service",
    key: `service:${name}`,
    label: name,
    ...where,
  }));
}

/** A task is named by the `Name` it reports in the task list; a class that builds its name at run time keeps its class name. */
export function taskItems(root) {
  const items = [];
  for (const [className, where] of classesImplementing(root, ["IPeriodicTask"])) {
    const text = readText(join(root, where.file));
    const from = text.search(new RegExp(`\\bclass\\s+${className}\\b`));
    const next = text.slice(from + 1).search(/\bclass\s+\w+/);
    const body = text.slice(from, next < 0 ? text.length : from + 1 + next);
    const name = /\bstring Name\s*=>\s*"([^"]+)"/.exec(body)?.[1] ?? className;
    items.push({ kind: "task", key: `task:${name}`, label: name, className, ...where });
  }
  return items;
}

// --------------------------------------------------------------------------------------------------------------- settings

/** The stored settings models: the type, its file and why it is not a place to look for a bookkeeping field. */
export const SETTINGS_MODELS = [
  { type: "SuiteSettingsRecord", file: "apps/server/src/Weir.Core/Settings/SuiteSettings.cs" },
  { type: "ProcessingOperatorSettingsRecord", file: "apps/server/src/Weir.Core/Processing/OperatorSettingsModel.cs" },
  { type: "ProcessingRuleSetRecord", file: "apps/server/src/Weir.Core/Processing/LibraryModel.cs" },
  { type: "ProcessingLibraryRecord", file: "apps/server/src/Weir.Core/Processing/LibraryModel.cs" },
  { type: "LibrarySettings", file: "apps/server/src/Weir.Core/LibraryMode/LibraryModeSettings.cs" },
  { type: "NotificationChannelRecord", file: "apps/server/src/Weir.Core/Notifications/Notifications.cs" },
  { type: "UpdateSettings", file: "apps/tray/Weir.Tray/UpdateSettings.cs" },
];

/** Fields every record carries that are bookkeeping, not something a person sets. */
const BOOKKEEPING = new Set(["Id", "CreatedAt", "UpdatedAt"]);

/** The body of `type X`, as the text of its parameter list and of its braces. */
function typeBody(text, type) {
  const at = text.search(new RegExp(`\\b(?:record|class)\\s+${type}\\b`));
  if (at < 0) return null;
  const afterName = at + text.slice(at).search(new RegExp(`${type}\\b`)) + type.length;
  let parameters = "";
  let cursor = afterName;
  if (text[cursor] === "(") {
    const args = argumentsOf(text, cursor);
    parameters = args.text;
    cursor = args.end + 1;
  }
  const open = text.indexOf("{", cursor);
  const semi = text.indexOf(";", cursor);
  if (open < 0 || (semi >= 0 && semi < open)) return { parameters, body: "" };
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    if (text[i] === "{") depth++;
    else if (text[i] === "}" && --depth === 0) return { parameters, body: text.slice(open + 1, i) };
  }
  return { parameters, body: text.slice(open + 1) };
}

/** Splits a parameter list at top-level commas. */
function splitParameters(parameters) {
  const parts = [];
  let depth = 0;
  let current = "";
  for (const c of parameters) {
    if ("<([".includes(c)) depth++;
    else if (">)]".includes(c)) depth--;
    if (c === "," && depth === 0) {
      parts.push(current);
      current = "";
    } else current += c;
  }
  if (current.trim()) parts.push(current);
  return parts;
}

export function modelFields(text, type) {
  const body = typeBody(text, type);
  if (!body) return [];
  const names = [];
  for (const parameter of splitParameters(body.parameters)) {
    const m = /(\w+)\s*(?:=[^,]*)?\s*$/.exec(parameter.replace(/=.*$/s, "").trim());
    if (m) names.push(m[1]);
  }
  for (const m of body.body.matchAll(/\bpublic\s+(?:required\s+)?[\w<>?,.\[\]() ]+?\s+(\w+)\s*\{\s*get;\s*(?:init|set);\s*\}/g)) names.push(m[1]);
  return names.filter((n) => !BOOKKEEPING.has(n));
}

export function settingItems(root) {
  const items = new Map();
  const add = (key, label, file, line) => {
    if (!items.has(key)) items.set(key, { kind: "setting", key, label, file, line });
  };
  for (const dir of ["apps/server/src", "apps/tray/Weir.Tray"]) {
    for (const file of files(root, dir, isSource)) {
      const text = readText(file);
      for (const m of text.matchAll(/"(WEIR_[A-Z0-9_]+)"/g)) add(`setting:env:${m[1]}`, m[1], rel(root, file), lineAt(text, m.index));
    }
  }
  for (const { type, file } of SETTINGS_MODELS) {
    const path = join(root, file);
    if (!existsSync(path)) continue;
    const text = readText(path);
    for (const field of modelFields(text, type)) add(`setting:${type}.${field}`, `${type}.${field}`, file, lineAt(text, text.indexOf(field)));
  }
  return [...items.values()];
}

// ---------------------------------------------------------------------------------------------------------------- events

const EVENT_SOURCES = ["apps/server/src/Weir.Core/Activity/ActivityEventTypes.cs", "apps/server/src/Weir.Core/MediaManagers/LibraryFileChangeRules.cs"];

export function eventItems(root) {
  const items = [];
  for (const file of EVENT_SOURCES) {
    const path = join(root, file);
    if (!existsSync(path)) continue;
    const text = readText(path);
    for (const m of text.matchAll(/const string (\w+) = "([a-z_]+(?:\.[a-z_]+)+)";/g)) {
      items.push({ kind: "event", key: `event:${m[2]}`, label: m[2], constant: m[1], file, line: lineAt(text, m.index) });
    }
  }
  return items;
}

// ------------------------------------------------------------------------------------------------------------- web routes

const ROUTER = "apps/web/src/app/router.tsx";
const SETUP_AREAS = "apps/web/src/lib/settings/setup-areas.ts";
const SETUP_ROUTES = "apps/web/src/app/setup-routes.tsx";

/** Index of the bracket matching the one at `open`. */
function closing(text, open) {
  const pairs = { "[": "]", "{": "}", "(": ")" };
  const stack = [];
  let inString = null;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      if (c === "\\") i++;
      else if (c === inString) inString = null;
      continue;
    }
    if (c === '"' || c === "'" || c === "`") inString = c;
    else if (c === "/" && text[i + 1] === "/") i = text.indexOf("\n", i) < 0 ? text.length : text.indexOf("\n", i);
    else if (pairs[c]) stack.push(pairs[c]);
    else if (c === stack[stack.length - 1] && stack.pop() !== undefined && stack.length === 0) return i;
  }
  return text.length - 1;
}

/** The route objects of the array literal at `open`, each with its own path and its children. */
function routeObjects(text, open, prefix, out) {
  const end = closing(text, open);
  let i = open + 1;
  while (i < end) {
    if (text[i] === "{") {
      const objectEnd = closing(text, i);
      const object = text.slice(i, objectEnd + 1);
      // Only this object's own keys: blank out the nested arrays and objects before reading them.
      let own = "";
      let depth = 0;
      for (const c of object) {
        if ("[{(".includes(c)) depth++;
        if (depth <= 1) own += c;
        if ("]})".includes(c)) depth--;
      }
      const path = /\bpath:\s*"([^"]*)"/.exec(own)?.[1];
      const isIndex = /\bindex:\s*true/.test(own);
      const here = path === undefined ? prefix : joinPath(prefix, path);
      if (path !== undefined || isIndex) out.push({ path: isIndex ? prefix || "/" : here, redirect: /Navigate|Redirect/.test(object) });
      const children = /\bchildren:\s*\[/.exec(object);
      if (children) routeObjects(text, i + children.index + children[0].length - 1, here, out);
      i = objectEnd + 1;
    } else i++;
  }
}

function joinPath(prefix, path) {
  if (path.startsWith("/")) return path;
  if (prefix === "") return path;
  return `${prefix.replace(/\/$/, "")}/${path}`;
}

function setupAreas(root) {
  const path = join(root, SETUP_AREAS);
  if (!existsSync(path)) return [];
  const text = readText(path);
  const areas = [];
  for (const m of text.matchAll(/declareArea\(\s*"([\w-]+)"\s*,/g)) {
    const start = text.indexOf("[", m.index + m[0].length);
    const tabs = [...text.slice(start, closing(text, start)).matchAll(/\[\s*"([\w-]+)"\s*,/g)].map((t) => t[1]);
    areas.push({ id: m[1], tabs });
  }
  return areas;
}

export function webRouteItems(root) {
  const items = new Map();
  const add = (path, file) => {
    const key = `web:${path}`;
    if (!items.has(key)) items.set(key, { kind: "webroute", key, label: path, file });
  };
  const routerPath = join(root, ROUTER);
  if (existsSync(routerPath)) {
    const text = readText(routerPath);
    const open = text.indexOf("[", text.indexOf("createBrowserRouter("));
    const routes = [];
    routeObjects(text, open, "", routes);
    for (const route of routes) add(route.path, ROUTER);
  }
  const retired = new Map();
  const setupRoutesPath = join(root, SETUP_ROUTES);
  if (existsSync(setupRoutesPath)) {
    const text = readText(setupRoutesPath);
    const table = /RETIRED_TABS[^=]*=\s*\{/.exec(text);
    if (table) {
      const open = text.indexOf("{", table.index + table[0].length - 1);
      for (const m of text.slice(open, closing(text, open)).matchAll(/(\w+):\s*\[([^\]]*)\]/g)) {
        retired.set(m[1], [...m[2].matchAll(/"([\w-]+)"/g)].map((t) => t[1]));
      }
    }
  }
  for (const area of setupAreas(root)) {
    add(`/setup/${area.id}`, SETUP_AREAS);
    for (const tab of area.tabs) add(`/setup/${area.id}/${tab}`, SETUP_AREAS);
    for (const tab of retired.get(area.id) ?? []) add(`/setup/${area.id}/${tab}`, SETUP_ROUTES);
  }
  return [...items.values()];
}

// -------------------------------------------------------------------------------------------------------- web actions

const INTERACTIVE = new Set(["button", "Button", "a", "Link", "NavLink", "form", "input", "select", "textarea", "details", "summary", "Chip", "MmListboxPicker", "MmMultiListboxPicker"]);

/** The tag of the JSX element whose attributes contain position `at`. */
function enclosingTag(text, at) {
  let depth = 0;
  for (let i = at; i >= 0; i--) {
    const c = text[i];
    if (c === "}") depth++;
    else if (c === "{") depth = Math.max(0, depth - 1);
    else if (c === "<" && depth === 0 && /[A-Za-z]/.test(text[i + 1] ?? "")) return /^<([A-Za-z][\w.]*)/.exec(text.slice(i))?.[1] ?? null;
  }
  return null;
}

/** `data-testid` values of a source file that sit on something a person operates, with a template's holes as `*`. */
export function actionTestIds(text) {
  const found = new Map();
  for (const m of text.matchAll(/\bdata-testid=(?:"([^"]+)"|\{`([^`]+)`\}|\{"([^"]+)"\})/g)) {
    const raw = m[1] ?? m[3] ?? m[2];
    const pattern = raw.replace(/\$\{[^}]*\}/g, "*");
    if (pattern === "*" || !INTERACTIVE.has(enclosingTag(text, m.index) ?? "")) continue;
    if (!found.has(pattern)) found.set(pattern, lineAt(text, m.index));
  }
  return found;
}

export function actionItems(root) {
  const items = [];
  for (const file of files(root, "apps/web/src", (f) => /\.tsx$/.test(f) && !/\.test\.tsx$/.test(f))) {
    const text = readText(file);
    for (const [pattern, line] of actionTestIds(text)) {
      items.push({ kind: "action", key: `action:${rel(root, file)}#${pattern}`, label: pattern, file: rel(root, file), line });
    }
  }
  return items;
}

// ------------------------------------------------------------------------------------------------------------------- tray

export function trayItems(root) {
  const file = "apps/tray/Weir.Tray/TrayMenu.cs";
  const path = join(root, file);
  if (!existsSync(path)) return [];
  const text = readText(path);
  const body = /enum TrayMenuItem\s*\{([^}]*)\}/.exec(text)?.[1] ?? "";
  return [...body.matchAll(/^\s*(\w+),?\s*$/gm)].map((m) => ({ kind: "tray", key: `tray:${m[1]}`, label: m[1], file }));
}

// ---------------------------------------------------------------------------------------------------------------- scenarios

export function scenarioItems(root) {
  const file = "scripts/scenarios/Weir.Scenarios.Catalog.ps1";
  const path = join(root, file);
  if (!existsSync(path)) return [];
  const text = readText(path);
  return [...text.matchAll(/\bId = '([a-z][a-z0-9-]*)';/g)].map((m) => ({ kind: "scenario", key: `scenario:${m[1]}`, label: m[1], file, line: lineAt(text, m.index) }));
}

// ---------------------------------------------------------------------------------------------------------------- inventory

export function buildInventory(root) {
  return [
    ...routeItems(root),
    ...jobItems(root),
    ...serviceItems(root),
    ...taskItems(root),
    ...settingItems(root),
    ...eventItems(root),
    ...webRouteItems(root),
    ...actionItems(root),
    ...trayItems(root),
    ...scenarioItems(root),
  ];
}

export function countsOf(inventory) {
  const counts = {};
  for (const item of inventory) counts[item.kind] = (counts[item.kind] ?? 0) + 1;
  return counts;
}

function main() {
  const args = process.argv.slice(2);
  const rootArg = args.indexOf("--root");
  const root = resolve(rootArg >= 0 ? args[rootArg + 1] : fileURLToPath(new URL("../..", import.meta.url)));
  const inventory = buildInventory(root);
  if (args.includes("--json")) {
    console.log(JSON.stringify(inventory, null, 1));
  } else if (args.includes("--list")) {
    for (const item of inventory) console.log(`${item.kind.padEnd(9)} ${item.key}${item.file ? `   (${item.file}${item.line ? `:${item.line}` : ""})` : ""}`);
  } else {
    for (const [kind, n] of Object.entries(countsOf(inventory))) console.log(`${kind.padEnd(9)} ${n}`);
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) main();
