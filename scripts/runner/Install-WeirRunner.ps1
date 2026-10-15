<#
.SYNOPSIS
    Turns a spare Windows PC into a GitHub Actions runner for Weir's Windows test jobs.

.DESCRIPTION
    Run once, in an elevated Windows PowerShell 5.1 or PowerShell 7 window, on the PC that will run the jobs.
    It installs Git and PowerShell 7 if they are missing, downloads the pinned Actions runner (checked against its
    SHA-256), registers it with jampat000/Weir under the labels self-hosted, Windows and weir-heavy, and installs it
    as a Windows service that runs as NETWORK SERVICE, so a job cannot read anyone's own files.

    The one secret is the registration token from the repository's Settings > Actions > Runners > New self-hosted
    runner page. The script asks for it without echoing it, uses it once, and keeps it nowhere; it is valid for an
    hour. Everything else the jobs need (the .NET SDK, Node, Playwright's browsers, the pinned FFmpeg and
    MKVToolNix) each job installs for itself, as it does on GitHub's own runners.

    docs/self-hosted-runner.md says how the workflows choose this runner, and how to remove it.

.PARAMETER RunnerRoot
    Where the runner and its work folder live. Kept out of any user's profile.
#>
[CmdletBinding()]
param(
    [string] $RunnerRoot = 'C:\actions-runner-weir'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RunnerVersion = '2.338.0'
$RunnerSha256 = 'f48e0750a21812bca5f82de5f7f5aeae71abee647fab5a582f1742d07eba455f'
$RepositoryUrl = 'https://github.com/jampat000/Weir'
$Labels = 'weir-heavy'
$RunnerName = 'weir-windows-1'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell window (right-click, Run as administrator): it installs a Windows service.'
}

function Install-IfMissing([string] $Command, [string] $WingetId) {
    if (Get-Command $Command -ErrorAction SilentlyContinue) {
        Write-Host "$Command is already installed."
        return
    }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "$Command is missing and winget is not available to install it. Install $WingetId by hand, then run this again."
    }
    Write-Host "Installing $WingetId..."
    winget install --id $WingetId --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "winget could not install $WingetId (exit code $LASTEXITCODE)." }
}

Install-IfMissing -Command 'git' -WingetId 'Git.Git'
Install-IfMissing -Command 'pwsh' -WingetId 'Microsoft.PowerShell'

# Weir's tests build paths deeper than 260 characters.
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -Value 1 -Type DWord

if (Test-Path (Join-Path $RunnerRoot '.runner')) {
    throw "A runner is already registered in $RunnerRoot. Remove it first (docs/self-hosted-runner.md, 'Removing the runner')."
}
New-Item -ItemType Directory -Force -Path $RunnerRoot | Out-Null

$zip = Join-Path $RunnerRoot "actions-runner-win-x64-$RunnerVersion.zip"
Write-Host "Downloading Actions runner $RunnerVersion..."
Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/actions/runner/releases/download/v$RunnerVersion/actions-runner-win-x64-$RunnerVersion.zip" -OutFile $zip
$actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
if ($actual -ne $RunnerSha256) {
    Remove-Item -Force $zip
    throw "The runner download does not match its pinned SHA-256 (expected $RunnerSha256, got $actual). Nothing was installed."
}
Expand-Archive -Path $zip -DestinationPath $RunnerRoot -Force
Remove-Item -Force $zip

$secure = Read-Host -AsSecureString 'Paste the registration token from Settings > Actions > Runners > New self-hosted runner'
$token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
if ([string]::IsNullOrWhiteSpace($token)) { throw 'No token was pasted. Nothing was registered.' }

Push-Location $RunnerRoot
try {
    & .\config.cmd --unattended --url $RepositoryUrl --token $token --name $RunnerName `
        --labels $Labels --work '_work' --runasservice --replace
    if ($LASTEXITCODE -ne 0) { throw "Registering the runner failed (exit code $LASTEXITCODE)." }
}
finally {
    $token = $null
    Pop-Location
}

$service = Get-Service -Name 'actions.runner.*' | Where-Object { $_.Name -like "*$RunnerName*" } | Select-Object -First 1
if (-not $service -or $service.Status -ne 'Running') {
    throw 'The runner registered but its service is not running. Look in Services for actions.runner.* and start it.'
}
Write-Host "Done. The runner '$($service.DisplayName)' is running as a service and waits for Weir's Windows jobs."
Write-Host 'Ask for the repository variable WEIR_SELF_HOSTED_WINDOWS to be set to true to send those jobs here.'
