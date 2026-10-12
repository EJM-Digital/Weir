<#
    What the scenarios share: the run's context, Weir's installed paths, getting a signed-in session, and the calls Activity, the
    file list and the job list need. Dot-sourced by Run-WeirScenarios.ps1 after Weir.Scenarios.Lib.ps1.
#>

# The context the runner fills in before the first scenario. Scenarios read it and add what later scenarios need.
$script:Ctx = @{}

function Get-Prop {
    param($Object, [Parameter(Mandatory)] [string] $Name)
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { $Object.$Name } else { $null }
}

function New-NotApplicable {
    param([Parameter(Mandatory)] [string] $Reason)
    [pscustomobject]@{ NotApplicable = $true; Detail = $Reason }
}

# --- the installed Weir -----------------------------------------------------------------------------------------------

function Get-WeirPaths {
    $install = $script:Ctx.InstallRoot
    [pscustomobject]@{
        TrayExe    = Join-Path $install 'current\Weir.exe'
        ServerExe  = Join-Path $install 'current\server\WeirServer.exe'
        TrayLog    = Join-Path $script:Ctx.RuntimeHome 'tray-host.log'
        ServerLog  = Join-Path $script:Ctx.RuntimeHome 'logs\weir.log'
        LogsFolder = Join-Path $script:Ctx.RuntimeHome 'logs'
    }
}

function Wait-WeirReady {
    param([int] $TimeoutSeconds = 120)
    Wait-Until -What "Weir to answer /ready at $($script:Ctx.Base)" -TimeoutSeconds $TimeoutSeconds -Probe {
        $response = Invoke-WebRequest -Uri "$($script:Ctx.Base)/ready" -UseBasicParsing -TimeoutSec 3
        if ((ConvertFrom-Json $response.Content).ready -eq $true) { $true } else { $false }
    } | Out-Null
}

# The version the running server reports (the OpenAPI document carries it), the way a client would read it.
function Get-ServerVersion {
    (Invoke-RestMethod -Uri "$($script:Ctx.Base)/openapi.json" -TimeoutSec 10).info.version
}

# True when $Reported is the build under test: the release's own version, and the commit when the build carries one.
function Test-VersionUnderTest {
    param([Parameter(Mandatory)] [string] $Reported, [Parameter(Mandatory)] [string] $Expected, [string] $ShortSha)
    $expectedBase = $Expected.Split('+')[0]
    $reportedBase = $Reported.Split('+')[0]
    if ($reportedBase -ne $expectedBase) { return $false }
    # A build made for the golden path carries its commit (`<version>+<short sha>`); one that does not cannot be tied to the commit.
    if ($ShortSha) { return $Reported.Contains('+') -and $Reported.Split('+')[1].StartsWith($ShortSha.Substring(0, [Math]::Min(7, $ShortSha.Length))) }
    $true
}

# A time as the machine's UTC clock reads it, from a DateTime or from the text a JSON answer carries (with its offset). Windows
# PowerShell 5.1 leaves ISO text as text and newer versions turn it into a DateTime; either way the instant is the same.
function ConvertTo-UtcTime {
    param($Value)
    if ($null -eq $Value -or [string]$Value -eq '') { return $null }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime() }
    [datetimeoffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}

# Where the first update scenario leaves the moment the build under test began answering. It sits beside the run folder, not in it,
# because the run folder is emptied at the start of every phase and the Deluno scenarios of a later phase need it.
function Get-BuildMarkerPath {
    Join-Path (Split-Path -Parent $script:Ctx.RunFolder) 'build-under-test.json'
}

function Save-BuildUnderTestMarker {
    param([Parameter(Mandatory)] [string] $Reported)
    $marker = @{ version = $script:Ctx.Version; short_sha = $script:Ctx.ShortSha; reported = $Reported; began_answering_ticks = [DateTime]::UtcNow.Ticks }
    Set-Content -LiteralPath (Get-BuildMarkerPath) -Value ($marker | ConvertTo-Json -Compress) -Encoding UTF8
}

# When the build under test took over from the previous release, as { Utc; Because }: the marker the update scenario left for this
# version and commit, else when the running server process started (a lower bound: whatever Deluno handed over after that was
# certainly handled by it). A hand-off made before this moment was handled by the previous release.
function Get-BuildUnderTestSince {
    $text = Get-FileTextOrNull -Path (Get-BuildMarkerPath)
    if ($text) {
        try { $marker = $text.TrimStart([char]0xFEFF) | ConvertFrom-Json } catch { $marker = $null }
        if ($marker -and $marker.version -eq $script:Ctx.Version -and $marker.short_sha -eq $script:Ctx.ShortSha) {
            return [pscustomobject]@{ Utc = [DateTime]::new([long]$marker.began_answering_ticks, [DateTimeKind]::Utc); Because = 'the update scenario saw it begin answering' }
        }
    }
    $server = @(Get-Process -Name 'WeirServer' -ErrorAction SilentlyContinue | Sort-Object StartTime) | Select-Object -First 1
    if (-not $server) { throw 'The build under test is not running, so there is no telling which hand-offs it handled.' }
    [pscustomobject]@{ Utc = $server.StartTime.ToUniversalTime(); Because = 'no update scenario left a marker, so this is when the running server started' }
}

function Get-TrayLogText {
    $text = Get-FileTextOrNull -Path (Get-WeirPaths).TrayLog
    if ($null -eq $text) { '' } else { $text }
}

# --- a signed-in session ----------------------------------------------------------------------------------------------

# The account the run uses. A throwaway password made for this run, never shown in the record.
function New-ScenarioAccount {
    $script:Ctx.Username = 'scenario-admin'
    $script:Ctx.Password = 'Sc-' + [guid]::NewGuid().ToString('N') + '-Aa1'
    Register-Secret $script:Ctx.Password
}

function Sign-InScenarioAccount {
    $session = New-WeirSession -BaseUrl $script:Ctx.Base
    Connect-Weir -Session $session -Username $script:Ctx.Username -Password $script:Ctx.Password
    $session
}

# Reads a login the Deluno session left in the VM (two lines: the user name, then the password), then deletes the file. Used
# only when Weir already has an account whose password this run was not given any other way. The same rule as the Deluno key file:
# it is read inside the VM and never copied out.
function Read-WeirLoginFile {
    $path = $script:Ctx.WeirLoginFile
    if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $lines = @(Get-Content -LiteralPath $path | Where-Object { $_.Trim() })
    Remove-Item -LiteralPath $path -Force
    if (Test-Path -LiteralPath $path) { throw "The login file $path was read but could not be deleted, so it was left lying around." }
    if ($lines.Count -ne 2) { throw "The login file $path did not hold two lines (the user name, then the password), and it has been deleted." }
    Register-Secret $lines[1].Trim()
    Add-Evidence "read Weir's login from $path inside this machine and deleted the file: user $($lines[0].Trim()), password ****"
    [pscustomobject]@{ Username = $lines[0].Trim(); Password = $lines[1].Trim() }
}

# Makes sure the run has a signed-in session on the Weir that is running. Weir with no account yet gets the run's own; one with an
# account needs its login from the login file in the VM. Returns $true, or $false with the reason in $script:Ctx.NoSessionReason.
function Initialize-WeirSession {
    if ($script:Ctx.Session) { return $true }
    if ($script:Ctx.ContainsKey('Username') -and $script:Ctx.Username) {
        $script:Ctx.Session = Sign-InScenarioAccount
        return $true
    }
    $anonymous = New-WeirSession -BaseUrl $script:Ctx.Base
    $status = Get-WeirJson $anonymous '/api/v1/auth/bootstrap/status'
    if ($status.bootstrap_allowed -eq $true) {
        New-ScenarioAccount
        $created = Invoke-Weir -Session $anonymous -Method POST -Path '/api/v1/auth/bootstrap' -Body @{ username = $script:Ctx.Username; password = $script:Ctx.Password }
        if ($created.Status -ne 200) { throw "Creating the run's account answered $($created.Status): $($created.Text)" }
        $script:Ctx.Session = Sign-InScenarioAccount
        return $true
    }
    $login = Read-WeirLoginFile
    if ($login) {
        $script:Ctx.Username = $login.Username
        $script:Ctx.Password = $login.Password
        $script:Ctx.Session = Sign-InScenarioAccount
        return $true
    }
    $script:Ctx.NoSessionReason = "Weir already has an account (it is not offering to create one), and no login was left for this run in the VM ($($script:Ctx.WeirLoginFile)), so there is no way to sign in without a secret from outside."
    $false
}

# --- Activity, files and jobs --------------------------------------------------------------------------------------------

function Get-ActivityItems {
    param([string] $EventType, [int] $Limit = 100)
    $path = "/api/v1/activity/recent?limit=$Limit"
    if ($EventType) { $path += "&event_type=$EventType" }
    @((Get-WeirJson $script:Ctx.Session $path).items)
}

# Every Activity entry, newest first. One request returns at most 100, and an update (or the run itself) adds entries, so a list of
# "the newest 100" read before and after cannot say whether an entry was lost: the oldest ones slide out of it. This pages back
# by `before_id` until Weir says there is no more.
function Get-AllActivityItems {
    $items = New-Object System.Collections.Generic.List[object]
    $before = $null
    for ($page = 1; $page -le 500; $page++) {
        $path = '/api/v1/activity/recent?limit=100&with_total=false'
        if ($before) { $path += "&before_id=$before" }
        $answer = Get-WeirJson $script:Ctx.Session $path
        $got = @($answer.items)
        foreach ($item in $got) { $items.Add($item) }
        if ($answer.has_more -ne $true -or $got.Count -eq 0) { return $items.ToArray() }
        $before = $got[$got.Count - 1].id
    }
    throw 'Activity has more than 50,000 entries, which is more than this check pages through.'
}

# The entries of $Before that $After does not hold exactly as they were: the same id with the same time, kind, title, detail,
# result and file. Nothing is excused: an entry that is missing, or that reads differently, is a finding. Entries only in $After
# (what happened since) are not.
function Find-ActivityLostOrChanged {
    param($Before, $After)
    $now = @{}
    foreach ($row in @($After)) { $now[[string]$row.id] = $row }
    $fields = 'created_at', 'event_type', 'module', 'title', 'detail', 'trigger', 'result', 'library_id', 'relative_path', 'run_key'
    foreach ($row in @($Before)) {
        $kept = $now[[string]$row.id]
        if ($null -eq $kept) { [pscustomobject]@{ Id = $row.id; Title = [string]$row.title; Problem = 'is gone' }; continue }
        foreach ($field in $fields) {
            $was = [string](Get-Prop $row $field)
            $is = [string](Get-Prop $kept $field)
            if ($was -ne $is) {
                $cut = { param($text) if ($text.Length -gt 80) { $text.Substring(0, 80) + '...' } else { $text } }
                [pscustomobject]@{ Id = $row.id; Title = [string]$row.title; Problem = "$field was '$(& $cut $was)' and now reads '$(& $cut $is)'" }
                break
            }
        }
    }
}

function Get-ScenarioFiles {
    param([int] $LibraryId)
    $path = '/api/v1/processing/files?limit=1000'
    if ($LibraryId) { $path += "&library_id=$LibraryId" }
    @((Get-WeirJson $script:Ctx.Session $path).files)
}

function Get-RemuxJobs {
    @((Get-WeirJson $script:Ctx.Session '/api/v1/processing/jobs/inspection?limit=100&status=pending&status=leased&status=completed&status=failed&status=handler_ok_finalize_failed&status=cancelled').jobs |
        Where-Object { $_.job_kind -like 'processing.file.remux_pass*' })
}

# --- the Weir log ----------------------------------------------------------------------------------------------------------

# System > Logs rows at warning or error, as the Logs page lists them.
function Get-LogPageProblems {
    # The Logs page lists at most 100 rows at a time. More than that is itself a failure, not something to page through and excuse.
    $page = Get-WeirJson $script:Ctx.Session '/api/v1/system/log?level=warning,error&limit=100'
    if ($page.next_cursor) { throw "Logs lists more than 100 warnings and errors ($($page.total) in all), which is not a clean run." }
    @($page.items)
}

# The warnings and errors on Logs that nothing above explains. $Known is { Pattern; Why } entries, matched against the row's title.
function Get-UnexpectedLogRows {
    param($Rows, $Known = @())
    @($Rows | Where-Object {
            $title = [string]$_.title
            -not (@($Known | Where-Object { $title -match $_.Pattern }).Count -gt 0)
        })
}
