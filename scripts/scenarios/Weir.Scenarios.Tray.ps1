<#
    The scenarios about what the tray and the server leave each other, and the logs: the update buttons, the copy saved before an
    update (a helper the update scenario uses), and the log watch every scenario ends with.
#>

# Standing in for the tray, for a rehearsal on a machine with no tray: it keeps the heartbeat fresh and takes the update
# flags the way the real one does. A real run (the golden VM) never calls this; the real tray is there.
function Invoke-RehearsalTray {
    $dataFolder = $script:Ctx.RuntimeHome
    Write-FileAtomically -Folder $dataFolder -Name 'tray-heartbeat.json' -Text (@{ at = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress)
    foreach ($flag in 'update-check-now', 'update-download-now') {
        $path = Join-Path $dataFolder $flag
        if (-not (Test-Path -LiteralPath $path)) { continue }
        Remove-Item -LiteralPath $path -Force
        $line = if ($flag -eq 'update-check-now') { 'Check-now flag detected - checking for an update.' } else { 'Download-now flag detected - downloading the update.' }
        Add-Content -LiteralPath (Get-WeirPaths).TrayLog -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $line) -Encoding UTF8
        $state = if ($flag -eq 'update-check-now') { @{ state = 'idle'; downloaded = $false; version = $null; failure = $null } } else { @{ state = 'failed'; downloaded = $false; version = $null; failure = 'There is no newer version of Weir to download.' } }
        Write-FileAtomically -Folder $dataFolder -Name 'update-state.json' -Text ($state | ConvertTo-Json -Compress)
    }
}

function Get-UpdateState {
    Get-WeirJson $script:Ctx.Session '/api/v1/suite/update-state'
}

# Waits for the tray to say a line in its log after $FromLength characters of it, and returns the line.
function Wait-TrayLogLine {
    param([Parameter(Mandatory)] [string] $Contains, [Parameter(Mandatory)] [int] $FromLength, [int] $TimeoutSeconds = 30)
    Wait-Until -What "the tray to log '$Contains'" -TimeoutSeconds $TimeoutSeconds -IntervalMilliseconds 500 -Probe {
        if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
        $text = Get-TrayLogText
        if ($text.Length -gt $FromLength) { $text.Substring($FromLength) -split "`r?`n" | Where-Object { $_.Contains($Contains) } | Select-Object -First 1 }
    }
}

# --- update-requests ----------------------------------------------------------------------------------------------------

function Scenario_update_requests {
    Initialize-FreshScenario
    $session = $script:Ctx.Session
    if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
    $state = Wait-Until -What 'the tray to say it is alive' -TimeoutSeconds 90 -Probe {
        if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
        $current = Get-UpdateState
        if ($current.tray_running -eq $true) { $current }
    }
    Assert-That ($state.tray_running -eq $true) "Weir counts the tray alive, so the buttons are offered (update state: $($state.state))"

    # A tray set to install by itself must not restart Weir under the run: only the person's own buttons are pressed.
    $original = Get-WeirJson $session '/api/v1/suite/update-settings'
    Get-WeirJson $session '/api/v1/suite/update-settings' -Method PUT -Body @{ mode = 'NotifyOnly'; check_on_startup = [bool]$original.check_on_startup; check_interval_minutes = [int]$original.check_interval_minutes } | Out-Null
    try {
        $mark = (Get-TrayLogText).Length
        $check = Invoke-Weir -Session $session -Method POST -Path '/api/v1/suite/check-update' -Body @{}
        Assert-That ($check.Status -eq 200 -and $check.Json.state -eq 'checking') "Check for updates was answered 200 with the step under way ($($check.Status), state $($check.Json.state))"
        Wait-TrayLogLine -Contains 'Check-now flag detected' -FromLength $mark | Out-Null
        $checked = Wait-Until -What 'the check to come to an answer' -TimeoutSeconds 180 -IntervalMilliseconds 1000 -Probe {
            if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
            $current = Get-UpdateState
            if ($current.state -in 'idle', 'failed', 'downloaded') { $current }
        }
        $checkedText = if ($checked.state -eq 'failed') { "failed with ""$($checked.failure)""" } elseif ($checked.state -eq 'downloaded') { "found and downloaded $($checked.pending_version)" } else { 'found nothing newer' }
        Assert-That (-not ($checked.state -eq 'failed' -and -not $checked.failure)) "a failed check says why ($checkedText)"

        if ($checked.state -ne 'downloaded') {
            $mark = (Get-TrayLogText).Length
            $download = Invoke-Weir -Session $session -Method POST -Path '/api/v1/suite/download-update' -Body @{}
            Assert-That ($download.Status -eq 200 -and $download.Json.state -eq 'downloading') "Download update was answered 200 with the step under way ($($download.Status), state $($download.Json.state))"
            Wait-TrayLogLine -Contains 'Download-now flag detected' -FromLength $mark | Out-Null
            $downloaded = Wait-Until -What 'the download request to come to an answer' -TimeoutSeconds 600 -IntervalMilliseconds 1000 -Probe {
                if ($script:Ctx.Rehearsal) { Invoke-RehearsalTray }
                $current = Get-UpdateState
                if ($current.state -in 'idle', 'failed', 'downloaded') { $current }
            }
            Assert-That (-not ($downloaded.state -eq 'failed' -and -not $downloaded.failure)) 'a failed download says why'
            $downloadText = if ($downloaded.state -eq 'downloaded') { "downloaded $($downloaded.pending_version)" } else { "answered ""$($downloaded.failure)""" }
            $apply = Invoke-Weir -Session $session -Method POST -Path '/api/v1/suite/apply-update' -Body @{}
            if ($downloaded.state -ne 'downloaded') { Assert-That ($apply.Status -eq 409) "Restart and apply with nothing downloaded is refused with 409 ($($apply.Status): $($apply.Json.detail))" }
        }
        else { $downloadText = 'not pressed: the check had already downloaded the update' }
    }
    finally {
        Get-WeirJson $session '/api/v1/suite/update-settings' -Method PUT -Body @{ mode = $original.mode; check_on_startup = [bool]$original.check_on_startup; check_interval_minutes = [int]$original.check_interval_minutes } | Out-Null
    }
    "The tray was alive; Check for updates -> the tray logged ""Check-now flag detected"" and the check $checkedText; Download update -> the tray logged ""Download-now flag detected"" and $downloadText."
}

# --- the copy saved before an update ------------------------------------------------------------------------------------

# Asks the running server for the copy of Weir's data the tray asks for before it applies an update (the tray's request file),
# and checks the answer. Returns a sentence for the record.
function Test-PreUpdateCopyRequest {
    $dataFolder = $script:Ctx.RuntimeHome
    $session = $script:Ctx.Session
    $ready = Wait-Until -What 'the server to say it can take a request for a copy of its data' -TimeoutSeconds 60 -Probe {
        $text = Get-FileTextOrNull -Path (Join-Path $dataFolder 'update-backup-ready')
        if ($text) { $text | ConvertFrom-Json }
    }
    Assert-That ([int]$ready.pid -gt 0) "the server's ready marker names process $($ready.pid)"
    $id = 'scenario-' + [guid]::NewGuid().ToString('N')
    $target = '9.9.9'
    Write-FileAtomically -Folder $dataFolder -Name 'update-backup-request.json' -Text (@{ id = $id; requested_at = (Get-Date).ToUniversalTime().ToString('o'); target_version = $target } | ConvertTo-Json -Compress)
    $answer = Wait-Until -What 'the server to save the copy' -TimeoutSeconds 120 -IntervalMilliseconds 250 -Probe {
        $text = Get-FileTextOrNull -Path (Join-Path $dataFolder 'update-backup-result.json')
        if ($text) { $result = $text | ConvertFrom-Json; if ($result.id -eq $id -and $result.state -in 'saved', 'failed') { $result } }
    }
    Assert-That ($answer.state -eq 'saved') "the server saved the copy (state $($answer.state)$(if ($answer.PSObject.Properties['reason']) { ': ' + $answer.reason }))"
    Assert-That ((Split-Path -Parent $answer.path) -eq (Join-Path $dataFolder 'backups\pre-update')) 'the copy is under backups\pre-update'
    Assert-That ((Split-Path -Leaf $answer.path) -match '^weir-\d{4}-to-9\.9\.9-\d{8}T\d{6}Z\.db$') "the copy is named for the version it was saved before ($(Split-Path -Leaf $answer.path))"
    $size = (Get-Item -LiteralPath $answer.path).Length
    Assert-That ($size -gt 0) "the copy is not empty ($size bytes)"
    Wait-Until -What 'the server to take the answered request away' -TimeoutSeconds 30 -Probe { -not (Test-Path -LiteralPath (Join-Path $dataFolder 'update-backup-request.json')) } | Out-Null
    $overview = Get-WeirJson $session '/api/v1/system/overview'
    Assert-That ($overview.last_update_backup.path -eq $answer.path -and $overview.last_update_backup.to_version -eq $target) 'System overview names that copy as the last update backup'
    "the tray's request for a copy was answered with $(Split-Path -Leaf $answer.path) ($size bytes), and System overview names it"
}

# --- logs ---------------------------------------------------------------------------------------------------------------

# Lines of Weir's JSON server log at warning or worse, as { Level; Message; At }.
function Get-ServerLogProblems {
    param([Parameter(Mandatory)] [string] $Path)
    $text = Get-FileTextOrNull -Path $Path
    if (-not $text) { return @() }
    $lines = @($text -split "`r?`n")
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if (-not $lines[$i].Trim().StartsWith('{')) { continue }
        try { $entry = $lines[$i] | ConvertFrom-Json } catch { continue }
        if ([string]$entry.level -in 'WARNING', 'ERROR', 'CRITICAL', 'FATAL') {
            [pscustomobject]@{ Level = $entry.level; Message = [string]$entry.message; At = [datetime]::Parse([string]$entry.timestamp).ToUniversalTime(); Line = $i }
        }
    }
}

# Lines of the tray's log that report trouble, as { Line; At } (the tray stamps local time).
function Get-TrayLogProblems {
    $pattern = '(?i)(could not|unhandled|fatal|exited unexpectedly|giving up|failed|did not stop|killing it|stopped after an error|not starting|error)'
    $lines = @((Get-TrayLogText) -split "`r?`n")
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if (-not $line.Trim() -or $line -notmatch $pattern) { continue }
        $at = [datetime]::MinValue
        if ($line -match '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]') { $at = ([datetime]::Parse($Matches[1])).ToUniversalTime() }
        [pscustomobject]@{ Line = $line; At = $at; Index = $i }
    }
}

# --- the log watch every scenario ends with -----------------------------------------------------------------------------
#
# A scenario does not read "the logs of the whole machine": the runner starts a watch when the scenario begins (the warnings and
# errors that exist then) and, when the scenario has passed, completes it (nothing unexplained is new). So what a scenario is
# judged on is what it and the Weir under it wrote, whichever scenarios ran before it.
#
# The tray and server log files are noted before the scenario does anything, so the start of Weir inside a scenario is judged. The
# rows on System > Logs need a signed-in session: they are noted when the scenario first has one (Initialize-WeirSession), or, for
# a scenario that began with no account, taken as none (the Weir had nothing to show). A scenario that never has a session leaves
# the Logs page to the next one that does.

# The rows on System > Logs at warning or error, newest first, as { Items; More } (More: Logs holds further rows than one page).
function Get-LogPageRows {
    $page = Get-WeirJson $script:Ctx.Session '/api/v1/system/log?level=warning,error&limit=100'
    [pscustomobject]@{ Items = @($page.items); More = [bool]$page.next_cursor }
}

# What identifies a Logs row, so that a row seen when the watch began is not taken for a new one.
function Get-LogPageKey {
    param([Parameter(Mandatory)] $Row)
    "$($Row.source)|$($Row.id)|$($Row.at)|$($Row.title)"
}

# The warnings and errors in the two log files now, each with what identifies it.
function Get-LogFileProblems {
    $paths = Get-WeirPaths
    [pscustomobject]@{
        Server = @(Get-ServerLogProblems -Path $paths.ServerLog | ForEach-Object { [pscustomobject]@{ Key = "$($_.At.ToString('o'))|$($_.Level)|$($_.Message)"; Text = "$($_.Level) $($_.Message)"; Message = $_.Message; Position = $_.Line } })
        Tray   = @(Get-TrayLogProblems | ForEach-Object { [pscustomobject]@{ Key = $_.Line; Text = $_.Line; Message = $_.Line; Position = $_.Index } })
    }
}

function Start-LogWatch {
    $files = Get-LogFileProblems
    # Mark is the second the scenario began (UTC). A scenario that begins with no account cannot note the Logs page, and on a machine that
    # was not restored (-NoRestore) the page holds what earlier runs and builds wrote, so only rows written from the mark on are its own.
    $script:Ctx.LogWatch = [pscustomobject]@{ Server = @($files.Server | ForEach-Object { $_.Key }); Tray = @($files.Tray | ForEach-Object { $_.Key }); Page = $null; Mark = Get-SecondMark; Expected = New-Object System.Collections.Generic.List[object]; Build = $null }
}

# --- a scenario that replaces the running Weir ----------------------------------------------------------------------------
#
# When the build under test is installed over another Weir (an update), that Weir is still writing to the same log files while the
# scenario goes on, and what it writes is not the build under test's to answer for: a warning the previous release logs before the
# update is its own. So such a scenario says so (Use-BuildUnderTestLogWindow, before it installs), and the watch then judges the
# server log, the tray log and System > Logs only from the moment the build under test began. That moment is the build's own line in
# the server log, "Weir <version>+<sha> is starting process=<pid>" (the first of them since the scenario said so), and not a time worked
# out from the clock. The tray log is judged from the new tray process's own first line ("Starting tray host.", after the install's "Velopack: after install v<version>"), and the line where that tray says it started the build's server ("Bundled server host
# pid=<pid>"). What the previous release logged meanwhile is told in the scenario's record as information. Everything the build under
# test logs is judged exactly as it is in a scenario that replaces nothing.

# The lines of the server log where a Weir of the build under test says it is starting, as { Line; At; ProcessId; Version }.
function Get-BuildUnderTestStarts {
    $text = Get-FileTextOrNull -Path (Get-WeirPaths).ServerLog
    if (-not $text) { return @() }
    $lines = @($text -split "`r?`n")
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if (-not $lines[$i].Trim().StartsWith('{')) { continue }
        try { $entry = $lines[$i] | ConvertFrom-Json } catch { continue }
        if ([string]$entry.message -notmatch '^Weir (?<version>\S+) is starting process=(?<process>\d+)\b') { continue }
        $version = $Matches['version']
        $started = [int]$Matches['process']
        if (-not (Test-VersionUnderTest -Reported $version -Expected $script:Ctx.Version -ShortSha $script:Ctx.ShortSha)) { continue }
        [pscustomobject]@{ Line = $i; At = [datetime]::Parse([string]$entry.timestamp).ToUniversalTime(); ProcessId = $started; Version = $version }
    }
}

# Says that this scenario replaces the running Weir with the build under test, so that the log watch judges only what the build under
# test writes. Call it before the build under test is installed: it notes the starts of the build that the log already holds.
function Use-BuildUnderTestLogWindow {
    $watch = $script:Ctx.LogWatch
    if ($null -eq $watch) { throw 'The build under test was named as replacing the running Weir with no log watch running: Start-LogWatch was not called when the scenario began.' }
    $trayText = Get-TrayLogText
    $trayLinesBefore = if ($trayText.Trim().Length -gt 0) { @($trayText.TrimEnd("`r", "`n") -split "`r?`n").Count } else { 0 }
    $watch.Build = [pscustomobject]@{ StartsBefore = @(Get-BuildUnderTestStarts).Count; TrayLinesBefore = $trayLinesBefore }
}

# Where the build under test began in the logs, as { Version; ProcessId; At; ServerLine; TrayIndex }. Throws when the server log has no
# start line of it since the scenario said it replaces the running Weir, or the tray log does not say it started that process.
function Get-BuildUnderTestStart {
    param([Parameter(Mandatory)] $Window)
    $starts = @(Get-BuildUnderTestStarts | Select-Object -Skip $Window.StartsBefore)
    if ($starts.Count -eq 0) {
        throw "the server log has no line saying that Weir $($script:Ctx.Version) ($($script:Ctx.ShortSha)) began ('Weir <version>+<sha> is starting process=<pid>') since the scenario installed it over the running Weir, so the logs cannot be judged from the moment the build under test began ($((Get-WeirPaths).ServerLog))"
    }
    $start = $starts[0]
    # The tray log is judged from the new tray process's own first line ("Starting tray host."), found after the install's line for the
    # build ("Velopack: after install v<version>"); the tray's line for the build's server process must follow it. Only lines written
    # since the scenario said it replaces the running Weir count.
    $trayLines = @((Get-TrayLogText) -split "`r?`n")
    $versionBase = [regex]::Escape($script:Ctx.Version.Split('+')[0])
    $installed = -1
    for ($i = $Window.TrayLinesBefore; $i -lt $trayLines.Count -and $installed -lt 0; $i++) { if ($trayLines[$i] -match "Velopack: after install v$versionBase\s*$") { $installed = $i } }
    $trayIndex = -1
    for ($i = $installed + 1; $installed -ge 0 -and $i -lt $trayLines.Count -and $trayIndex -lt 0; $i++) { if ($trayLines[$i] -match 'Starting tray host\.') { $trayIndex = $i } }
    $serverLine = -1
    for ($i = $trayIndex; $trayIndex -ge 0 -and $i -lt $trayLines.Count -and $serverLine -lt 0; $i++) { if ($trayLines[$i] -match "Bundled server host pid=$($start.ProcessId)\b") { $serverLine = $i } }
    if (-not $script:Ctx.Rehearsal) {
        if ($installed -lt 0) { throw "the tray log has no line saying that the build under test was installed ('Velopack: after install v$($script:Ctx.Version)') since the scenario installed it, so the tray log cannot be judged from the moment the build under test's tray began ($((Get-WeirPaths).TrayLog))" }
        if ($trayIndex -lt 0) { throw "the tray log has no line saying that the tray of the build under test started ('Starting tray host.') after its install, so the tray log cannot be judged from the moment that tray began ($((Get-WeirPaths).TrayLog))" }
        if ($serverLine -lt 0) { throw "the tray log has no line saying that the tray of the build under test started process $($start.ProcessId), the server of the build ('Bundled server host pid=$($start.ProcessId)') after it began ($((Get-WeirPaths).TrayLog))" }
    }
    [pscustomobject]@{ Version = $start.Version; ProcessId = $start.ProcessId; At = $start.At; ServerLine = $start.Line; TrayIndex = $trayIndex }
}

# Problems that sit at or after Position $From in their file (what the build under test wrote), and those before it (the previous release's).
function Split-ProblemsAt {
    param($Problems, [Parameter(Mandatory)] [int] $From)
    [pscustomobject]@{
        Build    = @($Problems | Where-Object { $_.Position -ge $From })
        Previous = @($Problems | Where-Object { $_.Position -lt $From })
    }
}

# Names a warning or error the running scenario is about to cause on purpose (a sign-in it makes with a password it knows is wrong, say),
# so that the log watch does not take it for trouble. It holds for this scenario only: the next one begins with none. Pattern is matched
# against the message in the log files and the title of the row on System > Logs. System > Logs also lists the Activity entry the same
# event leaves, under a title of its own: PageTitlePattern names that one, and it is looked for on System > Logs only.
function Add-ExpectedLogRow {
    param([Parameter(Mandatory)] [string] $Pattern, [Parameter(Mandatory)] [string] $Why, [string] $PageTitlePattern)
    if ($null -eq $script:Ctx.LogWatch) { throw 'A log row was named as caused on purpose with no log watch running: Start-LogWatch was not called when the scenario began.' }
    $script:Ctx.LogWatch.Expected.Add([pscustomobject]@{ Pattern = $Pattern; PageTitlePattern = $PageTitlePattern; Why = $Why })
}

# Notes the rows on the Logs page, once, when the scenario first has a session.
function Set-LogWatchPage {
    $watch = $script:Ctx.LogWatch
    if ($null -eq $watch -or $null -ne $watch.Page) { return }
    $watch.Page = @((Get-LogPageRows).Items | ForEach-Object { Get-LogPageKey -Row $_ })
}

# The problems in $Problems that are not in $Before (by key) and that $Known does not name as caused on purpose.
function Select-NewProblems {
    param($Problems, $Before, $Known)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($key in @($Before)) { [void]$seen.Add([string]$key) }
    @($Problems | Where-Object { -not $seen.Contains($_.Key) } | Where-Object { $row = $_; -not (@($Known | Where-Object { $row.Message -match $_.Pattern }).Count -gt 0) })
}

# Judges what the scenario that has just passed left in the logs: nothing at warning or error is new on the Logs page, in the
# server log file or in the tray log, except what the suite names as caused on purpose. Throws what was found; returns what it saw.
function Complete-LogWatch {
    $watch = $script:Ctx.LogWatch
    if ($null -eq $watch) { throw 'The scenario ended with no log watch to judge: Start-LogWatch was not called when it began.' }
    $script:Ctx.LogWatch = $null
    $paths = Get-WeirPaths
    $known = @(@($script:Ctx.KnownLogRows) + $watch.Expected.ToArray() | Where-Object { $_ })
    $files = Get-LogFileProblems

    $serverText = Get-FileTextOrNull -Path $paths.ServerLog
    Assert-That ($serverText -and $serverText.Length -gt 0) "the server log exists and has lines ($($paths.ServerLog))"
    # A scenario that replaced the running Weir is judged from where the build under test began; the previous release's lines are told.
    $build = $null
    if ($null -ne $watch.Build) { $build = Get-BuildUnderTestStart -Window $watch.Build }
    $newServer = @(Select-NewProblems -Problems $files.Server -Before $watch.Server -Known $known)
    $previousServer = @()
    if ($build) { $split = Split-ProblemsAt -Problems $newServer -From $build.ServerLine; $newServer = $split.Build; $previousServer = $split.Previous }
    Assert-That ($newServer.Count -eq 0) "the server log file gained no unexpected warning or error (found $($newServer.Count): $((@($newServer | ForEach-Object { $_.Text }) -join ' | ')))"

    if (-not $script:Ctx.Rehearsal) { Assert-That ((Get-TrayLogText).Length -gt 0) "the tray log exists and has lines ($($paths.TrayLog))" }
    $newTray = @(Select-NewProblems -Problems $files.Tray -Before $watch.Tray -Known $known)
    $previousTray = @()
    if ($build) { $split = Split-ProblemsAt -Problems $newTray -From ([Math]::Max($build.TrayIndex, 0)); $newTray = $split.Build; $previousTray = $split.Previous }
    Assert-That ($newTray.Count -eq 0) "the tray log gained no line reporting trouble (found $($newTray.Count): $((@($newTray | ForEach-Object { $_.Text }) -join ' | ')))"

    $onPurpose = if ($known.Count -gt 0) { " (named as caused on purpose: $((@($known | ForEach-Object { $_.Why }) -join '; ')))" } else { '' }
    $previous = New-Object System.Collections.Generic.List[string]
    foreach ($problem in $previousServer) { $previous.Add("server log: $($problem.Text)") }
    foreach ($problem in $previousTray) { $previous.Add("tray log: $($problem.Text)") }
    if (-not $script:Ctx.Session) { return "Logs since it began: no unexpected warning or error in the server log file or the tray log (there was no account to read System > Logs with)$onPurpose.$(Get-PreviousReleaseNote -Build $build -Previous $previous)" }
    $page = Get-LogPageRows
    $rows = @($page.Items | ForEach-Object { [pscustomobject]@{ Key = Get-LogPageKey -Row $_; Message = [string]$_.title; At = $_.at } })
    if ($null -eq $watch.Page) { $rows = @($rows | Where-Object { Test-WrittenFrom -At $_.At -Mark $watch.Mark }) }
    $knownOnPage = @($known | ForEach-Object { $_; if ($_.PSObject.Properties['PageTitlePattern'] -and $_.PageTitlePattern) { [pscustomobject]@{ Pattern = $_.PageTitlePattern; Why = $_.Why } } })
    if ($build) {
        # Rows have a time, not a place in a file: the build's rows are those written from the second its start line was logged.
        $from = New-Object DateTime ($build.At.Ticks - ($build.At.Ticks % [TimeSpan]::TicksPerSecond)), ([DateTimeKind]::Utc)
        $earlier = @($rows | Where-Object { -not (Test-WrittenFrom -At $_.At -Mark $from) })
        foreach ($row in @(Select-NewProblems -Problems $earlier -Before $watch.Page -Known $knownOnPage)) { $previous.Add("System > Logs: $($row.Message)") }
        $rows = @($rows | Where-Object { Test-WrittenFrom -At $_.At -Mark $from })
    }
    $newRows = @(Select-NewProblems -Problems $rows -Before $watch.Page -Known $knownOnPage)
    Assert-That (-not ($page.More -and $newRows.Count -ge $rows.Count)) 'System > Logs holds more new warnings and errors than one page shows, which is not a clean run'
    Assert-That ($newRows.Count -eq 0) "System > Logs gained no unexpected warning or error (found $($newRows.Count): $((@($newRows | ForEach-Object { $_.Message }) -join ' | ')))"
    "Logs since it began: no unexpected warning or error on System > Logs, in the server log file or in the tray log$onPurpose.$(Get-PreviousReleaseNote -Build $build -Previous $previous)"
}

# For the scenario's record, when the logs were judged from the build under test's start: where that was, and what the previous release
# logged before it (information; the build under test is not answerable for it). Nothing for a scenario that replaced nothing.
function Get-PreviousReleaseNote {
    param($Build, $Previous)
    if (-not $Build) { return '' }
    $began = " The logs were judged from the moment the build under test began ($($Build.Version), process $($Build.ProcessId), logged at $($Build.At.ToString('o')))."
    if (@($Previous).Count -eq 0) { return "$began The previous release logged no warning or error meanwhile." }
    "$began For information, not judged, the previous release logged $(@($Previous).Count) warning or error line(s) before that: $((@($Previous) -join ' | '))."
}
