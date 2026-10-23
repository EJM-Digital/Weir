// node --test scripts/align-coverage-reports.test.mjs
// Aligning the Cobertura files two runners wrote, using the Api tests' real reports from a Linux and a Windows CI leg
// (scripts/fixtures/coverage/runners, cut down to two source files).
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { align, alignFolder, checkoutOf } from "./align-coverage-reports.mjs";

const runners = path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "coverage", "runners");
const linux = readFileSync(path.join(runners, "linux.cobertura.xml"), "utf8");
const windows = readFileSync(path.join(runners, "windows.cobertura.xml"), "utf8");

const classNames = (text) => [...text.matchAll(/<class name="([^"]*)"/g)].map((match) => match[1]).sort();
const sources = (text) => [...text.matchAll(/<source>([^<]*)<\/source>/g)].map((match) => match[1]);
const hits = (text) => [...text.matchAll(/<line number="(\d+)" hits="(\d+)"/g)].map((match) => `${match[1]}:${match[2]}`);

test("each runner's report names its own checkout", () => {
  assert.equal(checkoutOf(linux), "/home/runner/work/Weir/Weir");
  assert.equal(checkoutOf(windows), "D:/a/Weir/Weir");
  assert.equal(checkoutOf("<coverage></coverage>"), undefined);
});

test("before aligning, the two runners name the same code differently", () => {
  assert.notDeepEqual(sources(linux), sources(windows));
  assert.notDeepEqual(classNames(linux), classNames(windows), "the file-local type carries a hash of the file's path");
});

test("after aligning, both runners' reports name the same checkout and the same classes", () => {
  const fromLinux = align(linux, "/work/checkout");
  const fromWindows = align(windows, "/work/checkout");
  assert.deepEqual(sources(fromLinux), ["/work/checkout/apps/server/src/"]);
  assert.deepEqual(sources(fromWindows), sources(fromLinux));
  assert.deepEqual(classNames(fromWindows), classNames(fromLinux));
  assert.ok(!/D:|home\/runner/.test(fromLinux + fromWindows), "no runner folder is left");
});

test("aligning changes only names: every line keeps its hit count", () => {
  assert.deepEqual(hits(align(windows, "/work/checkout")), hits(windows));
  assert.deepEqual(hits(align(linux, "/work/checkout")), hits(linux));
});

test("a trailing slash on the checkout, and aligning twice, change nothing more", () => {
  const once = align(windows, "C:\\work\\checkout\\");
  assert.deepEqual(sources(once), ["C:/work/checkout/apps/server/src/"]);
  assert.equal(align(once, "C:/work/checkout"), once);
});

test("absolute file names inside the checkout move with it, and others stay", () => {
  const text = [
    "<coverage><sources><source>D:\\a\\Weir\\Weir\\apps\\tray\\Weir.Tray\\</source></sources>",
    '<class name="A" filename="D:\\a\\Weir\\Weir\\apps\\tray\\Weir.Tray\\A.cs"/>',
    '<class name="B" filename="E:\\elsewhere\\B.cs"/>',
    '<class name="C" filename="Relative/C.cs"/></coverage>',
  ].join("\n");
  const aligned = align(text, "/work/checkout");
  assert.match(aligned, /filename="\/work\/checkout\/apps\/tray\/Weir\.Tray\/A\.cs"/);
  assert.match(aligned, /filename="E:\\elsewhere\\B\.cs"/);
  assert.match(aligned, /filename="Relative\/C\.cs"/);
});

test("a folder of reports is aligned in place, whatever depth they are at, and only the reports", () => {
  const root = mkdtempSync(path.join(tmpdir(), "weir-align-"));
  try {
    for (const [name, text] of [
      ["coverage-server-linux", linux],
      ["coverage-server-windows", windows],
    ]) {
      const folder = path.join(root, name, "Weir.Api.Tests", "TestResults", "a-run");
      mkdirSync(folder, { recursive: true });
      writeFileSync(path.join(folder, "coverage.cobertura.xml"), text);
      writeFileSync(path.join(folder, "other.xml"), text);
    }
    assert.equal(alignFolder(root, "/work/checkout"), 2);
    const read = (name, file = "coverage.cobertura.xml") => readFileSync(path.join(root, name, "Weir.Api.Tests", "TestResults", "a-run", file), "utf8");
    assert.deepEqual(classNames(read("coverage-server-windows")), classNames(read("coverage-server-linux")));
    assert.equal(read("coverage-server-linux", "other.xml"), linux);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// An OpenCover file names its files by full path and has no <source> folder; the shape is coverlet's own.
const openCover = (checkout, separator, hash) => {
  const file = ["apps", "server", "src", "Weir.Infrastructure", "Files", "FileIdentity.cs"].join(separator);
  return `<CoverageSession><Modules><Module><ModuleName>Weir.Infrastructure</ModuleName><Files>` +
    `<File uid="1" fullPath="${checkout}${separator}${file}" /></Files><Classes><Class>` +
    `<FullName>Weir.Infrastructure.&lt;FileIdentity&gt;F${hash}__Probe</FullName></Class></Classes></Module></Modules></CoverageSession>`;
};
const linuxOpenCover = openCover("/home/runner/work/Weir/Weir", "/", "A".repeat(64));
const windowsOpenCover = openCover("D:\\a\\Weir\\Weir","\\", "B".repeat(64));

test("an OpenCover report's checkout is found from its file paths", () => {
  assert.equal(checkoutOf(linuxOpenCover), "/home/runner/work/Weir/Weir");
  assert.equal(checkoutOf(windowsOpenCover), "D:/a/Weir/Weir");
});

test("after aligning, both runners' OpenCover reports name the same files and types", () => {
  const fromLinux = align(linuxOpenCover, "/work/checkout");
  const fromWindows = align(windowsOpenCover, "/work/checkout");
  assert.equal(fromWindows, fromLinux);
  assert.match(fromLinux, /fullPath="\/work\/checkout\/apps\/server\/src\/Weir\.Infrastructure\/Files\/FileIdentity\.cs"/);
});

test("a folder's OpenCover reports are aligned in place as well", () => {
  const root = mkdtempSync(path.join(tmpdir(), "weir-align-"));
  try {
    writeFileSync(path.join(root, "coverage.opencover.xml"), windowsOpenCover);
    assert.equal(alignFolder(root, "/work/checkout"), 1);
    assert.equal(readFileSync(path.join(root, "coverage.opencover.xml"), "utf8"), align(linuxOpenCover, "/work/checkout"));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
