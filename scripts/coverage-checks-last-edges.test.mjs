// node --test scripts/coverage-checks-last-edges.test.mjs
// The last branches of the check-* scripts and the coverage inventory that their own tests leave out (#1060).
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { blockingAdvisories } from "./check-npm-audit.mjs";
import { main } from "./check-coverage-register.mjs";
import { renderClusters, clustersOf } from "./check-mutation-floors.mjs";
import { currentManifest } from "./check-recorded-manifest.mjs";
import { webRouteItems } from "./coverage/inventory.mjs";

function inFolder(body) {
  const folder = mkdtempSync(path.join(tmpdir(), "weir-last-edges-"));
  try {
    return body(folder);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

const put = (root, file, text) => {
  mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
  writeFileSync(path.join(root, file), text);
};

test("an advisory that names a package the report has no entry for takes the fix flag of the package that carries it", () => {
  const advisory = { name: "absent", severity: "high", title: "t", url: "https://github.com/advisories/GHSA-x", range: "*" };
  const found = blockingAdvisories({ vulnerabilities: { carrier: { via: [advisory], fixAvailable: true } } });
  assert.equal(found.length, 1);
  assert.equal(found[0].fixAvailable, true);
});

test("a mutation survivor with one or two tests lists them all", () => {
  const survivor = { file: "a.cs", line: 1, column: 1, mutator: "Block", original: "a", replacement: "b", covered: true, tests: ["N.C.One"] };
  const rendered = renderClusters(clustersOf([survivor]));
  assert.match(rendered, /\| C\.One \|/);
  assert.doesNotMatch(rendered, /more/);
});

test("the recorded manifest is sorted by the whole name, so a version that starts another sorts after it", () => {
  inFolder((root) => {
    put(root, "1.0.0/a.json", "{}\n");
    put(root, "1.0.0-rc.1/a.json", "{}\n");
    const names = currentManifest(root)
      .trimEnd()
      .split("\n")
      .map((line) => line.slice(66));
    assert.deepEqual(names, ["1.0.0-rc.1/a.json", "1.0.0/a.json"]);
  });
});

test("a web route at the top of the router with a relative path keeps the path as written", () => {
  inFolder((root) => {
    put(root, "apps/web/src/app/router.tsx", 'const router = createBrowserRouter([{ path: "login", element: <A /> }]);');
    assert.deepEqual(webRouteItems(root).map((item) => item.key), ["web:login"]);
  });
});

test("the inventory command with no --root reads the repository it lives in", () => {
  const script = fileURLToPath(new URL("./coverage/inventory.mjs", import.meta.url));
  const run = spawnSync(process.execPath, [script, "--list"], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stdout, /^route +route:GET \/api\/v1\//m);
});

test("the register check names a tray item that has no row by its file alone, as it has no line", async () => {
  await inFolder(async (root) => {
    put(root, "docs/testing/coverage-register.json", JSON.stringify({ version: 1, areas: { tray: { code: "T", name: "Tray" } }, rows: [] }));
    put(root, "apps/tray/Weir.Tray/TrayMenu.cs", "enum TrayMenuItem\n{\n    Open,\n}\n");
    const complaints = [];
    const code = await main(["--root", root], { env: {}, log: () => {}, error: (line) => complaints.push(line) });
    assert.equal(code, 1);
    assert.match(complaints.join("\n"), /tray:Open {2}\(apps\/tray\/Weir\.Tray\/TrayMenu\.cs\)/);
  });
});
