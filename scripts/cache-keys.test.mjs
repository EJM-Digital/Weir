// node --test scripts/cache-keys.test.mjs
// The cache keys the CI jobs print for the pinned media tools, and the Linux FFmpeg installer that uses the same pin.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { cacheKey as ffmpegKey } from "./ffmpeg-cache-key.mjs";
import { installFfmpeg } from "./install-ffmpeg-linux.mjs";
import { cacheKey as mkvtoolnixKey } from "./mkvtoolnix-cache-key.mjs";

const scripts = path.dirname(fileURLToPath(import.meta.url));
const command = (file, ...args) => spawnSync(process.execPath, [path.join(scripts, file), ...args], { encoding: "utf8" });

const pin = {
  releaseTag: "autobuild-1",
  windows: { name: "win.zip", sha256: "aaa" },
  linux: { name: "linux.tar.xz", url: "https://example.com/linux.tar.xz", sha256: "bbb" },
};

test("the FFmpeg key names the tag, the archive and its hash for each platform, and no other platform has one", () => {
  assert.equal(ffmpegKey("windows", pin), "key=ffmpeg-vendor-autobuild-1-win.zip-aaa");
  assert.equal(ffmpegKey("linux", pin), "key=ffmpeg-linux-autobuild-1-linux.tar.xz-bbb");
  assert.equal(ffmpegKey("macos", pin), undefined);
});

test("the MKVToolNix key names the version and the AppImage's hash", () => {
  assert.equal(mkvtoolnixKey({ version: "99.0", linux: { sha256: "ccc" } }), "key=mkvtoolnix-linux-99.0-ccc");
});

test("run as commands they print the key of the committed pin", () => {
  assert.match(command("ffmpeg-cache-key.mjs").stdout, /^key=ffmpeg-vendor-\S+\n$/);
  assert.match(command("ffmpeg-cache-key.mjs", "linux").stdout, /^key=ffmpeg-linux-\S+\n$/);
  assert.match(command("mkvtoolnix-cache-key.mjs").stdout, /^key=mkvtoolnix-linux-\S+\n$/);
});

test("the FFmpeg key command refuses a platform it does not know, saying so", () => {
  const result = command("ffmpeg-cache-key.mjs", "plan9");
  assert.equal(result.status, 2);
  assert.match(result.stderr, /Usage: node scripts\/ffmpeg-cache-key\.mjs \[windows\|linux\] \(got "plan9"\)/);
});

function withFolder(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-ffmpeg-install-"));
  const messages = [];
  return Promise.resolve(callback({ root, log: (line) => messages.push(line), messages })).finally(() => rmSync(root, { recursive: true, force: true }));
}

test("a folder that already holds both tools is left alone", () =>
  withFolder(async ({ root, log, messages }) => {
    for (const tool of ["ffmpeg", "ffprobe"]) writeFileSync(path.join(root, tool), "");
    await installFfmpeg(root, { pin, log, download: () => assert.fail("nothing to download"), unpack: () => assert.fail("nothing to unpack") });
    assert.deepEqual(messages, [`ffmpeg and ffprobe are already in ${root}.`]);
  }));

test("a folder missing one tool gets the archive downloaded with its hash and unpacked into it", () =>
  withFolder(async ({ root, log, messages }) => {
    const destination = path.join(root, "bin");
    mkdirSync(destination);
    writeFileSync(path.join(destination, "ffmpeg"), "");
    const downloads = [];
    const unpacks = [];
    await installFfmpeg(destination, {
      pin,
      log,
      download: async (request) => downloads.push(request),
      unpack: (archive, into) => {
        unpacks.push([path.basename(archive), into]);
        return 0;
      },
    });
    assert.equal(downloads.length, 1);
    assert.deepEqual({ ...downloads[0], file: path.basename(downloads[0].file) }, { url: pin.linux.url, fileName: "linux.tar.xz", file: "linux.tar.xz", sha256: "bbb" });
    assert.deepEqual(unpacks, [["linux.tar.xz", destination]]);
    assert.deepEqual(messages, [`Downloading ${pin.linux.url}`, `Installed ffmpeg and ffprobe into ${destination}.`]);
    assert.equal(existsSync(path.dirname(downloads[0].file)), false, "the scratch folder is removed");
  }));

test("an archive that does not unpack stops the install, and the scratch folder is still removed", () =>
  withFolder(async ({ root, log }) => {
    let scratch;
    await assert.rejects(
      installFfmpeg(path.join(root, "new"), {
        pin,
        log,
        download: async ({ file }) => {
          scratch = path.dirname(file);
        },
        unpack: () => 2,
      }),
      /Unpacking linux\.tar\.xz failed/,
    );
    assert.equal(existsSync(scratch), false);
  }));

test("the installer command without a folder prints its usage and exits 2", () => {
  const result = command("install-ffmpeg-linux.mjs");
  assert.equal(result.status, 2);
  assert.match(result.stderr, /Usage: node scripts\/install-ffmpeg-linux\.mjs <folder>/);
});

test("the installer command leaves a folder that has both tools alone", () => {
  const root = mkdtempSync(path.join(tmpdir(), "weir-ffmpeg-install-"));
  try {
    for (const tool of ["ffmpeg", "ffprobe"]) writeFileSync(path.join(root, tool), "");
    const result = command("install-ffmpeg-linux.mjs", root);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /already in/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
