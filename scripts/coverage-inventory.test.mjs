// node --test scripts/coverage-inventory.test.mjs
// The inventory reads the source as text, so each reader is shown the shapes of source it has to understand, and the ones it has to leave alone.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  actionTestIds,
  buildInventory,
  countsOf,
  eventItems,
  jobItems,
  modelFields,
  routeItems,
  scenarioItems,
  serviceItems,
  settingItems,
  taskItems,
  trayItems,
  webRouteItems,
} from "./coverage/inventory.mjs";

const made = [];
after(() => {
  for (const folder of made) rmSync(folder, { recursive: true, force: true });
});

function tree(files) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-inventory-"));
  made.push(root);
  for (const [file, text] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), text);
  }
  return root;
}

const keysOf = (items) => items.map((item) => item.key);
const SRC = "apps/server/src/Weir.Api";

test("routes: a MapApi route keeps its whole template, a route constraint is dropped and a route mapped twice is one", () => {
  const root = tree({
    [`${SRC}/A.cs`]: 'endpoints.MapApi("POST", "/api/v1/things/{id:guid}/go", h);\nendpoints.MapApi("POST", "/api/v1/things/{id}/go", h);\nendpoints.MapMethods("/ready", [HttpMethods.Get], h);\nendpoints.MapMethods("/odd", HttpMethods.Post, h);\n',
  });
  assert.deepEqual(keysOf(routeItems(root)), ["route:POST /api/v1/things/{id}/go", "route:GET /ready"]);
});

test("services and tasks: a class is found by what it inherits after its constructor, not by a word in its constructor", () => {
  const root = tree({
    [`${SRC}/Services.cs`]: [
      'public sealed class Quoted(ILogger<Quoted> log, string label = "a \\" b (c)") : BackgroundService { }',
      "public sealed class Plain(IFoo foo) { }",
      "public sealed class Unclosed(IFoo foo {",
      "public sealed class Listener : IHostedService, IDisposable { }",
      "public sealed class Sweeper : IPeriodicTask",
      "{",
      "    public string Name => dynamicName;",
      "}",
      "public sealed class Named : IPeriodicTask",
      "{",
      '    public string Name => "named-task";',
      "}",
      "",
    ].join("\n"),
  });
  assert.deepEqual(keysOf(serviceItems(root)), ["service:Quoted", "service:Listener"]);
  assert.deepEqual(keysOf(taskItems(root)), ["task:Sweeper", "task:named-task"]);
  assert.deepEqual(taskItems(root).map((task) => task.className), ["Sweeper", "Named"]);
});

test("a job kind is found once however often it is written", () => {
  const root = tree({ [`${SRC}/J.cs`]: 'const A = "processing.thing.do.v1"; const B = "processing.thing.do.v1";' });
  assert.deepEqual(keysOf(jobItems(root)), ["job:processing.thing.do.v1"]);
});

test("model fields: record parameters, properties, and the types that are not found or have no body", () => {
  const text = [
    "public sealed record Wide(string Name, Dictionary<string, int> Counts = default, int Id, DateTimeOffset CreatedAt);",
    "public sealed record Narrow(bool On)",
    "{",
    "    public required string Extra { get; init; }",
    "    public int Mode { get; set; }",
    "    public int Computed => 1;",
    "}",
    "public sealed class Open",
    "{",
    "    public int Never { get; set; }",
  ].join("\n");
  assert.deepEqual(modelFields(text, "Wide"), ["Name", "Counts"]);
  assert.deepEqual(modelFields(text, "Narrow"), ["On", "Extra", "Mode"]);
  assert.deepEqual(modelFields(text, "Open"), ["Never"]);
  assert.deepEqual(modelFields(text, "Missing"), []);
  assert.deepEqual(modelFields("public record Bare;\npublic class After { public int X { get; set; } }", "Bare"), []);
  assert.deepEqual(modelFields("public record Trailing(int A, ", "Trailing"), ["A"]);
});

test("settings: each variable once, each model field, and a model file that is not there is skipped", () => {
  const root = tree({
    [`${SRC}/Env.cs`]: 'Get("WEIR_ONE"); Get("WEIR_ONE"); Get("NOT_WEIR");',
    "apps/tray/Weir.Tray/Env.cs": 'Get("WEIR_TWO");',
    "apps/tray/Weir.Tray/UpdateSettings.cs": "public sealed class UpdateSettings\n{\n    public bool Auto { get; set; }\n}\n",
  });
  assert.deepEqual(keysOf(settingItems(root)), ["setting:env:WEIR_ONE", "setting:env:WEIR_TWO", "setting:UpdateSettings.Auto"]);
});

test("events are read from the files that define them, and a missing file is skipped", () => {
  assert.deepEqual(eventItems(tree({})), []);
  const root = tree({ "apps/server/src/Weir.Core/MediaManagers/LibraryFileChangeRules.cs": 'public const string Moved = "library.file_moved";' });
  assert.deepEqual(eventItems(root).map((item) => [item.key, item.constant]), [["event:library.file_moved", "Moved"]]);
});

const ROUTER = [
  "// the routes",
  "const router = createBrowserRouter([",
  "  {",
  '    path: "/",',
  "    element: <Shell />,",
  "    children: [",
  '      { index: true, element: <Navigate to="/activity" /> },',
  '      { path: "activity", lazy: async () => ({}) },',
  '      { path: "/setup", children: [{ path: "deep/er", element: <Page title=\'it\\\'s\' /> }] },',
  "      { element: <Layout />, children: [{ path: \"inside\", element: <A /> }] },",
  '      { path: "*", element: <Navigate to="/" /> }, // anything else',
  "    ],",
  "  },",
  "]);",
].join("\n");

const AREAS = [
  "function declareArea(id: SetupAreaId, label: string): SetupArea {}",
  "export const AREAS = [",
  '  declareArea("rules", "Rules", "x", [',
  '    ["profiles", "Profiles"],',
  '    ["rules", "Rules"],',
  "  ]),",
  '  declareArea("connections", "Connections", "x", [["managers", "Managers"]]),',
  "];",
].join("\n");

const RETIRED = 'const RETIRED_TABS: Partial<Record<SetupAreaId, readonly string[]>> = {\n  rules: ["metadata", "old-tab"],\n};\n';

test("web routes: children join their parent, an index is the parent, and the setup areas and their retired tabs are routes", () => {
  const root = tree({
    "apps/web/src/app/router.tsx": ROUTER,
    "apps/web/src/lib/settings/setup-areas.ts": AREAS,
    "apps/web/src/app/setup-routes.tsx": RETIRED,
  });
  assert.deepEqual(
    keysOf(webRouteItems(root)),
    [
      "web:/",
      "web:/activity",
      "web:/setup",
      "web:/setup/deep/er",
      "web:/inside",
      "web:/*",
      "web:/setup/rules",
      "web:/setup/rules/profiles",
      "web:/setup/rules/rules",
      "web:/setup/rules/metadata",
      "web:/setup/rules/old-tab",
      "web:/setup/connections",
      "web:/setup/connections/managers",
    ],
  );
});

test("web routes: without a router, setup areas or a retired table there is simply nothing to list", () => {
  assert.deepEqual(webRouteItems(tree({})), []);
  const root = tree({ "apps/web/src/lib/settings/setup-areas.ts": AREAS, "apps/web/src/app/setup-routes.tsx": "export const x = 1;" });
  assert.deepEqual(keysOf(webRouteItems(root)), ["web:/setup/rules", "web:/setup/rules/profiles", "web:/setup/rules/rules", "web:/setup/connections", "web:/setup/connections/managers"]);
});

test("web routes: a router that never closes its list still gives the routes it has", () => {
  const root = tree({ "apps/web/src/app/router.tsx": 'const router = createBrowserRouter([\n  { path: "/a", element: <A /> },\n  { path: "/b", element: <B /> }' });
  assert.deepEqual(keysOf(webRouteItems(root)), ["web:/a", "web:/b"]);
});

test("actions: an id counts when what carries it is something a person operates; a template's holes become *", () => {
  const found = actionTestIds(
    [
      '<button data-testid="go">Go</button>',
      '<div>{items.map((i) => <a data-testid={`row-${i.id}`} href="x">x</a>)}</div>',
      '<input data-testid={"typed"} />',
      '<div data-testid="note">Note</div>',
      "<Link data-testid={`${all}`} />",
      '<Chip data-testid="go">Again</Chip>',
      'const orphan = { "data-testid": "x" }; <button\n  onClick={() => run({ a: 1 })}\n  data-testid="after-braces"\n/>',
    ].join("\n"),
  );
  assert.deepEqual([...found.keys()], ["go", "row-*", "typed", "after-braces"]);
  assert.equal(found.get("go"), 1);
  assert.equal(actionTestIds('data-testid="alone"').size, 0);
});

test("actions are read from the web screens, not from their tests", () => {
  const root = tree({
    "apps/web/src/pages/a.tsx": '<button data-testid="a-go" />',
    "apps/web/src/pages/a.test.tsx": '<button data-testid="a-test" />',
    "apps/web/src/pages/b.ts": '<button data-testid="b-go" />',
  });
  assert.deepEqual(keysOf(buildInventory(root).filter((item) => item.kind === "action")), ["action:apps/web/src/pages/a.tsx#a-go"]);
});

test("the tray menu and the scenarios are read, and a missing or empty file gives none", () => {
  assert.deepEqual(trayItems(tree({})), []);
  assert.deepEqual(trayItems(tree({ "apps/tray/Weir.Tray/TrayMenu.cs": "class TrayMenu {}" })), []);
  assert.deepEqual(scenarioItems(tree({})), []);
  const root = tree({
    "apps/tray/Weir.Tray/TrayMenu.cs": "enum TrayMenuItem\n{\n    Open,\n    Quit\n}\n",
    "scripts/scenarios/Weir.Scenarios.Catalog.ps1": "Id = 'fresh-install'; Id = 'Upper'; Id = 'update-2';",
  });
  assert.deepEqual(keysOf(trayItems(root)), ["tray:Open", "tray:Quit"]);
  assert.deepEqual(keysOf(scenarioItems(root)), ["scenario:fresh-install", "scenario:update-2"]);
});

test("counts say how many of each kind there are", () => {
  assert.deepEqual(countsOf([{ kind: "route" }, { kind: "job" }, { kind: "route" }]), { route: 2, job: 1 });
});

test("run as a command it prints the counts, the list or the JSON of the tree it is given", () => {
  const root = tree({ [`${SRC}/A.cs`]: 'endpoints.MapV1("GET", "/things", h);', "apps/tray/Weir.Tray/TrayMenu.cs": "enum TrayMenuItem\n{\n    Open,\n}\n", "apps/web/src/app/router.tsx": 'createBrowserRouter([{ path: "/x" }]);' });
  const script = fileURLToPath(new URL("./coverage/inventory.mjs", import.meta.url));
  const run = (...args) => spawnSync(process.execPath, [script, "--root", root, ...args], { encoding: "utf8" });

  assert.match(run().stdout, /^route {5}1\ntray {6}1\n|^route +1/m);
  assert.match(run("--list").stdout, /route {5}route:GET \/api\/v1\/things {3}\(apps\/server\/src\/Weir\.Api\/A\.cs:1\)/);
  assert.match(run("--list").stdout, /webroute +web:\/x {3}\(apps\/web\/src\/app\/router\.tsx\)/);
  assert.deepEqual(JSON.parse(run("--json").stdout).map((item) => item.key), ["route:GET /api/v1/things", "web:/x", "tray:Open"]);
});
