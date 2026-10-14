// node --test scripts/mkvtoolnix-pin.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { readMkvtoolnixPin } from "./mkvtoolnix-pin.mjs";

test("the committed pin names the version, the Windows archive and the Linux AppImage of it, each with a SHA-256", () => {
  const pin = readMkvtoolnixPin();
  assert.match(pin.version, /^\d+\.\d+$/);
  assert.equal(pin.windows.name, `mkvtoolnix-64-bit-${pin.version}.zip`);
  assert.equal(pin.windows.url, `https://mkvtoolnix.download/windows/releases/${pin.version}/${pin.windows.name}`);
  assert.equal(pin.linux.name, `MKVToolNix_GUI-${pin.version}-x86_64.AppImage`);
  assert.equal(pin.linux.url, `https://mkvtoolnix.download/appimage/${pin.linux.name}`);
  for (const archive of [pin.windows, pin.linux]) assert.match(archive.sha256, /^[0-9a-f]{64}$/);
});

test("the versions and hashes come from the build script's assignments", () => {
  const buildScript = [
    `$mkvtoolnixVersion = "9.9"`,
    `$mkvtoolnixArchiveSha256 = "${"a".repeat(64)}"`,
    `$mkvtoolnixLinuxAppImageSha256 = "${"b".repeat(64)}"`,
  ].join("\n");
  const pin = readMkvtoolnixPin(buildScript);
  assert.equal(pin.windows.sha256, "a".repeat(64));
  assert.equal(pin.linux.sha256, "b".repeat(64));
  assert.equal(pin.linux.name, "MKVToolNix_GUI-9.9-x86_64.AppImage");
});

test("a pin without the Linux AppImage hash is reported by name", () => {
  const buildScript = `$mkvtoolnixVersion = "9.9"\n$mkvtoolnixArchiveSha256 = "${"a".repeat(64)}"\n`;
  assert.throws(() => readMkvtoolnixPin(buildScript), /mkvtoolnixLinuxAppImageSha256 was not found/);
});

test("a Linux hash that is not 64 hex characters is rejected", () => {
  const buildScript = [
    `$mkvtoolnixVersion = "9.9"`,
    `$mkvtoolnixArchiveSha256 = "${"a".repeat(64)}"`,
    `$mkvtoolnixLinuxAppImageSha256 = "abc"`,
  ].join("\n");
  assert.throws(() => readMkvtoolnixPin(buildScript), /mkvtoolnixLinuxAppImageSha256.*64-character/);
});
