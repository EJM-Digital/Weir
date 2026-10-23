// node --test scripts/build-brand-icons-render.test.mjs
// Reading PNGs of every filter, finding the renderer, and rendering and checking the icons with a stand-in renderer.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { deflateSync } from "node:zlib";

import { OUTPUTS, APP_ICON, APP_ICON_SMALL, buildAll, check, decodePng, loadResvg, main, packIco, parseIco } from "./build-brand-icons.mjs";

const SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

function chunk(type, body) {
  const framed = Buffer.alloc(body.length + 12);
  framed.writeUInt32BE(body.length, 0);
  framed.write(type, 4, "latin1");
  body.copy(framed, 8);
  return framed;
}

const paeth = (left, up, upLeft) => {
  const estimate = left + up - upLeft;
  const toLeft = Math.abs(estimate - left);
  const toUp = Math.abs(estimate - up);
  const toUpLeft = Math.abs(estimate - upLeft);
  return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
};

/** A PNG of the given samples (`channels` per pixel, row by row) with each row filtered as `filters[row]`, as the PNG specification defines the filters. */
function encodePng(width, height, samples, { channels = 4, filters = [0], colorType = channels === 4 ? 6 : 2, depth = 8, interlace = 0 } = {}) {
  const stride = width * channels;
  const rows = [];
  for (let y = 0; y < height; y += 1) {
    const filter = filters[y % filters.length];
    const row = Buffer.alloc(stride + 1);
    row[0] = filter;
    for (let x = 0; x < stride; x += 1) {
      const at = (row_, column) => (row_ < 0 || column < 0 ? 0 : samples[row_ * stride + column]);
      const left = at(y, x - channels);
      const up = at(y - 1, x);
      const upLeft = at(y - 1, x - channels);
      const predicted = [0, left, up, (left + up) >> 1, paeth(left, up, upLeft)][filter] ?? 0;
      row[x + 1] = (samples[y * stride + x] - predicted) & 0xff;
    }
    rows.push(row);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([depth, colorType, 0, 0, interlace], 8);
  return Buffer.concat([SIGNATURE, chunk("IHDR", header), chunk("IDAT", deflateSync(Buffer.concat(rows))), chunk("IEND", Buffer.alloc(0))]);
}

const gradient = (count) => Array.from({ length: count }, (_, index) => (index * 37 + 11) % 256);

for (const [name, filter] of [["none", 0], ["sub", 1], ["up", 2], ["average", 3], ["paeth", 4]]) {
  test(`a PNG whose rows use the ${name} filter decodes to the pixels it was made from`, () => {
    const samples = gradient(3 * 3 * 4);
    const decoded = decodePng(encodePng(3, 3, samples, { filters: [filter] }));
    assert.deepEqual([decoded.width, decoded.height, [...decoded.rgba]], [3, 3, samples]);
  });
}

test("a PNG with no alpha channel decodes to opaque pixels", () => {
  const samples = gradient(2 * 2 * 3);
  const decoded = decodePng(encodePng(2, 2, samples, { channels: 3, filters: [1, 4] }));
  const expected = [];
  for (let pixel = 0; pixel < 4; pixel += 1) expected.push(...samples.slice(pixel * 3, pixel * 3 + 3), 255);
  assert.deepEqual([...decoded.rgba], expected);
});

test("what is not a PNG, an icon, or a PNG of a kind it reads is refused", () => {
  assert.throws(() => decodePng(Buffer.from("GIF89a and then some")), /Not a PNG/);
  assert.throws(() => parseIco(Buffer.alloc(22)), /Not an \.ico file/);
  assert.throws(() => decodePng(encodePng(1, 1, [1], { channels: 1, colorType: 0 })), /Unsupported PNG \(bit depth 8, colour type 0, interlace 0\)/);
  assert.throws(() => decodePng(encodePng(1, 1, [1, 2, 3, 4], { depth: 16 })), /bit depth 16/);
  assert.throws(() => decodePng(encodePng(1, 1, [1, 2, 3, 4], { interlace: 1 })), /interlace 1/);
  assert.throws(() => decodePng(encodePng(1, 1, [1, 2, 3, 4], { filters: [9] })), /Unknown PNG filter 9/);
});

/** Renders a flat square of one colour at the size asked for, and remembers what it was asked to render. */
function standIn(colour = [10, 20, 30, 255]) {
  const asked = [];
  class Resvg {
    constructor(svg, options) {
      this.size = options.fitTo.value;
      asked.push({ svg, size: this.size });
    }
    render() {
      const pixels = Array.from({ length: this.size * this.size }, () => colour).flat();
      return { asPng: () => encodePng(this.size, this.size, pixels) };
    }
  }
  return { Resvg, asked };
}

function withFolder(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-brand-icons-test-"));
  try {
    return callback(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("the renderer is the web app's package, and its absence says how to install it", () => {
  withFolder((root) => {
    assert.throws(() => loadResvg(root), /@resvg\/resvg-js is not installed\. Run `npm ci` in apps\/web first\./);
    const package_ = path.join(root, "apps", "web", "node_modules", "@resvg", "resvg-js");
    mkdirSync(package_, { recursive: true });
    writeFileSync(path.join(package_, "index.js"), "exports.Resvg = class Resvg {};");
    assert.equal(loadResvg(root).name, "Resvg");
  });
});

test("every icon is rendered at its sizes, the 16px frame from the small tile, and written where the repository keeps it", () => {
  withFolder((root) => {
    const { Resvg, asked } = standIn();

    const written = buildAll(root, Resvg);

    assert.deepEqual(written, OUTPUTS.map(({ file }) => file));
    const favicon = parseIco(readFileSync(path.join(root, OUTPUTS[0].file)));
    assert.deepEqual(favicon.map(({ size }) => size), [16, 24, 32, 48, 64, 256]);
    assert.deepEqual(decodePng(favicon[0].png).width, 16);
    assert.equal(decodePng(readFileSync(path.join(root, OUTPUTS[1].file))).width, 180);
    const smallTile = readFileSync(APP_ICON_SMALL, "utf8");
    const bigTile = readFileSync(APP_ICON, "utf8");
    assert.ok(asked.filter(({ size }) => size === 16).every(({ svg }) => svg === smallTile));
    assert.ok(asked.filter(({ size }) => size > 16).every(({ svg }) => svg === bigTile));
  });
});

function committedIcons(root, colour) {
  buildAll(root, standIn(colour).Resvg);
}

test("committed icons that match a fresh render pass", () => {
  withFolder((root) => {
    committedIcons(root);
    const lines = [];

    const code = check(96, { Resvg: standIn().Resvg, root, log: (line) => lines.push(line), error: () => assert.fail("nothing to complain about") });

    assert.equal(code, 0);
    assert.ok(lines.includes("ok   apps/web/public/favicon.ico"));
    assert.ok(lines.includes("  16px: largest channel difference 0, 0 of 256 pixels differ"));
    assert.match(lines.at(-1), /The committed icons match a fresh render within 96 per channel\./);
  });
});

test("an icon that is not the render of the mark any more fails, naming the frame and the command that fixes it", () => {
  withFolder((root) => {
    committedIcons(root, [200, 20, 30, 255]);
    const lines = [];
    const errors = [];

    const code = check(96, { Resvg: standIn().Resvg, root, log: (line) => lines.push(line), error: (line) => errors.push(line) });

    assert.equal(code, 1);
    assert.ok(lines.includes("FAIL apps/web/public/favicon.ico"));
    assert.ok(lines.includes("  16px: largest channel difference 190, 256 of 256 pixels differ  <-- over tolerance"));
    assert.match(errors[0], /The committed icons are not current \(tolerance 96\)\. Run node scripts\/build-brand-icons\.mjs\./);
  });
});

test("a frame in only one of the two files, or drawn at another pixel size, fails", () => {
  withFolder((root) => {
    committedIcons(root);
    const fresh = standIn().Resvg;
    const frame = (size) => encodePng(size, size, Array.from({ length: size * size }, () => [10, 20, 30, 255]).flat());
    writeFileSync(path.join(root, OUTPUTS[0].file), packIco(new Map([[16, frame(16)], [20, frame(20)], [32, frame(16)], [24, frame(24)], [48, frame(48)], [64, frame(64)], [256, frame(256)]])));
    const lines = [];

    const code = check(96, { Resvg: fresh, root, log: (line) => lines.push(line), error: () => {} });

    assert.equal(code, 1);
    assert.ok(lines.includes("  20px: not in a fresh render"));
    assert.ok(lines.includes("  32px: pixel size differs (32x32 against 16x16)  <-- over tolerance"));

    writeFileSync(path.join(root, OUTPUTS[0].file), packIco(new Map([[16, frame(16)]])));
    const missing = [];
    check(96, { Resvg: fresh, root, log: (line) => missing.push(line), error: () => {} });
    assert.ok(missing.includes("  24px: missing from the committed file"));
  });
});

test("the command renders into the repository, checks with --check, and refuses a tolerance that is not 0 to 255", () => {
  withFolder((root) => {
    const { Resvg } = standIn();
    const lines = [];
    const errors = [];
    const dependencies = { root, Resvg, log: (line) => lines.push(line), error: (line) => errors.push(line) };

    assert.equal(main([], dependencies), 0);
    assert.match(lines[0], /^Brand icons rendered from packaging[\\/]brand:\n {2}apps\/web\/public\/favicon\.ico\n/);

    assert.equal(main(["--check"], dependencies), 0);
    assert.equal(main(["--check", "--tolerance=0"], dependencies), 0);
    assert.equal(main(["--tolerance=256"], dependencies), 2);
    assert.equal(main(["--tolerance=1.5"], dependencies), 2);
    assert.equal(main(["--tolerance=-1"], dependencies), 2);
    assert.deepEqual(errors, Array(3).fill("--tolerance must be a whole number from 0 to 255."));
  });
});

test("run as a command it exits 2 for a tolerance out of range", () => {
  const script = fileURLToPath(new URL("./build-brand-icons.mjs", import.meta.url));
  const result = spawnSync(process.execPath, [script, "--tolerance=300"], { encoding: "utf8" });
  assert.equal(result.status, 2);
  assert.match(result.stderr, /--tolerance must be a whole number/);
});
