<#
    The lock-out recovery on the installed Weir: a person who forgot the only password runs `WeirServer.exe recover` from the install
    folder, as docs-site/docs/guides/security.md tells them to. The package smoke
    (scripts/smoke-windows-recover.ps1) runs the same command from the packaged folder against a data folder it chose; this runs it
    against the data folder the installed tray's server really uses.
#>

# Runs the installed WeirServer.exe with the arguments as a person's console would: stdin closed (nothing can be prompted), and none of
# the variables the tray gives the server. The data folder is always named: the command opens whatever database WEIR_HOME points at, and
# with no WEIR_HOME it would open this machine's own default, so a call without a folder is refused before anything starts. Never throws on
# the exit code.
function Invoke-InstalledRecover {
    param([Parameter(Mandatory)] [string[]] $Arguments, [string] $DataFolder)
    if (-not $DataFolder -or -not $DataFolder.Trim()) { throw 'Invoke-InstalledRecover refuses to run without an explicit data folder: with no WEIR_HOME the command would open this machine''s own Weir database.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = (Get-WeirPaths).ServerExe
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['WEIR_HOME'] = $DataFolder
    foreach ($name in 'WEIR_SESSION_SECRET', 'WEIR_SESSION_COOKIE_SECURE', 'WEIR_ENV', 'WEIR_WEB_DIST') { $null = $info.EnvironmentVariables.Remove($name) }
    # Windows PowerShell 5.1 has no ArgumentList: quote each argument by hand.
    $info.Arguments = (@('recover') + $Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $process = [Diagnostics.Process]::Start($info)
    $process.StandardInput.Close()
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id -Force
        throw "WeirServer.exe recover did not finish within 120 s."
    }
    $result = [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $output.Result; Stderr = $errors.Result }
    Add-Evidence "WeirServer.exe recover $($Arguments -join ' ') -> exit $($result.ExitCode)`n--- stdout ---`n$($result.Stdout)`n--- stderr ---`n$($result.Stderr)"
    $result
}

# Keeps what the command printed with the record.
function Save-RecoverOutput {
    param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] $Result)
    $text = "exit code: $($Result.ExitCode)`r`n--- stdout ---`r`n$($Result.Stdout)`r`n--- stderr ---`r`n$($Result.Stderr)"
    Set-Content -LiteralPath (Join-Path $script:Ctx.RunFolder "evidence\recover-$Name.txt") -Value (Protect-Secrets $text) -Encoding UTF8
}

# After a recovery the account has a new password: the run holds that one from now on, signed in with it.
function Adopt-RecoveredPassword {
    param([Parameter(Mandatory)] [string] $Password)
    Register-Secret $Password
    $script:Ctx.Password = $Password
    Restart-WeirSession
}

# --- recover-locked-out-account -----------------------------------------------------------------------------------------------

function Scenario_recover_locked_out_account {
    Initialize-FreshScenario
    $session = $script:Ctx.Session
    $username = [string]$script:Ctx.Username
    $forgotten = [string]$script:Ctx.Password
    $recovered = 'Rc-' + [guid]::NewGuid().ToString('N') + '-Aa1'
    Register-Secret $recovered

    # A setting the reset must leave alone, and a second browser signed in, besides the run's own session.
    $settings = Get-WeirJson $session '/api/v1/suite/settings'
    $original = [int]$settings.log_retention_days
    $retention = if ($original -eq 17) { 18 } else { 17 }
    Get-WeirJson $session '/api/v1/suite/settings' -Method PUT -Body @{ app_timezone = $settings.app_timezone; log_retention_days = $retention } | Out-Null
    $other = Sign-InScenarioAccount
    $ended = @((Get-WeirJson $session '/api/v1/auth/sessions').items).Count
    Assert-That ($ended -ge 2) "two browsers are signed in before the reset ($ended sessions)"

    try {
        Stop-InstalledWeir

        $help = Invoke-InstalledRecover -DataFolder $script:Ctx.RuntimeHome -Arguments @('--help')
        Save-RecoverOutput -Name 'help' -Result $help
        Assert-That ($help.ExitCode -eq 0) "recover --help exited 0 (it exited $($help.ExitCode))"
        foreach ($wanted in 'usage: Weir recover', '--username USERNAME', '--password PASSWORD', '--list') {
            Assert-That $help.Stdout.Contains($wanted) "recover --help mentions $wanted"
        }

        $list = Invoke-InstalledRecover -DataFolder $script:Ctx.RuntimeHome -Arguments @('--list')
        Save-RecoverOutput -Name 'list' -Result $list
        Assert-That ($list.ExitCode -eq 0) "recover --list exited 0 (it exited $($list.ExitCode): $($list.Stderr))"
        $database = Join-Path $script:Ctx.RuntimeHome 'data\weir.sqlite3'
        Assert-That $list.Stdout.Contains("Accounts in ${database}:") "the command lists the account in the database the tray's server uses ($database)"
        Assert-That ($list.Stdout -match "(?m)^\s+$([regex]::Escape($username))\s+role=admin\s+active\s*$") "recover --list shows $username as an active admin"

        $reset = Invoke-InstalledRecover -DataFolder $script:Ctx.RuntimeHome -Arguments @('--username', $username, '--password', $recovered)
        Save-RecoverOutput -Name 'recover' -Result $reset
        Assert-That ($reset.ExitCode -eq 0) "recover exited 0 (it exited $($reset.ExitCode): $($reset.Stderr))"
        Assert-That $reset.Stdout.Contains("Password reset for '$username'.") 'recover says the password was reset'
        Assert-That $reset.Stdout.Contains('The account is active and has the admin role.') 'recover says the account is an active admin'
        $endedWords = if ($ended -eq 1) { '1 signed-in session was ended' } else { "$ended signed-in sessions were ended" }
        Assert-That ($reset.Stdout -match "(?m)^$([regex]::Escape($endedWords))\W+sign in again with the new password\.\s*$") "recover says $endedWords"
        Assert-That $reset.Stderr.Contains('--password stays in your shell history') 'recover warns that --password stays in the shell history'

        Start-WeirTray | Out-Null
        Wait-WeirReady -TimeoutSeconds 120
    }
    catch {
        # Whatever happened, the next scenario finds Weir running.
        try { if (@(Get-InstalledWeirProcesses).Count -eq 0) { Start-WeirTray | Out-Null; Wait-WeirReady -TimeoutSeconds 120 } } catch { Add-Evidence "could not start Weir again: $($_.Exception.Message)" }
        throw
    }

    foreach ($earlier in @($session, $other)) {
        $answer = Invoke-Weir -Session $earlier -Method GET -Path '/api/v1/auth/me'
        Assert-That ($answer.Status -eq 401) "a session from before the reset is ended (HTTP $($answer.Status))"
    }
    $withForgotten = Invoke-Weir -Session (New-WeirSession -BaseUrl $script:Ctx.Base) -Method POST -Path '/api/v1/auth/login' -Body @{ username = $username; password = $forgotten; trusted_device = $false }
    Assert-That ($withForgotten.Status -eq 401) "the forgotten password is refused (HTTP $($withForgotten.Status))"

    Adopt-RecoveredPassword -Password $recovered
    $me = Get-WeirJson $script:Ctx.Session '/api/v1/auth/me'
    Assert-That ($me.user.username -eq $username) "the new password signs in as $username"
    $kept = Get-WeirJson $script:Ctx.Session '/api/v1/suite/settings'
    Assert-That ([int]$kept.log_retention_days -eq $retention) "the saved setting is as it was (log_retention_days $($kept.log_retention_days))"
    $said = @(Get-ActivityItems -EventType 'auth.password_changed' | Where-Object { $_.title -eq 'Password recovered from the server' })
    Assert-That ($said.Count -eq 1) 'Activity records that the password was recovered from the server'

    Get-WeirJson $script:Ctx.Session '/api/v1/suite/settings' -Method PUT -Body @{ app_timezone = $settings.app_timezone; log_retention_days = $original } | Out-Null
    "WeirServer.exe recover, run from the install on the run's own data folder, listed $username, reset the password and said $endedWords; the forgotten password is refused (401), the new one signs in, $ended earlier sessions are ended, the saved setting is kept and Activity says the password was recovered from the server."
}
