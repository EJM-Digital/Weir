// node --test scripts/check-wall-clock-waits.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { findWaits, judge } from "./check-wall-clock-waits.mjs";

const kinds = (kind, source) => findWaits(kind, source).map((wait) => wait.kind);

test("a delay, a sleep and Playwright's timeout wait in a C# test are found, with their lines", () => {
  const source = "await Task.Delay(50);\nThread.Sleep(10);\nawait page.WaitForTimeoutAsync(2000);\n";
  assert.deepEqual(findWaits("csharp", source).map(({ kind, line }) => [kind, line]), [
    ["delay", 1],
    ["delay", 2],
    ["delay", 3],
  ]);
});

test("a word in a C# comment or a string is not a wait", () => {
  assert.deepEqual(kinds("csharp", "// await Task.Delay(50);\n/* Thread.Sleep(1) */\nvar url = \"http://x/Task.Delay\";\n"), []);
});

test("a bare promise sleep in a web test is found, however its resolver is named", () => {
  assert.deepEqual(kinds("web", "await new Promise((resolve) => setTimeout(resolve, 50));"), ["sleep"]);
  assert.deepEqual(kinds("web", "await new Promise(r => window.setTimeout(r, 50));"), ["sleep"]);
  assert.deepEqual(kinds("web", "await new Promise<void>((done) => setTimeout(done, 0));"), ["sleep"]);
});

test("a promise that is not a sleep is not found", () => {
  assert.deepEqual(kinds("web", "const gate = new Promise<void>((resolve) => (release = resolve));"), []);
});

test("a fake clock that keeps running with real time is found", () => {
  assert.deepEqual(kinds("web", "vi.useFakeTimers({ shouldAdvanceTime: true });"), ["running-clock"]);
});

test("a wait over a second is found unless a hang guard is named beside it", () => {
  assert.deepEqual(kinds("web", "await screen.findByText('x', {}, { timeout: 3000 });"), ["long-wait"]);
  assert.deepEqual(kinds("web", "await waitFor(() => ok(), { timeout: 10_000 });"), ["long-wait"]);
  assert.deepEqual(kinds("web", "await waitFor(() => ok(), { timeout: 1000 });"), []);
  assert.deepEqual(kinds("web", "// A hang guard, not a pace.\nexport const CEILING = { timeout: 10_000 };"), []);
  assert.deepEqual(kinds("web", "await waitFor(() => ok(), { timeout: 5000 }); // hang guard"), []);
});

test("Start-Sleep in a PowerShell script is found, and in a comment it is not", () => {
  assert.deepEqual(kinds("script", "Start-Sleep -Seconds 2\n"), ["sleep"]);
  assert.deepEqual(kinds("script", "# Start-Sleep -Seconds 2\n$x = 1 # Start-Sleep\n<# Start-Sleep #>\n"), []);
});

test("a sleep in a Node script's test is found", () => {
  assert.deepEqual(kinds("node", "await new Promise((done) => setTimeout(done, 100));"), ["sleep"]);
});

const wait = (kind, line = 1) => ({ kind, line, text: "Task.Delay(" });
const reason = "A poll that ends the moment its condition holds and gives up at a ceiling.";

test("a wait in no list is a finding, named by its file and line", () => {
  const { findings, stale } = judge(new Map([["a/B.cs", [wait("delay", 7)]]]), []);
  assert.equal(findings.length, 1);
  assert.match(findings[0], /^a\/B\.cs:7: /);
  assert.deepEqual(stale, []);
});

test("a listed wait passes", () => {
  const { findings, stale } = judge(new Map([["a/B.cs", [wait("delay")]]]), [{ file: "a/B.cs", kind: "delay", count: 1, reason }]);
  assert.deepEqual([findings, stale], [[], []]);
});

test("a second wait in a file that lists one is a finding", () => {
  const { findings } = judge(new Map([["a/B.cs", [wait("delay", 3), wait("delay", 9)]]]), [{ file: "a/B.cs", kind: "delay", count: 1, reason }]);
  assert.equal(findings.length, 1);
  assert.match(findings[0], /^a\/B\.cs:9: /);
});

test("a wait of another kind in a listed file is a finding", () => {
  const { findings } = judge(new Map([["a/B.cs", [wait("delay"), wait("sleep", 4)]]]), [{ file: "a/B.cs", kind: "delay", count: 1, reason }]);
  assert.equal(findings.length, 1);
});

test("an entry for a wait that is gone, or that has fewer than it lists, is stale", () => {
  const gone = judge(new Map(), [{ file: "a/B.cs", kind: "delay", count: 1, reason }]);
  assert.equal(gone.stale.length, 1);
  const fewer = judge(new Map([["a/B.cs", [wait("delay")]]]), [{ file: "a/B.cs", kind: "delay", count: 2, reason }]);
  assert.equal(fewer.stale.length, 1);
});

test("an entry with no real reason fails", () => {
  const { stale } = judge(new Map([["a/B.cs", [wait("delay")]]]), [{ file: "a/B.cs", kind: "delay", count: 1, reason: "needed" }]);
  assert.equal(stale.length, 1);
  assert.match(stale[0], /gives no reason/);
});
