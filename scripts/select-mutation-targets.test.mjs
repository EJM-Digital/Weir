// node --test scripts/select-mutation-targets.test.mjs
// Which mutation targets a change touches (#977), against the real targets file and Stryker configs.
import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { configOf, loadTargets, mutatePatterns, outputsOf, parseArguments, patternToRegExp, selectTargets } from "./select-mutation-targets.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const targets = loadTargets(root);
const idsFor = (...changed) => selectTargets(changed, targets, root).map((target) => target.target);

test("a pattern matches the files it names and nothing beside them", () => {
  const swap = patternToRegExp("**/LibraryMode/SafeSwap.cs{0..6789}");
  assert.ok(swap.test("apps/server/src/Weir.Infrastructure/LibraryMode/SafeSwap.cs"));
  assert.ok(!swap.test("apps/server/src/Weir.Infrastructure/LibraryMode/SafeSwap.Support.cs"));
  assert.ok(!swap.test("apps/server/src/Weir.Infrastructure/LibraryMode/XSafeSwap.cs"));
  const reporter = patternToRegExp("**/MediaManagers/HandoffCompletionReporter*.cs");
  assert.ok(reporter.test("apps/server/src/Weir.Infrastructure/MediaManagers/HandoffCompletionReporter.Send.cs"));
  assert.ok(!reporter.test("apps/server/src/Weir.Infrastructure/MediaManagers/HandoffCompletionReporterTests/x.cs"));
  const client = patternToRegExp("src/lib/api/client.ts", "apps/web/");
  assert.ok(client.test("apps/web/src/lib/api/client.ts"));
  assert.ok(!client.test("apps/web/src/lib/api/client-extra.ts"));
  assert.ok(!client.test("src/lib/api/client.ts"));
});

test("a changed file selects every target that mutates it, and only those", () => {
  assert.deepEqual(idsFor("apps/server/src/Weir.Infrastructure/Sqlite/UnitOfWork.cs"), ["write-gate-unit-of-work"]);
  assert.deepEqual(idsFor("apps/server/src/Weir.Infrastructure/LibraryMode/SafeSwap.cs"), [
    "file-lifecycle-swap-preflight",
    "file-lifecycle-swap-prepare",
    "file-lifecycle-swap-commit",
  ]);
  assert.deepEqual(idsFor("apps/tray/Weir.Tray/StartWatch.cs"), ["start-supervision"]);
  assert.deepEqual(idsFor("apps/web/src/components/ui/confirm-dialog.tsx"), ["web-dialogs-library"]);
});

test("a change to a target's own config selects it, and a change nobody mutates selects nothing", () => {
  assert.deepEqual(idsFor("apps/server/mutation/handoff-intake/stryker-config.json"), ["handoff-intake"]);
  assert.deepEqual(idsFor("apps/web/mutation/web-api-client/stryker.config.json"), ["web-api-client"]);
  assert.deepEqual(idsFor("README.md", "apps/server/src/Weir.Api/Endpoints/ActivityEndpoints.cs", "apps/web/src/lib/api/client.test.ts"), []);
  assert.deepEqual(idsFor(), []);
});

test("paths with backslashes and blank lines are read as the paths they are", () => {
  assert.deepEqual(idsFor("apps\\server\\src\\Weir.Infrastructure\\Sqlite\\WriteLockTurns.cs", "", "  "), ["write-gate-database"]);
});

test("every target in the file has a config that lists real files to mutate", () => {
  assert.ok(targets.length > 0);
  for (const target of targets) {
    assert.ok(existsSync(path.join(root, configOf(target).file)), `${target.target}: ${configOf(target).file} exists`);
    assert.ok(mutatePatterns(target, root).length > 0, `${target.target} mutates something`);
  }
  assert.equal(new Set(targets.map((target) => target.target)).size, targets.length, "each target is listed once");
});

test("the outputs are the matrix, the ids and whether anything is selected", () => {
  const selected = selectTargets(["apps/server/src/Weir.Infrastructure/Sqlite/UnitOfWork.cs"], targets, root);
  const outputs = outputsOf(selected);
  assert.equal(outputs.any, "true");
  assert.equal(outputs.targets, "write-gate-unit-of-work");
  assert.deepEqual(JSON.parse(outputs.matrix), { include: selected });
  assert.deepEqual(outputsOf([]), { matrix: '{"include":[]}', targets: "", any: "false" });
});

test("only --all or --changed with a file are accepted", () => {
  assert.deepEqual(parseArguments(["--all"]), { all: true });
  assert.deepEqual(parseArguments(["--changed", "paths.txt"]), { changedFile: "paths.txt" });
  assert.throws(() => parseArguments([]), /--all, or --changed/);
  assert.throws(() => parseArguments(["--changed"]), /--all, or --changed/);
});
