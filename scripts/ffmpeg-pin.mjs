// The FFmpeg pin in packaging/windows/build-velopack-vendored-media-tools.ps1 (dot-sourced from
// build-velopack.ps1), read as text so the Windows package and the Linux CI jobs can never disagree about
// which build is meant. No network call is needed.
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const buildScriptName = "build-velopack-vendored-media-tools.ps1";

export function assignment(buildScript, name) {
  const match = buildScript.match(new RegExp(`^\\$${name} = "([^"$]+)"`, "m"));
  if (!match) throw new Error(`$${name} was not found in ${buildScriptName}.`);
  return match[1];
}

export function sha256Assignment(buildScript, name) {
  const value = assignment(buildScript, name);
  if (!/^[0-9a-f]{64}$/i.test(value)) {
    throw new Error(`$${name} in ${buildScriptName} is not a 64-character hex SHA256: ${value}`);
  }
  return value.toLowerCase();
}

/** The text of the build script that holds the pins for FFmpeg and MKVToolNix. */
export function readPinnedBuildScript() {
  return readFileSync(path.join(repoRoot, "packaging", "windows", buildScriptName), "utf8");
}

/** The Windows and Linux archives of the pinned BtbN release: `{ releaseTag, windows, linux }`, each `{ name, url, sha256 }`. */
export function readFfmpegPin(buildScript = readPinnedBuildScript()) {
  const releaseTag = assignment(buildScript, "ffmpegReleaseTag");
  const archive = (nameVariable, sha256Variable) => {
    const name = assignment(buildScript, nameVariable);
    return {
      name,
      url: `https://github.com/BtbN/FFmpeg-Builds/releases/download/${releaseTag}/${name}`,
      sha256: sha256Assignment(buildScript, sha256Variable),
    };
  };
  return {
    releaseTag,
    windows: archive("ffmpegArchiveName", "ffmpegArchiveSha256"),
    linux: archive("ffmpegLinuxArchiveName", "ffmpegLinuxArchiveSha256"),
  };
}
