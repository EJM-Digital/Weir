<#
    What a Fresh scenario starts from, made by the scenario itself so that it runs alone: the build under test installed and
    running, and an account the run holds. Nothing here relies on an earlier scenario having run; each step looks at the machine,
    and only does what is missing. Dot-sourced by Run-WeirScenarios.ps1 after Weir.Scenarios.Install.ps1.

    The Fresh phase is the clean machine, so a Weir that already has an account the run does not hold is set back to having none
    (its data folder is moved aside, never deleted). A rehearsal is pointed at a Weir somebody else started and never does that.
#>

# The Weir processes (tray and server) running from the install folder.
function Get-InstalledWeirProcesses {
    $root = [string]$script:Ctx.InstallRoot
    @(Get-Process -Name 'Weir', 'WeirServer' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object { if ($_.ProcessName -eq 'Weir') { 0 } else { 1 } })
}

# Stops the installed tray, then the server it started, and waits until neither is running. Nothing else is touched.
function Stop-InstalledWeir {
    foreach ($process in @(Get-InstalledWeirProcesses)) {
        Add-Evidence "stopping $($process.ProcessName) pid $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Wait-Until -What 'the installed Weir to stop' -TimeoutSeconds 60 -Probe { @(Get-InstalledWeirProcesses).Count -eq 0 } | Out-Null
}

# The version the Weir on the run's port reports, or $null when nothing answers.
function Get-AnsweringVersion {
    try { Get-ServerVersion } catch { $null }
}

# Installs the build under test if it is not what is there, and starts it if it is not running, so that it answers on the run's
# port. Looks first, so a Weir that is already the build under test is left exactly as it is.
function Install-BuildUnderTest {
    $paths = Get-WeirPaths
    $expected = @{ Expected = $script:Ctx.Version; ShortSha = $script:Ctx.ShortSha }
    $answering = Get-AnsweringVersion
    if ($answering -and (Test-VersionUnderTest -Reported $answering @expected)) { return $answering }

    if ($answering) {
        # Another version is running: the build under test goes over it, the way an update does.
        Add-Evidence "Weir $answering is running, not the build under test: installing the build under test over it"
        $update = Invoke-WeirSetup -Path $script:Ctx.SetupPath
        Assert-That ($update.ExitCode -eq 0) "Setup --silent of the build under test over the running Weir exited 0 (it exited $($update.ExitCode))"
        Wait-Until -What 'Weir to be running the build under test again' -TimeoutSeconds 240 -IntervalMilliseconds 1000 -Probe {
            Test-VersionUnderTest -Reported (Get-ServerVersion) @expected
        } | Out-Null
    }
    else {
        $installed = $false
        if (Test-Path -LiteralPath $paths.ServerExe) {
            $installedVersion = try { (Invoke-Tool -Program $paths.ServerExe -Arguments @('--version')).Trim() } catch { '' }
            $installed = $installedVersion -and (Test-VersionUnderTest -Reported $installedVersion @expected)
        }
        if (-not $installed) {
            Stop-InstalledWeir
            $setup = Invoke-WeirSetup -Path $script:Ctx.SetupPath
            Assert-That ($setup.ExitCode -eq 0) "Setup --silent of the build under test exited 0 (it exited $($setup.ExitCode))"
        }
        if (@(Get-InstalledWeirProcesses | Where-Object { $_.ProcessName -eq 'Weir' }).Count -eq 0) { Start-WeirTray | Out-Null }
    }
    Wait-WeirReady -TimeoutSeconds 120
    $reported = Get-ServerVersion
    Assert-That (Test-VersionUnderTest -Reported $reported @expected) "the running server reports $reported, the build under test ($($script:Ctx.Version) $($script:Ctx.ShortSha))"
    $reported
}

# What to do about the account, from what Weir says and what the run holds: 'keep' (nothing to do), 'forget' (the run held an
# account that is gone) or 'reset' (Weir has an account the run does not hold, or the scenario wants none).
function Resolve-AccountAction {
    param([Parameter(Mandatory)] [bool] $AccountExists, [Parameter(Mandatory)] [bool] $Held, [Parameter(Mandatory)] [bool] $NoAccount)
    if (-not $AccountExists) { return $(if ($Held) { 'forget' } else { 'keep' }) }
    if ($Held -and -not $NoAccount) { return 'keep' }
    'reset'
}

# Sets the installed Weir back to having no account: stops it, moves its data folder aside (never deletes it) and starts it again.
function Reset-WeirAccount {
    if ($script:Ctx.Rehearsal) { throw 'A rehearsal is pointed at a Weir somebody else started, so it cannot set that Weir back to having no account. Start a Weir with an empty data folder and rehearse against that.' }
    $data = Join-Path $script:Ctx.RuntimeHome 'data'
    Stop-InstalledWeir
    if (Test-Path -LiteralPath $data) {
        $aside = "$data.before-$(Get-RunId)-$([guid]::NewGuid().ToString('N').Substring(0, 4))"
        Move-Item -LiteralPath $data -Destination $aside
        Add-Evidence "moved Weir's data folder aside to $aside so that it offers to create an account"
    }
    Remove-ScenarioAccount
    Start-WeirTray | Out-Null
    Wait-WeirReady -TimeoutSeconds 120
}

# Forgets the account, and the session signed in to it, that the run held.
function Remove-ScenarioAccount {
    foreach ($key in 'Username', 'Password', 'Session') { $script:Ctx.Remove($key) }
    $script:Ctx.Session = $null
}

# Makes the Weir a Fresh scenario needs: the build under test installed and running, and an account state the run knows. With
# -NoAccount Weir must offer to create an account (for the scenario that creates it); otherwise the run holds one, or Weir offers
# to create one (Initialize-WeirSession does).
function Ensure-FreshWeir {
    param([switch] $NoAccount)
    if ($script:Ctx.Rehearsal) { Wait-WeirReady -TimeoutSeconds 60 }
    else { Install-BuildUnderTest | Out-Null }

    $anonymous = New-WeirSession -BaseUrl $script:Ctx.Base
    $offered = (Get-WeirJson $anonymous '/api/v1/auth/bootstrap/status').bootstrap_allowed -eq $true
    $held = $script:Ctx.ContainsKey('Username') -and [bool]$script:Ctx.Username
    switch (Resolve-AccountAction -AccountExists (-not $offered) -Held $held -NoAccount ([bool]$NoAccount)) {
        'forget' { Remove-ScenarioAccount; Add-Evidence 'the account the run held is gone; the run forgot it' }
        'reset' { Reset-WeirAccount }
    }
    if ($NoAccount) {
        $after = (Get-WeirJson (New-WeirSession -BaseUrl $script:Ctx.Base) '/api/v1/auth/bootstrap/status').bootstrap_allowed
        Assert-That ($after -eq $true) 'Weir offers to create an account'
    }
}

# What every Fresh scenario but the one that creates the account begins with: Weir installed and running, and a signed-in session.
function Initialize-FreshScenario {
    Ensure-FreshWeir
    if (-not (Initialize-WeirSession)) { throw "There is no way to sign in to this Weir: $($script:Ctx.NoSessionReason)" }
}
