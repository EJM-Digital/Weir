#!/usr/bin/env node
// Renders every raster Weir icon from the brand SVGs in packaging/brand.
//
// The SVGs are the source of truth; the rasters are committed so nothing needs regenerating to build Weir. Run this
// after changing the mark. The .ico files are PNG-framed, the format Windows has read since Vista.
//
// Usage:
//   node scripts/build-brand-icons.mjs            render and overwrite the committed icons
//   node scripts/build-brand-icons.mjs --check    render to a temp folder, compare with the committed icons,
//                                                 change nothing (--tolerance=N sets the largest accepted
//                                                 per-channel difference, default 96)
//
// Rendering needs @resvg/resvg-js, a dev dependency of apps/web: run `npm ci` there first.
//
// Two optical sizes: the 16px frame of every .ico comes from weir-app-icon-small.svg (two streams), every larger
// frame from weir-app-icon.svg (three streams). At 16px a band is sub-pixel and three streams fuse into a smear.
// See packaging/brand/README.md.
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { inflateSync } from "node:zlib";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const BRAND = path.join(ROOT, "packaging", "brand");
export const APP_ICON = path.join(BRAND, "weir-app-icon.svg");
export const APP_ICON_SMALL = path.join(BRAND, "weir-app-icon-small.svg");

// Frames at or below this size render from the small tile.
export const SMALL_ICON_MAX = 16;

const FAVICON_SIZES = [16, 24, 32, 48, 64, 256];
const TRAY_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const TOUCH_ICON_SIZE = 180;

// Every raster icon: its path under the repository root and the frame sizes it holds.
export const OUTPUTS = [
  { file: "apps/web/public/favicon.ico", format: "ico", sizes: FAVICON_SIZES },
  { file: "apps/web/public/apple-touch-icon.png", format: "png", sizes: [TOUCH_ICON_SIZE] },
  { file: "packaging/windows/assets/weir-tray-icon.ico", format: "ico", sizes: TRAY_SIZES },
  { file: "docs-site/static/img/favicon.ico", format: "ico", sizes: FAVICON_SIZES },
];

// A different rasteriser moves anti-aliased edge pixels by up to about 90 of 255; a changed mark moves solid ones by more.
const DEFAULT_TOLERANCE = 96;
const ICO_HEADER_BYTES = 6;
const ICO_ENTRY_BYTES = 16;
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

export function sourceFor(size) {
  return size <= SMALL_ICON_MAX ? APP_ICON_SMALL : APP_ICON;
}

// frames: Map of pixel size -> PNG bytes. A size of 256 is stored as 0, as the format requires.
export function packIco(frames) {
  const sizes = [...frames.keys()].sort((a, b) => a - b);
  const header = Buffer.alloc(ICO_HEADER_BYTES);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);
  const entries = Buffer.alloc(ICO_ENTRY_BYTES * sizes.length);
  let offset = ICO_HEADER_BYTES + entries.length;
  sizes.forEach((size, index) => {
    const png = frames.get(size);
    const at = index * ICO_ENTRY_BYTES;
    const dimension = size >= 256 ? 0 : size;
    entries.writeUInt8(dimension, at);
    entries.writeUInt8(dimension, at + 1);
    entries.writeUInt16LE(1, at + 4);
    entries.writeUInt16LE(32, at + 6);
    entries.writeUInt32LE(png.length, at + 8);
    entries.writeUInt32LE(offset, at + 12);
    offset += png.length;
  });
  return Buffer.concat([header, entries, ...sizes.map((size) => frames.get(size))]);
}

// The inverse of packIco: [{ size, offset, length, png }] in file order.
export function parseIco(buffer) {
  if (buffer.readUInt16LE(0) !== 0 || buffer.readUInt16LE(2) !== 1) throw new Error("Not an .ico file.");
  const count = buffer.readUInt16LE(4);
  const frames = [];
  for (let index = 0; index < count; index += 1) {
    const at = ICO_HEADER_BYTES + index * ICO_ENTRY_BYTES;
    const length = buffer.readUInt32LE(at + 8);
    const offset = buffer.readUInt32LE(at + 12);
    frames.push({ size: buffer.readUInt8(at) || 256, offset, length, png: buffer.subarray(offset, offset + length) });
  }
  return frames;
}

// Decodes an 8-bit, non-interlaced RGB or RGBA PNG to RGBA pixels.
export function decodePng(buffer) {
  if (!buffer.subarray(0, 8).equals(PNG_SIGNATURE)) throw new Error("Not a PNG.");
  let header;
  const compressed = [];
  for (let at = 8; at < buffer.length;) {
    const length = buffer.readUInt32BE(at);
    const type = buffer.toString("latin1", at + 4, at + 8);
    const body = buffer.subarray(at + 8, at + 8 + length);
    if (type === "IHDR") header = body;
    if (type === "IDAT") compressed.push(body);
    at += length + 12;
  }
  const width = header.readUInt32BE(0);
  const height = header.readUInt32BE(4);
  const [depth, colorType, , , interlace] = header.subarray(8, 13);
  const channels = colorType === 6 ? 4 : colorType === 2 ? 3 : 0;
  if (depth !== 8 || channels === 0 || interlace !== 0) {
    throw new Error(`Unsupported PNG (bit depth ${depth}, colour type ${colorType}, interlace ${interlace}).`);
  }
  const raw = inflateSync(Buffer.concat(compressed));
  const stride = width * channels;
  const rows = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y += 1) {
    const filter = raw[y * (stride + 1)];
    for (let x = 0; x < stride; x += 1) {
      const left = x >= channels ? rows[y * stride + x - channels] : 0;
      const up = y > 0 ? rows[(y - 1) * stride + x] : 0;
      const upLeft = x >= channels && y > 0 ? rows[(y - 1) * stride + x - channels] : 0;
      rows[y * stride + x] = (raw[y * (stride + 1) + 1 + x] + predict(filter, left, up, upLeft)) & 0xff;
    }
  }
  const rgba = Buffer.alloc(width * height * 4, 0xff);
  for (let pixel = 0; pixel < width * height; pixel += 1) {
    for (let channel = 0; channel < channels; channel += 1)
      rgba[pixel * 4 + channel] = rows[pixel * channels + channel];
  }
  return { width, height, rgba };
}

function predict(filter, left, up, upLeft) {
  switch (filter) {
    case 0:
      return 0;
    case 1:
      return left;
    case 2:
      return up;
    case 3:
      return (left + up) >> 1;
    case 4: {
      const estimate = left + up - upLeft;
      const toLeft = Math.abs(estimate - left);
      const toUp = Math.abs(estimate - up);
      const toUpLeft = Math.abs(estimate - upLeft);
      return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }
    default:
      throw new Error(`Unknown PNG filter ${filter}.`);
  }
}

// Largest per-channel difference between two decoded images, and how many pixels differ at all. The colour of a fully
// transparent pixel is not visible, so it is not compared.
export function compareImages(a, b) {
  if (a.width !== b.width || a.height !== b.height) {
    return {
      sizeMismatch: `${a.width}x${a.height} against ${b.width}x${b.height}`,
      maxDifference: 255,
      differingPixels: 0,
    };
  }
  let maxDifference = 0;
  let differingPixels = 0;
  for (let at = 0; at < a.rgba.length; at += 4) {
    const bothClear = a.rgba[at + 3] === 0 && b.rgba[at + 3] === 0;
    let pixelMax = 0;
    for (let channel = bothClear ? 3 : 0; channel < 4; channel += 1) {
      pixelMax = Math.max(pixelMax, Math.abs(a.rgba[at + channel] - b.rgba[at + channel]));
    }
    if (pixelMax > 0) differingPixels += 1;
    maxDifference = Math.max(maxDifference, pixelMax);
  }
  return { maxDifference, differingPixels };
}

// The renderer is a dev dependency of the web app, looked for under `root`.
export function loadResvg(root = ROOT) {
  try {
    return createRequire(path.join(root, "apps", "web", "package.json"))("@resvg/resvg-js").Resvg;
  } catch {
    throw new Error("@resvg/resvg-js is not installed. Run `npm ci` in apps/web first.");
  }
}

function renderPng(Resvg, svgFile, size) {
  const svg = readFileSync(svgFile, "utf8");
  return Buffer.from(new Resvg(svg, { fitTo: { mode: "width", value: size } }).render().asPng());
}

// Renders every output under outputRoot, laid out as it is in the repository.
export function buildAll(outputRoot, Resvg = loadResvg()) {
  const written = [];
  for (const { file, format, sizes } of OUTPUTS) {
    const frames = new Map(sizes.map((size) => [size, renderPng(Resvg, sourceFor(size), size)]));
    const target = path.join(outputRoot, file);
    mkdirSync(path.dirname(target), { recursive: true });
    writeFileSync(target, format === "ico" ? packIco(frames) : frames.get(sizes[0]));
    written.push(file);
  }
  return written;
}

function framesOf({ format }, buffer) {
  return format === "ico"
    ? parseIco(buffer).map(({ size, png }) => ({ size, png }))
    : [{ size: decodePng(buffer).width, png: buffer }];
}

// Compares one rendered output with the committed one; returns a line per frame and whether it is within tolerance.
export function compareOutput(output, renderedPath, committedPath, tolerance) {
  const rendered = framesOf(output, readFileSync(renderedPath));
  const committed = framesOf(output, readFileSync(committedPath));
  const lines = [];
  let ok = true;
  const sizes = [...new Set([...rendered, ...committed].map((frame) => frame.size))].sort((a, b) => a - b);
  for (const size of sizes) {
    const mine = rendered.find((frame) => frame.size === size);
    const theirs = committed.find((frame) => frame.size === size);
    if (!mine || !theirs) {
      ok = false;
      lines.push(`  ${size}px: ${mine ? "missing from the committed file" : "not in a fresh render"}`);
      continue;
    }
    const result = compareImages(decodePng(mine.png), decodePng(theirs.png));
    const within = !result.sizeMismatch && result.maxDifference <= tolerance;
    ok &&= within;
    const detail = result.sizeMismatch
      ? `pixel size differs (${result.sizeMismatch})`
      : `largest channel difference ${result.maxDifference}, ${result.differingPixels} of ${size * size} pixels differ`;
    lines.push(`  ${size}px: ${detail}${within ? "" : "  <-- over tolerance"}`);
  }
  return { ok, lines };
}

// Renders into a temp folder and compares with the committed icons under `root`; returns the exit code.
export function check(tolerance, { Resvg = loadResvg(), root = ROOT, log = console.log, error = console.error } = {}) {
  const folder = mkdtempSync(path.join(os.tmpdir(), "weir-brand-icons-"));
  try {
    buildAll(folder, Resvg);
    let allOk = true;
    for (const output of OUTPUTS) {
      const { ok, lines } = compareOutput(output, path.join(folder, output.file), path.join(root, output.file), tolerance);
      allOk &&= ok;
      log(`${ok ? "ok  " : "FAIL"} ${output.file}`);
      for (const line of lines) log(line);
    }
    if (!allOk) {
      error(`\nThe committed icons are not current (tolerance ${tolerance}). Run node scripts/build-brand-icons.mjs.`);
      return 1;
    }
    log(`\nThe committed icons match a fresh render within ${tolerance} per channel.`);
    return 0;
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

// Returns the exit code.
export function main(args, { root = ROOT, log = console.log, error = console.error, Resvg } = {}) {
  const toleranceArg = args.find((arg) => arg.startsWith("--tolerance="));
  const tolerance = toleranceArg ? Number(toleranceArg.slice("--tolerance=".length)) : DEFAULT_TOLERANCE;
  if (!Number.isInteger(tolerance) || tolerance < 0 || tolerance > 255) {
    error("--tolerance must be a whole number from 0 to 255.");
    return 2;
  }
  if (args.includes("--check")) return check(tolerance, { Resvg, root, log, error });
  const written = buildAll(root, Resvg);
  log(`Brand icons rendered from ${path.relative(root, path.join(root, "packaging", "brand"))}:\n${written.map((file) => `  ${file}`).join("\n")}`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = main(process.argv.slice(2));
