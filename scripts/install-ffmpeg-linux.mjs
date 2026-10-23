#!/usr/bin/env node
// Installs the pinned static Linux FFmpeg (ffmpeg and ffprobe only) into a folder, so a CI job never takes it
// from a package mirror. The pin is the one the Windows package uses (scripts/ffmpeg-pin.mjs). The archive is
// downloaded with retries (from Weir's own releases when BtbN no longer has it, scripts/pinned-download.mjs) and
// its SHA-256 is checked before anything is unpacked; a folder that already holds both binaries (a cache hit,
// keyed on the pin) is left alone.
//
// Usage: node scripts/install-ffmpeg-linux.mjs <folder>
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { readFfmpegPin } from "./ffmpeg-pin.mjs";
import { downloadPinned } from "./pinned-download.mjs";

export const tools = ["ffmpeg", "ffprobe"];

function unpackWithTar(archive, destination) {
  return spawnSync("tar", ["-xJf", archive, "-C", destination, "--strip-components=2", "--wildcards", ...tools.map((tool) => `*/bin/${tool}`)], {
    stdio: "inherit",
  }).status;
}

/**
 * Puts the pinned ffmpeg and ffprobe into `destination` unless both are there already.
 * `download` fetches and checks the archive, `unpack` extracts it and returns the exit status; they are what a test replaces.
 */
export async function installFfmpeg(destination, { pin = readFfmpegPin(), download = downloadPinned, unpack = unpackWithTar, log = console.log } = {}) {
  if (tools.every((tool) => existsSync(path.join(destination, tool)))) {
    log(`ffmpeg and ffprobe are already in ${destination}.`);
    return;
  }
  const { linux } = pin;
  const scratch = mkdtempSync(path.join(tmpdir(), "weir-ffmpeg-"));
  try {
    const archive = path.join(scratch, linux.name);
    log(`Downloading ${linux.url}`);
    await download({ url: linux.url, fileName: linux.name, file: archive, sha256: linux.sha256 });

    mkdirSync(destination, { recursive: true });
    if (unpack(archive, destination) !== 0) throw new Error(`Unpacking ${linux.name} failed.`);
    log(`Installed ${tools.join(" and ")} into ${destination}.`);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

async function main() {
  const destination = process.argv[2];
  if (!destination) {
    console.error("Usage: node scripts/install-ffmpeg-linux.mjs <folder>");
    process.exitCode = 2;
    return;
  }
  await installFfmpeg(destination);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) await main();
