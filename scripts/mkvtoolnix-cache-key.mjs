#!/usr/bin/env node
// Prints the cache key for the Linux CI jobs' mkvmerge, read straight from the pin in
// packaging/windows/build-velopack-vendored-media-tools.ps1: the MKVToolNix version and the AppImage's SHA-256.
// The cache changes exactly when the pin does, and never otherwise (see scripts/ffmpeg-cache-key.mjs).
//
// Usage (a workflow step with an id): node scripts/mkvtoolnix-cache-key.mjs >> "$GITHUB_OUTPUT"
//   -> key=mkvtoolnix-linux-<version>-<AppImage sha256>
import { readMkvtoolnixPin } from "./mkvtoolnix-pin.mjs";

const { version, linux } = readMkvtoolnixPin();
console.log(`key=mkvtoolnix-linux-${version}-${linux.sha256}`);
