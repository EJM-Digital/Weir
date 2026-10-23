// node --test scripts/coverage/testindex-branches.test.mjs
// The test index on files made for the test: names cited loosely, tests judged by prefix, and files the index has not got.
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, test } from "node:test";

import { TestIndex, walk } from "./testindex.mjs";

const made = [];
after(() => made.forEach((folder) => rmSync(folder, { recursive: true, force: true })));

function indexOf(files) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-testindex-"));
  made.push(root);
  for (const [file, text] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), text);
  }
  return new TestIndex(root);
}

const cs = (header, ...tests) => `public sealed class T\n{\n${header}\n${tests.map(([name, body]) => `    [Fact]\n    public void ${name}()\n    {\n${body}\n    }\n`).join("\n")}}\n`;
const API = "apps/server/tests/Weir.Api.Tests/T.cs";
const E2E = "apps/server/tests/Weir.E2E.Tests/T.cs";
const CONTRACT = "apps/server/tests/Weir.Contract.Tests/T.cs";

test("the walk leaves out folders no test lives in, and a path it cannot stat", () => {
  const root = mkdtempSync(path.join(tmpdir(), "weir-walk-"));
  made.push(root);
  for (const file of ["a.txt", "node_modules/b.txt", "bin/c.txt", "gone.txt"]) {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), "x");
  }
  const stat = (file) => {
    if (file.endsWith("gone.txt")) throw Object.assign(new Error("vanished"), { code: "ENOENT" });
    return { isDirectory: () => !file.endsWith(".txt") };
  };
  const found = walk(root, () => true, [], stat).map((file) => path.basename(file));
  assert.deepEqual(found, ["a.txt"]);
});

test("a cited name is found by its exact name, loosely by its words, and never when the file or the words are not there", () => {
  const index = indexOf({ "apps/web/src/pages/thing.test.tsx": 'it("opens the thing", () => {});\ndescribe("the other group", () => {});\n' });
  const file = "apps/web/src/pages/thing.test.tsx";
  assert.equal(index.hasName("apps/web/src/missing.test.tsx", "anything"), false);
  assert.equal(index.hasName(file, "*"), true);
  assert.equal(index.hasName(file, "opens the thing"), true);
  assert.equal(index.hasName(file, "Opens The Thing!"), true, "the same words in other case and punctuation");
  assert.equal(index.hasName(file, "?!"), false, "a name with no letters or digits names nothing");
  assert.equal(index.hasName(file, "something else entirely"), false);
});

test("a packaging script that does not start the container is refused in the words of its name, or of the file when it has no step name", () => {
  const index = indexOf({ "scripts/docker-thing.sh": "echo hello\n" });
  assert.equal(index.containerSmoke("scripts/docker-thing.sh", "").why, "scripts/docker-thing.sh does not start the container image with docker run or docker compose up");
  assert.equal(index.containerSmoke("scripts/docker-thing.sh", "the smoke").why, "the smoke does not start the container image with docker run or docker compose up");
});

test("a class with no test in it, or a name no test starts with, has no test to judge as real-fs", () => {
  const index = indexOf({ [API]: cs("", ["Writes_the_file", "File.WriteAllText(Path.Combine(Path.GetTempPath(), \"a\"), \"x\");"]), "apps/server/tests/Weir.Core.Tests/Empty.cs": "public sealed class Empty { }\n" });
  assert.deepEqual(index.realFs(API, "No_such_test"), { real: false, why: "no test found to judge" });
  assert.deepEqual(index.realFs("apps/server/tests/Weir.Core.Tests/Empty.cs", "*"), { real: false, why: "no test found to judge" });
  assert.equal(index.realFs(API, "Writes_the_file").real, true);
  assert.equal(index.realFs(API, "Writes").real, true, "a fragment that starts a test's name is judged over the tests it starts");
});

test("whether an e2e test is stubbed is decided from its body or from the one a longer title starts with, and left undecided otherwise", () => {
  const index = indexOf({
    [E2E]: cs("", ["Opens_the_page_for_real", "await page.GotoAsync(url);"], ["Opens_the_page_with_a_stub", "await page.RouteAsync(url, h);"]),
  });
  assert.deepEqual(index.stubInfo("apps/server/tests/Weir.E2E.Tests/Missing.cs", "x"), { stubbed: true, decided: false });
  assert.deepEqual(index.stubInfo("apps/web/src/a.test.tsx", "x"), { stubbed: true, decided: false });
  assert.deepEqual(index.stubInfo(E2E, "Opens_the_page_for_real"), { stubbed: false, decided: true });
  assert.deepEqual(index.stubInfo(E2E, "Opens the page with"), { stubbed: true, decided: true }, "a fragment is judged by the test it starts");
  assert.deepEqual(index.stubInfo(E2E, "Never_heard_of_it"), { stubbed: true, decided: false });
});

test("a test replays a recording when the class loads one, or its own body does, or a test its fragment starts does", () => {
  const index = indexOf({
    [CONTRACT]: cs("", ["Replays_a_reply", "var r = RecordedDeluno.Load();"], ["Calls_the_server", "var r = 1;"]),
    "apps/server/tests/Weir.Contract.Tests/Setup.cs": cs("var shared = RecordedDeluno.Load();", ["Anything", "var r = 1;"]),
  });
  assert.equal(index.replaysRecording("apps/server/tests/Weir.Contract.Tests/Missing.cs", "x"), false);
  assert.equal(index.replaysRecording("apps/web/src/a.test.tsx", "x"), false);
  assert.equal(index.replaysRecording("apps/server/tests/Weir.Contract.Tests/Setup.cs", "Anything"), true, "loaded in the class setup");
  assert.equal(index.replaysRecording(CONTRACT, "*"), true);
  assert.equal(index.replaysRecording(CONTRACT, undefined), true);
  assert.equal(index.replaysRecording(CONTRACT, "Replays_a_reply"), true);
  assert.equal(index.replaysRecording(CONTRACT, "Calls_the_server"), false);
  assert.equal(index.replaysRecording(CONTRACT, "Replays a"), true, "a fragment that starts a replaying test");
  assert.equal(index.replaysRecording(CONTRACT, "Calls the"), false, "a fragment that starts a test that replays nothing");
  assert.equal(index.replaysRecording(CONTRACT, "Nothing like it"), false);
});
