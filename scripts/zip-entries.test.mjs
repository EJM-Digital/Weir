// Reading what a zip holds without unpacking it, and telling two zips apart by their files and not by their bytes.
import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { crc32 } from "node:zlib";

import { differencesInEntries, zipEntries } from "./scenarios-ci/zip-entries.mjs";

/** A zip of stored (uncompressed) files, and of folders for the names that end in a slash, laid out as the zip format has it. */
function zipOf(files, { comment = "" } = {}) {
  const parts = [];
  const directory = [];
  let offset = 0;
  for (const [name, content] of Object.entries(files)) {
    const nameBytes = Buffer.from(name);
    const data = Buffer.from(content);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt32LE(crc32(data), 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBytes.length, 26);
    const entry = Buffer.alloc(46);
    entry.writeUInt32LE(0x02014b50, 0);
    entry.writeUInt16LE(20, 4);
    entry.writeUInt16LE(20, 6);
    entry.writeUInt32LE(crc32(data), 16);
    entry.writeUInt32LE(data.length, 20);
    entry.writeUInt32LE(data.length, 24);
    entry.writeUInt16LE(nameBytes.length, 28);
    entry.writeUInt32LE(offset, 42);
    directory.push(entry, nameBytes);
    parts.push(local, nameBytes, data);
    offset += local.length + nameBytes.length + data.length;
  }
  const central = Buffer.concat(directory);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(Object.keys(files).length, 8);
  end.writeUInt16LE(Object.keys(files).length, 10);
  end.writeUInt32LE(central.length, 12);
  end.writeUInt32LE(offset, 16);
  end.writeUInt16LE(Buffer.byteLength(comment), 20);
  return Buffer.concat([...parts, central, end, Buffer.from(comment)]);
}

async function withZip(files, options, use) {
  const folder = await mkdtemp(join(tmpdir(), "weir-zip-"));
  try {
    const path = join(folder, "a.zip");
    await writeFile(path, zipOf(files, options));
    return await use(path);
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
}

test("a zip's files are listed with their size and CRC-32, and its folders are left out", async () => {
  await withZip({ "lib/": "", "lib/Weir.dll": "binary", "[Content_Types].xml": "<xml/>" }, {}, async (path) => {
    const entries = await zipEntries(path);
    assert.deepEqual([...entries.keys()], ["lib/Weir.dll", "[Content_Types].xml"]);
    assert.deepEqual(entries.get("lib/Weir.dll"), { size: 6, crc32: crc32(Buffer.from("binary")) });
  });
});

test("a comment after the directory does not hide it", async () => {
  await withZip({ "a.txt": "a" }, { comment: "written by a test" }, async (path) => {
    assert.deepEqual([...(await zipEntries(path)).keys()], ["a.txt"]);
  });
});

test("something that is not a zip is refused, saying so", async () => {
  const folder = await mkdtemp(join(tmpdir(), "weir-zip-"));
  try {
    const path = join(folder, "not.zip");
    await writeFile(path, "this is plain text, long enough to hold an end of directory record if it had one");
    await assert.rejects(() => zipEntries(path), /not a zip file/);
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

test("two zips with the same files are the same however they are ordered, and a missing, extra or changed file is named", async () => {
  const published = await withZip({ "a.dll": "one", "b.dll": "two", "c.dll": "three" }, {}, zipEntries);
  const rebuilt = await withZip({ "c.dll": "three", "b.dll": "two", "a.dll": "one" }, {}, zipEntries);
  assert.deepEqual(differencesInEntries(published, rebuilt), []);
  const different = await withZip({ "a.dll": "ONE", "c.dll": "three", "d.dll": "four" }, {}, zipEntries);
  assert.deepEqual(differencesInEntries(published, different), ["a.dll holds different bytes", "b.dll is missing", "d.dll is not in the published package"]);
});
