<#
    The update scenario: the build under test installed over the Weir that Deluno's picker installed, with that Weir running and
    holding what it was used for. Needs Weir.Scenarios.Install.ps1 (Invoke-WeirSetup) and Weir.Scenarios.WeirOnly.ps1.
#>

# Deluno's tray keeps the picker's install log in its program or data folder.
function Get-DelunoInstallHookLogs {
    @('Deluno', 'DelunoData') | ForEach-Object { Join-Path (Join-Path $env:LOCALAPPDATA $_) 'logs\install-hook.log' } | Where-Object { Test-Path -LiteralPath $_ }
}

# What Deluno's component picker is doing, from its install log. The picker runs only in Deluno's first run (a reinstall or update over
# an existing Deluno never asks), and the log shows each run it began: "first run: the tray icon is shown..." when first run starts,
# "start with Windows: ..." just before the picker opens, and, only once the picker has finished with every component, one
# "component '<kind>': <Outcome>: <message>" line per component (or "component picker failed"). Only the last run counts, so a log kept
# from an earlier install says nothing about this one. Returns State:
#   Done     the picker said it finished: Said is the line about Weir (or the picker's failure), WeirInstalled whether it says Weir is in;
#   Running  a first run began and has not said it finished;
#   NotRun   no first run in the log, or one that asked nothing (a reinstall over an existing Deluno): there is no picker to wait for.
function Get-PickerState {
    param([Parameter(Mandatory)] [string[]] $LogPaths)
    $lines = @($LogPaths | ForEach-Object { Get-FileTextOrNull -Path $_ } | Where-Object { $_ } | ForEach-Object { $_ -split "`r?`n" })
    $begins = '^\S+ (first run: the tray icon is shown while Deluno sets itself up|start with Windows: )'
    $lastRun = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match $begins) { $lastRun = $i } }
    $thisRun = @($lines | Select-Object -Skip ([Math]::Max($lastRun, 0)))
    $weir = $null
    foreach ($line in $thisRun) {
        if ($line -match "^\S+ component 'weir': (?<outcome>\w+): ") {
            $weir = [pscustomobject]@{ State = 'Done'; Said = $line.Trim(); WeirInstalled = ($Matches['outcome'] -in 'Installed', 'AlreadyInstalled', 'InstalledNeedsSetup') }
        }
    }
    if ($weir) { return $weir }
    $other = @($thisRun | Where-Object { $_ -match "^\S+ component '|^\S+ component picker failed" }) | Select-Object -Last 1
    if ($other) { return [pscustomobject]@{ State = 'Done'; Said = $other.Trim(); WeirInstalled = $false } }
    $askedNothing = @($thisRun | Where-Object { $_ -match '^\S+ first run: this is a reinstall over an existing Deluno' }).Count -gt 0
    if ($lastRun -ge 0 -and -not $askedNothing) { return [pscustomobject]@{ State = 'Running'; Said = $null; WeirInstalled = $false } }
    [pscustomobject]@{ State = 'NotRun'; Said = $null; WeirInstalled = $false }
}

# The Weir Deluno's picker installs comes last, after every download client, and the picker takes as long as those installs take. So when
# its log shows a picker at work, the scenario waits for the picker to say it is done (its ceiling is a limit on a wait for that event,
# not a guess at how long it takes), then for Weir to answer. A picker that finished before the scenario began has already said so in its
# log; a Deluno that never ran one (an update over an existing Deluno) or has no log has nothing to wait for: Weir must simply be running.
function Wait-PickerInstalledWeir {
    param([string[]] $LogPaths = @(Get-DelunoInstallHookLogs), [int] $CeilingSeconds = 1200, [int] $IntervalMilliseconds = 2000)
    if ($LogPaths.Count -eq 0) {
        Add-Evidence 'no Deluno picker log is on this machine, so there is no picker to wait for'
        Wait-WeirReady -TimeoutSeconds 60
        return
    }
    $state = Wait-Until -What "Deluno's picker to finish installing the components it was asked for" -TimeoutSeconds $CeilingSeconds -IntervalMilliseconds $IntervalMilliseconds -Probe {
        $now = Get-PickerState -LogPaths $LogPaths
        if ($now.State -ne 'Running') { $now }
    }
    if ($state.State -eq 'NotRun') {
        Add-Evidence 'Deluno ran no component picker for this install (its log shows none, as after an update over an existing Deluno), so there is no picker to wait for'
        Wait-WeirReady -TimeoutSeconds 60
        return
    }
    if (-not $state.WeirInstalled) { throw "Deluno's picker finished without installing Weir: $($state.Said)" }
    Add-Evidence "Deluno's picker finished: $($state.Said)"
    Wait-WeirReady -TimeoutSeconds 120
}

function Scenario_update_over_installed_weir {
    $paths = Get-WeirPaths
    Assert-That (Test-Path -LiteralPath $script:Ctx.SetupPath) "the build under test's installer is in the machine ($($script:Ctx.SetupPath))"

    # 1. The Weir Deluno's picker installed, as it was left.
    Wait-PickerInstalledWeir
    $oldVersion = Get-ServerVersion
    Assert-That (-not (Test-VersionUnderTest -Reported $oldVersion -Expected $script:Ctx.Version)) "the machine runs the Weir Deluno installed ($oldVersion), not the build under test"
    if (-not (Initialize-WeirSession)) { return New-NotApplicable $script:Ctx.NoSessionReason }
    $workflow = New-ScenarioWorkflow -Name 'Scenario survivors' -Root (Join-Path $script:Ctx.MediaRoot 'update')
    $before = Invoke-WeirOnlyFilm -Name 'Survivor.Film.2010' -Workflow $workflow
    Assert-CleanedFilm -OutputPath $before.Release.Output | Out-Null
    $workflowsBefore = @(Get-WeirJson $script:Ctx.Session '/api/v1/processing/libraries')
    $activityBefore = @(Get-AllActivityItems)
    Assert-That (@($activityBefore | Where-Object event_type -eq 'processing.file_remux_pass_completed').Count -ge 1) "Weir $oldVersion recorded the film it cleaned in Activity ($($activityBefore.Count) entries in all)"

    # 2. The build under test, installed over the running Weir.
    # The previous release goes on logging until the install stops it; the logs are judged from the moment the build under test began.
    Use-BuildUnderTestLogWindow
    $update = Invoke-WeirSetup -Path $script:Ctx.SetupPath
    Assert-That ($update.ExitCode -eq 0) "the build under test's Setup --silent over the running Weir exited 0 (it exited $($update.ExitCode))"
    Wait-Until -What 'Weir to be running the build under test again without being started by hand' -TimeoutSeconds 240 -IntervalMilliseconds 1000 -Probe {
        Test-VersionUnderTest -Reported (Get-ServerVersion) -Expected $script:Ctx.Version -ShortSha $script:Ctx.ShortSha
    } | Out-Null
    $newVersion = Get-ServerVersion
    Save-BuildUnderTestMarker -Reported $newVersion
    $tray = @(Get-Process -Name 'Weir' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $paths.TrayExe })
    Assert-That ($tray.Count -ge 1) 'a tray from the new install is running'
    Assert-That ((Get-TrayLogText) -match 'Weir was running before this install') 'the tray log says Weir was running before the install and started again'

    # 3. What survived.
    Restart-WeirSession
    $session = $script:Ctx.Session
    $workflowsAfter = @(Get-WeirJson $session '/api/v1/processing/libraries')
    foreach ($kept in $workflowsBefore | Where-Object { $_.watched_folder }) {
        $same = @($workflowsAfter | Where-Object { $_.id -eq $kept.id -and $_.watched_folder -eq $kept.watched_folder -and $_.output_folder -eq $kept.output_folder })
        Assert-That ($same.Count -eq 1) "the workflow '$($kept.name)' and its folders are as they were"
    }
    # Every entry, paged: the newest 100 of a list that the update (and this run) adds to cannot show whether one was lost.
    $activityAfter = @(Get-AllActivityItems)
    $lost = @(Find-ActivityLostOrChanged -Before $activityBefore -After $activityAfter)
    $lostText = (@($lost | Select-Object -First 5 | ForEach-Object { "#$($_.Id) '$($_.Title)' $($_.Problem)" }) -join '; ')
    Assert-That ($lost.Count -eq 0) "all $($activityBefore.Count) Activity entries from before the update are still there, unchanged (found $($lost.Count) gone or changed: $lostText)"
    Assert-That ($activityAfter.Count -ge $activityBefore.Count) "Activity holds at least as many entries as before ($($activityAfter.Count) after, $($activityBefore.Count) before)"

    # 4. The copy saved before the update, when the update changed the database; and the tray's own request for one.
    $log = Get-FileTextOrNull -Path $paths.ServerLog
    $opened = @([regex]::Matches([string]$log, '(Upgraded database to schema revision|Opened database at schema revision)=\S+') | Select-Object -Last 1)
    $backup = Get-Prop (Get-WeirJson $session '/api/v1/system/overview') 'last_update_backup'
    $backupText = 'the database was not changed by the update, so no copy was due'
    if ($opened.Count -eq 1 -and $opened[0].Value -like 'Upgraded*') {
        Assert-That ($null -ne $backup -and (Test-Path -LiteralPath $backup.path) -and (Get-Item -LiteralPath $backup.path).Length -gt 0) 'the update changed the database, so a copy of the data was saved first and System overview names it'
        $backupText = "the update upgraded the database, and the copy $([IO.Path]::GetFileName($backup.path)) was saved first"
    }
    $requested = Test-PreUpdateCopyRequest

    # 5. Working on the new version.
    $after = Invoke-WeirOnlyFilm -Name 'Survivor.Film.2011' -Workflow $workflow
    Assert-CleanedFilm -OutputPath $after.Release.Output | Out-Null
    "The Weir Deluno installed ($oldVersion) was used (account, workflow, one film cleaned), then Setup --silent over it took $($update.Seconds) s and Weir came back as $newVersion by itself; account, workflow and $($activityBefore.Count) Activity entries survived; $backupText; $requested; a film dropped in afterwards was cleaned in $($after.Seconds) s."
}
