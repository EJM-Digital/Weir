param(
  [string]$PackageDir = "",
  # Weir's own default (PortChoice.DefaultPort in the tray, ServerListenOptions.DefaultPort in the
  # server), so the release gate runs the package on the port it ships with. Pass -Port if a Weir
  # you have installed is already using it.
  [int]$Port = 9347,
  [string]$ExpectedVersion = ""
)

$ErrorActionPreference = "Stop"

# Smoke test for the assembled Windows package (packaging/windows/build-velopack.ps1): the exact
# server directory the tray app launches (dist\windows\pack\server), started the way the tray starts
# it, then driven through sign-up, a library and a real pass-through job with the bundled ffmpeg.
# Then the packaged tray itself (Weir.exe), started the way an unattended install starts it — with
# --port and no one to answer a dialog — must come up on that port, save it, and show no window.
# Then again with --silent (#779), which must skip every window regardless of what the desktop
# heuristic reports on this runner. Finally, the real Weir-win-Setup.exe is installed and uninstalled
# for real, with its own and the installed Weir.exe's stdout/stderr piped exactly the way a program
# driving Weir would capture them, proving neither hangs a caller waiting on that output to close.

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $PackageDir) {
  $PackageDir = Join-Path $repoRoot "dist\windows\pack\server"
}
$packagePath = (Resolve-Path -LiteralPath $PackageDir).Path
if (-not $ExpectedVersion -and $env:WEIR_BUILD_VERSION) {
  # The version build-velopack.ps1 stamped, as CI sets it for a golden-path build.
  $ExpectedVersion = $env:WEIR_BUILD_VERSION.TrimStart("v")
}
if (-not $ExpectedVersion) {
  $propsPath = Join-Path $repoRoot "apps\server\Directory.Build.props"
  $propsMatch = [regex]::Match((Get-Content -LiteralPath $propsPath -Raw), '<WeirVersion>([^<]+)</WeirVersion>')
  if (-not $propsMatch.Success) {
    throw "WeirVersion was not found in $propsPath."
  }
  $ExpectedVersion = $propsMatch.Groups[1].Value.Trim()
}
$serverExe = Join-Path $packagePath "WeirServer.exe"
$webDist = Join-Path $packagePath "web-dist"
$webIndex = Join-Path $webDist "index.html"
$ffmpegExe = Join-Path $packagePath "bin\ffmpeg\ffmpeg.exe"
$ffprobeExe = Join-Path $packagePath "bin\ffmpeg\ffprobe.exe"
$trayExe = Join-Path (Split-Path -Parent $packagePath) "Weir.exe"

if (-not (Test-Path -LiteralPath $serverExe)) {
  throw "Packaged server executable not found: $serverExe"
}
if (-not (Test-Path -LiteralPath $trayExe)) {
  throw "Packaged tray executable not found next to the server folder: $trayExe"
}
if (-not (Test-Path -LiteralPath $webIndex)) {
  throw "Packaged web index not found: $webIndex"
}
if (-not (Test-Path -LiteralPath $ffmpegExe)) {
  throw "Packaged ffmpeg executable not found: $ffmpegExe"
}
if (-not (Test-Path -LiteralPath $ffprobeExe)) {
  throw "Packaged ffprobe executable not found: $ffprobeExe"
}

# Removes a folder a process that was just stopped may still hold open, trying again until it is gone. The timeout is a
# hang guard: the removal ends the moment the last handle is released, and a folder that cannot be removed is only left behind.
function Remove-FolderWhenFree {
  param(
    [Parameter(Mandatory)] [string]$Path,
    [int]$TimeoutSeconds = 10
  )
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline)
}
$indexText = Get-Content -LiteralPath $webIndex -Raw
if ($indexText -notmatch "Weir") {
  throw "Packaged web index does not look like Weir."
}

$serverVersion = (& $serverExe --version).Trim()
if ($serverVersion -ne $ExpectedVersion) {
  throw "Packaged WeirServer.exe reports version '$serverVersion' but expected '$ExpectedVersion'."
}

$runtimeHome = Join-Path ([System.IO.Path]::GetTempPath()) ("weir-package-smoke-" + [System.Guid]::NewGuid().ToString("N"))
$stdout = Join-Path $runtimeHome "server.stdout.log"
$stderr = Join-Path $runtimeHome "server.stderr.log"
New-Item -ItemType Directory -Path $runtimeHome | Out-Null

$oldHome = $env:WEIR_HOME
$oldSecret = $env:WEIR_SESSION_SECRET
$oldCookieSecure = $env:WEIR_SESSION_COOKIE_SECURE
$oldEnv = $env:WEIR_ENV
$oldWebDist = $env:WEIR_WEB_DIST
$oldFfmpegDir = $env:WEIR_FFMPEG_DIR
$oldPath = $env:PATH
$proc = $null

try {
  $env:WEIR_HOME = $runtimeHome
  $env:WEIR_SESSION_SECRET = "ci-weir-session-secret-32chars-min"
  $env:WEIR_SESSION_COOKIE_SECURE = "false"
  # The environment the tray app gives the server (apps/tray/Weir.Tray/Program.cs, PrepareEnvironment).
  $env:WEIR_ENV = "production"
  $env:WEIR_WEB_DIST = $webDist
  # The bundled ffmpeg must be found on its own (<app>\bin\ffmpeg), not through a developer's PATH.
  Remove-Item Env:\WEIR_FFMPEG_DIR -ErrorAction SilentlyContinue
  $env:PATH = (($oldPath -split ";") | Where-Object {
      $_ -and -not (Test-Path -LiteralPath (Join-Path $_ "ffprobe.exe")) -and -not (Test-Path -LiteralPath (Join-Path $_ "ffmpeg.exe"))
    }) -join ";"

  $proc = Start-Process -FilePath $serverExe `
    -ArgumentList @("--port", [string]$Port) `
    -WorkingDirectory $packagePath `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -WindowStyle Hidden `
    -PassThru

  $readyUrl = "http://127.0.0.1:$Port/ready"
  $openApiUrl = "http://127.0.0.1:$Port/openapi.json"
  $deadline = (Get-Date).AddSeconds(60)
  $serverReady = $false
  do {
    if ($proc.HasExited) {
      throw "Packaged Weir server exited early with code $($proc.ExitCode)."
    }
    try {
      $ready = Invoke-RestMethod -Uri $readyUrl -Method Get -TimeoutSec 2
      if ($ready.ready -eq $true) {
        $openApi = Invoke-RestMethod -Uri $openApiUrl -Method Get -TimeoutSec 2
        $reportedVersion = [string]$openApi.info.version
        if ($reportedVersion -ne $ExpectedVersion) {
          throw "Packaged Weir server reported version '$reportedVersion' but expected '$ExpectedVersion'."
        }
        Write-Host "Packaged Weir server readiness and version checks passed on $readyUrl"
        $serverReady = $true
        break
      }
    } catch {
      Start-Sleep -Milliseconds 500
    }
  } while ((Get-Date) -lt $deadline)

  if (-not $serverReady) {
    throw "Packaged Weir server did not become ready at $readyUrl."
  }

  # Prove the exact packaged server can pass an intentional edge-case file
  # through unchanged, publish it to the configured processed tree, and only
  # then remove the watched source. This is a real filesystem lifecycle test,
  # not an API-shape assertion. A sibling of $runtimeHome, not inside it: Weir refuses its own
  # data folder as a library folder, and a real install never keeps media there either.
  $fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("weir-package-smoke-fixture-" + [System.Guid]::NewGuid().ToString("N"))
  $watchedRoot = Join-Path $fixtureRoot "watch"
  $workRoot = Join-Path $fixtureRoot "work"
  $outputRoot = Join-Path $fixtureRoot "processed"
  $releaseRoot = Join-Path $watchedRoot "ForeignFilm"
  New-Item -ItemType Directory -Path $releaseRoot, $workRoot, $outputRoot | Out-Null
  $sourcePath = Join-Path $releaseRoot "foreign-only.mkv"
  & $ffmpegExe `
    -nostdin -hide_banner -loglevel error `
    -f lavfi -i "color=c=black:s=320x180:d=2" `
    -f lavfi -i "sine=frequency=440:duration=2" `
    -map 0:v -map 1:a -c:v mpeg4 -c:a aac `
    -metadata:s:a:0 language=jpn -y $sourcePath
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Packaged FFmpeg could not create the pass-through fixture."
  }
  (Get-Item -LiteralPath $sourcePath).LastWriteTime = (Get-Date).AddMinutes(-10)
  $sourceLength = (Get-Item -LiteralPath $sourcePath).Length
  $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash

  $baseUrl = "http://127.0.0.1:$Port"
  $browserHeaders = @{
    Origin = $baseUrl
    "Content-Type" = "application/json"
    "X-Requested-With" = "XMLHttpRequest"
  }
  $readHeaders = @{ "X-Requested-With" = "XMLHttpRequest" }
  $webSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession

  $csrf = (Invoke-RestMethod -Uri "$baseUrl/api/v1/auth/csrf" -WebSession $webSession -Headers $readHeaders -TimeoutSec 15).csrf_token
  $bootstrapBody = @{
    username = "package-smoke-admin"
    password = "package-smoke-pass-20260901"
    csrf_token = $csrf
  } | ConvertTo-Json -Compress
  Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/auth/bootstrap" -WebSession $webSession -Headers $browserHeaders -Body $bootstrapBody -TimeoutSec 15 | Out-Null

  $csrf = (Invoke-RestMethod -Uri "$baseUrl/api/v1/auth/csrf" -WebSession $webSession -Headers $readHeaders -TimeoutSec 15).csrf_token
  $loginBody = @{
    username = "package-smoke-admin"
    password = "package-smoke-pass-20260901"
    csrf_token = $csrf
    trusted_device = $false
  } | ConvertTo-Json -Compress
  Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/auth/login" -WebSession $webSession -Headers $browserHeaders -Body $loginBody -TimeoutSec 15 | Out-Null

  $csrf = (Invoke-RestMethod -Uri "$baseUrl/api/v1/auth/csrf" -WebSession $webSession -Headers $readHeaders -TimeoutSec 15).csrf_token
  # The Direct Play device list is a data file; prove the packaged app can read it (#467).
  $devices = Invoke-RestMethod -Uri "$baseUrl/api/v1/processing/direct-play/devices" -WebSession $webSession -Headers $readHeaders -TimeoutSec 15
  if (-not $devices.devices -or $devices.devices.Count -lt 1) {
    throw "The packaged app could not load its Direct Play device list."
  }

  # The server must find the ffmpeg bundled next to it (<app>\bin\ffmpeg); PATH was stripped of ffmpeg above.
  $hardware = Invoke-RestMethod -Uri "$baseUrl/api/v1/processing/hardware" -WebSession $webSession -Headers $readHeaders -TimeoutSec 60
  if ([string]$hardware.detail -match "could not find ffmpeg") {
    throw "The packaged server did not find its bundled ffmpeg: $($hardware.detail)"
  }
  Write-Host "Packaged server found its bundled ffmpeg."

  # #548: and the mkvmerge bundled next to it (<app>\bin\mkvtoolnix). This is the assertion that keeps
  # the mkvmerge writer from silently shipping inert again: the writer setting's default is "best",
  # and "best" degrades to ffmpeg without complaint wherever mkvmerge cannot be found, so nothing else
  # in the smoke test would notice a package that stopped carrying it. MKVToolNix is not installed on
  # the CI runner and PATH was stripped above, so the only way this can answer with a version is the
  # copy the package itself shipped.
  $mediaTools = Invoke-RestMethod -Uri "$baseUrl/api/v1/system/media-tools" -WebSession $webSession -Headers $readHeaders -TimeoutSec 60
  if ([string]$mediaTools.mkvmerge -notmatch "^mkvmerge ") {
    throw "The packaged server did not find its bundled mkvmerge: $($mediaTools.mkvmerge)"
  }
  Write-Host "Packaged server found its bundled mkvmerge: $($mediaTools.mkvmerge)"

  # Configure the seeded Movies library directly (the path-settings route was retired in #460).
  $libraries = Invoke-RestMethod -Uri "$baseUrl/api/v1/processing/libraries" -WebSession $webSession -Headers $readHeaders -TimeoutSec 15
  $moviesLibrary = $libraries | Where-Object { $_.media_type -eq "movie" } | Select-Object -First 1
  if (-not $moviesLibrary) {
    throw "The packaged install has no Movies library to configure."
  }
  $pathBody = @{
    csrf_token = $csrf
    name = $moviesLibrary.name
    media_type = "movie"
    watched_folder = $watchedRoot
    work_folder = $workRoot
    output_folder = $outputRoot
    manager_connection_ids = @($moviesLibrary.manager_connection_ids)
  } | ConvertTo-Json -Compress
  Invoke-RestMethod -Method Put -Uri "$baseUrl/api/v1/processing/libraries/$($moviesLibrary.id)" -WebSession $webSession -Headers $browserHeaders -Body $pathBody -TimeoutSec 15 | Out-Null

  $csrf = (Invoke-RestMethod -Uri "$baseUrl/api/v1/auth/csrf" -WebSession $webSession -Headers $readHeaders -TimeoutSec 15).csrf_token
  $enqueueBody = @{
    csrf_token = $csrf
    relative_media_path = "ForeignFilm/foreign-only.mkv"
    media_scope = "movie"
    pass_through_unchanged = $true
  } | ConvertTo-Json -Compress
  $job = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/processing/jobs/file-remux-pass/enqueue" -WebSession $webSession -Headers $browserHeaders -Body $enqueueBody -TimeoutSec 15
  if (-not $job.job_id) {
    throw "Pass-through enqueue did not return a job id."
  }

  $outputPath = Join-Path $outputRoot "ForeignFilm\foreign-only.mkv"
  $jobDeadline = (Get-Date).AddSeconds(90)
  $jobStatus = ""
  $lastError = ""
  do {
    $inspection = Invoke-RestMethod -Uri "$baseUrl/api/v1/processing/jobs/inspection?limit=100" -WebSession $webSession -Headers $readHeaders -TimeoutSec 15
    $jobRow = $inspection.jobs | Where-Object { $_.id -eq $job.job_id } | Select-Object -First 1
    if ($jobRow) {
      $jobStatus = [string]$jobRow.status
      $lastError = [string]$jobRow.last_error
      if ($jobStatus -in @("failed", "cancelled")) {
        break
      }
    }
    if ($jobStatus -eq "completed" -and (Test-Path -LiteralPath $outputPath -PathType Leaf) -and -not (Test-Path -LiteralPath $sourcePath)) {
      break
    }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $jobDeadline)

  if ($jobStatus -ne "completed") {
    throw "Pass-through job did not complete (status='$jobStatus', error='$lastError')."
  }
  if (Test-Path -LiteralPath $sourcePath) {
    throw "Pass-through source was not removed after successful output validation."
  }
  if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw "Pass-through output was not placed in the configured processed folder."
  }
  $outputLength = (Get-Item -LiteralPath $outputPath).Length
  $outputHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath).Hash
  if ($outputLength -ne $sourceLength -or $outputHash -ne $sourceHash) {
    throw "Pass-through output is not byte-identical to the source fixture."
  }
  Write-Host "Packaged Processing pass-through lifecycle passed: source cleaned after byte-identical processed output was validated."
} catch {
  Write-Host "Packaged server smoke failed."
  if (Test-Path -LiteralPath $stdout) {
    Write-Host "--- stdout ---"
    Get-Content -LiteralPath $stdout -Tail 200
  }
  if (Test-Path -LiteralPath $stderr) {
    Write-Host "--- stderr ---"
    Get-Content -LiteralPath $stderr -Tail 200
  }
  $logPath = Join-Path $runtimeHome "logs\weir.log"
  if (Test-Path -LiteralPath $logPath) {
    Write-Host "--- weir.log ---"
    Get-Content -LiteralPath $logPath -Tail 200
  }
  throw
} finally {
  if ($proc -and -not $proc.HasExited) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  }
  if ($null -ne $oldHome) { $env:WEIR_HOME = $oldHome } else { Remove-Item Env:\WEIR_HOME -ErrorAction SilentlyContinue }
  if ($null -ne $oldSecret) { $env:WEIR_SESSION_SECRET = $oldSecret } else { Remove-Item Env:\WEIR_SESSION_SECRET -ErrorAction SilentlyContinue }
  if ($null -ne $oldCookieSecure) { $env:WEIR_SESSION_COOKIE_SECURE = $oldCookieSecure } else { Remove-Item Env:\WEIR_SESSION_COOKIE_SECURE -ErrorAction SilentlyContinue }
  if ($null -ne $oldEnv) { $env:WEIR_ENV = $oldEnv } else { Remove-Item Env:\WEIR_ENV -ErrorAction SilentlyContinue }
  if ($null -ne $oldWebDist) { $env:WEIR_WEB_DIST = $oldWebDist } else { Remove-Item Env:\WEIR_WEB_DIST -ErrorAction SilentlyContinue }
  if ($null -ne $oldFfmpegDir) { $env:WEIR_FFMPEG_DIR = $oldFfmpegDir } else { Remove-Item Env:\WEIR_FFMPEG_DIR -ErrorAction SilentlyContinue }
  $env:PATH = $oldPath
  Remove-Item -LiteralPath $runtimeHome -Recurse -Force -ErrorAction SilentlyContinue
  if ($fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

# The packaged tray, started the way a scripted or remote (WinRM) install starts it: the port is
# supplied, so it must be used and saved with no dialog, and the server must come up on it. A tray
# that showed its first-run window here would sit waiting for a click that never comes, so this
# phase also fails if the tray has a window at all.
$trayHome = Join-Path ([System.IO.Path]::GetTempPath()) ("weir-package-tray-smoke-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $trayHome | Out-Null
$oldHome = $env:WEIR_HOME
$oldWeirPort = $env:WEIR_PORT
$trayProc = $null
try {
  $env:WEIR_HOME = $trayHome
  Remove-Item Env:\WEIR_PORT -ErrorAction SilentlyContinue
  $trayProc = Start-Process -FilePath $trayExe `
    -ArgumentList @("--port", [string]$Port, "--no-browser") `
    -WorkingDirectory (Split-Path -Parent $trayExe) `
    -PassThru

  $readyUrl = "http://127.0.0.1:$Port/ready"
  $deadline = (Get-Date).AddSeconds(90)
  $trayReady = $false
  do {
    if ($trayProc.HasExited) {
      throw "Packaged tray exited with code $($trayProc.ExitCode) before Weir was ready (exit code 0 this early means another Weir tray is already running in this session)."
    }
    try {
      $ready = Invoke-RestMethod -Uri $readyUrl -Method Get -TimeoutSec 2
      if ($ready.ready -eq $true) { $trayReady = $true; break }
    } catch {
      Start-Sleep -Milliseconds 500
    }
  } while ((Get-Date) -lt $deadline)
  if (-not $trayReady) {
    throw "Packaged tray did not bring Weir up at $readyUrl."
  }

  $trayProc.Refresh()
  if ($trayProc.MainWindowHandle -ne [IntPtr]::Zero) {
    throw "Packaged tray opened a window ('$($trayProc.MainWindowTitle)') although --port was supplied; an unattended install would hang on it."
  }
  $savedPort = (Get-Content -LiteralPath (Join-Path $trayHome "port.txt") -Raw).Trim()
  if ($savedPort -ne [string]$Port) {
    throw "Packaged tray saved port '$savedPort' but was started with --port $Port."
  }
  $currentPort = (Get-Content -LiteralPath (Join-Path $trayHome "current-port.txt") -Raw).Trim()
  if ($currentPort -ne [string]$Port) {
    throw "Packaged tray wrote current-port.txt '$currentPort' but is running on $Port."
  }
  # The tray may be writing its log at the moment it is read, so a read can come back short: look again for a while.
  $portLine = [regex]::Escape("Using port $Port from --port")
  $logDeadline = (Get-Date).AddSeconds(10)
  do {
    $trayLog = Get-Content -LiteralPath (Join-Path $trayHome "tray-host.log") -Raw -ErrorAction SilentlyContinue
    if ($trayLog -match $portLine) {
      break
    }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $logDeadline)
  if ($trayLog -notmatch $portLine) {
    throw "Packaged tray did not log that it used the supplied port."
  }
  Write-Host "Packaged tray started unattended on port $Port with --port: no dialog, port saved, server ready."
} catch {
  Write-Host "Packaged tray smoke failed."
  $trayLogPath = Join-Path $trayHome "tray-host.log"
  if (Test-Path -LiteralPath $trayLogPath) {
    Write-Host "--- tray-host.log ---"
    Get-Content -LiteralPath $trayLogPath -Tail 100
  }
  throw
} finally {
  if ($trayProc -and -not $trayProc.HasExited) {
    # The tray supervises WeirServer.exe as a child; take the whole tree down.
    & taskkill.exe /PID $trayProc.Id /T /F | Out-Null
  }
  if ($null -ne $oldHome) { $env:WEIR_HOME = $oldHome } else { Remove-Item Env:\WEIR_HOME -ErrorAction SilentlyContinue }
  if ($null -ne $oldWeirPort) { $env:WEIR_PORT = $oldWeirPort }
  Remove-FolderWhenFree -Path $trayHome
}

# --silent (#779): the flag documented in docs/release.md "Installing Weir from another program" for a program
# (Deluno's installer, a provisioning script) driving Weir unattended. It must skip every window on its own, not
# just because the desktop heuristic happens to guess right on this runner.
$silentHome = Join-Path ([System.IO.Path]::GetTempPath()) ("weir-package-silent-smoke-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $silentHome | Out-Null
$oldHome = $env:WEIR_HOME
$oldWeirPort = $env:WEIR_PORT
$silentProc = $null
try {
  $env:WEIR_HOME = $silentHome
  Remove-Item Env:\WEIR_PORT -ErrorAction SilentlyContinue
  $silentProc = Start-Process -FilePath $trayExe `
    -ArgumentList @("--port", [string]$Port, "--silent") `
    -WorkingDirectory (Split-Path -Parent $trayExe) `
    -PassThru

  $readyUrl = "http://127.0.0.1:$Port/ready"
  $deadline = (Get-Date).AddSeconds(90)
  $silentReady = $false
  do {
    if ($silentProc.HasExited) {
      throw "Packaged tray exited with code $($silentProc.ExitCode) before Weir was ready under --silent."
    }
    try {
      $ready = Invoke-RestMethod -Uri $readyUrl -Method Get -TimeoutSec 2
      if ($ready.ready -eq $true) { $silentReady = $true; break }
    } catch {
      Start-Sleep -Milliseconds 500
    }
  } while ((Get-Date) -lt $deadline)
  if (-not $silentReady) {
    throw "Packaged tray did not bring Weir up at $readyUrl under --silent."
  }

  $silentProc.Refresh()
  if ($silentProc.MainWindowHandle -ne [IntPtr]::Zero) {
    throw "Packaged tray opened a window ('$($silentProc.MainWindowTitle)') under --silent; an unattended install would hang on it."
  }
  Write-Host "Packaged tray started unattended on port $Port with --silent: no dialog, no window, server ready."
} catch {
  Write-Host "Packaged tray --silent smoke failed."
  $silentLogPath = Join-Path $silentHome "tray-host.log"
  if (Test-Path -LiteralPath $silentLogPath) {
    Write-Host "--- tray-host.log ---"
    Get-Content -LiteralPath $silentLogPath -Tail 100
  }
  throw
} finally {
  if ($silentProc -and -not $silentProc.HasExited) {
    & taskkill.exe /PID $silentProc.Id /T /F | Out-Null
  }
  if ($null -ne $oldHome) { $env:WEIR_HOME = $oldHome } else { Remove-Item Env:\WEIR_HOME -ErrorAction SilentlyContinue }
  if ($null -ne $oldWeirPort) { $env:WEIR_PORT = $oldWeirPort }
  Remove-FolderWhenFree -Path $silentHome
}

# ---------------------------------------------------------------------------------------------------
# The real Weir-win-Setup.exe (#779), run and captured exactly the way another program installing Weir
# does it: piped stdout/stderr, not a file, waited on independently of the process's own exit. A file
# redirect would not reproduce the failure mode this proves against. .NET's Process class, when asked
# to redirect a child's output, creates real anonymous pipes and marks their write ends inheritable --
# the same thing a .NET-based caller (or anything else that redirects a child's stdio the ordinary
# way) does. If Setup, or anything Setup starts, leaves a duplicate of a write end open, the read side
# below never reaches end-of-file, even once Setup itself has exited; a caller reading with
# ReadToEnd()/communicate() and treating "the streams closed" as "the child is done" hangs forever.
# Everything above this point used the pack directory directly and never touched the real installer.
# ---------------------------------------------------------------------------------------------------
$setupExePath = Join-Path $repoRoot "dist\windows\releases\Weir-win-Setup.exe"
if (-not (Test-Path -LiteralPath $setupExePath)) {
  throw "Real installer not found: $setupExePath (expected from the vpk pack step of packaging/windows/build-velopack.ps1)."
}

function Wait-ForPipeEndOfFile {
  param(
    [Parameter(Mandatory)] [System.Threading.Tasks.Task[]]$Tasks,
    [Parameter(Mandatory)] [int]$TimeoutSeconds,
    [Parameter(Mandatory)] [string]$What
  )
  if (-not [System.Threading.Tasks.Task]::WaitAll($Tasks, [TimeSpan]::FromSeconds($TimeoutSeconds))) {
    throw "$What did not reach end-of-file within $TimeoutSeconds s: something is still holding an inherited pipe handle open (#779)."
  }
}

# The addresses something is listening on for a TCP port, straight from the Windows TCP table (Get-NetTCPConnection),
# independently of anything Weir reports about itself: "127.0.0.1" and "::1" are this PC only, "::" and "0.0.0.0"
# are every network interface.
function Get-ListenAddresses {
  param([Parameter(Mandatory)] [int]$ListenPort)
  @(Get-NetTCPConnection -LocalPort $ListenPort -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object { $_.LocalAddress } |
    Sort-Object -Unique)
}

function Test-ListensOnThisPcOnly {
  param([string[]]$Addresses)
  $loopback = @("127.0.0.1", "::1")
  $strangers = @($Addresses | Where-Object { $loopback -notcontains $_ })
  # Both loopback addresses are expected wherever the runner has IPv6 at all.
  $ipv6Missing = [System.Net.Sockets.Socket]::OSSupportsIPv6 -and ($Addresses -notcontains "::1")
  ($Addresses -contains "127.0.0.1") -and $strangers.Count -eq 0 -and -not $ipv6Missing
}

function Test-ListensOnEveryInterface {
  param([string[]]$Addresses)
  ($Addresses -contains "::") -or ($Addresses -contains "0.0.0.0")
}

# Polls the listen addresses until $Test accepts them, so a restart in progress is waited out and a wrong answer is
# reported with what was actually seen.
function Wait-ForListenAddresses {
  param(
    [Parameter(Mandatory)] [int]$ListenPort,
    [Parameter(Mandatory)] [scriptblock]$Test,
    [Parameter(Mandatory)] [int]$TimeoutSeconds,
    [Parameter(Mandatory)] [string]$Expected
  )
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $addresses = Get-ListenAddresses -ListenPort $ListenPort
    if (& $Test $addresses) { return $addresses }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $deadline)
  throw "Expected the server to listen $Expected on port $ListenPort within $TimeoutSeconds s, but it listens on: $($addresses -join ', ')."
}

function Assert-WeirFirewallRule {
  param([Parameter(Mandatory)] [string]$ServerExe, [Parameter(Mandatory)] [string]$Because)
  $rule = Get-NetFirewallRule -DisplayName "Weir" -ErrorAction SilentlyContinue
  if (-not $rule) {
    throw "$Because did not create a firewall rule named 'Weir'."
  }
  if ($rule.Direction -ne "Inbound" -or $rule.Action -ne "Allow" -or $rule.Enabled -ne "True") {
    throw "The Weir firewall rule is not an enabled inbound allow rule (Direction=$($rule.Direction), Action=$($rule.Action), Enabled=$($rule.Enabled))."
  }
  $ruleProfile = [string]$rule.Profile
  # Windows reports a rule that covers every network as "Any"; otherwise the three profile names are listed.
  $coversEveryProfile = $ruleProfile -match "Any" -or ($ruleProfile -match "Domain" -and $ruleProfile -match "Private" -and $ruleProfile -match "Public")
  if (-not $coversEveryProfile) {
    throw "The Weir firewall rule's profile is '$ruleProfile'; expected every profile (Domain, Private and Public)."
  }
  $ruleProgram = (Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule).Program
  if ($ruleProgram -ne $ServerExe) {
    throw "The Weir firewall rule's program is '$ruleProgram', expected '$ServerExe'."
  }
  Write-Host "Weir firewall rule verified after ${Because}: inbound allow, profile '$ruleProfile', scoped to $ruleProgram."
}

# The Weir and WeirServer processes running from the installed copy, whatever else runs on the machine: the package
# folder's earlier smoke trays, a development build and the like are not the install.
function Get-InstalledWeirProcesses {
  param([Parameter(Mandatory)] [string]$InstallDirectory)
  @(Get-Process -Name "Weir", "WeirServer" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDirectory + "\", [System.StringComparison]::OrdinalIgnoreCase) })
}

# Waits until nothing of the install's is running and returns what is still running when it gives up (nothing, when it is
# not). The timeout is a hang guard: the wait ends the moment the last process has gone.
function Wait-ForNoInstalledWeirProcesses {
  param(
    [Parameter(Mandatory)] [string]$InstallDirectory,
    [Parameter(Mandatory)] [int]$TimeoutSeconds
  )
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $running = Get-InstalledWeirProcesses -InstallDirectory $InstallDirectory
    if (-not $running) { return @() }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline)
  return $running
}

# Waits for the installed Weir's /ready to answer {"ready": true}.
function Wait-ForInstalledReady {
  param(
    [Parameter(Mandatory)] [int]$ReadyPort,
    [Parameter(Mandatory)] [int]$TimeoutSeconds,
    [Parameter(Mandatory)] [string]$What
  )
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    try {
      $ready = Invoke-RestMethod -Uri "http://127.0.0.1:$ReadyPort/ready" -Method Get -TimeoutSec 2
      if ($ready.ready -eq $true) { return }
    } catch {
      Start-Sleep -Milliseconds 500
    }
  } while ((Get-Date) -lt $deadline)
  throw "$What did not answer /ready on port $ReadyPort within $TimeoutSeconds s."
}

# Hang guards, not paces: each wait below ends the moment what it waits for has happened, and these are the longest a wait
# is given on a busy machine before the run is called hung.
$setupTimeoutSeconds = 120
$weirHandleTimeoutSeconds = 30
$installedHealthTimeoutSeconds = 90
$lanAccessTimeoutSeconds = 90
$hookExitTimeoutSeconds = 10
$localAppData = [Environment]::GetFolderPath("LocalApplicationData")
$installedTrayExe = Join-Path $localAppData "Weir\current\Weir.exe"
$installedDirectory = Join-Path $localAppData "Weir"
$installedServerExe = Join-Path (Split-Path -Parent $installedTrayExe) "server\WeirServer.exe"
$updateExe = Join-Path $localAppData "Weir\Update.exe"
$defaultRuntimeHome = "C:\ProgramData\Weir"

try {
  Write-Host "Installing the real Setup.exe silently, its output piped the way a capturing caller reads it..."
  $setupPsi = New-Object System.Diagnostics.ProcessStartInfo
  $setupPsi.FileName = $setupExePath
  $setupPsi.Arguments = "--silent"
  $setupPsi.UseShellExecute = $false
  $setupPsi.RedirectStandardOutput = $true
  $setupPsi.RedirectStandardError = $true
  $setupPsi.CreateNoWindow = $true

  $setupProc = [System.Diagnostics.Process]::Start($setupPsi)
  # Read async before waiting on exit: a synchronous ReadToEnd here, before the process has exited,
  # can itself deadlock if the child fills the pipe buffer while this script blocks on WaitForExit.
  $setupStdoutTask = $setupProc.StandardOutput.ReadToEndAsync()
  $setupStderrTask = $setupProc.StandardError.ReadToEndAsync()

  if (-not $setupProc.WaitForExit($setupTimeoutSeconds * 1000)) {
    Stop-Process -Id $setupProc.Id -Force -ErrorAction SilentlyContinue
    throw "Weir-win-Setup.exe --silent did not exit within $setupTimeoutSeconds s (#779)."
  }
  if ($setupProc.ExitCode -ne 0) {
    throw "Weir-win-Setup.exe --silent exited with code $($setupProc.ExitCode)."
  }
  Write-Host "Setup.exe --silent exited 0 within $setupTimeoutSeconds s."

  Wait-ForPipeEndOfFile -Tasks @($setupStdoutTask, $setupStderrTask) -TimeoutSeconds $setupTimeoutSeconds -What "Setup.exe's own piped output"
  Write-Host "Setup.exe's piped stdout and stderr both reached end-of-file: nothing it started is still holding them open."

  if (-not (Test-Path -LiteralPath $installedTrayExe)) {
    throw "Setup.exe --silent did not install Weir.exe at $installedTrayExe."
  }
  # --silent must not launch the app (docs.velopack.io), and a Weir that was not running from the install is not
  # started again either (#942). Setup has exited and its output is at end-of-file, so every process it started that
  # held a pipe has gone; a hook process that held none is waited for until it is gone, and anything of the install's
  # still running then is a Weir that was launched. The earlier smoke steps ran the package folder's tray and left it
  # marked as running, which a first install must ignore.
  $leftRunning = Wait-ForNoInstalledWeirProcesses -InstallDirectory $installedDirectory -TimeoutSeconds $hookExitTimeoutSeconds
  if ($leftRunning) {
    throw "Weir-win-Setup.exe --silent left Weir running (pid(s): $($leftRunning.Id -join ', ')); --silent must never launch the app."
  }
  Write-Host "Confirmed: --silent installed Weir without starting it."

  # This build is 0.0.1-dev, so an installed tray left on Auto would download and install the newest published release
  # after its idle minutes. Notify only downloads nothing, so no step below ever installs a published release.
  New-Item -ItemType Directory -Path $defaultRuntimeHome -Force | Out-Null
  Set-Content -LiteralPath (Join-Path $defaultRuntimeHome "update-settings.json") -Value '{ "mode": "NotifyOnly" }' -Encoding ASCII

  Write-Host "Starting the installed Weir.exe --silent, its output piped the same way..."
  $weirPsi = New-Object System.Diagnostics.ProcessStartInfo
  $weirPsi.FileName = $installedTrayExe
  $weirPsi.Arguments = "--port $Port --silent"
  $weirPsi.UseShellExecute = $false
  $weirPsi.RedirectStandardOutput = $true
  $weirPsi.RedirectStandardError = $true
  $weirPsi.CreateNoWindow = $true

  $weirProc = $null
  try {
    $weirProc = [System.Diagnostics.Process]::Start($weirPsi)
    $weirStdoutTask = $weirProc.StandardOutput.ReadToEndAsync()
    $weirStderrTask = $weirProc.StandardError.ReadToEndAsync()

    # Weir keeps running once started -- it is not supposed to exit -- so the proof here is that its
    # own piped output reaches end-of-file quickly on its own, independent of the process ever exiting.
    Wait-ForPipeEndOfFile -Tasks @($weirStdoutTask, $weirStderrTask) -TimeoutSeconds $weirHandleTimeoutSeconds -What "the installed Weir.exe's piped output"
    Write-Host "Weir.exe's piped stdout and stderr reached end-of-file within $weirHandleTimeoutSeconds s while Weir keeps running: it is not holding a caller's pipes open."

    $healthUrl = "http://127.0.0.1:$Port/health"
    $deadline = (Get-Date).AddSeconds($installedHealthTimeoutSeconds)
    $installedHealthy = $false
    do {
      if ($weirProc.HasExited) {
        throw "The installed Weir.exe exited with code $($weirProc.ExitCode) before it became healthy."
      }
      try {
        Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 2 | Out-Null
        $installedHealthy = $true
        break
      } catch {
        Start-Sleep -Milliseconds 500
      }
    } while ((Get-Date) -lt $deadline)
    if (-not $installedHealthy) {
      throw "The installed Weir did not answer $healthUrl within $installedHealthTimeoutSeconds s."
    }
    Write-Host "Installed Weir answered $healthUrl."

    # Nobody has allowed LAN access yet, so the server must listen on this PC only (#808): the TCP table's view of
    # WeirServer.exe, not just what Weir says about itself.
    $beforeAllowLan = Wait-ForListenAddresses -ListenPort $Port -Test { param($a) Test-ListensOnThisPcOnly $a } -TimeoutSeconds 10 -Expected "on this PC only (127.0.0.1 and ::1)"
    Write-Host "Before --allow-lan, the installed server listens only on this PC: $($beforeAllowLan -join ', ')."

    # --allow-lan (#779, #808): the runner is elevated, so it turns LAN access on and creates the firewall rule. The
    # running tray notices the saved choice and restarts the server for other devices.
    Write-Host "Running the installed Weir.exe --allow-lan..."
    $allowLanProc = Start-Process -FilePath $installedTrayExe -ArgumentList @("--allow-lan") -PassThru -Wait -WindowStyle Hidden
    if ($allowLanProc.ExitCode -ne 0) {
      throw "Weir.exe --allow-lan exited with code $($allowLanProc.ExitCode)."
    }
    Assert-WeirFirewallRule -ServerExe $installedServerExe -Because "--allow-lan"
    $afterAllowLan = Wait-ForListenAddresses -ListenPort $Port -Test { param($a) Test-ListensOnEveryInterface $a } -TimeoutSeconds $lanAccessTimeoutSeconds -Expected "on every interface"
    Write-Host "After --allow-lan, the installed server listens on every interface: $($afterAllowLan -join ', ')."
    Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 5 | Out-Null
    Write-Host "Installed Weir still answers $healthUrl after the restart."
    Get-Content -LiteralPath (Join-Path $defaultRuntimeHome "tray-host.log") |
      Where-Object { $_ -match "LAN access" } |
      ForEach-Object { Write-Host "tray-host.log: $_" }
  } finally {
    if ($weirProc -and -not $weirProc.HasExited) {
      & taskkill.exe /PID $weirProc.Id /T /F | Out-Null
      Start-Sleep -Milliseconds 500
    }
  }

  # --remove-firewall and --configure-firewall (#801): the elevated helper modes behind the tray menu and the
  # first-run prompt. --allow-lan above already created the rule, so it is removed first and the helper has to put
  # it back. The CI runner is elevated and disposable, so this proves the rule against the real installed package
  # and the real Windows Firewall, independently of Weir's own code, with the NetSecurity PowerShell module. The
  # uninstall below proves the before-uninstall hook removes it too, since this runner is elevated.
  Write-Host "Running the installed Weir.exe --remove-firewall..."
  $removeProc = Start-Process -FilePath $installedTrayExe -ArgumentList @("--remove-firewall") -PassThru -Wait -WindowStyle Hidden
  if ($removeProc.ExitCode -ne 0) {
    throw "Weir.exe --remove-firewall exited with code $($removeProc.ExitCode)."
  }
  if (Get-NetFirewallRule -DisplayName "Weir" -ErrorAction SilentlyContinue) {
    throw "--remove-firewall left the firewall rule named 'Weir' in place."
  }
  Write-Host "Running the installed Weir.exe --configure-firewall..."
  $firewallProc = Start-Process -FilePath $installedTrayExe -ArgumentList @("--configure-firewall") -PassThru -Wait -WindowStyle Hidden
  if ($firewallProc.ExitCode -ne 0) {
    throw "Weir.exe --configure-firewall exited with code $($firewallProc.ExitCode)."
  }
  Assert-WeirFirewallRule -ServerExe $installedServerExe -Because "--configure-firewall"

  # Setup --silent over a Weir that is running from the install (#942): Setup stops it and starts nothing itself, so
  # the tray has to bring Weir back, as an update driven by Deluno needs. The uninstall below stops it in an orderly way.
  Write-Host "Starting the installed Weir.exe, then running Setup.exe --silent over it..."
  $runningProc = Start-Process -FilePath $installedTrayExe -ArgumentList @("--port", [string]$Port, "--no-browser", "--silent") -PassThru -WindowStyle Hidden
  Wait-ForInstalledReady -ReadyPort $Port -TimeoutSeconds $installedHealthTimeoutSeconds -What "The installed Weir before the second Setup"
  # Setup runs with its output piped, as Deluno runs it, and is waited for on its own: Start-Process -Wait in Windows
  # PowerShell also waits for everything Setup's hook starts, which here includes the Weir that comes back.
  $overInstallPsi = New-Object System.Diagnostics.ProcessStartInfo
  $overInstallPsi.FileName = $setupExePath
  $overInstallPsi.Arguments = "--silent"
  $overInstallPsi.UseShellExecute = $false
  $overInstallPsi.RedirectStandardOutput = $true
  $overInstallPsi.RedirectStandardError = $true
  $overInstallPsi.CreateNoWindow = $true
  $overInstallProc = [System.Diagnostics.Process]::Start($overInstallPsi)
  $overInstallStdoutTask = $overInstallProc.StandardOutput.ReadToEndAsync()
  $overInstallStderrTask = $overInstallProc.StandardError.ReadToEndAsync()
  if (-not $overInstallProc.WaitForExit($setupTimeoutSeconds * 1000)) {
    Stop-Process -Id $overInstallProc.Id -Force -ErrorAction SilentlyContinue
    throw "Weir-win-Setup.exe --silent over a running Weir did not exit within $setupTimeoutSeconds s."
  }
  if ($overInstallProc.ExitCode -ne 0) {
    throw "Weir-win-Setup.exe --silent over a running Weir exited with code $($overInstallProc.ExitCode)."
  }
  if (-not $runningProc.HasExited) {
    throw "Setup.exe --silent did not stop the running Weir.exe (pid $($runningProc.Id))."
  }
  Wait-ForInstalledReady -ReadyPort $Port -TimeoutSeconds $installedHealthTimeoutSeconds -What "Weir after a silent Setup over it"
  $restarted = @(Get-InstalledWeirProcesses -InstallDirectory $installedDirectory | Where-Object { $_.Name -eq "Weir" })
  if ($restarted.Count -ne 1 -or $restarted[0].Id -eq $runningProc.Id) {
    throw "Expected one new Weir.exe from the install after the silent Setup; found: $($restarted.Id -join ', ')."
  }
  Write-Host "Setup.exe --silent over a running Weir: it came back by itself (pid $($restarted[0].Id)) and answers /ready."

  # Weir is running again, so a pipe that reaches end-of-file now is not held open by the helper that restarted it or
  # by Weir itself: a caller capturing Setup's output, as Deluno does, is not kept waiting for as long as Weir runs.
  Wait-ForPipeEndOfFile -Tasks @($overInstallStdoutTask, $overInstallStderrTask) -TimeoutSeconds 15 -What "Setup.exe's piped output while the restarted Weir runs"
  Write-Host "Setup.exe's piped stdout and stderr reached end-of-file while the restarted Weir keeps running."

  Write-Host "Uninstalling with the Velopack uninstaller..."
  if (-not (Test-Path -LiteralPath $updateExe)) {
    throw "Velopack updater not found at $updateExe; cannot uninstall."
  }
  $uninstallProc = Start-Process -FilePath $updateExe -ArgumentList @("uninstall", "--silent") -PassThru -Wait
  if ($uninstallProc.ExitCode -ne 0) {
    throw "Update.exe uninstall --silent exited with code $($uninstallProc.ExitCode)."
  }
  Write-Host "Uninstalled cleanly (exit code 0)."
  $leftAfterUninstall = Get-InstalledWeirProcesses -InstallDirectory $installedDirectory
  if ($leftAfterUninstall) {
    throw "Uninstalling left Weir running (pid(s): $($leftAfterUninstall.Id -join ', '))."
  }

  # Uninstall's before-uninstall hook removes the firewall rule itself when already elevated (this runner is);
  # see Weir.Tray/Firewall/FirewallInstallHooks.cs RemoveRuleIfElevated. A rule surviving uninstall would point at a program
  # that no longer exists.
  $weirFirewallRuleAfterUninstall = Get-NetFirewallRule -DisplayName "Weir" -ErrorAction SilentlyContinue
  if ($weirFirewallRuleAfterUninstall) {
    throw "The Weir firewall rule still exists after uninstall."
  }
  Write-Host "Confirmed: uninstall removed the Weir firewall rule."
} catch {
  Write-Host "Real installer smoke failed."
  $installedLogPath = Join-Path $defaultRuntimeHome "tray-host.log"
  if (Test-Path -LiteralPath $installedLogPath) {
    Write-Host "--- tray-host.log ---"
    Get-Content -LiteralPath $installedLogPath -Tail 100
  }
  throw
} finally {
  # A failed run must not leave the installed Weir running on a machine that is reused.
  foreach ($left in Get-InstalledWeirProcesses -InstallDirectory $installedDirectory) {
    & taskkill.exe /PID $left.Id /T /F | Out-Null
  }
  # Velopack's uninstall does not remove runtime data (docs-site/docs/deployment/windows.md,
  # "Uninstalling leaves the runtime data ... in place"); remove it so the runner is left clean.
  if (Test-Path -LiteralPath $defaultRuntimeHome) {
    Remove-Item -LiteralPath $defaultRuntimeHome -Recurse -Force -ErrorAction SilentlyContinue
  }
}
