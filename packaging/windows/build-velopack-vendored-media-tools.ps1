# Vendored FFmpeg and MKVToolNix for build-velopack.ps1: dot-sourced from there
# (". $PSScriptRoot\build-velopack-vendored-media-tools.ps1"), split out (#747) so build-velopack.ps1
# stays under the project's line-count guideline. Dot-sourcing runs this in the caller's own scope, so
# $PSScriptRoot here is still packaging/windows (the dot-sourcing script's own folder), and the
# variables and functions below are usable from build-velopack.ps1 exactly as if this were inline.
#
# Both tools follow the same download / verify / cache shape: a pinned version and checksum, a stamp
# file recording what is currently vendored, and a re-hash of the extracted exe(s) on every run
# (including a cache hit) so a stale or tampered vendor folder is caught without trusting the stamp's
# own say-so about what it contains.

# Every download below goes through Save-PinnedDownload: the upstream URL first, then Weir's own
# releases, which carry a copy of each archive, against the same pinned SHA-256.
. "$PSScriptRoot\pinned-download.ps1"

# Ignored by version control and reused between builds: Ensure-WindowsFfmpegRuntime downloads again
# only when the vendored copy doesn't match the pin below (CI caches this folder too).
#
# Pinned the same way #548 pins MKVToolNix below: a specific, immutable BtbN release rather than
# the `latest` tag. BtbN republishes `latest` from git master continuously, so it is a moving
# target with no stable checksum authority of its own — its checksums.sha256 is published inside
# that same mutable release, which proves a download wasn't corrupted in transit, not who built it
# or that it is the build Weir was tested against.
#
# The shared build (ffmpeg.exe/ffprobe.exe linked against av*.dll instead of each embedding its
# own copy of every library) rather than BtbN's static build, because Weir vendors both exes: the
# static zip is ~163 MB compressed with each exe carrying its own ~130 MB of statically-linked
# libraries, while the shared zip is ~73 MB compressed with the exes under 1 MB combined and the six
# av*.dll files (avcodec/avdevice/avfilter/avformat/avutil/swresample/swscale) shared between them.
# Windows always searches an exe's own directory for a DLL first regardless of PATH (the "safe DLL
# search order"), so ffmpeg.exe and ffprobe.exe find their av*.dll siblings without any code change —
# MediaToolLocations/MediaToolResolver only ever check that ffmpeg.exe and ffprobe.exe exist in the
# candidate directory (apps/server/src/Weir.Infrastructure/Media/MediaToolResolver.cs), never their
# internal linkage. ffplay.exe ships in the same zip but is never copied out: Weir has no use for it.
#
# BtbN deletes its dated releases after about two weeks, so a pin is downloadable from BtbN for only that
# long. Every Weir release carries a copy of the archives it was built with (fetch-bundled-tool-sources.ps1),
# and a build whose pinned file is gone from BtbN takes that copy instead (pinned-download.ps1). A bump
# still takes the newest dated release, the one certain to be on BtbN, because the first release built
# from a new pin has no copy of its archives on Weir's releases yet.
#
# To bump: browse https://github.com/BtbN/FFmpeg-Builds/releases, pick a dated
# autobuild-YYYY-MM-DD-HH-MM tag (or a versioned "release" build such as n9.0.2 if BtbN has cut
# one), and find the win64 LGPL **shared** zip in its assets — e.g. with
# `gh release view <tag> -R BtbN/FFmpeg-Builds --json assets`. Update $ffmpegVersion,
# $ffmpegReleaseTag and $ffmpegArchiveName together, then get $ffmpegArchiveSha256 by downloading
# that zip to a scratch folder (never the repo) and hashing it yourself — don't just copy the
# number from the release's own checksums.sha256, since that is the thing being pinned against.
# Extract the zip and hash ffmpeg.exe, ffprobe.exe and every av*.dll under bin\ for
# $ffmpegExeSha256/$ffprobeExeSha256/$ffmpegSharedLibrarySha256 (a name -> hash map, one entry per
# library — the versioned filenames such as avcodec-63.dll change across ffmpeg releases). Finally
# delete packaging/windows/vendor/ffmpeg and re-run this script once to pick up and verify the
# new build end to end.
$ffmpegVersion = "n9.0.2 (BtbN autobuild-2026-10-08-13-05, shared)"
$ffmpegReleaseTag = "autobuild-2026-10-08-13-05"
$ffmpegVendorDir = Join-Path $PSScriptRoot "vendor\\ffmpeg"
$ffmpegArchiveName = "ffmpeg-n9.0.2-23-g27b46f0fbc-win64-lgpl-shared-9.0.zip"
$ffmpegArchiveUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$ffmpegReleaseTag/$ffmpegArchiveName"
$ffmpegArchiveSha256 = "a168ed7b315366354df532a755a93341b6e7e66b65834f56ce36b3ee8be0d6f0"
$ffmpegExeSha256 = "3331a285282a2ece9d6f6002f200751fab1c4863ac48dfcd7854dc181d4f3b0e"
$ffprobeExeSha256 = "4384bd5640215a35df01ea122376698929bb7dbfa29bffec1bf79a6bf4595e3e"
# The libraries ffmpeg.exe/ffprobe.exe load from their own directory. Names carry the BtbN build's
# so-version suffix (bumps only when that library's ABI changes upstream), verified the same way as
# the exes above: re-hashed on every run, including a cache hit, never trusted from the stamp file.
$ffmpegSharedLibrarySha256 = [ordered]@{
  "avcodec-63.dll"    = "177d76dc53c234fe3df9e524f7f4315478829118fed01557860be33e82707d12"
  "avdevice-63.dll"   = "443c74c662ead7ff8b73902494bf42047d8ba838f77eb3a761dce503aa7d1410"
  "avfilter-12.dll"   = "3d7a491e91c6fd1ced6aa67c68767529d11b241ffe57d33771d6c48c7b5c25dc"
  "avformat-63.dll"   = "42e0e9898f567597da237be9db95e1e1bc61d7ef79a1bcc47b27a8653c1c1873"
  "avutil-61.dll"     = "4e170ffe8eba38ffad7ae4f15d4deaa536aa7e4ff2be9c584f57d435ce132307"
  "swresample-7.dll"  = "3e7f341e5a6c096897cc8c3aaf3fbfd6f3043b850b58c05001e7ecef581952fd"
  "swscale-10.dll"    = "4bd3bf170788b61a1027f616feaaa13184848a512b8252ea764c315863df3b50"
}
# The Linux CI jobs (contract suite, Docker smoke, release candidate) use the static LGPL linux64 build
# of the same BtbN release (same FFmpeg commit as the Windows build above), so no job takes ffmpeg from a
# package mirror. scripts/install-ffmpeg-linux.mjs reads these two lines; bump them with the Windows ones
# and hash the tar.xz yourself in the same way.
$ffmpegLinuxArchiveName = "ffmpeg-n9.0.2-23-g27b46f0fbc-linux64-lgpl-9.0.tar.xz"
$ffmpegLinuxArchiveUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$ffmpegReleaseTag/$ffmpegLinuxArchiveName"
$ffmpegLinuxArchiveSha256 = "d991116970e7eb350c5e49e502c3dc9b91b847f1494b7d1a8f02118c896f359b"
# #548: MKVToolNix, for the mkvmerge writer (Weir.Infrastructure.Media.MkvmergeRemuxWriter). Vendored,
# cached and pinned exactly like ffmpeg above, against MKVToolNix's own immutable per-version release
# directories rather than BtbN's. Bumping means editing both lines below together — the checksum is
# the one upstream publishes at
# https://mkvtoolnix.download/windows/releases/<version>/mkvtoolnix-64-bit-<version>.zip.sha256
# (also listed in that directory's sha256sums.txt). Weir uses only mkvmerge's long-stable CLI surface
# (`-o`, `--identification-format json`, per-track selection — see Weir.Core.Media.MkvmergeCommands),
# so any current stable release serves.
$mkvtoolnixVersion = "102.0"
$mkvtoolnixArchiveSha256 = "c02e918900f6d945d9307b426237e456378b79a200589ac6928de39064409a44"
$mkvtoolnixVendorDir = Join-Path $PSScriptRoot "vendor\\mkvtoolnix"
$mkvtoolnixArchiveName = "mkvtoolnix-64-bit-$mkvtoolnixVersion.zip"
$mkvtoolnixArchiveUrl = "https://mkvtoolnix.download/windows/releases/$mkvtoolnixVersion/$mkvtoolnixArchiveName"
# The server tests that run the real mkvmerge also run on Linux in CI. MKVToolNix publishes no Linux build other
# than its AppImage, so that job takes the AppImage of this same version from
# https://mkvtoolnix.download/appimage/MKVToolNix_GUI-<version>-x86_64.AppImage (scripts/install-mkvtoolnix-linux.mjs
# reads this line and the version above). Bump the version and this hash together; hash the AppImage yourself.
$mkvtoolnixLinuxAppImageSha256 = "c66345b30d6d5fd640ea982ab5e202a99b9f541a20e42aa19edd90f3ddd5dc9b"

# AGPL/LGPL/GPL source-availability pins, downloaded and hash-verified by
# fetch-bundled-tool-sources.ps1 and attached to each GitHub Release, so the exact source behind the
# bundled binaries above travels with the release rather than only a link to the projects (the
# binary pins above prove what was bundled; these prove where its source is). Each pin identifies the
# precise commit or version the corresponding binary pin was built from — update all three together
# whenever $ffmpegArchiveName/$ffmpegReleaseTag/$mkvtoolnixVersion above are bumped.
#
# BtbN's build string ("n9.0.2 (BtbN autobuild-2026-10-08-13-05, shared)") names the FFmpeg git
# describe output "n9.0.2-23-g27b46f0fbc": 23 commits after the n9.0.2 tag, at commit 27b46f0fbc. That
# exact commit, not the n9.0.2 tag, is what BtbN actually compiled, so it is what must be linked.
$ffmpegSourceCommit = "27b46f0fbc"
$ffmpegSourceUrl = "https://github.com/FFmpeg/FFmpeg/archive/$ffmpegSourceCommit.tar.gz"
$ffmpegSourceSha256 = "994cd93470473d910c2af6424637527dec1622bc34a0428cfec4f99cd93991f4"
$ffmpegSourceFileName = "ffmpeg-source-$ffmpegSourceCommit.tar.gz"

# BtbN/FFmpeg-Builds is both the release repo and the build-scripts repo: the release tag is a git
# ref pointing at the exact commit its assets were built from, so a source archive of that tag is
# the matching build-scripts commit, not just "whatever master happens to be now".
$btbnBuildScriptsUrl = "https://github.com/BtbN/FFmpeg-Builds/archive/refs/tags/$ffmpegReleaseTag.tar.gz"
$btbnBuildScriptsSha256 = "846f7222a102eeca27afaf83715f336f36087e24b16ec9fb924831c716a36d94"
$btbnBuildScriptsFileName = "ffmpeg-build-scripts-$ffmpegReleaseTag.tar.gz"

$mkvtoolnixSourceUrl = "https://mkvtoolnix.download/sources/mkvtoolnix-$mkvtoolnixVersion.tar.xz"
$mkvtoolnixSourceSha256 = "9f0a810f17c7df8adb9064a3a41d5784399be412d19704cf080745ad7d45da30"
$mkvtoolnixSourceFileName = "mkvtoolnix-source-$mkvtoolnixVersion.tar.xz"

function Assert-WindowsFfmpegExeHashes {
  # Re-hashing the extracted exes and shared libraries — both right after download and again on
  # every cache hit — is deliberately redundant with the archive-hash check: it catches the vendor
  # folder being repopulated by something other than this function (a stale or tampered CI cache
  # entry, a half-written previous run) without trusting the stamp file's own say-so about what it
  # contains. Verified against the pinned constants above, not against whatever the stamp records.
  param(
    [Parameter(Mandatory)][string]$FfmpegExePath,
    [Parameter(Mandatory)][string]$FfprobeExePath,
    [Parameter(Mandatory)][string]$BinDir
  )
  $actualFfmpegSha256 = (Get-FileHash -LiteralPath $FfmpegExePath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actualFfmpegSha256 -ne $ffmpegExeSha256) {
    throw "ffmpeg.exe hash mismatch at $FfmpegExePath. Expected $ffmpegExeSha256 but got $actualFfmpegSha256. Delete $ffmpegVendorDir and re-run, or re-pin the hash if $ffmpegVersion was deliberately bumped."
  }
  $actualFfprobeSha256 = (Get-FileHash -LiteralPath $FfprobeExePath -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actualFfprobeSha256 -ne $ffprobeExeSha256) {
    throw "ffprobe.exe hash mismatch at $FfprobeExePath. Expected $ffprobeExeSha256 but got $actualFfprobeSha256. Delete $ffmpegVendorDir and re-run, or re-pin the hash if $ffmpegVersion was deliberately bumped."
  }
  foreach ($libraryName in $ffmpegSharedLibrarySha256.Keys) {
    $libraryPath = Join-Path $BinDir $libraryName
    if (-not (Test-Path -LiteralPath $libraryPath)) {
      throw "Expected shared library $libraryName was not found in $BinDir. The BtbN shared build layout may have changed; re-pin `$ffmpegSharedLibrarySha256 in build-velopack-vendored-media-tools.ps1."
    }
    $actualLibrarySha256 = (Get-FileHash -LiteralPath $libraryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedLibrarySha256 = $ffmpegSharedLibrarySha256[$libraryName]
    if ($actualLibrarySha256 -ne $expectedLibrarySha256) {
      throw "$libraryName hash mismatch at $libraryPath. Expected $expectedLibrarySha256 but got $actualLibrarySha256. Delete $ffmpegVendorDir and re-run, or re-pin the hash if $ffmpegVersion was deliberately bumped."
    }
  }
}

function Ensure-WindowsFfmpegRuntime {
  $ffmpegExe = Join-Path $ffmpegVendorDir "ffmpeg.exe"
  $ffprobeExe = Join-Path $ffmpegVendorDir "ffprobe.exe"
  $stampPath = Join-Path $ffmpegVendorDir ".ffmpeg-archive.sha256"

  if ((Test-Path -LiteralPath $ffmpegExe) -and
      (Test-Path -LiteralPath $ffprobeExe) -and
      (Test-Path -LiteralPath $stampPath)) {
    $vendoredSha256 = (Get-Content -LiteralPath $stampPath -Raw).Trim().ToLowerInvariant()
    if ($vendoredSha256 -eq $ffmpegArchiveSha256) {
      Assert-WindowsFfmpegExeHashes -FfmpegExePath $ffmpegExe -FfprobeExePath $ffprobeExe -BinDir $ffmpegVendorDir
      Write-Host "Vendored FFmpeg $ffmpegVersion already matches the pinned hashes; skipping download."
      return
    }
    Write-Host "Vendored FFmpeg archive stamp is stale (have $vendoredSha256, want $ffmpegArchiveSha256); refreshing."
  }

  $downloadRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("weir-ffmpeg-" + [System.Guid]::NewGuid().ToString("N"))
  $archivePath = Join-Path $downloadRoot $ffmpegArchiveName
  $extractRoot = Join-Path $downloadRoot "extract"
  try {
    New-Item -ItemType Directory -Path $downloadRoot | Out-Null
    New-Item -ItemType Directory -Path $extractRoot | Out-Null
    Write-Host "Downloading Windows FFmpeg runtime ($ffmpegVersion)..."
    Save-PinnedDownload -Url $ffmpegArchiveUrl -OutFile $archivePath -ExpectedSha256 $ffmpegArchiveSha256 -Description "FFmpeg archive"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
    $binDir = Get-ChildItem -Path $extractRoot -Recurse -Directory |
      Where-Object {
        (Test-Path (Join-Path $_.FullName "ffmpeg.exe")) -and
        (Test-Path (Join-Path $_.FullName "ffprobe.exe"))
      } |
      Select-Object -First 1
    if (-not $binDir) {
      throw "Downloaded FFmpeg archive did not contain ffmpeg.exe and ffprobe.exe."
    }
    Assert-WindowsFfmpegExeHashes `
      -FfmpegExePath (Join-Path $binDir.FullName "ffmpeg.exe") `
      -FfprobeExePath (Join-Path $binDir.FullName "ffprobe.exe") `
      -BinDir $binDir.FullName
    if (Test-Path $ffmpegVendorDir) {
      Remove-Item -LiteralPath $ffmpegVendorDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ffmpegVendorDir | Out-Null
    $vendoredFileNames = @("ffmpeg.exe", "ffprobe.exe") + @($ffmpegSharedLibrarySha256.Keys)
    foreach ($name in $vendoredFileNames) {
      $src = Join-Path $binDir.FullName $name
      Copy-Item -LiteralPath $src -Destination (Join-Path $ffmpegVendorDir $name) -Force
    }
    Set-Content -LiteralPath (Join-Path $ffmpegVendorDir ".ffmpeg-archive.sha256") -Value $ffmpegArchiveSha256 -Encoding ascii
  } finally {
    if (Test-Path $downloadRoot) {
      Remove-Item -LiteralPath $downloadRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
}

function Ensure-WindowsMkvtoolnixRuntime {
  # #548: the same download / verify / cache shape as Ensure-WindowsFfmpegRuntime, against a pinned
  # version and checksum instead of an upstream checksums file. Only mkvmerge.exe is vendored: the
  # portable archive is ~85 MB because it carries the Qt GUI, mkvinfo, mkvextract, mkvpropedit, the
  # docs and 40-odd locales, none of which Weir ever runs. MKVToolNix's Windows CLI binaries are
  # statically linked — no sibling DLLs, no data directory — verified by copying mkvmerge.exe alone
  # out of an install and running `--version` and an `--identification-format json` identify from the
  # copy, both of which succeeded. That keeps the package's growth to the ~23 MB of mkvmerge itself
  # rather than the ~120 MB the whole archive would expand to.
  $mkvmergeExe = Join-Path $mkvtoolnixVendorDir "mkvmerge.exe"
  $stampPath = Join-Path $mkvtoolnixVendorDir ".mkvtoolnix-archive.sha256"

  if ((Test-Path -LiteralPath $mkvmergeExe) -and (Test-Path -LiteralPath $stampPath)) {
    $vendoredSha256 = (Get-Content -LiteralPath $stampPath -Raw).Trim().ToLowerInvariant()
    if ($vendoredSha256 -eq $mkvtoolnixArchiveSha256) {
      Write-Host "Vendored MKVToolNix already matches the pin ($mkvtoolnixVersion, $mkvtoolnixArchiveSha256); skipping download."
      return
    }
    Write-Host "Vendored MKVToolNix is stale (have $vendoredSha256, want $mkvtoolnixArchiveSha256); refreshing."
  }

  $downloadRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("weir-mkvtoolnix-" + [System.Guid]::NewGuid().ToString("N"))
  $archivePath = Join-Path $downloadRoot $mkvtoolnixArchiveName
  $extractRoot = Join-Path $downloadRoot "extract"
  try {
    New-Item -ItemType Directory -Path $downloadRoot | Out-Null
    New-Item -ItemType Directory -Path $extractRoot | Out-Null
    Write-Host "Downloading MKVToolNix $mkvtoolnixVersion..."
    Save-PinnedDownload -Url $mkvtoolnixArchiveUrl -OutFile $archivePath -ExpectedSha256 $mkvtoolnixArchiveSha256 -Description "MKVToolNix archive"
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot -Force
    # The archive nests everything under a "mkvtoolnix\" folder, but searching for the directory that
    # actually holds mkvmerge.exe (rather than hard-coding that name) means a future layout change
    # fails loudly on the throw below instead of silently shipping a package without the tool.
    $binDir = Get-ChildItem -Path $extractRoot -Recurse -Directory |
      Where-Object { Test-Path (Join-Path $_.FullName "mkvmerge.exe") } |
      Select-Object -First 1
    if (-not $binDir) {
      throw "Downloaded MKVToolNix archive did not contain mkvmerge.exe."
    }
    if (Test-Path $mkvtoolnixVendorDir) {
      Remove-Item -LiteralPath $mkvtoolnixVendorDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $mkvtoolnixVendorDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $binDir.FullName "mkvmerge.exe") -Destination $mkvmergeExe -Force
    Set-Content -LiteralPath $stampPath -Value $mkvtoolnixArchiveSha256 -Encoding ascii
  } finally {
    if (Test-Path $downloadRoot) {
      Remove-Item -LiteralPath $downloadRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
}
