<#
    The scenarios that need the real Deluno in the VM: workflows set up from it, and the hand-offs, resends and outcomes Deluno's
    own suite really made, read through Deluno's API with a key that never leaves the VM.

    Deluno's side of a hand-off cannot be made by a script (a hand-off starts when a download finishes in one of its clients), so
    these scenarios follow the real ones from both ends and drive what Deluno does offer: its own send-again for an outcome.
    A precondition that is not met (no Deluno answering, no key, no hand-off of the kind) is recorded not-applicable with the
    reason, and a record with any not-applicable scenario sets no success status.
#>

# --- the key (it never leaves this machine) ---------------------------------------------------------------------------------

# The first way to have a key: the Deluno session of a combined round mints a read,imports key and leaves it in this machine, in a
# file holding that one line (the key, nothing else). It is read here, never copied out, and the file is deleted at once so it is
# not left lying around. Returns the key, or $null when there is no file. A file that is there but unusable is a failure.
function Read-DelunoKeyFile {
    $path = $script:Ctx.DelunoKeyFile
    if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $lines = @(Get-Content -LiteralPath $path | Where-Object { $_.Trim() })
    $key = if ($lines.Count -eq 1) { $lines[0].Trim() } else { $null }
    Remove-Item -LiteralPath $path -Force
    if (Test-Path -LiteralPath $path) { throw "The key file $path was read but could not be deleted, so it was left lying around." }
    if (-not $key -or $key -notmatch '^deluno_\S{8,}$') { throw "The key file $path did not hold one line with a Deluno API key (deluno_...), and it has been deleted. The file is the key alone, on one line." }
    Register-Secret $key
    Add-Evidence "read the API key from $path inside this machine and deleted the file: ****"
    $key
}

# Gets an API key for the Deluno on this machine without any secret from outside the VM, in this order: the key file the Deluno
# session left in the VM (Read-DelunoKeyFile), then a throwaway account on a Deluno that has none, otherwise no key and the reason.
# Deluno keeps only a hash of each key it hands out (api_keys.key_hash), so an existing key cannot be read back; one is minted
# through Deluno's own local API (POST /api/api-keys, signed in). Returns { Key } or { Reason }. The key and the token are
# registered as secrets, so nothing the run writes can show them.
function Get-DelunoApiKey {
    $fromFile = Read-DelunoKeyFile
    if ($fromFile) { return [pscustomobject]@{ Key = $fromFile; Reason = $null } }
    $url = $script:Ctx.DelunoUrl.TrimEnd('/')
    try { $status = Invoke-RestMethod -Uri "$url/api/auth/bootstrap-status" -TimeoutSec 10 }
    catch { return [pscustomobject]@{ Key = $null; Reason = "No Deluno answers at $url (GET /api/auth/bootstrap-status: $($_.Exception.Message)), and no key file was left in the VM ($($script:Ctx.DelunoKeyFile))." } }
    if ($null -eq $status.PSObject.Properties['requiresSetup']) { return [pscustomobject]@{ Key = $null; Reason = "Something answers at $url but it does not look like Deluno (no requiresSetup in its bootstrap status)." } }
    if (-not $status.requiresSetup) {
        return [pscustomobject]@{ Key = $null; Reason = "The Deluno at $url already has an account and no key file was left in the VM ($($script:Ctx.DelunoKeyFile)). Deluno keeps only a hash of each API key it hands out, so an existing key cannot be read back, and minting one needs a signed-in Deluno account whose password only the session that made it holds. Nothing in this run carries that password." }
    }
    $password = 'Sc-' + [guid]::NewGuid().ToString('N') + '-Aa1'
    Register-Secret $password
    $account = @{ username = 'weir-scenarios'; displayName = 'Weir scenarios'; password = $password } | ConvertTo-Json -Compress
    $signedIn = Invoke-RestMethod -Method POST -Uri "$url/api/auth/bootstrap" -ContentType 'application/json' -Body $account -TimeoutSec 30
    Register-Secret $signedIn.accessToken
    Add-Evidence "created a throwaway Deluno account 'weir-scenarios' on the Deluno at $url (it had none); its password was made here and is kept nowhere"
    $minted = Invoke-RestMethod -Method POST -Uri "$url/api/api-keys" -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($signedIn.accessToken)" } `
        -Body (@{ name = 'Weir scenarios (throwaway)'; scopes = 'read,imports' } | ConvertTo-Json -Compress) -TimeoutSec 30
    Register-Secret $minted.apiKey
    Add-Evidence 'minted an API key (scopes read, imports) through Deluno''s own API: ****'
    [pscustomobject]@{ Key = $minted.apiKey; Reason = $null }
}

# Sets up the run's way into Deluno once (the key file can only be read once). $true, or $false with the reason in
# $script:Ctx.NoDelunoReason.
function Initialize-Deluno {
    if ($script:Ctx.ContainsKey('DelunoReady')) { return $script:Ctx.DelunoReady }
    $script:Ctx.DelunoReady = $false
    if (-not $script:Ctx.DelunoUrl) {
        $script:Ctx.NoDelunoReason = 'This run was not pointed at a Deluno (a rehearsal does not look for one).'
        return $false
    }
    $obtained = Get-DelunoApiKey
    if (-not $obtained.Key) { $script:Ctx.NoDelunoReason = $obtained.Reason; return $false }
    $script:Ctx.DelunoKey = $obtained.Key
    $script:Ctx.DelunoReady = $true
    $true
}

# What a scenario needs before it starts: Deluno reachable with a key, and a signed-in Weir. Returns $null when it can go ahead,
# else the not-applicable result to return.
function Test-DelunoPreconditions {
    if (-not (Initialize-Deluno)) { return New-NotApplicable $script:Ctx.NoDelunoReason }
    if (-not (Initialize-WeirSession)) { return New-NotApplicable $script:Ctx.NoSessionReason }
    $null
}

# One call to Deluno with the key. Never throws on an HTTP error; the key is never written anywhere.
function Invoke-Deluno {
    param([string] $Method = 'GET', [Parameter(Mandatory)] [string] $Path, $Body)
    $parameters = @{ Uri = "$($script:Ctx.DelunoUrl.TrimEnd('/'))$Path"; Method = $Method; Headers = @{ 'X-Api-Key' = $script:Ctx.DelunoKey; Accept = 'application/json' }; UseBasicParsing = $true; TimeoutSec = 60 }
    if ($null -ne $Body) { $parameters['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8 -Compress)); $parameters['ContentType'] = 'application/json' }
    $status = 0
    $text = ''
    try { $response = Invoke-WebRequest @parameters; $status = [int]$response.StatusCode; $text = [string]$response.Content }
    catch {
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode; $text = if ($_.ErrorDetails) { [string]$_.ErrorDetails.Message } else { '' } }
        else { $text = $_.Exception.Message }
    }
    $json = $null
    if ($text.TrimStart().StartsWith('{') -or $text.TrimStart().StartsWith('[')) { try { $json = $text | ConvertFrom-Json } catch { $json = $null } }
    Add-Evidence "Deluno $Method $Path -> $status"
    [pscustomobject]@{ Status = $status; Json = $json; Text = $text }
}

# Deluno's hand-offs (its own list, newest first), as Deluno reports them.
function Get-DelunoHandoffs {
    $answer = Invoke-Deluno -Path '/api/integrations/processors/handoffs?take=200'
    if ($answer.Status -ne 200) { throw "Deluno's hand-off list answered $($answer.Status): $($answer.Text)" }
    @($answer.Json)
}

function Get-OutcomeOf {
    param($Handoff)
    Get-Prop $Handoff 'outcome'
}

# The Weir files a Deluno hand-off ended up as: its output path's last part is the cleaned file's name, or the release folder's.
function Find-WeirFilesFor {
    param([Parameter(Mandatory)] $Handoff, [Parameter(Mandatory)] $WeirFiles)
    $output = [string](Get-Prop $Handoff 'outputPath')
    if (-not $output) { return @() }
    $leaf = Split-Path -Leaf ($output.TrimEnd('\', '/'))
    @($WeirFiles | Where-Object {
            $copy = [string](Get-Prop (Get-Prop $_ 'handback') 'output_path')
            $copy -and ((Split-Path -Leaf $copy) -eq $leaf -or (Split-Path -Leaf (Split-Path -Parent $copy)) -eq $leaf)
        })
}

# The connection Weir has to this Deluno (the one Deluno's Connect Weir made, or the one this run makes).
function Get-WeirDelunoConnection {
    $port = ([uri]$script:Ctx.DelunoUrl).Port
    @((Get-WeirJson $script:Ctx.Session '/api/v1/media-managers/connections') | Where-Object { $_.kind -eq 'deluno' -and ([uri]$_.base_url).Port -eq $port }) | Select-Object -First 1
}

# --- workflows-from-deluno ----------------------------------------------------------------------------------------------

function Scenario_workflows_from_deluno {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    $session = $script:Ctx.Session
    $connection = Get-WeirDelunoConnection
    $how = 'found the connection Deluno had already made'
    if (-not $connection) {
        $created = Invoke-Weir -Session $session -Method POST -Path '/api/v1/media-managers/connections' -Body @{ kind = 'deluno'; base_url = $script:Ctx.DelunoUrl; api_key = $script:Ctx.DelunoKey; enabled = $true }
        Assert-That ($created.Status -in 200, 201) "Weir accepted the connection to Deluno at $($script:Ctx.DelunoUrl) (answered $($created.Status): $($created.Text))"
        $connection = $created.Json
        $how = 'connected Weir to Deluno with the key from the key file'
    }
    $connectionId = [int]$connection.id
    $workflows = Wait-Until -What 'Weir to set up its workflows from Deluno' -TimeoutSeconds 120 -IntervalMilliseconds 2000 -Probe {
        Invoke-Weir -Session $session -Method POST -Path "/api/v1/media-managers/connections/$connectionId/test" -Body @{} | Out-Null
        $linked = @((Get-WeirJson $session '/api/v1/processing/libraries') | Where-Object { $_.folders_synced_from_connection_id -eq $connectionId -and $_.watched_folder -and $_.output_folder })
        if ($linked.Count -ge 1) { $linked }
    }
    foreach ($workflow in $workflows) {
        Assert-That (@($workflow.manager_connection_ids) -contains $connectionId) "'$($workflow.name)' is linked to Deluno"
        Assert-That ($workflow.watched_folder -and $workflow.output_folder) "'$($workflow.name)' has its folders from Deluno (watched $($workflow.watched_folder), output $($workflow.output_folder))"
        $chain = Get-WeirJson $session "/api/v1/processing/libraries/$($workflow.id)/folder-chain"
        Assert-That ($chain.ready -eq $true) "the folder chain for '$($workflow.name)' is ready"
    }
    $script:Ctx.DelunoConnectionId = $connectionId
    "Weir $how; $($workflows.Count) workflow(s) are set up from Deluno ($(@($workflows | ForEach-Object { $_.name }) -join ', ')), linked, with their folders from Deluno and every step of the folder chain ready."
}

# --- which Weir handled a hand-off ---------------------------------------------------------------------------------------

# Deluno's suite hands films over for an hour or more, and the build under test is installed over the Weir its picker installed (the
# update scenario). A hand-off made before the build began answering was handled by the previous release, so what Weir recorded for
# it says nothing about the build under test: it is not judged here. Only hand-offs made after are. A hand-off with no creation time
# is counted with the earlier ones, never with the build's. Returns { Build; Previous; Since }.
function Split-HandoffsByWeir {
    param([Parameter(Mandatory)] $Handoffs)
    $since = Get-BuildUnderTestSince
    $build = New-Object System.Collections.Generic.List[object]
    $previous = New-Object System.Collections.Generic.List[object]
    foreach ($handoff in @($Handoffs)) {
        $made = ConvertTo-UtcTime (Get-Prop $handoff 'createdUtc')
        if ($null -ne $made -and $made -ge $since.Utc) { $build.Add($handoff) } else { $previous.Add($handoff) }
    }
    [pscustomobject]@{ Build = $build.ToArray(); Previous = $previous.ToArray(); Since = $since }
}

# The not-applicable answer for a scenario whose hand-offs were all made before the build under test took over: what it needs.
function New-NoHandoffByBuild {
    param([Parameter(Mandatory)] $Split, [Parameter(Mandatory)] [string] $Need)
    New-NotApplicable ("$($Split.Previous.Count) hand-off(s) from Deluno exist and none was made after the build under test began answering ($($Split.Since.Utc.ToString('u')); $($Split.Since.Because)), so the previous release handled them all and what Weir recorded for them says nothing about the build. " +
        "$Need Deluno's suite has to make its hand-offs while the build under test is the Weir installed: install the build over the Weir Deluno's picker installed first (Invoke-WeirScenarios.ps1 -Phases Update), then run Deluno's films, resends and outages, then the rest (-Phases WithDeluno,Fresh).")
}

# Deluno's completed film hand-offs.
function Get-CompletedFilmHandoffs {
    @(Get-DelunoHandoffs | Where-Object { $_.mediaType -eq 'movies' -and $_.status -eq 'completed' })
}

# --- deluno-film-handoff ------------------------------------------------------------------------------------------------

function Scenario_deluno_film_handoff {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    $films = @(Get-CompletedFilmHandoffs)
    if ($films.Count -eq 0) { return New-NotApplicable 'Deluno has made no completed film hand-off, so there is no film to follow through Weir. Deluno''s own suite makes them (its torrent and Usenet films) before this phase.' }
    $split = Split-HandoffsByWeir -Handoffs $films
    if ($split.Build.Count -eq 0) { return New-NoHandoffByBuild -Split $split -Need 'This scenario follows the films the build under test cleaned for Deluno.' }
    $weirFiles = @(Get-ScenarioFiles)
    $followed = 0
    foreach ($film in $split.Build) {
        $copies = @(Find-WeirFilesFor -Handoff $film -WeirFiles $weirFiles)
        Assert-That ($copies.Count -ge 1) "Weir shows the cleaned file for Deluno's hand-off of $($film.releaseName)"
        foreach ($copy in $copies) { Assert-That ($copy.status -eq 'processed') "Weir shows $($copy.relative_path) as processed (it reads $($copy.status))" }
        $outcome = Get-OutcomeOf $film
        if ($outcome -and $outcome.outcome -eq 'imported') {
            foreach ($copy in $copies) { Assert-That ((Get-Prop $copy.handback 'outcome') -eq 'imported') "Weir has Deluno's imported answer recorded against $($copy.relative_path)" }
        }
        $followed++
    }
    $imported = @($split.Build | Where-Object { $o = Get-OutcomeOf $_; $o -and $o.outcome -eq 'imported' }).Count
    Assert-That ($imported -ge 1) 'at least one film Deluno handed to the build under test was imported by Deluno'
    "Followed $followed completed film hand-off(s) Deluno made after the build under test took over ($imported imported) through Weir: each shows as processed, with Deluno's imported answer recorded against it. $($split.Previous.Count) hand-off(s) made before that, by the previous release, were not judged."
}

# --- deluno-release-with-extra ------------------------------------------------------------------------------------------

# The video files a release folder may carry as extras (samples, featurettes), by extension.
$script:ExtraExtensions = '.mkv', '.mp4', '.m4v', '.avi', '.mov', '.wmv', '.webm', '.ts', '.m2ts'

# Deluno leaves files under the workflow's minimum size out of a hand-off (the owner's decision of 10 Oct 2026) and the extra stays in the
# download client's folder, which is the hand-off's source path. So the release is found where the extra is: a completed film hand-off
# whose source folder still holds a video file under the minimum of the workflow Weir cleaned the film in.
function Scenario_deluno_release_with_extra {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    if (-not $script:Ctx.ContainsKey('DelunoConnectionId')) { $script:Ctx.DelunoConnectionId = [int](Get-WeirDelunoConnection).id }
    $fed = @((Get-WeirJson $script:Ctx.Session '/api/v1/processing/libraries') | Where-Object { @($_.manager_connection_ids) -contains $script:Ctx.DelunoConnectionId })
    if ($fed.Count -eq 0) { return New-NotApplicable 'No Weir workflow is fed by this Deluno, so there is no release to look at.' }
    $films = @(Get-CompletedFilmHandoffs)
    if ($films.Count -eq 0) { return New-NotApplicable 'Deluno has made no completed film hand-off, so there is no release to look at.' }
    $split = Split-HandoffsByWeir -Handoffs $films
    $need = 'This scenario needs a release the build under test cleaned for Deluno whose folder still holds a film and an extra under the workflow''s minimum size.'
    if ($split.Build.Count -eq 0) { return New-NoHandoffByBuild -Split $split -Need $need }
    $weirFiles = @(Get-ScenarioFiles)
    foreach ($handoff in $split.Build) {
        $folder = [string](Get-Prop $handoff 'sourcePath')
        if (-not $folder -or -not (Test-Path -LiteralPath $folder -PathType Container)) { continue }
        $copies = @(Find-WeirFilesFor -Handoff $handoff -WeirFiles $weirFiles)
        if ($copies.Count -eq 0) { continue }
        $workflow = @($fed | Where-Object { $_.id -eq $copies[0].library_id }) | Select-Object -First 1
        if (-not $workflow -or [long]$workflow.min_file_size_mb -le 0) { continue }
        $minimum = [long]$workflow.min_file_size_mb * 1MB
        $extras = @(Get-ChildItem -LiteralPath $folder -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $script:ExtraExtensions -contains $_.Extension.ToLowerInvariant() -and $_.Length -lt $minimum })
        if ($extras.Count -eq 0) { continue }

        foreach ($copy in $copies) { Assert-That ($copy.status -eq 'processed') "the film $($copy.relative_path) was cleaned (it reads $($copy.status))" }
        $output = [string]$workflow.output_folder
        foreach ($extra in $extras) {
            Assert-That (Test-Path -LiteralPath $extra.FullName -PathType Leaf) "the extra $($extra.FullName) ($($extra.Length) bytes, under the $($workflow.min_file_size_mb) MB minimum) is still in the release folder where Deluno's client left it"
            $rows = @($weirFiles | Where-Object { $_.library_id -eq $workflow.id -and (Split-Path -Leaf ([string]$_.relative_path)) -eq $extra.Name })
            $notLeftAlone = @($rows | Where-Object { $_.status -ne 'skipped' })
            Assert-That ($notLeftAlone.Count -eq 0) "Weir either never saw the extra $($extra.Name) or left it alone as skipped (it has $($rows.Count) row(s) and $($notLeftAlone.Count) of them read something else: $((@($notLeftAlone | ForEach-Object { $_.status }) -join ', ')))"
            $written = @(if ($output -and (Test-Path -LiteralPath $output)) { Get-ChildItem -LiteralPath $output -Recurse -File -Filter $extra.Name -ErrorAction SilentlyContinue })
            Assert-That ($written.Count -eq 0) "Weir wrote nothing named $($extra.Name) into the workflow's output folder ($output)"
        }
        $counts = (Get-WeirJson $script:Ctx.Session "/api/v1/processing/files?limit=1&library_id=$($workflow.id)").status_counts
        Assert-That ((Get-Prop $counts 'processing_failed') -eq 0 -and (Get-Prop $counts 'rejected') -eq 0) "nothing in '$($workflow.name)' needs the person (failed $(Get-Prop $counts 'processing_failed'), rejected $(Get-Prop $counts 'rejected'))"
        return "In '$($workflow.name)', the release at $folder holds the film $($copies[0].relative_path) (processed) and $($extras.Count) extra(s) under the $($workflow.min_file_size_mb) MB minimum ($((@($extras | ForEach-Object { $_.Name + ' ' + $_.Length + ' bytes' }) -join ', '))): they are still in the client's folder, Weir wrote none of them to its output, and it either never saw them or left them alone as skipped; nothing in the workflow needs the person."
    }
    New-NotApplicable "None of the $($split.Build.Count) completed film hand-off(s) the build under test handled has a source folder that still exists and holds a video file under its workflow's minimum size (a workflow with a minimum of 0 MB has no extras). $need Deluno's suite has to leave such a release in the client's completed folder until the scenarios have run (its films group does, and its Remove-this-copy scenarios use films of their own)."
}

# --- deluno-resend ------------------------------------------------------------------------------------------------------

function Scenario_deluno_resend {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    $weirFiles = @(Get-ScenarioFiles)
    $chosen = $null
    foreach ($handoff in Get-DelunoHandoffs) {
        $outcome = Get-OutcomeOf $handoff
        if (-not $outcome -or $outcome.outcome -ne 'imported' -or $outcome.delivery -notin 'delivered', 'settled') { continue }
        $copies = @(Find-WeirFilesFor -Handoff $handoff -WeirFiles $weirFiles | Where-Object { (Get-Prop $_.handback 'outcome') -eq 'imported' })
        if ($copies.Count -ge 1) { $chosen = [pscustomobject]@{ Handoff = $handoff; Copies = $copies }; break }
    }
    if (-not $chosen) { return New-NotApplicable 'Deluno has no imported hand-off with its outcome delivered and recorded in Weir, so there is nothing to send again.' }
    $before = @($chosen.Copies | ForEach-Object { "$($_.id)|$($_.handback.outcome)|$($_.handback.settled_at)|$($_.handback.released_at)" })
    $failuresBefore = @(Get-ActivityItems | Where-Object { $_.result -in 'failed', 'error' }).Count

    # Deluno answers with its own record of the outcome after the attempt ({ message; outcome = { outcome; delivery; attempts;
    # lastAttemptUtc; processorSaid; ... } }); delivery is how Weir took it: delivered, settled (Weir keeps the answer it has) or
    # refused, pending, never-received, given-up.
    $asked = [DateTime]::UtcNow
    $sent = Invoke-Deluno -Method POST -Path "/api/integrations/processors/handoffs/$($chosen.Handoff.id)/outcome/send-again"
    Assert-That ($sent.Status -eq 200) "Deluno sent the outcome again (answered $($sent.Status): $(Get-Prop $sent.Json 'message'))"
    $record = Get-Prop $sent.Json 'outcome'
    Assert-That ($null -ne $record) 'Deluno answered with its record of the outcome it sent'
    Assert-That ($record.outcome -eq 'imported') "the outcome Deluno sent again is imported (it reads $($record.outcome))"
    $attemptedAt = ConvertTo-UtcTime (Get-Prop $record 'lastAttemptUtc')
    Assert-That ($null -ne $attemptedAt -and $attemptedAt -ge $asked.AddSeconds(-5) -and [int]$record.attempts -ge 1) "Deluno really sent it to Weir just now (attempt $($record.attempts) at $(if ($attemptedAt) { $attemptedAt.ToString('u') } else { 'no time' }); asked at $($asked.ToString('u')))"
    Assert-That ($record.delivery -in 'delivered', 'settled') "Weir took the repeat: Deluno records it as delivered or settled, never refused (it reads $($record.delivery): $(Get-Prop $record 'processorSaid'))"
    $after = Get-DelunoHandoffs | Where-Object { $_.id -eq $chosen.Handoff.id } | Select-Object -First 1
    Assert-That ($after.outcome.delivery -in 'delivered', 'settled') "Deluno's own list reads the repeat as delivered or settled (it reads $($after.outcome.delivery))"

    $afterCopies = @(Find-WeirFilesFor -Handoff $after -WeirFiles @(Get-ScenarioFiles) | Where-Object { $chosen.Copies.id -contains $_.id })
    $afterState = @($afterCopies | ForEach-Object { "$($_.id)|$($_.handback.outcome)|$($_.handback.settled_at)|$($_.handback.released_at)" })
    Assert-That ((($before | Sort-Object) -join ';') -eq (($afterState | Sort-Object) -join ';')) 'Weir kept the one imported answer and the file it had already settled, unchanged'
    Assert-That (@(Get-ActivityItems | Where-Object { $_.result -in 'failed', 'error' }).Count -eq $failuresBefore) 'the repeat raised no failure in Weir''s Activity'
    "Deluno sent the outcome for $($chosen.Handoff.releaseName) again; Weir took it (Deluno reads the delivery as $($after.outcome.delivery), attempt $($record.attempts)), and Weir's file stayed imported and settled as it was, with no failure."
}

# --- deluno-outcomes ----------------------------------------------------------------------------------------------------

# Deluno writes a closing outcome for a hand-off that handed no file back (Weir was down when it was sent, or it failed): not-imported,
# given-up with no attempt, and its own words that there was nothing to tell Weir (Deluno#1221, #1235). It is the one given-up that is
# no failure to tell: Weir was owed nothing and never held a copy. Any other reason for giving up (a removed connection, a processor
# that does not ask to be told, a week without an answer) is not this, and is judged as the failure it is.
function Test-NothingWasOwed {
    param([Parameter(Mandatory)] $Handoff, [Parameter(Mandatory)] $Outcome, $WeirCopies = @())
    $word = [string](Get-Prop $Outcome 'outcome')
    $delivery = [string](Get-Prop $Outcome 'delivery')
    $attempts = [int](Get-Prop $Outcome 'attempts')
    $said = [string](Get-Prop $Outcome 'processorSaid')
    $handedBack = [string](Get-Prop $Handoff 'outputPath')
    ($word -eq 'not-imported') -and ($delivery -eq 'given-up') -and ($attempts -eq 0) -and (-not $handedBack.Trim()) -and
        ($said -match 'no file was handed back, so there was nothing to tell it') -and (@($WeirCopies).Count -eq 0)
}

function Scenario_deluno_outcomes {
    $unmet = Test-DelunoPreconditions
    if ($unmet) { return $unmet }
    $handoffs = @(Get-DelunoHandoffs)
    $withOutcome = @($handoffs | Where-Object { Get-OutcomeOf $_ })
    if ($withOutcome.Count -eq 0) { return New-NotApplicable 'Deluno has recorded no outcome for any hand-off yet, so there is nothing to compare.' }
    $weirFiles = @(Get-ScenarioFiles)

    # What Deluno was told is Deluno's own record, true whichever Weir took the hand-off: every outcome is delivered, or settled, or
    # was never owed.
    $owedNothing = 0
    foreach ($handoff in $withOutcome) {
        $outcome = Get-OutcomeOf $handoff
        $copies = @(Find-WeirFilesFor -Handoff $handoff -WeirFiles $weirFiles)
        if (Test-NothingWasOwed -Handoff $handoff -Outcome $outcome -WeirCopies $copies) {
            $owedNothing++
            Add-Evidence "ok: Deluno's outcome for $($handoff.releaseName) is not-imported, given-up with no attempt, because no file was handed back and Weir was owed nothing (Deluno said: $($outcome.processorSaid))"
            continue
        }
        Assert-That ($outcome.delivery -notin 'pending', 'refused', 'never-received', 'given-up') "Deluno's outcome for $($handoff.releaseName) is $($outcome.outcome), delivery $($outcome.delivery), not pending, refused, never received or given up$(if ($outcome.delivery -in 'pending', 'refused', 'never-received', 'given-up') { ' (Deluno said: ' + $outcome.processorSaid + ')' })"
    }

    # What Weir recorded is judged for the hand-offs the build under test took.
    $split = Split-HandoffsByWeir -Handoffs $withOutcome
    if ($split.Build.Count -eq 0) { return New-NoHandoffByBuild -Split $split -Need 'This scenario compares Deluno''s answer with the one the build under test recorded for each file.' }
    $compared = 0
    foreach ($handoff in $split.Build) {
        $outcome = Get-OutcomeOf $handoff
        if ($outcome.delivery -ne 'delivered') { continue }
        $copies = @(Find-WeirFilesFor -Handoff $handoff -WeirFiles $weirFiles)
        Assert-That ($copies.Count -ge 1) "Weir holds the file Deluno's outcome ($($outcome.outcome)) for $($handoff.releaseName) is about"
        foreach ($copy in $copies) {
            $weir = Get-Prop $copy.handback 'outcome'
            Assert-That ($weir -eq $outcome.outcome) "$($copy.relative_path): Deluno says $($outcome.outcome) and Weir says $(if ($weir) { $weir } else { 'nothing' })"
            $compared++
        }
    }
    Assert-That ($compared -ge 1) 'at least one outcome Deluno delivered to the build under test could be compared with Weir''s'
    "Read the outcomes of $($withOutcome.Count) hand-off(s) in Deluno: none pending, refused, never received or given up on ($owedNothing closed with nothing owed because no file was handed back); $compared of Weir's files from the $($split.Build.Count) hand-off(s) made after the build under test took over carry the same answer as Deluno. $($split.Previous.Count) made before that, by the previous release, were not compared."
}

# --- logs-clean-deluno --------------------------------------------------------------------------------------------------

function Scenario_logs_clean_deluno {
    if (-not (Initialize-WeirSession)) { return New-NotApplicable $script:Ctx.NoSessionReason }
    Test-LogsClean -Since $script:Ctx.PhaseStartUtc
}
