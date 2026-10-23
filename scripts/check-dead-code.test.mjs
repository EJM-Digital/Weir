// node --test scripts/check-dead-code.test.mjs
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { main, phantomClassNames, report, webUnusedExports } from "./check-dead-code.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "check-dead-code.mjs");

/** A repository folder with a stylesheet, a component and an allowlist; `knip` adds a stand-in for the Knip command. */
function withRepository({ css = ".mm-card { color: red; }", tsx = '<div className="mm-card" />', allowlist = {}, knip }, callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-dead-code-"));
  try {
    const put = (file, text) => {
      mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
      writeFileSync(path.join(root, file), text);
    };
    put("apps/web/src/styles/app.css", css);
    put("apps/web/src/page.tsx", tsx);
    put("scripts/dead-code-allowlist.json", JSON.stringify(allowlist));
    if (knip !== undefined) put("apps/web/node_modules/knip/bin/knip.js", knip);
    callback({ root, put });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const knipPrinting = (issues, code = 1) => `process.stdout.write(${JSON.stringify(JSON.stringify({ issues }))}); process.exit(${code});`;

function capture() {
  const lines = { log: [], error: [] };
  return { lines, out: { log: (line) => lines.log.push(line), error: (line) => lines.error.push(line) } };
}

test("a class name with a rule in a stylesheet is styled; one without is a phantom, with its place in the file", () => {
  withRepository(
    {
      css: "/* .mm-commented { } */ .mm-card, .mm-card--wide { }",
      tsx: ['<div className="mm-card mm-card--wide p-2" />', '<div className="mm-phantom" />', "<div className={`mm-${x}`} />", '<div className="plain" />'].join("\n"),
    },
    ({ root, put }) => {
      put("apps/web/src/page.test.tsx", '<div className="mm-in-a-test" />');
      const result = phantomClassNames(root);
      assert.deepEqual(result.findings, [{ at: "apps/web/src/page.tsx:2", token: "mm-phantom" }]);
      assert.equal(result.literals, 3);
      assert.equal(result.styled, 2);
    },
  );
});

test("Knip's findings are keyed by file, or by file and name, with Windows slashes made forward", () => {
  const issues = [{ file: "src\\a.ts", files: ["x"], exports: [{ name: "one" }], types: [{ name: "Two" }] }, { file: "src/b.ts", files: [], exports: [], types: [] }];
  withRepository({ knip: knipPrinting(issues) }, ({ root }) => {
    assert.deepEqual(webUnusedExports(root), { findings: ["src/a.ts", "src/a.ts:one", "src/a.ts:Two"] });
  });
});

test("Knip stopping with a failure and no findings is an error, and its absence is a skip", () => {
  withRepository({ knip: "process.exit(2);" }, ({ root }) => {
    assert.throws(() => webUnusedExports(root));
  });
  withRepository({}, ({ root }) => {
    assert.match(webUnusedExports(root).skipped, /run npm ci/);
  });
});

test("Knip ending cleanly is read the same way as one that reports findings", () => {
  withRepository({ knip: knipPrinting([], 0) }, ({ root }) => {
    assert.deepEqual(webUnusedExports(root), { findings: [] });
  });
});

test("the report names what is not on the allowlist and what no longer needs to be, and counts both", () => {
  const lines = [];
  const count = report("web exports", ["a.ts:one", "b.ts"], new Set(["b.ts", "c.ts:gone"]), { "c.ts:gone": "kept for the plugin" }, (line) => lines.push(line));
  assert.equal(count, 2);
  assert.match(lines.join("\n"), /web exports: 1 unreferenced item\(s\) not on the allowlist:\n {2}a\.ts:one\n/);
  assert.match(lines.join("\n"), /no longer dead \(remove them\):\n {2}c\.ts:gone {2}— kept for the plugin/);
  assert.equal(report("web exports", ["a.ts"], new Set(["a.ts"]), {}, () => assert.fail("nothing to say")), 0);
});

test("a repository with no dead code passes, saying what it measured", () => {
  withRepository({ knip: knipPrinting([], 0) }, ({ root }) => {
    const { lines, out } = capture();
    assert.equal(main(root, out), 0);
    assert.deepEqual(lines.log, [
      "[dead-code] phantom classes: 0 of 1 literal mm-* use(s) unstyled, against 1 styled name(s).",
      "[dead-code] web exports: 0 unreferenced, 0 allowlisted",
      "[dead-code] No unexpected dead code.",
    ]);
  });
});

test("without Knip installed the web half is skipped, and the phantom half still runs", () => {
  withRepository({}, ({ root }) => {
    const { lines, out } = capture();
    assert.equal(main(root, out), 0);
    assert.match(lines.log[1], /web skipped: apps\/web\/node_modules is missing/);
  });
});

test("a phantom class fails the guard and says there is no allowlist for it", () => {
  withRepository({ tsx: '<div className="mm-nothing" />' }, ({ root }) => {
    const { lines, out } = capture();
    assert.equal(main(root, out), 1);
    assert.equal(lines.error[0], "[dead-code] phantom classes: 1 mm-* class name(s) that no stylesheet styles:");
    assert.equal(lines.error[1], "  apps/web/src/page.tsx:1  mm-nothing");
    assert.match(lines.error[3], /There is no allowlist for these/);
  });
});

test("an unreferenced export that is not on the allowlist fails the guard", () => {
  const issues = [{ file: "src/a.ts", files: [], exports: [{ name: "unused" }], types: [] }];
  withRepository({ knip: knipPrinting(issues), allowlist: { webExports: {} } }, ({ root }) => {
    const { lines, out } = capture();
    assert.equal(main(root, out), 1);
    assert.match(lines.error.join("\n"), /src\/a\.ts:unused/);
  });
});

test("run as a command it checks this repository and exits 0", () => {
  const result = spawnSync(process.execPath, [script], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /No unexpected dead code/);
});
