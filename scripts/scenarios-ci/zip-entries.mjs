// What a zip file holds, read from its central directory without unpacking it: each file's name, size and CRC-32. A Velopack package is a zip;
// the one the tray rebuilds from a delta is written again, so it is compared with the published one by what it holds, not by its bytes.
import { open } from "node:fs/promises";

const END_OF_DIRECTORY = 0x06054b50;
const ENTRY = 0x02014b50;
const END_RECORD_BYTES = 22;
const LONGEST_COMMENT_BYTES = 0xffff;

/** @returns {Promise<Map<string, {size: number, crc32: number}>>} the files of the zip (not its folders), by name */
export async function zipEntries(path) {
  const file = await open(path, "r");
  try {
    const { size } = await file.stat();
    const tailBytes = Math.min(size, END_RECORD_BYTES + LONGEST_COMMENT_BYTES);
    const tail = Buffer.alloc(tailBytes);
    await file.read(tail, 0, tailBytes, size - tailBytes);
    let end = tail.length - END_RECORD_BYTES;
    while (end >= 0 && tail.readUInt32LE(end) !== END_OF_DIRECTORY) end -= 1;
    if (end < 0) throw new Error(`${path} is not a zip file: it has no end of directory.`);
    const count = tail.readUInt16LE(end + 10);
    const directoryBytes = tail.readUInt32LE(end + 12);
    const directoryAt = tail.readUInt32LE(end + 16);
    if (count === 0xffff || directoryBytes === 0xffffffff || directoryAt === 0xffffffff) throw new Error(`${path} is a zip64 file, which this does not read.`);
    const directory = Buffer.alloc(directoryBytes);
    await file.read(directory, 0, directoryBytes, directoryAt);
    const entries = new Map();
    let at = 0;
    for (let index = 0; index < count; index += 1) {
      if (directory.readUInt32LE(at) !== ENTRY) throw new Error(`${path} has a damaged directory at entry ${index}.`);
      const nameBytes = directory.readUInt16LE(at + 28);
      const extraBytes = directory.readUInt16LE(at + 30);
      const commentBytes = directory.readUInt16LE(at + 32);
      const name = directory.toString("utf8", at + 46, at + 46 + nameBytes);
      if (!name.endsWith("/")) entries.set(name, { size: directory.readUInt32LE(at + 24), crc32: directory.readUInt32LE(at + 16) });
      at += 46 + nameBytes + extraBytes + commentBytes;
    }
    return entries;
  } finally {
    await file.close();
  }
}

/** What differs between two zips' contents, in words; empty when they hold the same files. */
export function differencesInEntries(expected, actual) {
  const findings = [];
  for (const [name, entry] of expected) {
    const found = actual.get(name);
    if (!found) findings.push(`${name} is missing`);
    else if (found.size !== entry.size || found.crc32 !== entry.crc32) findings.push(`${name} holds different bytes`);
  }
  for (const name of actual.keys()) if (!expected.has(name)) findings.push(`${name} is not in the published package`);
  return findings;
}
