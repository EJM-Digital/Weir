#!/usr/bin/env node
// Installs the pinned Linux mkvmerge into a folder, so a CI job never takes it from a package mirror. The pin is the
// MKVToolNix version the Windows package uses (scripts/mkvtoolnix-pin.mjs); upstream publishes its Linux build only as
// an AppImage. The AppImage is downloaded with retries and its SHA-256 is checked before it is run (--appimage-extract
// needs no FUSE), and only mkvmerge and the libraries it loads are kept, behind a `mkvmerge` launcher that points the
// loader at them. A folder that already holds the launcher (a cache hit, keyed on the pin) is left alone.
//
// Usage: node scripts/install-mkvtoolnix-linux.mjs <folder>
import { spawnSync } from "node:child_process";
import { chmodSync, cpSync, existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { setDefaultAutoSelectFamilyAttemptTimeout } from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { readMkvtoolnixPin } from "./mkvtoolnix-pin.mjs";
import { downloadPinned } from "./pinned-download.mjs";

const launcher = `#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
LD_LIBRARY_PATH="$here/usr/lib\${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" exec "$here/usr/bin/mkvmerge" "$@"
`;

/** Copies mkvmerge and its libraries out of an extracted AppImage into `destination` and writes the launcher beside them. */
export function placeMkvmerge(extractedAppImage, destination) {
  mkdirSync(path.join(destination, "usr", "bin"), { recursive: true });
  cpSync(path.join(extractedAppImage, "usr", "bin", "mkvmerge"), path.join(destination, "usr", "bin", "mkvmerge"));
  cpSync(path.join(extractedAppImage, "usr", "lib"), path.join(destination, "usr", "lib"), { recursive: true });
  const launcherPath = path.join(destination, "mkvmerge");
  writeFileSync(launcherPath, launcher);
  chmodSync(launcherPath, 0o755);
}

async function main() {
  const destination = process.argv[2];
  if (!destination) {
    console.error("Usage: node scripts/install-mkvtoolnix-linux.mjs <folder>");
    process.exit(2);
  }

  if (existsSync(path.join(destination, "mkvmerge"))) {
    console.log(`mkvmerge is already in ${destination}.`);
    return;
  }

  // Node gives each address 250 ms to connect before it tries the next. A connection to MKVToolNix's server from a
  // far-away network takes longer than that, and then every attempt fails.
  setDefaultAutoSelectFamilyAttemptTimeout(10_000);
  const { linux } = readMkvtoolnixPin();
  const scratch = mkdtempSync(path.join(tmpdir(), "weir-mkvtoolnix-"));
  try {
    const appImage = path.join(scratch, linux.name);
    console.log(`Downloading ${linux.url}`);
    await downloadPinned({ url: linux.url, fileName: linux.name, file: appImage, sha256: linux.sha256 });
    chmodSync(appImage, 0o755);

    const extracted = spawnSync(appImage, ["--appimage-extract"], { cwd: scratch, stdio: ["ignore", "ignore", "inherit"] });
    if (extracted.status !== 0) throw new Error(`Unpacking ${linux.name} failed.`);
    placeMkvmerge(path.join(scratch, "squashfs-root"), destination);
    console.log(`Installed mkvmerge into ${destination}.`);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  await main();
}
