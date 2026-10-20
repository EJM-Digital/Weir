<#
    What the WithDeluno phase does to put Deluno into the state its scenarios need, through Deluno's public API with the key from the
    key file: a library that refines before import with Weir as its processor, and one real hand-off. A Deluno set up by hand is not a
    state a tag-gating run may depend on, because nobody recorded how it got there; this is recorded (every call is in the evidence file,
    and the scenarios say what they made).

    The library is what Deluno's own suite makes with PUT /api/libraries/{id}/workflow. The hand-off is a real download: a release folder
    (the film the scenarios already use, an .nfo and a small extra) is offered as a torrent by a small web server in this process, Deluno
    is asked to grab it, its download client fetches every byte from that server (BEP 19 web seed), and Deluno hands the finished download to
    Weir. That is the CI with-deluno shard's way (scripts/scenarios-ci/with-deluno), with the server written in PowerShell because the VM
    has no Node.

    Deluno refusing any step is a failure, never a skip. The key needs the scopes read, imports and write to set a library up.
#>

# --- Deluno refusing --------------------------------------------------------------------------------------------------------

# Throws unless Deluno accepted the call. A refusal for want of a scope says which scopes the key needs.
function Assert-DelunoAccepted {
    param([Parameter(Mandatory)] $Answer, [Parameter(Mandatory)] [string] $What)
    if ($Answer.Status -ge 200 -and $Answer.Status -lt 300) { return }
    $scopes = if ($Answer.Status -in 401, 403) { ' The key from the key file did not allow it: setting Deluno up through its API needs a key with the scopes read, imports and write.' } else { '' }
    throw "Deluno refused to $What (answered $($Answer.Status): $($Answer.Text)).$scopes"
}

# --- Weir as Deluno's processor -----------------------------------------------------------------------------------------------

# The processor connection Deluno already holds for this Weir (the one Connect Weir made), when it is switched on.
function Get-DelunoWeirProcessor {
    $answer = Invoke-Deluno -Path '/api/integrations/processors/connections'
    Assert-DelunoAccepted -Answer $answer -What 'list its processor connections'
    $weirPort = ([uri]$script:Ctx.Base).Port
    @(@($answer.Json) | Where-Object {
            $url = [string](Get-Prop $_ 'submissionUrl')
            $_ -and (Get-Prop $_ 'isEnabled') -eq $true -and (Get-Prop $_ 'provider') -eq 'generic-webhook' -and $url -and ([uri]$url).Port -eq $weirPort
        }) | Select-Object -First 1
}

# Deluno's processor connection to this Weir: the enabled one it already has, else one made here with Weir's own webhook secret (the same
# call the CI shard makes). A secret is made only when there is no connection, because making one again would stop an existing one working.
# Returns { Id; Name; How }.
function Ensure-DelunoWeirProcessor {
    $existing = Get-DelunoWeirProcessor
    if ($existing) {
        return [pscustomobject]@{ Id = [string]$existing.id; Name = [string]$existing.name; How = "used the processor connection '$($existing.name)' Deluno already had for Weir" }
    }
    $connection = Ensure-WeirDelunoConnection
    $secret = Invoke-Weir -Session $script:Ctx.Session -Method POST -Path "/api/v1/media-managers/connections/$($connection.Id)/webhook-secret" -Body @{}
    Assert-That ($secret.Status -eq 200) "Weir made a webhook secret for its connection to Deluno (answered $($secret.Status): $($secret.Text))"
    Register-Secret $secret.Json.webhook_secret
    $header = if (Get-Prop $secret.Json 'header_name') { [string]$secret.Json.header_name } else { 'X-Webhook-Secret' }
    $base = $script:Ctx.Base.TrimEnd('/')
    $made = Invoke-Deluno -Method POST -Path '/api/integrations/processors/connections' -Body ([ordered]@{
            name = 'Weir'; provider = 'generic-webhook'; submissionUrl = "$base$($secret.Json.webhook_url_path)"
            authHeaderName = $header; secret = [string]$secret.Json.webhook_secret; isEnabled = $true; healthCheckUrl = "$base/ready"
        })
    Assert-DelunoAccepted -Answer $made -What 'add Weir as a processor'
    [pscustomobject]@{ Id = [string]$made.Json.id; Name = [string]$made.Json.name; How = 'added Weir to Deluno as a processor, with a webhook secret made in Weir' }
}

# --- the library ---------------------------------------------------------------------------------------------------------------

# Where Weir hands the cleaned films back to Deluno: a folder of its own beside the run's other media.
function Get-DelunoReadyFolder {
    (New-Item -ItemType Directory -Force -Path (Join-Path $script:Ctx.MediaRoot 'deluno\Ready\Movies')).FullName
}

function Get-DelunoMovieLibraryRow {
    $answer = Invoke-Deluno -Path '/api/libraries'
    Assert-DelunoAccepted -Answer $answer -What 'list its libraries'
    $movies = @(@($answer.Json) | Where-Object { $_ -and (Get-Prop $_ 'mediaType') -eq 'movies' })
    if ($movies.Count -eq 0) { throw "The Deluno at $($script:Ctx.DelunoUrl) has no movie library to refine films with, so Weir has nothing to take films from. Deluno's own suite makes its libraries before this phase." }
    $movies | Select-Object -First 1
}

# Puts a library of Deluno's on "refine before import" with Weir as its processor: PUT /api/libraries/{id}/workflow with the processor
# connection, Weir's output folder and a failure mode of block, keeping what the library already says about the other choices. A library
# that already does so is left as it is.
# Returns { OutputPath; Changed }.
function Set-DelunoLibraryToRefine {
    param([Parameter(Mandatory)] $Library, [Parameter(Mandatory)] $Processor)
    $output = ([string](Get-Prop $Library 'processorOutputPath')).Trim()
    $already = (Get-Prop $Library 'importWorkflow') -eq 'refine-before-import' -and [string](Get-Prop $Library 'processorConnectionId') -eq $Processor.Id -and
        (Get-Prop $Library 'processorFailureMode') -eq 'block' -and $output
    if ($already) {
        Add-Evidence "Deluno's library '$($Library.name)' already refines before import with '$($Processor.Name)' as its processor, handing back to $output"
        return [pscustomobject]@{ OutputPath = $output; Changed = $false }
    }
    if (-not $output) { $output = Get-DelunoReadyFolder }
    $timeout = Get-Prop $Library 'processorTimeoutMinutes'
    $cleanup = Get-Prop $Library 'cleanupMode'
    $emptyFolders = Get-Prop $Library 'removeEmptySourceFolders'
    $answer = Invoke-Deluno -Method PUT -Path "/api/libraries/$($Library.id)/workflow" -Body ([ordered]@{
            importWorkflow = 'refine-before-import'; processorConnectionId = $Processor.Id; processorName = $Processor.Name
            processorOutputPath = $output; processorTimeoutMinutes = $(if ($timeout) { [int]$timeout } else { 2880 })
            processorFailureMode = 'block'; cleanupMode = $(if ($cleanup) { [string]$cleanup } else { 'keep-source' })
            removeEmptySourceFolders = $(if ($null -ne $emptyFolders) { [bool]$emptyFolders } else { $false })
        })
    Assert-DelunoAccepted -Answer $answer -What "set its library '$($Library.name)' to refine before import"
    [pscustomobject]@{ OutputPath = $output; Changed = $true }
}

# Deluno has a library that refines before import with Weir as its processor, set up through its API when it was not. Done once per run.
# Returns { Library; Processor; OutputPath; Summary }.
function Initialize-DelunoRefiningLibrary {
    if ($script:Ctx.ContainsKey('DelunoLibrary')) { return $script:Ctx.DelunoLibrary }
    $processor = Ensure-DelunoWeirProcessor
    $library = Get-DelunoMovieLibraryRow
    $set = Set-DelunoLibraryToRefine -Library $library -Processor $processor
    $refining = (Get-DelunoLibraries).Refining
    Assert-That (@($refining | Where-Object { (Get-Prop $_ 'name') -eq $library.name }).Count -eq 1) "Deluno's manifest lists '$($library.name)' as refining before import"
    $verb = if ($set.Changed) { "set Deluno's library '$($library.name)' to refine before import" } else { "found Deluno's library '$($library.name)' already refining before import" }
    $script:Ctx.DelunoLibrary = [pscustomobject]@{
        Library = $library; Processor = $processor; OutputPath = $set.OutputPath
        Summary = "$($processor.How); $verb, with '$($processor.Name)' as its processor, failure mode block and its cleaned films handed back to $($set.OutputPath)"
    }
    Add-Evidence $script:Ctx.DelunoLibrary.Summary
    $script:Ctx.DelunoLibrary
}

# Waits for Weir to set up its workflows from Deluno's libraries and folders, asking it to look as it does when a person presses Test.
# Returns the workflows, each linked to Deluno with its folders.
function Wait-WeirWorkflowsFromDeluno {
    $session = $script:Ctx.Session
    $connectionId = (Ensure-WeirDelunoConnection).Id
    Wait-Until -What 'Weir to set up its workflows from Deluno' -TimeoutSeconds 120 -IntervalMilliseconds 2000 -Probe {
        Invoke-Weir -Session $session -Method POST -Path "/api/v1/media-managers/connections/$connectionId/test" -Body @{} | Out-Null
        $linked = @((Get-WeirJson $session '/api/v1/processing/libraries') | Where-Object { $_.folders_synced_from_connection_id -eq $connectionId -and $_.watched_folder -and $_.output_folder })
        if ($linked.Count -ge 1) { $linked }
    }
}

# --- a torrent, written without Node ----------------------------------------------------------------------------------------

# BEP 3 bencoding of a number, a string or byte array, a list, or a dictionary with its keys in byte order, written to a stream.
function Write-Bencode {
    param([Parameter(Mandatory)] [IO.Stream] $Stream, $Value)
    $text = { param($s) $bytes = [Text.Encoding]::UTF8.GetBytes($s); $Stream.Write($bytes, 0, $bytes.Length) }
    if ($Value -is [byte[]]) {
        & $text "$($Value.Length):"
        $Stream.Write($Value, 0, $Value.Length)
    }
    elseif ($Value -is [string]) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        & $text "$($bytes.Length):"
        $Stream.Write($bytes, 0, $bytes.Length)
    }
    elseif ($Value -is [int] -or $Value -is [long]) { & $text "i${Value}e" }
    elseif ($Value -is [System.Collections.IDictionary]) {
        & $text 'd'
        $keys = [string[]]@($Value.Keys)
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        foreach ($key in $keys) {
            Write-Bencode -Stream $Stream -Value $key
            Write-Bencode -Stream $Stream -Value $Value[$key]
        }
        & $text 'e'
    }
    elseif ($Value -is [System.Collections.IEnumerable]) {
        & $text 'l'
        foreach ($item in $Value) { Write-Bencode -Stream $Stream -Value $item }
        & $text 'e'
    }
    else { throw "Cannot bencode a $($Value.GetType().FullName)." }
}

function ConvertTo-Bencode {
    param($Value)
    $stream = New-Object IO.MemoryStream
    Write-Bencode -Stream $stream -Value $Value
    , $stream.ToArray()
}

# A torrent over a folder of files, the way a tracker-less release is shared: no tracker, and a web seed that names the server the files come
# from, so a client given the torrent downloads every byte over HTTP from there. The same bytes as scripts/scenarios-ci/with-deluno/fixture/
# torrent.mjs makes for the same folder. Returns { Torrent; InfoHash; Total; Files }.
function New-ReleaseTorrent {
    param([Parameter(Mandatory)] [string] $Folder, [Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $WebSeedBase)
    $pieceBytes = 256 * 1024
    $root = (Resolve-Path -LiteralPath $Folder).Path.TrimEnd('\')
    $relative = [string[]]@(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') })
    [Array]::Sort($relative, [StringComparer]::Ordinal)
    $sha = [Security.Cryptography.SHA1]::Create()
    $pieces = New-Object IO.MemoryStream
    $buffer = New-Object byte[] $pieceBytes
    $filled = 0
    $entries = New-Object System.Collections.Generic.List[object]
    $total = [long]0
    foreach ($file in $relative) {
        $path = Join-Path $root $file.Replace('/', '\')
        $length = (Get-Item -LiteralPath $path).Length
        $entries.Add([ordered]@{ length = [long]$length; path = [string[]]($file -split '/') })
        $total += $length
        $stream = [IO.File]::OpenRead($path)
        try {
            while (($read = $stream.Read($buffer, $filled, $pieceBytes - $filled)) -gt 0) {
                $filled += $read
                if ($filled -eq $pieceBytes) {
                    $hash = $sha.ComputeHash($buffer, 0, $pieceBytes)
                    $pieces.Write($hash, 0, $hash.Length)
                    $filled = 0
                }
            }
        }
        finally { $stream.Dispose() }
    }
    if ($filled -gt 0) {
        $hash = $sha.ComputeHash($buffer, 0, $filled)
        $pieces.Write($hash, 0, $hash.Length)
    }
    $info = [ordered]@{ name = $Name; 'piece length' = [long]$pieceBytes; pieces = $pieces.ToArray(); files = $entries.ToArray() }
    $top = [ordered]@{ info = $info; 'url-list' = [string[]]@($WebSeedBase); comment = 'Weir scenario release' }
    [pscustomobject]@{
        Torrent  = (ConvertTo-Bencode $top)
        InfoHash = ([BitConverter]::ToString($sha.ComputeHash([byte[]](ConvertTo-Bencode $info))).Replace('-', '').ToLowerInvariant())
        Total    = $total
        Files    = @($entries | ForEach-Object { [pscustomobject]@{ Path = ($_.path -join '/'); Size = $_.length } })
    }
}

# --- the release source -------------------------------------------------------------------------------------------------------

# What the release source does for each request, run on a thread of its own: the torrent at /dl/<release>.torrent, and the release's bytes at
# /webseed/<release>/<file> with the ranges a download client asks for. Anything else is 404.
$script:ReleaseSourceLoop = {
    param($listener, [string] $root, [string] $name, [byte[]] $torrent)
    $send = {
        param($response, [int] $status, [string] $type, [byte[]] $bytes)
        $response.StatusCode = $status
        $response.ContentType = $type
        $response.ContentLength64 = $bytes.Length
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
    }
    while ($listener.IsListening) {
        try { $context = $listener.GetContext() } catch { break }
        $response = $context.Response
        try {
            $path = $context.Request.Url.AbsolutePath
            if ($path -eq "/dl/$name.torrent") { & $send $response 200 'application/x-bittorrent' $torrent; continue }
            $full = $null
            if ($path.StartsWith('/webseed/')) {
                $candidate = [IO.Path]::GetFullPath((Join-Path $root ([uri]::UnescapeDataString($path.Substring('/webseed/'.Length)).Replace('/', '\'))))
                if ($candidate.StartsWith($root + '\') -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { $full = $candidate }
            }
            if (-not $full) { & $send $response 404 'text/plain' ([Text.Encoding]::UTF8.GetBytes('not here')); continue }
            $size = (Get-Item -LiteralPath $full).Length
            $start = [long]0
            $end = $size - 1
            $status = 200
            $range = $context.Request.Headers['Range']
            if ($range -and $range -match '^bytes=(\d*)-(\d*)$' -and ($Matches[1] -ne '' -or $Matches[2] -ne '')) {
                if ($Matches[1] -eq '') { $start = [Math]::Max($size - [long]$Matches[2], 0) } else { $start = [long]$Matches[1] }
                if ($Matches[1] -ne '' -and $Matches[2] -ne '') { $end = [Math]::Min([long]$Matches[2], $size - 1) }
                if ($start -gt $end -or $start -ge $size) {
                    $response.AddHeader('Content-Range', "bytes */$size")
                    & $send $response 416 'text/plain' ([byte[]]@())
                    continue
                }
                $status = 206
            }
            $response.StatusCode = $status
            $response.ContentType = 'application/octet-stream'
            $response.AddHeader('Accept-Ranges', 'bytes')
            if ($status -eq 206) { $response.AddHeader('Content-Range', "bytes $start-$end/$size") }
            $response.ContentLength64 = $end - $start + 1
            if ($context.Request.HttpMethod -ne 'HEAD') {
                $file = [IO.File]::OpenRead($full)
                try {
                    [void]$file.Seek($start, [IO.SeekOrigin]::Begin)
                    $remaining = $end - $start + 1
                    $chunk = New-Object byte[] 65536
                    while ($remaining -gt 0) {
                        $read = $file.Read($chunk, 0, [int][Math]::Min($chunk.Length, $remaining))
                        if ($read -le 0) { break }
                        $response.OutputStream.Write($chunk, 0, $read)
                        $remaining -= $read
                    }
                }
                finally { $file.Dispose() }
            }
        }
        catch { }
        finally { try { $response.Close() } catch { } }
    }
}

# Serves the torrent and the files of one release from this machine until it is stopped. $Folder is the release's folder, named $Name, and
# its torrent names this server as the web seed. Returns { Origin; TorrentUrl; Torrent; Stop } where Torrent is what New-ReleaseTorrent made
# and Stop is a script block that ends the server.
function Start-ReleaseSource {
    param([Parameter(Mandatory)] [string] $Folder, [Parameter(Mandatory)] [string] $Name)
    $listener = $null
    foreach ($port in (Get-Random -InputObject (18700..18799) -Count 100)) {
        $candidate = New-Object Net.HttpListener
        $candidate.Prefixes.Add("http://localhost:$port/")
        try { $candidate.Start(); $listener = $candidate; $origin = "http://localhost:$port"; break }
        catch [Net.HttpListenerException] { $candidate.Close() }
    }
    if (-not $listener) { throw 'No port from 18700 to 18799 was free for the release source.' }
    $torrent = New-ReleaseTorrent -Folder $Folder -Name $Name -WebSeedBase "$origin/webseed/"
    $root = (Resolve-Path -LiteralPath (Split-Path -Parent $Folder)).Path.TrimEnd('\')
    $shell = [powershell]::Create()
    [void]$shell.AddScript($script:ReleaseSourceLoop.ToString()).AddArgument($listener).AddArgument($root).AddArgument($Name).AddArgument($torrent.Torrent)
    $running = $shell.BeginInvoke()
    Add-Evidence "the release source for $Name answers at $origin"
    [pscustomobject]@{
        Origin     = $origin
        TorrentUrl = "$origin/dl/$Name.torrent"
        Torrent    = $torrent
        Stop       = { $listener.Close(); try { [void]$shell.EndInvoke($running) } catch { }; $shell.Dispose() }.GetNewClosure()
    }
}

# --- a release ------------------------------------------------------------------------------------------------------------------

# A film long enough for Deluno not to take it for a sample (two minutes) and large enough to pass Weir's default minimum (50 MB): the film
# the run was given, else one made with the FFmpeg the installed Weir ships.
function Get-HandoffFilm {
    if ($script:Ctx.SourceFilm) { return $script:Ctx.SourceFilm }
    $folder = Join-Path $script:Ctx.MediaRoot 'deluno\source'
    $film = Join-Path $folder 'Hand-off film.mkv'
    if (-not (Test-Path -LiteralPath $film -PathType Leaf)) { New-ScenarioFilm -Ffmpeg (Get-ScenarioTool ffmpeg) -Path $film -Seconds 150 | Out-Null }
    $film
}

# A release folder as a download client leaves one: the film, an .nfo, and a small extra under the workflow's minimum size (Deluno leaves
# it out of the hand-off, and the release-with-extra scenario looks for it). Named for the run, so a run on a machine an earlier run used
# never meets what it left. Returns { Title; Year; Name; Root; Folder }.
function New-DelunoRelease {
    $title = "Scn$(Get-RunId)"
    $year = 2020
    $name = "$title.$year.1080p.WEB-DL.x264-WEIR"
    $root = (New-Item -ItemType Directory -Force -Path (Join-Path $script:Ctx.MediaRoot 'deluno\releases')).FullName
    $folder = (New-Item -ItemType Directory -Force -Path (Join-Path $root $name)).FullName
    Copy-Item -LiteralPath (Get-HandoffFilm) -Destination (Join-Path $folder "$name.mkv") -Force
    Set-Content -LiteralPath (Join-Path $folder "$name.nfo") -Value 'Weir scenario release' -Encoding ASCII
    $extras = (New-Item -ItemType Directory -Force -Path (Join-Path $folder 'Extras')).FullName
    [IO.File]::WriteAllBytes((Join-Path $extras 'Featurette.mkv'), (New-Object byte[] 2000000))
    [pscustomobject]@{ Title = $title; Year = $year; Name = $name; Root = $root; Folder = $folder }
}

# Deluno's hand-off for a release, as it lists it.
function Get-DelunoHandoffOf {
    param([Parameter(Mandatory)] [string] $Release)
    @(Get-DelunoHandoffs | Where-Object { [string]$_.releaseName -like "*$Release*" } | Sort-Object { [string]$_.createdUtc } -Descending) | Select-Object -First 1
}

# Makes one real film hand-off: Deluno is asked to grab the release, its download client downloads it from this machine, Deluno hands the
# finished download to Weir, Weir cleans the film and Deluno imports it. Returns when Deluno has imported it and Weir holds Deluno's answer.
# Returns { Release; Handoff; Copies }.
function New-DelunoFilmHandoff {
    $setup = Initialize-DelunoRefiningLibrary
    Wait-WeirWorkflowsFromDeluno | Out-Null
    $release = New-DelunoRelease
    $source = Start-ReleaseSource -Folder $release.Folder -Name $release.Name
    try {
        $made = $source.Torrent
        $movie =Invoke-Deluno -Method POST -Path '/api/movies' -Body ([ordered]@{ title = $release.Title; releaseYear = $release.Year; imdbId = $null; monitored = $true })
        Assert-DelunoAccepted -Answer $movie -What "add the film $($release.Title)"
        $grab = Invoke-Deluno -Method POST -Path "/api/movies/$($movie.Json.id)/grab" -Body ([ordered]@{
                releaseName = $release.Name; indexerId = $null; indexerName = 'Weir scenarios'; candidateQuality = 'WEB 1080p'
                downloadUrl = $source.TorrentUrl; sizeBytes = $made.Total; seeders = 50; force = $true; overrideReason = 'Weir scenarios'; duplicateAction = $null
            })
        Assert-DelunoAccepted -Answer $grab -What "grab the release $($release.Name)"
        Add-Evidence "Deluno was asked to grab $($release.Name) ($($made.Total) bytes in $(@($made.Files).Count) files, info hash $($made.InfoHash)) from $($source.TorrentUrl)"
        $handoff = Wait-Until -What "Deluno to hand $($release.Name) to Weir" -TimeoutSeconds 900 -IntervalMilliseconds 3000 -Probe {
            $found = Get-DelunoHandoffOf -Release $release.Name
            if ($found -and $found.status -in 'completed', 'failed') { $found }
        }
    }
    finally { & $source.Stop }
    Assert-That ($handoff.status -eq 'completed') "Deluno's hand-off of $($release.Name) completed (it reads $($handoff.status): $(Get-Prop $handoff 'failureMessage'))"
    Wait-Until -What "Weir to show the cleaned file for Deluno's hand-off of $($release.Name)" -TimeoutSeconds 180 -IntervalMilliseconds 2000 -Probe {
        $found = @(Find-WeirFilesFor -Handoff (Get-DelunoHandoffOf -Release $release.Name) -WeirFiles @(Get-ScenarioFiles))
        if ($found.Count -ge 1) { $found }
    } | Out-Null
    Wait-Until -What "Deluno to show its own import of $($release.Name) done" -TimeoutSeconds 180 -IntervalMilliseconds 2000 -Probe {
        $outcome = Get-OutcomeOf (Get-DelunoHandoffOf -Release $release.Name)
        if ($outcome -and $outcome.outcome -eq 'imported') { $true }
    } | Out-Null
    $copies = Wait-Until -What 'Weir to have Deluno''s imported answer recorded against the cleaned file' -TimeoutSeconds 180 -IntervalMilliseconds 2000 -Probe {
        $found = @(Find-WeirFilesFor -Handoff (Get-DelunoHandoffOf -Release $release.Name) -WeirFiles @(Get-ScenarioFiles))
        if ($found.Count -ge 1 -and @($found | Where-Object { (Get-Prop $_.handback 'outcome') -ne 'imported' }).Count -eq 0) { $found }
    }
    $summary = "made the hand-off of $($release.Name) in Deluno's library '$($setup.Library.name)': Deluno grabbed it, its download client fetched $($made.Total) bytes from this machine, Deluno handed it to Weir and imported what came back"
    Add-Evidence $summary
    $script:Ctx.DelunoHandoffMade = $summary
    [pscustomobject]@{ Release = $release; Handoff = $handoff; Copies = $copies }
}

# Deluno's completed film hand-offs made after the build under test began answering. With none, this run makes one, so no run depends on a
# hand-off somebody made by hand. Returns what Split-HandoffsByWeir returns.
function Get-OrMakeBuildFilmHandoffs {
    $split = Split-HandoffsByWeir -Handoffs @(Get-CompletedFilmHandoffs)
    if ($split.Build.Count -gt 0) { return $split }
    New-DelunoFilmHandoff | Out-Null
    Split-HandoffsByWeir -Handoffs @(Get-CompletedFilmHandoffs)
}

# What a scenario's result says about what the run itself made in Deluno, or nothing when it made nothing.
function Get-DelunoMadeNote {
    $parts = @()
    if ($script:Ctx.ContainsKey('DelunoLibrary')) { $parts += $script:Ctx.DelunoLibrary.Summary }
    if ($script:Ctx.ContainsKey('DelunoHandoffMade')) { $parts += $script:Ctx.DelunoHandoffMade }
    if ($parts.Count -eq 0) { '' } else { ' In Deluno this run ' + ($parts -join '; and ') + '.' }
}
