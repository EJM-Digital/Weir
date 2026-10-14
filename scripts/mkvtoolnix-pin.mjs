// The MKVToolNix pin in packaging/windows/build-velopack-vendored-media-tools.ps1, read as text like the FFmpeg
// pin (scripts/ffmpeg-pin.mjs), so the Windows package and the Linux CI job can never disagree about which version
// is meant. No network call is needed.
import { readPinnedBuildScript, sha256Assignment, assignment } from "./ffmpeg-pin.mjs";

/**
 * The Windows archive and the Linux AppImage of the pinned MKVToolNix version: `{ version, windows, linux }`, each
 * `{ name, url, sha256 }`. MKVToolNix's own per-version directories are immutable, so neither URL moves.
 */
export function readMkvtoolnixPin(buildScript = readPinnedBuildScript()) {
  const version = assignment(buildScript, "mkvtoolnixVersion");
  const windowsName = `mkvtoolnix-64-bit-${version}.zip`;
  const linuxName = `MKVToolNix_GUI-${version}-x86_64.AppImage`;
  return {
    version,
    windows: {
      name: windowsName,
      url: `https://mkvtoolnix.download/windows/releases/${version}/${windowsName}`,
      sha256: sha256Assignment(buildScript, "mkvtoolnixArchiveSha256"),
    },
    linux: {
      name: linuxName,
      url: `https://mkvtoolnix.download/appimage/${linuxName}`,
      sha256: sha256Assignment(buildScript, "mkvtoolnixLinuxAppImageSha256"),
    },
  };
}
