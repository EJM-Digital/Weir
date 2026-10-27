// node --test scripts/install-linux-tools.test.mjs
// The two installers of pinned Linux tools (FFmpeg, mkvmerge), with the download and the unpacking replaced where they
// need the network or a Linux program; the command lines and the real tar run for real.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { installFfmpeg, tools, unpackWithTar } from "./install-ffmpeg-linux.mjs";
import { extractWithAppImage, installMkvmerge } from "./install-mkvtoolnix-linux.mjs";

const ffmpegScript = fileURLToPath(new URL("./install-ffmpeg-linux.mjs", import.meta.url));
const mkvmergeScript = fileURLToPath(new URL("./install-mkvtoolnix-linux.mjs", import.meta.url));
const pin = { linux: { name: "tool.pkg", url: "https://example.invalid/tool.pkg", sha256: "ab".repeat(32) } };

function inFolder(body) {
  const root = mkdtempSync(path.join(tmpdir(), "weir-install-tools-"));
  return Promise.resolve(body(root)).finally(() => rmSync(root, { recursive: true, force: true }));
}

const gnuTar = spawnSync("tar", ["--version"], { encoding: "utf8" }).stdout?.includes("GNU tar");

test("the FFmpeg installer downloads the pinned archive, unpacks it and leaves a folder it has filled alone", () =>
  inFolder(async (root) => {
    const destination = path.join(root, "bin");
    const lines = [];
    const asked = [];
    const download = async (request) => asked.push(request);
    const unpack = (archive, into) => {
      for (const tool of tools) writeFileSync(path.join(into, tool), tool);
      return archive.endsWith("tool.pkg") ? 0 : 1;
    };

    await installFfmpeg(destination, { pin, download, unpack, log: (line) => lines.push(line) });
    assert.equal(asked.length, 1);
    assert.deepEqual([asked[0].url, asked[0].fileName, asked[0].sha256], [pin.linux.url, "tool.pkg", pin.linux.sha256]);
    assert.deepEqual(lines, [`Downloading ${pin.linux.url}`, "Installed ffmpeg and ffprobe into " + destination + "."]);

    await installFfmpeg(destination, { pin, download, unpack: () => assert.fail("already installed"), log: (line) => lines.push(line) });
    assert.equal(lines.at(-1), `ffmpeg and ffprobe are already in ${destination}.`);
    assert.equal(asked.length, 1);
  }));

test("an FFmpeg archive that will not unpack fails the install", () =>
  inFolder((root) =>
    assert.rejects(installFfmpeg(path.join(root, "bin"), { pin, download: async () => {}, unpack: () => 2, log: () => {} }), /Unpacking tool\.pkg failed/),
  ));

test("tar takes only ffmpeg and ffprobe out of an FFmpeg archive, flat", { skip: !gnuTar && "needs GNU tar, as the Linux runners have" }, () =>
  inFolder((root) => {
    const build = path.join(root, "ffmpeg-build");
    mkdirSync(path.join(build, "bin"), { recursive: true });
    for (const name of [...tools, "ffplay"]) writeFileSync(path.join(build, "bin", name), name);
    assert.equal(spawnSync("tar", ["-cJf", "ffmpeg.tar.xz", "ffmpeg-build"], { cwd: root }).status, 0);

    const destination = path.join(root, "out");
    mkdirSync(destination);
    // GNU tar reads "C:" at the start of a path as a host, so on Windows the paths are given relative to here.
    const here = (name) => path.relative(process.cwd(), path.join(root, name)).replaceAll("\\", "/");
    assert.equal(unpackWithTar(here("ffmpeg.tar.xz"), here("out")), 0);
    assert.equal(readFileSync(path.join(destination, "ffprobe"), "utf8"), "ffprobe");
    assert.equal(existsSync(path.join(destination, "ffplay")), false);
    assert.notEqual(unpackWithTar(here("missing.tar.xz"), here("out")), 0);
  }));

test("the mkvmerge installer downloads the AppImage, unpacks it, keeps mkvmerge and leaves a filled folder alone", () =>
  inFolder(async (root) => {
    const destination = path.join(root, "mkvtoolnix");
    const lines = [];
    const asked = [];
    const extract = (appImage, scratch) => {
      assert.equal(path.basename(appImage), "tool.pkg");
      mkdirSync(path.join(scratch, "squashfs-root", "usr", "bin"), { recursive: true });
      mkdirSync(path.join(scratch, "squashfs-root", "usr", "lib"), { recursive: true });
      writeFileSync(path.join(scratch, "squashfs-root", "usr", "bin", "mkvmerge"), "program");
      return 0;
    };
    const download = async (request) => {
      asked.push(request);
      writeFileSync(request.file, "appimage");
    };

    await installMkvmerge(destination, { pin, download, extract, log: (line) => lines.push(line) });
    assert.equal(asked.length, 1);
    assert.equal(asked[0].sha256, pin.linux.sha256);
    assert.equal(readFileSync(path.join(destination, "usr", "bin", "mkvmerge"), "utf8"), "program");
    assert.deepEqual(lines, [`Downloading ${pin.linux.url}`, `Installed mkvmerge into ${destination}.`]);

    await installMkvmerge(destination, { pin, download: () => assert.fail("already installed"), log: (line) => lines.push(line) });
    assert.equal(lines.at(-1), `mkvmerge is already in ${destination}.`);
  }));

test("the AppImage is run with --appimage-extract in the scratch folder, and its exit status is the answer", () => {
  const seen = [];
  const status = extractWithAppImage("/scratch/tool.AppImage", "/scratch", (program, args, options) => (seen.push({ program, args, cwd: options.cwd }), { status: 7 }));
  assert.equal(status, 7);
  assert.deepEqual(seen, [{ program: "/scratch/tool.AppImage", args: ["--appimage-extract"], cwd: "/scratch" }]);
});

test("an AppImage that will not unpack fails the install and leaves nothing behind", () =>
  inFolder(async (root) => {
    const destination = path.join(root, "mkvtoolnix");
    await assert.rejects(
      installMkvmerge(destination, { pin, download: async (request) => writeFileSync(request.file, "appimage"), extract: () => 1, log: () => {} }),
      /Unpacking tool\.pkg failed/,
    );
    assert.equal(existsSync(destination), false);
  }));

test("both installers, run as commands, ask for a folder and leave one that is filled alone", () =>
  inFolder((root) => {
    for (const script of [ffmpegScript, mkvmergeScript]) {
      const refused = spawnSync(process.execPath, [script], { encoding: "utf8" });
      assert.equal(refused.status, 2);
      assert.match(refused.stderr, /Usage: node scripts\/install-/);
    }

    const filled = path.join(root, "filled");
    mkdirSync(filled);
    for (const name of [...tools, "mkvmerge"]) writeFileSync(path.join(filled, name), name);
    for (const script of [ffmpegScript, mkvmergeScript]) {
      const left = spawnSync(process.execPath, [script, filled], { encoding: "utf8" });
      assert.equal(left.status, 0, left.stderr);
      assert.match(left.stdout, /already in/);
    }
  }));
