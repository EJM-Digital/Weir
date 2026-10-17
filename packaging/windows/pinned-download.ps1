# Pinned third-party downloads for the Windows package scripts: dot-sourced from
# build-velopack-vendored-media-tools.ps1, which build-velopack.ps1 and fetch-bundled-tool-sources.ps1
# both dot-source in turn.
#
# Every archive is fetched from its upstream URL first and checked against its pinned SHA-256. Upstream
# does not keep files forever (BtbN prunes its dated autobuild releases after a couple of weeks), so when
# the upstream download fails, the same-named file is taken from Weir's own GitHub releases, which carry a
# copy of every archive they were built with (release.yml). The same pin applies to both sources: bytes
# that do not match it are refused, wherever they came from. Weir's releases are found from the public
# release feed and their download addresses, which do not count against GitHub's API allowance (60 an hour
# without a token, shared by the whole network); the API's release list is asked only when those find
# nothing. scripts/pinned-download.mjs does the same for the Node scripts.

. "$PSScriptRoot\..\..\scripts\repository.ps1"

$weirReleasesUrl = "$WeirApiRepositoryUrl/releases?per_page=30"
$weirReleasesFeedUrl = "$WeirRepositoryUrl/releases.atom"

function Save-WeirReleaseAsset {
  # Downloads $FileName into $OutFile from the newest Weir release that carries it and returns where it came
  # from: each release in the release feed is tried in turn, and the API's release list is asked only when
  # the feed cannot be read or none of its releases has the file.
  param(
    [Parameter(Mandatory)][string]$FileName,
    [Parameter(Mandatory)][string]$OutFile
  )
  $tags = @()
  try {
    $feed = (Invoke-WebRequest -Uri $weirReleasesFeedUrl -UseBasicParsing).Content
    $tags = @([regex]::Matches($feed, 'href="[^"]*/releases/tag/([^"]+)"') | ForEach-Object { [uri]::UnescapeDataString($_.Groups[1].Value) })
  } catch {
    Write-Host "Weir's release feed could not be read ($($_.Exception.Message)); asking the API for the release list instead."
  }
  foreach ($tag in $tags) {
    $source = "$WeirRepositoryUrl/releases/download/$([uri]::EscapeDataString($tag))/$([uri]::EscapeDataString($FileName))"
    try {
      Invoke-WebRequest -Uri $source -OutFile $OutFile -UseBasicParsing
      return $source
    } catch {
      # This release does not carry the file; the next one may.
    }
  }
  $source = Get-WeirReleaseAssetUrl -FileName $FileName
  Invoke-WebRequest -Uri $source -OutFile $OutFile -UseBasicParsing
  return $source
}

function Get-WeirReleaseAssetUrl {
  # The download URL of the newest Weir release that has an asset named exactly $FileName, from the API's release list.
  param([Parameter(Mandatory)][string]$FileName)
  $headers = @{ "User-Agent" = "weir-build"; "Accept" = "application/vnd.github+json" }
  $token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { $env:GH_TOKEN }
  if ($token) { $headers["Authorization"] = "Bearer $token" }
  $releases = Invoke-RestMethod -Uri $weirReleasesUrl -Headers $headers
  foreach ($release in $releases) {
    if ($release.draft) { continue }
    $asset = $release.assets | Where-Object { $_.name -ceq $FileName } | Select-Object -First 1
    if ($asset) { return $asset.browser_download_url }
  }
  throw "No Weir release has an asset named $FileName."
}

function Save-PinnedDownload {
  param(
    [Parameter(Mandatory)][string]$Url,
    [Parameter(Mandatory)][string]$OutFile,
    [Parameter(Mandatory)][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$Description
  )
  $fileName = Split-Path -Leaf $OutFile
  $source = $Url
  try {
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
  } catch {
    Write-Host "Could not download $Description from $Url ($($_.Exception.Message)); looking for $fileName on Weir's own releases."
    try {
      $source = Save-WeirReleaseAsset -FileName $fileName -OutFile $OutFile
    } catch {
      throw "Could not download $Description from $Url, nor from Weir's own releases: $($_.Exception.Message)"
    }
  }
  $actualSha256 = (Get-FileHash -LiteralPath $OutFile -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actualSha256 -ne $ExpectedSha256) {
    throw "$Description hash mismatch. Expected $ExpectedSha256 but got $actualSha256 from $source. Upstream may have rewritten the file this pin names; re-verify before re-pinning."
  }
}
