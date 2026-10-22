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

# Each tool is started once here, before the test run loads the machine: a build that does not start (missing libraries,
# a blocked file) fails this step with its own output, and the first start of a freshly restored file is not paid
# during the test run.
foreach ($tool in @(
    @{ Path = Join-Path $ffmpegDir "ffmpeg.exe"; Argument = "-version" },
    @{ Path = Join-Path $ffmpegDir "ffprobe.exe"; Argument = "-version" },
    @{ Path = Join-Path $mkvtoolnixDir "mkvmerge.exe"; Argument = "--version" })) {
  $banner = & $tool.Path $tool.Argument
  if ($LASTEXITCODE -ne 0) {
    throw "$($tool.Path) did not run: exit code $LASTEXITCODE. $($banner | Select-Object -First 3)"
  }
  Write-Host ($banner | Select-Object -First 1)
}
if ($env:GITHUB_OUTPUT) {
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding ascii -Value "ffmpeg-dir=$ffmpegDir"
  Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding ascii -Value "mkvtoolnix-dir=$mkvtoolnixDir"
}
