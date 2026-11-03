// node --test scripts/build-brand-icons.test.mjs
import assert from "node:assert/strict";
import path from "node:path";
import { test } from "node:test";
import { deflateSync } from "node:zlib";

import {
  APP_ICON,
  APP_ICON_SMALL,
  OUTPUTS,
  SMALL_ICON_MAX,
  compareImages,
  decodePng,
  packIco,
  parseIco,
  sourceFor,
} from "./build-brand-icons.mjs";

const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

// Stands in for a rendered frame: the packer only needs distinct bytes of a known length per size.
function fakePng(size) {
  return Buffer.concat([PNG_SIGNATURE, Buffer.alloc(size, size)]);
}

// A real, tiny PNG (8-bit RGBA, filter type 0 on every row) made from the given pixels.
function rgbaPng(width, height, pixels) {
  const rows = [];
  for (let y = 0; y < height; y += 1)
    rows.push(Buffer.from([0]), Buffer.from(pixels.slice(y * width * 4, (y + 1) * width * 4)));
  const chunk = (type, body) => {
    const framed = Buffer.alloc(body.length + 12);
    framed.writeUInt32BE(body.length, 0);
    framed.write(type, 4, "latin1");
    body.copy(framed, 8);
    return framed;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 6, 0, 0, 0], 8);
  return Buffer.concat([
    PNG_SIGNATURE,
    chunk("IHDR", header),
    chunk("IDAT", deflateSync(Buffer.concat(rows))),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

test("an .ico reads back with its header, frame count, sizes and offsets", () => {
  const sizes = [64, 16, 256, 32];
  const packed = packIco(new Map(sizes.map((size) => [size, fakePng(size)])));
  assert.deepEqual([...packed.subarray(0, 6)], [0, 0, 1, 0, 4, 0]);

  const frames = parseIco(packed);
  assert.deepEqual(
    frames.map((frame) => frame.size),
    [16, 32, 64, 256],
  );
  let expectedOffset = 6 + 16 * frames.length;
  for (const frame of frames) {
    assert.equal(frame.offset, expectedOffset);
    assert.equal(frame.length, 8 + frame.size);
    assert.deepEqual(frame.png, fakePng(frame.size));
    expectedOffset += frame.length;
  }
  assert.equal(expectedOffset, packed.length);
});

test("a 256px frame is stored as 0 and the entry says 32 bits, one plane", () => {
  const packed = packIco(new Map([[256, fakePng(256)]]));
  assert.equal(packed.readUInt8(6), 0);
  assert.equal(packed.readUInt8(7), 0);
  assert.equal(packed.readUInt16LE(10), 1);
  assert.equal(packed.readUInt16LE(12), 32);
  assert.equal(parseIco(packed)[0].size, 256);
});

test("only the 16px frame comes from the small tile, and only where an output uses it", () => {
  assert.equal(SMALL_ICON_MAX, 16);
  assert.equal(sourceFor(16), APP_ICON_SMALL);
  for (const size of [24, 32, 48, 64, 128, 180, 256]) assert.equal(sourceFor(size), APP_ICON);
  assert.equal(sourceFor(16, false), APP_ICON);
});

test("the favicons use the small 16px tile and the tray icon does not", () => {
  const uses = Object.fromEntries(OUTPUTS.map(({ file, smallFrames }) => [file, smallFrames]));
  assert.deepEqual(uses, {
    "apps/web/public/favicon.ico": true,
    "apps/web/public/apple-touch-icon.png": false,
    "packaging/windows/assets/weir-tray-icon.ico": false,
    "docs-site/static/img/favicon.ico": true,
  });
});

test("every output is a known set of sizes, each .ico opens with its 16px frame", () => {
  assert.deepEqual(
    OUTPUTS.map(({ file }) => path.basename(file)),
    ["favicon.ico", "apple-touch-icon.png", "weir-tray-icon.ico", "favicon.ico"],
  );
  for (const { sizes, format } of OUTPUTS) {
    assert.equal(new Set(sizes).size, sizes.length);
    if (format === "ico") assert.equal(Math.min(...sizes), 16);
  }
});

test("a PNG decodes to its pixels and two images compare channel by channel", () => {
  const red = [255, 0, 0, 255, 0, 0, 0, 0];
  const nearlyRed = [250, 3, 0, 255, 9, 9, 9, 0];
  const a = decodePng(rgbaPng(2, 1, red));
  assert.deepEqual([a.width, a.height, [...a.rgba]], [2, 1, red]);

  assert.deepEqual(compareImages(a, decodePng(rgbaPng(2, 1, red))), { maxDifference: 0, differingPixels: 0 });
  // The colour of a fully transparent pixel is invisible, so only the first pixel counts.
  assert.deepEqual(compareImages(a, decodePng(rgbaPng(2, 1, nearlyRed))), { maxDifference: 5, differingPixels: 1 });
  assert.ok(compareImages(a, decodePng(rgbaPng(1, 2, red))).sizeMismatch);
});
