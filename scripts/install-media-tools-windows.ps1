# Installs the pinned Windows FFmpeg and MKVToolNix into packaging/windows/vendor, the folders the Windows package is
# built from, so a CI job tests with exactly the tools the package ships. The pins, the checksums and the download
# (upstream first, then Weir's own releases) are build-velopack-vendored-media-tools.ps1's; a folder that already
# matches its pin is left alone. When GITHUB_OUTPUT is set, the two folders are written to it as ffmpeg-dir and
# mkvtoolnix-dir.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/install-media-tools-windows.ps1
$ErrorActionPreference = "Stop"

. "$PSScriptRoot\..\packaging\windows\build-velopack-vendored-media-tools.ps1"

Ensure-WindowsFfmpegRuntime
Ensure-WindowsMkvtoolnixRuntime

$ffmpegDir = [System.IO.Path]::GetFullPath($ffmpegVendorDir)
$mkvtoolnixDir = [System.IO.Path]::GetFullPath($mkvtoolnixVendorDir)
Write-Host "FFmpeg is in $ffmpegDir"
Write-Host "MKVToolNix is in $mkvtoolnixDir"
if ($env:GITHUB_OUTPUT) {
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding ascii -Value "ffmpeg-dir=$ffmpegDir"
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding ascii -Value "mkvtoolnix-dir=$mkvtoolnixDir"
}
