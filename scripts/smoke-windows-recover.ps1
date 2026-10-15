param(
  [string]$PackageDir = "",
  # Not Weir's default port: the package smoke that runs before this one has stopped its server, but a Weir
  # you have installed may be using 9347.
  [int]$Port = 9349,
  # Where the printed help and the command's output are kept, for the workflow to upload.
  [string]$Recording = ""
)

$ErrorActionPreference = "Stop"

# PLAT-20: the lock-out recovery through the packaged executable. A person who forgot the only password runs
# `WeirServer.exe recover` where Weir is installed (docs-site/docs/guides/security.md). This runs that command
# from the exact server folder the tray launches (dist\windows\pack\server) against a data folder holding an
# account that was made through the API, with Weir stopped, and then starts Weir again and proves:
#   - the command's help, account list and output say what the documentation tells a person to expect;
#   - the old password is refused and the new one signs in;
#   - the session that was signed in before the reset is ended;
#   - the other settings are as they were, and Activity records the reset;
#   - pointed at a folder Weir never used, the command says so in plain words and makes no database.
# What it prints is kept in the recording folder.

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $PackageDir) { $PackageDir = Join-Path $repoRoot "dist\windows\pack\server" }
if (-not $Recording) { $Recording = Join-Path $repoRoot "artifacts\windows-recover-smoke" }
$packagePath = (Resolve-Path -LiteralPath $PackageDir).Path
$serverExe = Join-Path $packagePath "WeirServer.exe"
$webDist = Join-Path $packagePath "web-dist"
if (-not (Test-Path -LiteralPath $serverExe)) { throw "Packaged server executable not found: $serverExe" }
New-Item -ItemType Directory -Force -Path $Recording | Out-Null

$baseUrl = "http://localhost:$Port"
$username = "recover-smoke-admin"
$forgottenPassword = "forgotten-pass-" + [guid]::NewGuid().ToString("N")
$newPassword = "recovered-pass-" + [guid]::NewGuid().ToString("N")
$retentionDays = 17

$runtimeHome = Join-Path ([System.IO.Path]::GetTempPath()) ("weir-recover-smoke-" + [guid]::NewGuid().ToString("N"))
$emptyHome = Join-Path ([System.IO.Path]::GetTempPath()) ("weir-recover-smoke-empty-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $runtimeHome, $emptyHome | Out-Null

$saved = @{}
foreach ($name in "WEIR_HOME", "WEIR_SESSION_SECRET", "WEIR_SESSION_COOKIE_SECURE", "WEIR_ENV", "WEIR_WEB_DIST") {
  $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
$server = $null

function Start-WeirServer {
  $script:serverLog = Join-Path $runtimeHome ("server-{0}.log" -f [guid]::NewGuid().ToString("N").Substring(0, 6))
  $script:server = Start-Process -FilePath $serverExe -ArgumentList @("--port", [string]$Port) -WorkingDirectory $packagePath `
    -RedirectStandardOutput $serverLog -RedirectStandardError "$serverLog.err" -WindowStyle Hidden -PassThru
  $deadline = (Get-Date).AddSeconds(60)
  do {
    if ($server.HasExited) { throw "Weir exited early with code $($server.ExitCode)." }
    try {
      if ((Invoke-RestMethod -Uri "$baseUrl/ready" -TimeoutSec 2).ready -eq $true) { return }
    } catch { Start-Sleep -Milliseconds 500 }
  } while ((Get-Date) -lt $deadline)
  throw "Weir did not become ready at $baseUrl/ready."
}

function Stop-WeirServer {
  if ($null -eq $script:server) { return }
  if (-not $script:server.HasExited) {
    Stop-Process -Id $script:server.Id -Force
    if (-not $script:server.WaitForExit(30000)) { throw "Weir did not stop." }
  }
  $script:server = $null
}

# One request that never throws on an HTTP error: { Status; Json }.
function Invoke-Api {
  param([string]$Method, [string]$Path, $Session, $Body)
  $headers = @{ Origin = $baseUrl; "X-Requested-With" = "XMLHttpRequest" }
  $parameters = @{ Uri = "$baseUrl/api/v1$Path"; Method = $Method; WebSession = $Session; Headers = $headers; UseBasicParsing = $true; TimeoutSec = 30 }
  if ($null -ne $Body) {
    $csrf = (Invoke-RestMethod -Uri "$baseUrl/api/v1/auth/csrf" -WebSession $Session -Headers $headers -TimeoutSec 15).csrf_token
    $parameters.Body = ($Body + @{ csrf_token = $csrf }) | ConvertTo-Json -Compress
    $parameters.ContentType = "application/json"
  }
  try {
    $response = Invoke-WebRequest @parameters
    $status = [int]$response.StatusCode
    $text = [string]$response.Content
  } catch {
    if (-not $_.Exception.Response) { throw }
    $status = [int]$_.Exception.Response.StatusCode
    $text = if ($_.ErrorDetails) { [string]$_.ErrorDetails.Message } else { "" }
  }
  $json = $null
  if ($text.TrimStart().StartsWith("{")) { $json = $text | ConvertFrom-Json }
  [pscustomobject]@{ Status = $status; Json = $json }
}

function Assert-Status {
  param($Response, [int]$Expected, [string]$What)
  if ($Response.Status -ne $Expected) { throw "$What : expected HTTP $Expected, got $($Response.Status)." }
}

# Runs WeirServer.exe with the arguments as a person's console would, output captured whole. Never prompts: stdin is closed.
function Invoke-Recover {
  param([string[]]$Arguments)
  # The command opens whatever database WEIR_HOME names, and with none this machine's own: it only ever runs on this script's temporary folders.
  $dataHome = [Environment]::GetEnvironmentVariable("WEIR_HOME")
  $temp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
  if (-not $dataHome -or -not [System.IO.Path]::GetFullPath($dataHome).StartsWith($temp, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to run recover: WEIR_HOME ('$dataHome') is not a temporary folder of this script."
  }
  $info = New-Object System.Diagnostics.ProcessStartInfo
  $info.FileName = $serverExe
  $info.WorkingDirectory = $packagePath
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardInput = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  # A person's console has only the data folder variable, if any; the settings the tray gives the server are not there.
  foreach ($name in "WEIR_SESSION_SECRET", "WEIR_SESSION_COOKIE_SECURE", "WEIR_ENV", "WEIR_WEB_DIST") { $null = $info.EnvironmentVariables.Remove($name) }
  $info.Arguments = (@("recover") + $Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join " "
  $process = [System.Diagnostics.Process]::Start($info)
  $process.StandardInput.Close()
  $stdout = $process.StandardOutput.ReadToEndAsync()
  $stderr = $process.StandardError.ReadToEndAsync()
  if (-not $process.WaitForExit(120000)) {
    Stop-Process -Id $process.Id -Force
    throw "WeirServer.exe recover $($Arguments -join ' ') did not finish within 120 s."
  }
  [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout.Result; Stderr = $stderr.Result }
}

function Save-Recording {
  param([string]$Name, $Result)
  $text = "exit code: $($Result.ExitCode)`r`n--- stdout ---`r`n$($Result.Stdout)`r`n--- stderr ---`r`n$($Result.Stderr)"
  [IO.File]::WriteAllText((Join-Path $Recording $Name), $text, (New-Object System.Text.UTF8Encoding($false)))
}

try {
  [Environment]::SetEnvironmentVariable("WEIR_HOME", $runtimeHome)
  [Environment]::SetEnvironmentVariable("WEIR_SESSION_SECRET", "ci-weir-session-secret-32chars-min")
  [Environment]::SetEnvironmentVariable("WEIR_SESSION_COOKIE_SECURE", "false")
  # The environment the tray gives the server (apps/tray/Weir.Tray/Program.cs, PrepareEnvironment).
  [Environment]::SetEnvironmentVariable("WEIR_ENV", "production")
  [Environment]::SetEnvironmentVariable("WEIR_WEB_DIST", $webDist)

  # --- an account, a saved setting and a signed-in session, made through the API ---------------------------------
  Start-WeirServer
  $browser = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  Assert-Status (Invoke-Api POST "/auth/bootstrap" $browser @{ username = $username; password = $forgottenPassword }) 200 "creating the account"
  $before = Invoke-Api GET "/auth/me" $browser
  Assert-Status $before 200 "the signed-in session before the reset"
  if ($before.Json.user.username -ne $username) { throw "The session before the reset is signed in as '$($before.Json.user.username)'." }
  $settings = Invoke-Api GET "/suite/settings" $browser
  Assert-Status $settings 200 "reading the settings"
  Assert-Status (Invoke-Api PUT "/suite/settings" $browser @{ app_timezone = $settings.Json.app_timezone; log_retention_days = $retentionDays }) 200 "saving a setting"
  $sessions = Invoke-Api GET "/auth/sessions" $browser
  Assert-Status $sessions 200 "listing the signed-in sessions"
  $sessionsEnded = @($sessions.Json.items).Count
  if ($sessionsEnded -lt 1) { throw "No session is signed in, so there is nothing for the reset to end." }
  Stop-WeirServer
  Write-Host "An account, a saved setting and a signed-in session exist; Weir is stopped."

  # --- the documented command, against that data folder ----------------------------------------------------------
  $help = Invoke-Recover @("--help")
  Save-Recording "help.txt" $help
  if ($help.ExitCode -ne 0) { throw "recover --help exited $($help.ExitCode)." }
  foreach ($expected in "usage: Weir recover", "--username USERNAME", "--password PASSWORD", "--list") {
    if (-not $help.Stdout.Contains($expected)) { throw "recover --help does not mention '$expected': $($help.Stdout)" }
  }

  $list = Invoke-Recover @("--list")
  Save-Recording "list.txt" $list
  if ($list.ExitCode -ne 0) { throw "recover --list exited $($list.ExitCode): $($list.Stderr)" }
  if ($list.Stdout -notmatch "(?m)^\s+$([regex]::Escape($username))\s+role=admin\s+active\s*$") { throw "recover --list does not show the account as an active admin: $($list.Stdout)" }

  $reset = Invoke-Recover @("--username", $username, "--password", $newPassword)
  Save-Recording "recover.txt" $reset
  if ($reset.ExitCode -ne 0) { throw "recover exited $($reset.ExitCode): $($reset.Stderr)" }
  if (-not $reset.Stdout.Contains("Password reset for '$username'.")) { throw "recover does not say the password was reset: $($reset.Stdout)" }
  if (-not $reset.Stdout.Contains("The account is active and has the admin role.")) { throw "recover does not say the account is an active admin: $($reset.Stdout)" }
  $endedText = if ($sessionsEnded -eq 1) { "1 signed-in session was ended" } else { "$sessionsEnded signed-in sessions were ended" }
  if ($reset.Stdout -notmatch "(?m)^$([regex]::Escape($endedText))\W+sign in again with the new password\.\s*$") { throw "recover does not say $sessionsEnded signed-in session(s) were ended: $($reset.Stdout)" }
  if (-not $reset.Stderr.Contains("--password stays in your shell history")) { throw "recover does not warn that --password stays in the shell history: $($reset.Stderr)" }
  Write-Host "recover exited 0 and said: $($reset.Stdout.Trim())"

  # --- Weir again: the new password, and nothing else, is different -----------------------------------------------
  Start-WeirServer
  $status = Invoke-Api GET "/auth/bootstrap/status" (New-Object Microsoft.PowerShell.Commands.WebRequestSession)
  if ($status.Json.bootstrap_allowed -ne $false) { throw "Weir offers to create the first account again: the account is gone." }

  $afterReset = Invoke-Api GET "/auth/me" $browser
  if ($afterReset.Status -ne 401) { throw "The session from before the reset is still accepted (HTTP $($afterReset.Status))." }

  $withOld = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  $oldLogin = Invoke-Api POST "/auth/login" $withOld @{ username = $username; password = $forgottenPassword; trusted_device = $false }
  if ($oldLogin.Status -eq 200) { throw "The forgotten password still signs in." }
  if ($oldLogin.Status -ne 401) { throw "The forgotten password was answered HTTP $($oldLogin.Status), not 401." }

  $withNew = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  Assert-Status (Invoke-Api POST "/auth/login" $withNew @{ username = $username; password = $newPassword; trusted_device = $false }) 200 "signing in with the new password"
  $me = Invoke-Api GET "/auth/me" $withNew
  Assert-Status $me 200 "the session after the reset"
  if ($me.Json.user.username -ne $username) { throw "The new password signed in as '$($me.Json.user.username)'." }

  $kept = Invoke-Api GET "/suite/settings" $withNew
  Assert-Status $kept 200 "reading the settings after the reset"
  if ($kept.Json.log_retention_days -ne $retentionDays) { throw "The saved setting changed: log_retention_days is $($kept.Json.log_retention_days), not $retentionDays." }

  $activity = Invoke-Api GET "/activity/recent?event_type=auth.password_changed&limit=20" $withNew
  Assert-Status $activity 200 "reading Activity"
  $recorded = @($activity.Json.items | Where-Object { $_.title -eq "Password recovered from the server" })
  if ($recorded.Count -ne 1) { throw "Activity holds $($recorded.Count) entries titled 'Password recovered from the server', not 1." }
  Stop-WeirServer
  Write-Host "The forgotten password is refused, the new one signs in, the earlier session is ended, the saved setting is kept and Activity records the reset."

  # --- a folder Weir never used --------------------------------------------------------------------------------------
  [Environment]::SetEnvironmentVariable("WEIR_HOME", $emptyHome)
  $elsewhere = Invoke-Recover @("--password", $newPassword)
  Save-Recording "no-database.txt" $elsewhere
  if ($elsewhere.ExitCode -ne 2) { throw "recover in a folder Weir never used exited $($elsewhere.ExitCode), not 2." }
  if (-not $elsewhere.Stderr.Contains("There is no Weir database at")) { throw "recover in a folder Weir never used does not say there is no database: $($elsewhere.Stderr)" }
  if (-not $elsewhere.Stderr.Contains("set WEIR_HOME to that folder")) { throw "recover in a folder Weir never used does not say how to point it at Weir's data: $($elsewhere.Stderr)" }
  if ($elsewhere.Stderr -match "Unhandled exception|   at ") { throw "recover in a folder Weir never used printed a stack trace: $($elsewhere.Stderr)" }
  if (Get-ChildItem -LiteralPath $emptyHome -Recurse -Filter "*.sqlite3" -ErrorAction SilentlyContinue) { throw "recover in a folder Weir never used left a database behind." }
  Write-Host "Pointed at a folder Weir never used, recover said so plainly and left no database."
} catch {
  # The server's own log, for whoever reads the failure.
  foreach ($log in Get-ChildItem -LiteralPath $runtimeHome -Filter "server-*.log*" -ErrorAction SilentlyContinue) {
    Copy-Item -LiteralPath $log.FullName -Destination $Recording -Force
  }
  throw
} finally {
  try { Stop-WeirServer } catch { Write-Warning $_.Exception.Message }
  foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
  Remove-Item -LiteralPath $runtimeHome, $emptyHome -Recurse -Force -ErrorAction SilentlyContinue
}
