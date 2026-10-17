<#
.SYNOPSIS
    Turns a spare Windows PC into GitHub Actions runners for Weir's Windows test jobs.

.DESCRIPTION
    Run once, in an elevated Windows PowerShell 5.1 or PowerShell 7 window, on the PC that will run the jobs.
    It installs Git and PowerShell 7 if they are missing, downloads the pinned Actions runner (checked against its
    SHA-256), registers runners with the Weir repository under the labels self-hosted, Windows and weir-heavy, and installs
    each as a Windows service that runs as NETWORK SERVICE, so a job cannot read anyone's own files.

    Each runner takes one job at a time, so several runners let one PC run several jobs at once. Runner 1 lives in
    C:\actions-runner-weir and is named weir-windows-1; runner N lives in C:\actions-runner-weir-N and is named
    weir-windows-N. Running the script again adds the runners that are missing and leaves the others alone.

    The one secret is the registration token from the repository's Settings > Actions > Runners > New self-hosted
    runner page. The script asks for it without echoing it, uses it for every runner it adds, and keeps it nowhere;
    it is valid for an hour. Everything else the jobs need (the .NET SDK, Node, Playwright's browsers, the pinned FFmpeg and
    MKVToolNix) each job installs for itself, as it does on GitHub's own runners.

    docs/self-hosted-runner.md says how the workflows choose this runner, and how to remove it.

.PARAMETER Count
    How many runners the PC should have in total. Left out, it is one per four logical processors and per 6 GB of
    memory, whichever allows fewer, from 1 to 8: a server or tray test job keeps about four processors busy and
    needs up to about 6 GB.

.PARAMETER RunnerRoot
    Where runner 1 and its work folder live; runner N adds -N to it. Kept out of any user's profile.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 8)]
    [int] $Count,
    [string] $RunnerRoot = 'C:\actions-runner-weir'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RunnerVersion = '2.338.0'
$RunnerSha256 = 'f48e0750a21812bca5f82de5f7f5aeae71abee647fab5a582f1742d07eba455f'
# Copied alone to the PC, so it cannot read scripts/repository.ps1; scripts/check-repository-owner.mjs keeps it equal.
$RepositoryUrl = 'https://github.com/jampat000/Weir'
$Labels = 'weir-heavy'
$RunnerNamePrefix = 'weir-windows-'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell window (right-click, Run as administrator): it installs a Windows service.'
}

# Installed for the whole PC, not one user: the runner service runs as NETWORK SERVICE and only sees machine-wide
# programs. A copy installed for one person (or from the Microsoft Store) does not count.
function Install-ForMachine([string] $ExePath, [string] $WingetId) {
    if (Test-Path -LiteralPath $ExePath) {
        Write-Host "$WingetId is already installed for the whole PC."
        return
    }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "$WingetId is not installed for the whole PC and winget is not available to install it. Install it by hand for all users, then run this again."
    }
    Write-Host "Installing $WingetId for the whole PC..."
    winget install --id $WingetId --exact --scope machine --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "winget could not install $WingetId (exit code $LASTEXITCODE)." }
    if (-not (Test-Path -LiteralPath $ExePath)) { throw "$WingetId installed, but $ExePath is not there." }
}

Install-ForMachine -ExePath "$env:ProgramFiles\Git\cmd\git.exe" -WingetId 'Git.Git'
Install-ForMachine -ExePath "$env:ProgramFiles\PowerShell\7\pwsh.exe" -WingetId 'Microsoft.PowerShell'

# Weir's tests build paths deeper than 260 characters.
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -Value 1 -Type DWord

if (-not $PSBoundParameters.ContainsKey('Count')) {
    $processors = [Environment]::ProcessorCount
    $memoryGb = [math]::Floor((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    $Count = [math]::Max(1, [math]::Min(8, [math]::Min([math]::Floor($processors / 4), [math]::Floor($memoryGb / 6))))
    Write-Host "This PC has $processors logical processors and $memoryGb GB of memory: it gets $Count runners."
}

function Get-RunnerFolder([int] $Number) {
    if ($Number -eq 1) { return $RunnerRoot }
    return "$RunnerRoot-$Number"
}

$missing = @(1..$Count | Where-Object { -not (Test-Path (Join-Path (Get-RunnerFolder $_) '.runner')) })
if ($missing.Count -eq 0) {
    Write-Host "All $Count runners are already registered. Nothing to do."
    return
}
Write-Host "Adding runner(s) $($missing -join ', ')."

$zip = Join-Path ([IO.Path]::GetTempPath()) "actions-runner-win-x64-$RunnerVersion.zip"
Write-Host "Downloading Actions runner $RunnerVersion..."
Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/actions/runner/releases/download/v$RunnerVersion/actions-runner-win-x64-$RunnerVersion.zip" -OutFile $zip
$actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
if ($actual -ne $RunnerSha256) {
    Remove-Item -Force $zip
    throw "The runner download does not match its pinned SHA-256 (expected $RunnerSha256, got $actual). Nothing was installed."
}

$secure = Read-Host -AsSecureString 'Paste the registration token from Settings > Actions > Runners > New self-hosted runner'
$token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
if ([string]::IsNullOrWhiteSpace($token)) {
    Remove-Item -Force $zip
    throw 'No token was pasted. Nothing was registered.'
}

try {
    foreach ($number in $missing) {
        $folder = Get-RunnerFolder $number
        $name = "$RunnerNamePrefix$number"
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        Expand-Archive -Path $zip -DestinationPath $folder -Force

        Push-Location $folder
        try {
            & .\config.cmd --unattended --url $RepositoryUrl --token $token --name $name `
                --labels $Labels --work '_work' --runasservice --replace
            if ($LASTEXITCODE -ne 0) { throw "Registering $name failed (exit code $LASTEXITCODE)." }
        }
        finally {
            Pop-Location
        }

        $service = Get-Service -Name 'actions.runner.*' | Where-Object { $_.Name -like "*.$name" } | Select-Object -First 1
        if (-not $service -or $service.Status -ne 'Running') {
            throw "$name registered but its service is not running. Look in Services for actions.runner.* and start it."
        }
        Write-Host "$name is running as a service."
    }
}
finally {
    $token = $null
    Remove-Item -Force $zip -ErrorAction SilentlyContinue
}

Write-Host "Done. This PC has $Count runners waiting for Weir's Windows jobs."
Write-Host 'If this is the first install, ask for the repository variable WEIR_SELF_HOSTED_WINDOWS to be set to true to send those jobs here.'