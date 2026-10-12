// node --test scripts/scenarios.test.mjs
// Weir's VM scenario suite (scripts/scenarios, docs/release.md "Proof before tagging"): every PowerShell file parses in
// Windows PowerShell 5.1, every scenario in the catalog has code and is described in the docs, and the plan modes list the
// scenarios without touching a machine. Windows PowerShell exists on Windows only, so elsewhere these tests are skipped.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const scenarios = join(repoRoot, "scripts", "scenarios");
const hasWindowsPowerShell = process.platform === "win32" && spawnSync("powershell.exe", ["-NoProfile", "-Command", "exit 0"]).status === 0;
const skip = hasWindowsPowerShell ? false : "Windows PowerShell is not available";

function powershell(args) {
  const run = spawnSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", ...args], { encoding: "utf8" });
  return { status: run.status, out: run.stdout, err: run.stderr };
}

const plan = () => {
  const run = powershell(["-File", join(scenarios, "Run-WeirScenarios.ps1"), "-Plan", "-Json"]);
  assert.equal(run.status, 0, run.err || run.out);
  return JSON.parse(run.out);
};

test("every scenario file parses in Windows PowerShell 5.1", { skip }, () => {
  const files = readdirSync(scenarios).filter((name) => name.endsWith(".ps1"));
  assert.ok(files.length >= 8, "the suite's scripts are there");
  const script = [
    "$bad = 0",
    `foreach ($f in Get-ChildItem -LiteralPath '${scenarios}' -Filter *.ps1) {`,
    "  $t = $null; $e = $null",
    "  [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$t, [ref]$e)",
    "  foreach ($x in $e) { $bad++; Write-Output ('{0}:{1}: {2}' -f $f.Name, $x.Extent.StartLineNumber, $x.Message) }",
    "}",
    "exit $bad",
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.equal(run.status, 0, run.out + run.err);
});

test("every scenario in the catalog has code, a phase, and words for what it does and what passes", { skip }, () => {
  const entries = plan();
  assert.ok(entries.length >= 12);
  assert.equal(new Set(entries.map((entry) => entry.Id)).size, entries.length, "scenario ids are unique");
  for (const entry of entries) {
    assert.ok(entry.Implemented, `${entry.Id} has no Scenario_ function`);
    assert.ok(["Update", "WithDeluno", "Fresh"].includes(entry.Phase), `${entry.Id} has a phase`);
    assert.ok(entry.Does.length > 40 && entry.Passes.length > 40, `${entry.Id} says what it does and what passes`);
  }
  assert.ok(entries.every((entry) => entry.Required), "every scenario is required: none may be optional");
  const order = entries.map((entry) => entry.Phase);
  assert.equal(order.indexOf("WithDeluno"), order.lastIndexOf("Update") + 1, "the Update scenarios come first, then the WithDeluno ones");
  assert.equal(order.indexOf("Fresh"), order.lastIndexOf("WithDeluno") + 1, "the WithDeluno scenarios come before the Fresh ones");
  assert.equal(order[0], "Update", "the update over the picker's Weir is the first scenario");
});

test("the scenarios the issue asks for are all there", { skip }, () => {
  const ids = plan().map((entry) => entry.Id);
  for (const wanted of [
    "update-over-installed-weir", "workflows-from-deluno", "deluno-film-handoff", "deluno-release-with-extra", "deluno-resend",
    "deluno-outcomes", "logs-clean-deluno", "fresh-install", "first-visit-account", "weir-only-film", "process-again",
    "pause-resume", "deleted-queued-file", "update-requests", "logs-clean",
  ]) {
    assert.ok(ids.includes(wanted), `missing scenario ${wanted}`);
  }
});

test("docs/release.md names every scenario by its title", { skip }, () => {
  const docs = readFileSync(join(repoRoot, "docs", "release.md"), "utf8");
  for (const entry of plan()) assert.ok(docs.includes(entry.Title), `docs/release.md does not name "${entry.Title}"`);
});

test("-Plan prints the plan and touches nothing", { skip }, () => {
  const run = powershell(["-File", join(scenarios, "Run-WeirScenarios.ps1"), "-Plan"]);
  assert.equal(run.status, 0, run.err);
  assert.match(run.out, /Fresh install/);
  assert.match(run.out, /An update over the Weir Deluno installed/);
});

test("no secret is a parameter: the Deluno step takes a URL only, defaulting to the Deluno inside the VM", { skip }, () => {
  for (const name of ["Invoke-WeirScenarios.ps1", "Run-WeirScenarios.ps1"]) {
    const source = readFileSync(join(scenarios, name), "utf8");
    assert.doesNotMatch(source, /\$DelunoApiKey/, `${name} takes a Deluno API key`);
    assert.match(source, /\[string\] \$DelunoUrl = 'http:\/\/127\.0\.0\.1:7879'/, `${name} does not default -DelunoUrl to the Deluno in the VM`);
  }
  const run = powershell(["-Command", `(Get-Command '${join(scenarios, "Invoke-WeirScenarios.ps1")}').Parameters.Keys -join ','`]);
  assert.equal(run.status, 0, run.err);
  assert.doesNotMatch(run.out, /DelunoApiKey|Password|Secret/i);
});

test("a secret the run holds is masked wherever it would be written", { skip }, () => {
  const script = [
    `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
    "Register-Secret 'throwaway-secret-123'",
    "Protect-Secrets 'key throwaway-secret-123 and deluno_AbC123xyz_-Q9 and plain text'",
    "Protect-Secrets '{\"connection_id\":1,\"webhook_secret\":\"abcDEF123_-xyz\",\"header_name\":\"X-Webhook-Secret\"}'",
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.deepEqual(run.out.trim().split(/\r?\n/), [
    "key **** and **** and plain text",
    '{"connection_id":1,"webhook_secret":"****","header_name":"X-Webhook-Secret"}',
  ]);
});

test("-WhatIf lists the run and the scenarios, and makes no folder or file", { skip }, () => {
  const out = mkdtempSync(join(tmpdir(), "weir-scenarios-whatif-"));
  const run = powershell([
    "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
    "-RigHost", "no-such-host", "-InstallerPath", join(out, "missing", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
    "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-OutDirectory", join(out, "artifacts"), "-WhatIf",
  ]);
  assert.equal(run.status, 0, run.err || run.out);
  assert.match(run.out, /restore checkpoint 'clean'/);
  assert.match(run.out, /set the golden-path status on 0123456789abcdef0123456789abcdef01234567/);
  assert.match(run.out, /Pause and resume from the tray/);
  assert.match(run.out, /An update over the Weir Deluno installed/);
  assert.equal(existsSync(join(out, "artifacts")), false, "-WhatIf made the output folder");
});

const whatIf = (...extra) => {
  const out = mkdtempSync(join(tmpdir(), "weir-scenarios-order-"));
  const run = powershell([
    "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
    "-RigHost", "no-such-host", "-InstallerPath", join(out, "x", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
    "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-OutDirectory", join(out, "artifacts"), "-WhatIf", ...extra,
  ]);
  assert.equal(run.status, 0, run.err || run.out);
  return run.out;
};

test("-WhatIf shows the order: Update first, then WithDeluno, both with no restore, then Fresh on the clean checkpoint, and no success without all three", { skip }, () => {
  const out = whatIf();
  const update = out.indexOf("Phase Update (first, NO restore)");
  const withDeluno = out.indexOf("Phase WithDeluno (next, NO restore)");
  const fresh = out.indexOf("Phase Fresh (last)");
  assert.ok(update >= 0 && withDeluno > update && fresh > withDeluno, "Update, then WithDeluno, then Fresh");
  assert.match(out, /restore nothing/);
  assert.match(out, /C:\\golden\\deluno-weir-scenario-key\.txt/);
  assert.equal(out.indexOf("restore checkpoint 'clean'") > fresh, true, "the checkpoint is restored only in the Fresh phase");
  assert.match(out, /success only if ALL THREE phases passed every scenario/);
  assert.match(out, /pending if none failed but some were not applicable/);
  assert.match(out, /Deluno's suite must make its hand-offs after this phase/);
});

test("-Phases picks the phases one invocation runs, always in order, and says the others are left to another", { skip }, () => {
  const update = whatIf("-Phases", "Update");
  assert.match(update, /Phases in this invocation: Update \(the others are left to another invocation/);
  assert.doesNotMatch(update, /Phase WithDeluno \(next/);
  assert.doesNotMatch(update, /Phase Fresh \(last/);
  assert.doesNotMatch(update, /restore checkpoint 'clean'/, "an Update-only invocation restores nothing");
  const rest = whatIf("-Phases", "Fresh,WithDeluno");
  assert.match(rest, /Phases in this invocation: WithDeluno, Fresh /);
  assert.ok(rest.indexOf("Phase WithDeluno (next") < rest.indexOf("Phase Fresh (last"), "the order does not depend on how they are named");
  assert.doesNotMatch(rest, /Phase Update \(first/);
  const all = whatIf();
  assert.match(all, /Phases in this invocation: Update, WithDeluno, Fresh$/m);
});

test("the Deluno key file is read inside the machine and deleted, and a bad one is refused without showing what it held", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-keyfile-"));
  const good = join(folder, "good.txt");
  const bad = join(folder, "bad.txt");
  writeFileSync(good, "deluno_TestKey0123456789abcdef\n");
  writeFileSync(bad, "not-a-key-line-one\nsecret-looking-line-two\n");
  const script = [
    `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Context.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Deluno.ps1")}'`,
    `$script:Ctx.DelunoKeyFile = '${good}'`,
    "'first=' + (Read-DelunoKeyFile)",
    `'goodGone=' + (-not (Test-Path '${good}'))`,
    "'second=' + [bool](Read-DelunoKeyFile)",
    `$script:Ctx.DelunoKeyFile = '${bad}'`,
    "try { Read-DelunoKeyFile | Out-Null; 'bad=accepted' } catch { 'bad=' + (Protect-Secrets $_.Exception.Message) }",
    `'badGone=' + (-not (Test-Path '${bad}'))`,
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.equal(run.status, 0, run.err);
  const lines = run.out.trim().split(/\r?\n/);
  assert.equal(lines[0], "first=deluno_TestKey0123456789abcdef");
  assert.equal(lines[1], "goodGone=True");
  assert.equal(lines[2], "second=False");
  assert.match(lines[3], /^bad=The key file .* did not hold one line with a Deluno API key/);
  assert.doesNotMatch(lines[3], /secret-looking-line-two|not-a-key-line-one/);
  assert.equal(lines[4], "badGone=True");
});

test("with no Deluno answering and no key file, every Deluno scenario is not-applicable with the reason, never a substitute", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-nodeluno-"));
  const script = [
    `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Context.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Deluno.ps1")}'`,
    "$script:Ctx.DelunoUrl = 'http://127.0.0.1:1'",
    `$script:Ctx.DelunoKeyFile = '${join(folder, "missing-key.txt")}'`,
    "foreach ($id in 'workflows_from_deluno', 'deluno_film_handoff', 'deluno_release_with_extra', 'deluno_resend', 'deluno_outcomes') { $r = & (\"Scenario_$id\"); $id + '|' + $r.NotApplicable + '|' + $r.Detail }",
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.equal(run.status, 0, run.err);
  const lines = run.out.trim().split(/\r?\n/);
  assert.equal(lines.length, 5);
  for (const line of lines) {
    assert.match(line, /\|True\|No Deluno answers at http:\/\/127\.0\.0\.1:1 .*no key file was left in the VM/);
  }
});

// --- what the rc.14 first run got wrong, each pinned with the shape it was found in -------------------------------------------

// Runs PowerShell source from a file (long scripts, and nothing in them is treated as a command-line argument).
function psFile(source) {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-ps-"));
  const file = join(folder, "test.ps1");
  writeFileSync(file, source);
  return powershell(["-File", file]);
}

// The shared helpers and the Deluno scenarios, dot-sourced, with $script:Ctx pointing at a run folder in a temporary place.
function harness(body) {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-harness-"));
  const run = join(folder, "run");
  const lines = [
    "$ErrorActionPreference = 'Stop'",
    `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Context.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Deluno.ps1")}'`,
    "New-Item -ItemType Directory -Force -Path '" + run + "' | Out-Null",
    "$script:Ctx.RunFolder = '" + run + "'",
    "$script:Ctx.Version = '1.0.0-rc.99'",
    "$script:Ctx.ShortSha = 'abcdef0'",
    "$script:Ctx.Session = 'a session'",
    "$script:Ctx.DelunoUrl = 'http://127.0.0.1:7879'",
    "$script:Ctx.DelunoConnectionId = 1",
    body,
  ];
  return { source: lines.join("\n"), folder };
}

test("Activity is compared across an update entry by entry, paged, so entries sliding out of a newest-100 list are not taken for lost ones", { skip }, () => {
  const { source } = harness(String.raw`
function New-Row($id, $title) { [pscustomobject]@{ id = $id; created_at = '2026-10-11T19:51:44'; event_type = 'processing.x'; module = 'processing'; title = $title; detail = 'd'; trigger = 'manual'; result = 'success'; library_id = $null; relative_path = $null; poster_url = $null; run_key = $null } }
$script:All = @(289..90 | ForEach-Object { New-Row $_ ('entry ' + $_) })
$script:Paths = New-Object System.Collections.Generic.List[string]
function Get-WeirJson {
    param($Session, $Path)
    $script:Paths.Add($Path)
    $beforeId = $null
    if ($Path -match 'before_id=(\d+)') { $beforeId = [int]$Matches[1] }
    $matching = @($script:All | Where-Object { $null -eq $beforeId -or $_.id -lt $beforeId })
    [pscustomobject]@{ items = @($matching | Select-Object -First 100); has_more = ($matching.Count -gt 100) }
}
$before = @(Get-AllActivityItems)
'before=' + $before.Count + ' first=' + $before[0].id + ' last=' + $before[$before.Count - 1].id + ' requests=' + $script:Paths.Count
'paths=' + ($script:Paths -join ' ')
# The update adds three entries at the top; the newest 100 of what is there now no longer reach back to the old ones.
$script:All = @(292..290 | ForEach-Object { New-Row $_ ('new ' + $_) }) + $script:All
$after = @(Get-AllActivityItems)
'after=' + $after.Count
'untouched=' + @(Find-ActivityLostOrChanged -Before $before -After $after).Count
$newest100 = @($after | Select-Object -First 100)
'window=' + @(Find-ActivityLostOrChanged -Before $before -After $newest100).Count
# A real loss and a real change are found.
$lossy = @($after | Where-Object { $_.id -ne 150 })
$lossy = @($lossy | ForEach-Object { if ($_.id -eq 120) { $copy = $_.PSObject.Copy(); $copy.title = 'rewritten'; $copy } else { $_ } })
foreach ($finding in Find-ActivityLostOrChanged -Before $before -After $lossy) { 'finding=' + $finding.Id + ' ' + $finding.Problem }
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const out = run.out.trim().split(/\r?\n/);
  assert.equal(out[0], "before=200 first=289 last=90 requests=2");
  assert.match(out[1], /limit=100&with_total=false .*limit=100&with_total=false&before_id=190$/);
  assert.equal(out[2], "after=203");
  assert.equal(out[3], "untouched=0");
  assert.equal(out[4], "window=103", "the newest 100 after the update no longer reach back to 103 of the 200 entries from before: the window rc.14's first run compared");
  assert.deepEqual(out.slice(5), ["finding=150 is gone", "finding=120 title was 'entry 120' and now reads 'rewritten'"]);
});

test("a closing outcome for a hand-off that handed no file back is the one given-up that is nothing owed; any other given-up is a failure", { skip }, () => {
  const { source } = harness(String.raw`
function Case($label, $handoff, $outcome, $copies = @()) { $label + '=' + (Test-NothingWasOwed -Handoff $handoff -Outcome $outcome -WeirCopies $copies) }
# The shape Deluno recorded for the problem film on 11 Oct 2026 (Weir was down when it was sent).
$said = 'Gpproblem.2020.1080p.WEB-DL.x264-GOLDEN.mkv''s hand-off to Weir on DELUNO-CLEAN ended as failed and no file was handed back, so there was nothing to tell it and nothing for it to release.'
$handoff = [pscustomobject]@{ status = 'dismissed'; outputPath = $null }
$closed = [pscustomobject]@{ outcome = 'not-imported'; delivery = 'given-up'; attempts = 0; processorSaid = $said }
Case 'nothing-owed' $handoff $closed
Case 'a-file-was-handed-back' ([pscustomobject]@{ status = 'dismissed'; outputPath = 'C:\Weir\Ready\Movies\x.mkv' }) $closed
Case 'weir-holds-a-copy' $handoff $closed @([pscustomobject]@{ id = 1 })
Case 'a-week-without-an-answer' $handoff ([pscustomobject]@{ outcome = 'not-imported'; delivery = 'given-up'; attempts = 40; processorSaid = 'Nobody acknowledged it in 7 days, so Deluno stopped trying.' })
Case 'connection-removed' $handoff ([pscustomobject]@{ outcome = 'not-imported'; delivery = 'given-up'; attempts = 0; processorSaid = 'The connection to Weir on DELUNO-CLEAN was removed, so there is nobody left to tell.' })
Case 'imported-then-given-up' $handoff ([pscustomobject]@{ outcome = 'imported'; delivery = 'given-up'; attempts = 0; processorSaid = $said })
Case 'refused' $handoff ([pscustomobject]@{ outcome = 'not-imported'; delivery = 'refused'; attempts = 0; processorSaid = $said })
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  assert.deepEqual(run.out.trim().split(/\r?\n/), [
    "nothing-owed=True", "a-file-was-handed-back=False", "weir-holds-a-copy=False", "a-week-without-an-answer=False",
    "connection-removed=False", "imported-then-given-up=False", "refused=False",
  ]);
});

test("times from Deluno are read as the instant they name, whether PowerShell gives them as text or as a date", { skip }, () => {
  const { source } = harness(String.raw`
(ConvertTo-UtcTime '2026-10-11T19:23:33.2365561+00:00').ToString('o')
(ConvertTo-UtcTime '2026-10-11T21:23:33+02:00').ToString('o')
(ConvertTo-UtcTime ([datetime]::new(2026, 10, 11, 19, 23, 33, [DateTimeKind]::Utc))).ToString('o')
[string]($null -eq (ConvertTo-UtcTime $null))
[string]($null -eq (ConvertTo-UtcTime ''))
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  assert.deepEqual(run.out.trim().split(/\r?\n/), ["2026-10-11T19:23:33.2365561Z", "2026-10-11T19:23:33.0000000Z", "2026-10-11T19:23:33.0000000Z", "True", "True"]);
});

// The hand-off and file shapes the Deluno scenarios read, as Deluno's API and Weir's list give them.
const delunoFixtures = String.raw`
$script:Marker = [datetime]::new(2026, 10, 11, 20, 0, 0, [DateTimeKind]::Utc)
(@{ version = '1.0.0-rc.99'; short_sha = 'abcdef0'; reported = '1.0.0-rc.99+abcdef0'; began_answering_ticks = $script:Marker.Ticks } | ConvertTo-Json -Compress) | Set-Content -LiteralPath (Get-BuildMarkerPath)
function New-Handoff($name, $created, $outcome = 'imported', $delivery = 'delivered', $source = $null) {
    [pscustomobject]@{
        id = 'id-' + $name; releaseName = $name; mediaType = 'movies'; status = 'completed'; createdUtc = $created; sourcePath = $source
        outputPath = 'C:\Weir\Ready\Movies\' + $name + '\' + $name + '.mkv'
        outcome = [pscustomobject]@{ outcome = $outcome; delivery = $delivery; attempts = 1; processorSaid = ''; lastAttemptUtc = ([DateTime]::UtcNow.AddSeconds(2).ToString('o')) }
    }
}
function New-WeirFile($name, $word = 'imported', $status = 'processed', $library = 1) {
    [pscustomobject]@{
        id = [Math]::Abs($name.GetHashCode()); library_id = $library; relative_path = $name + '/' + $name + '.mkv'; status = $status; size_bytes = 5000000
        handback = [pscustomobject]@{ output_path = 'C:\Weir\Ready\Movies\' + $name + '\' + $name + '.mkv'; outcome = $word; settled_at = '2026-10-11T21:00:00'; released_at = '2026-10-11T21:00:00' }
    }
}
function Test-DelunoPreconditions { $null }
function Get-DelunoHandoffs { $script:Handoffs }
function Get-ScenarioFiles { param([int] $LibraryId) $script:Files }
function Get-ActivityItems { @() }
function Show($scenario) {
    try { $r = & $scenario; if ($r.NotApplicable) { 'NA: ' + $r.Detail } else { 'PASSED: ' + $r } } catch { 'FAILED: ' + $_.Exception.Message }
}
`;

test("a film Deluno handed over is judged only when the build under test took it: one the previous release handled is counted, never judged", { skip }, () => {
  const { source } = harness(delunoFixtures + String.raw`
$before = '2026-10-11T19:36:00+00:00'; $after = '2026-10-11T20:30:00+00:00'
# Gpreduring...: the previous release never recorded Deluno's answer (rc.14's first run); it is not the build's to answer for.
$script:Handoffs = @(New-Handoff 'Gpreduringbndf23' $before)
$script:Files = @(New-WeirFile 'Gpreduringbndf23' $null)
'only-previous: ' + (Show { Scenario_deluno_film_handoff })
# The same hand-off made after the build took over, with Weir having recorded nothing: that is the build's fault.
$script:Handoffs = @(New-Handoff 'Gpreduringbndf23' $after)
'build-no-answer: ' + (Show { Scenario_deluno_film_handoff })
$script:Files = @(New-WeirFile 'Gpreduringbndf23' 'imported')
'build-answered: ' + (Show { Scenario_deluno_film_handoff })
$script:Files = @(New-WeirFile 'Gpreduringbndf23' 'imported' 'processing_failed')
'build-not-processed: ' + (Show { Scenario_deluno_film_handoff })
# Both kinds together: the previous release's is not judged and the build's is.
$script:Handoffs = @((New-Handoff 'Gpold' $before), (New-Handoff 'Gpnew' $after))
$script:Files = @((New-WeirFile 'Gpold' $null), (New-WeirFile 'Gpnew' 'imported'))
'mixed: ' + (Show { Scenario_deluno_film_handoff })
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.match(lines[0], /^only-previous: NA: 1 hand-off\(s\) from Deluno exist and none was made after the build under test began answering \(2026-10-11 20:00:00Z; the update scenario saw it begin answering\).* -Phases Update/);
  assert.match(lines[1], /^build-no-answer: FAILED: Weir has Deluno's imported answer recorded against Gpreduringbndf23\/Gpreduringbndf23\.mkv$/);
  assert.match(lines[2], /^build-answered: PASSED: Followed 1 completed film hand-off\(s\) .* 0 hand-off\(s\) made before that/);
  assert.match(lines[3], /^build-not-processed: FAILED: Weir shows Gpreduringbndf23\/Gpreduringbndf23\.mkv as processed \(it reads processing_failed\)$/);
  assert.match(lines[4], /^mixed: PASSED: Followed 1 completed film hand-off\(s\) .* 1 hand-off\(s\) made before that, by the previous release, were not judged\.$/);
});

test("Deluno's outcomes: the problem film closed with nothing owed passes, any other given-up fails, and the build's hand-offs must agree with Deluno", { skip }, () => {
  const { source } = harness(delunoFixtures + String.raw`
$after = '2026-10-11T20:30:00+00:00'
$problem = New-Handoff 'Gpproblem' $after 'not-imported' 'given-up'
$problem.outputPath = $null
$problem.outcome.attempts = 0
$problem.outcome.processorSaid = 'Gpproblem hand-off to Weir ended as failed and no file was handed back, so there was nothing to tell it and nothing for it to release.'
$good = New-Handoff 'Gpgood' $after
$script:Files = @((New-WeirFile 'Gpgood' 'imported'))
$script:Handoffs = @($problem, $good)
'nothing-owed: ' + (Show { Scenario_deluno_outcomes })
$problem.outcome.attempts = 40
$problem.outcome.processorSaid = 'Nobody acknowledged it in 7 days, so Deluno stopped trying.'
'a-week: ' + (Show { Scenario_deluno_outcomes })
$problem.outcome.attempts = 0; $problem.outcome.processorSaid = 'ended as failed and no file was handed back, so there was nothing to tell it'
$script:Files = @((New-WeirFile 'Gpgood' $null))
'weir-has-nothing: ' + (Show { Scenario_deluno_outcomes })
$script:Files = @((New-WeirFile 'Gpgood' 'not-imported'))
'weir-disagrees: ' + (Show { Scenario_deluno_outcomes })
$problem.createdUtc = '2026-10-11T19:10:00+00:00'
$script:Handoffs = @($problem, (New-Handoff 'Gpearly' '2026-10-11T19:00:00+00:00'))
$script:Files = @((New-WeirFile 'Gpearly' $null))
'previous-only: ' + (Show { Scenario_deluno_outcomes })
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.match(lines[0], /^nothing-owed: PASSED: Read the outcomes of 2 hand-off\(s\) in Deluno: none pending, refused, never received or given up on \(1 closed with nothing owed because no file was handed back\); 1 of Weir's files/);
  assert.match(lines[1], /^a-week: FAILED: Deluno's outcome for Gpproblem is not-imported, delivery given-up, not pending, refused, never received or given up \(Deluno said: Nobody acknowledged/);
  assert.match(lines[2], /^weir-has-nothing: FAILED: Gpgood\/Gpgood\.mkv: Deluno says imported and Weir says nothing$/);
  assert.match(lines[3], /^weir-disagrees: FAILED: Gpgood\/Gpgood\.mkv: Deluno says imported and Weir says not-imported$/);
  assert.match(lines[4], /^previous-only: NA: 2 hand-off\(s\) from Deluno exist and none was made after the build under test began answering/);
});

test("the resend scenario reads Deluno's own record of the attempt: a real new attempt, delivered or settled, never refused", { skip }, () => {
  const { source } = harness(delunoFixtures + String.raw`
$script:Handoffs = @(New-Handoff 'Gpretry' '2026-10-11T19:00:00+00:00')
$script:Files = @(New-WeirFile 'Gpretry' 'imported')
$script:Delivery = 'delivered'; $script:Attempts = 1; $script:LastAttempt = [DateTime]::UtcNow.AddSeconds(1).ToString('o')
function Invoke-Deluno {
    param([string] $Method = 'GET', [string] $Path, $Body)
    # The answer Deluno's send-again gives: { message; outcome = the record after the attempt }.
    [pscustomobject]@{ Status = 200; Text = ''; Json = [pscustomobject]@{ message = 'Deluno told Weir on DELUNO-CLEAN it has Gpretry. Weir recorded that Deluno imported the file.'
        outcome = [pscustomobject]@{ handoffId = 'id-Gpretry'; outcome = 'imported'; delivery = $script:Delivery; attempts = $script:Attempts; lastAttemptUtc = $script:LastAttempt; processorSaid = 'Weir recorded that Deluno imported the file.' } } }
}
'delivered: ' + (Show { Scenario_deluno_resend })
$script:Delivery = 'settled'
'settled: ' + (Show { Scenario_deluno_resend })
$script:Delivery = 'refused'
'refused: ' + (Show { Scenario_deluno_resend })
$script:Delivery = 'delivered'; $script:LastAttempt = [DateTime]::UtcNow.AddMinutes(-30).ToString('o')
'not-sent-now: ' + (Show { Scenario_deluno_resend })
$script:LastAttempt = [DateTime]::UtcNow.AddSeconds(1).ToString('o'); $script:Handoffs[0].outcome.delivery = 'delivered'
$script:Files = @(New-WeirFile 'Gpretry' $null)
'weir-never-recorded-it: ' + (Show { Scenario_deluno_resend })
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.match(lines[0], /^delivered: PASSED: Deluno sent the outcome for Gpretry again; Weir took it \(Deluno reads the delivery as delivered, attempt 1\)/);
  assert.match(lines[1], /^settled: PASSED: /);
  assert.match(lines[2], /^refused: FAILED: Weir took the repeat: Deluno records it as delivered or settled, never refused \(it reads refused: Weir recorded that Deluno imported the file\.\)$/);
  assert.match(lines[3], /^not-sent-now: FAILED: Deluno really sent it to Weir just now/);
  assert.match(lines[4], /^weir-never-recorded-it: NA: Deluno has no imported hand-off with its outcome delivered and recorded in Weir/);
});

test("the release with a small extra is read where the extra stays: the client's folder, left alone by Weir, and a failed extra or a copied one fails it", { skip }, () => {
  const { source, folder } = harness(delunoFixtures);
  const release = join(folder, "client", "Gpfilm.2020.1080p");
  const body = String.raw`
$release = '` + release + String.raw`'
New-Item -ItemType Directory -Force -Path (Join-Path $release 'Featurettes') | Out-Null
[IO.File]::WriteAllBytes((Join-Path $release 'Gpfilm.2020.1080p.mkv'), (New-Object byte[] 3000000))
[IO.File]::WriteAllBytes((Join-Path $release 'Featurettes\featurette.mkv'), (New-Object byte[] 700000))
$output = Join-Path (Split-Path -Parent (Split-Path -Parent $release)) 'weir-output'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$workflow = [pscustomobject]@{ id = 1; name = 'Movies'; min_file_size_mb = 1; output_folder = $output; manager_connection_ids = @(1) }
function Get-WeirDelunoConnection { [pscustomobject]@{ id = 1 } }
function Get-WeirJson {
    param($Session, $Path)
    if ($Path -like '*/libraries') { return @($workflow) }
    [pscustomobject]@{ status_counts = [pscustomobject]@{ processing_failed = $script:Failed; rejected = 0 } }
}
$script:Failed = 0
$film = New-WeirFile 'Gpfilm.2020.1080p'
$script:Handoffs = @(New-Handoff 'Gpfilm.2020.1080p' '2026-10-11T20:30:00+00:00' 'imported' 'delivered' $release)
$script:Files = @($film)
'extra-never-seen: ' + (Show { Scenario_deluno_release_with_extra })
$script:Files = @($film, [pscustomobject]@{ id = 99; library_id = 1; relative_path = 'Gpfilm.2020.1080p/Featurettes/featurette.mkv'; status = 'skipped'; size_bytes = 700000; handback = $null })
'extra-skipped: ' + (Show { Scenario_deluno_release_with_extra })
$script:Files = @($film, [pscustomobject]@{ id = 99; library_id = 1; relative_path = 'Gpfilm.2020.1080p/Featurettes/featurette.mkv'; status = 'processing_failed'; size_bytes = 700000; handback = $null })
'extra-failed: ' + (Show { Scenario_deluno_release_with_extra })
$script:Files = @($film)
[IO.File]::WriteAllBytes((Join-Path $output 'featurette.mkv'), (New-Object byte[] 700000))
'extra-copied: ' + (Show { Scenario_deluno_release_with_extra })
Remove-Item (Join-Path $output 'featurette.mkv')
$script:Failed = 2
'workflow-has-failures: ' + (Show { Scenario_deluno_release_with_extra })
$script:Failed = 0
Remove-Item -Recurse -Force (Join-Path $release 'Featurettes')
'extra-gone-from-disk: ' + (Show { Scenario_deluno_release_with_extra })
$script:Handoffs = @(New-Handoff 'Gpfilm.2020.1080p' '2026-10-11T19:00:00+00:00' 'imported' 'delivered' $release)
'handled-by-previous: ' + (Show { Scenario_deluno_release_with_extra })
`;
  const run = psFile(source + "\n" + body);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.match(lines[0], /^extra-never-seen: PASSED: In 'Movies', the release at .*Gpfilm\.2020\.1080p holds the film Gpfilm\.2020\.1080p\/Gpfilm\.2020\.1080p\.mkv \(processed\) and 1 extra\(s\) under the 1 MB minimum \(featurette\.mkv 700000 bytes\)/);
  assert.match(lines[1], /^extra-skipped: PASSED:/);
  assert.match(lines[2], /^extra-failed: FAILED: Weir either never saw the extra featurette\.mkv or left it alone as skipped \(it has 1 row\(s\) and 1 of them read something else: processing_failed\)$/);
  assert.match(lines[3], /^extra-copied: FAILED: Weir wrote nothing named featurette\.mkv into the workflow's output folder/);
  assert.match(lines[4], /^workflow-has-failures: FAILED: nothing in 'Movies' needs the person \(failed 2, rejected 0\)$/);
  assert.match(lines[5], /^extra-gone-from-disk: NA: None of the 1 completed film hand-off\(s\) the build under test handled has a source folder that still exists and holds a video file under its workflow's minimum size/);
  assert.match(lines[6], /^handled-by-previous: NA: 1 hand-off\(s\) from Deluno exist and none was made after the build under test began answering/);
});

test("the phase wrapper always leaves a number in the exit code file, so a run that ends without one is read, not lost", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-wrapper-"));
  const results = [];
  for (const [name, body] of [["falls-off-the-end", "'no exit here'"], ["exits-zero", "exit 0"], ["exits-one", "exit 1"], ["throws", "throw 'boom'"]]) {
    const runFolder = join(folder, name);
    mkdirSync(runFolder, { recursive: true });
    const runner = join(folder, `${name}-runner.ps1`);
    writeFileSync(runner, `param([string] $Phase)\n${body}\n`);
    const make = psFile([
      "$ErrorActionPreference = 'Stop'",
      `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
      `New-PhaseWrapperText -RunFolder '${runFolder}' -RunnerPath '${runner}' -Arguments "-Phase 'Fresh'" | Set-Content -LiteralPath '${join(runFolder, "start.ps1")}'`,
    ].join("\n"));
    assert.equal(make.status, 0, make.err + make.out);
    const ran = powershell(["-File", join(runFolder, "start.ps1")]);
    assert.equal(ran.status, 0, `${name}: ${ran.err}${ran.out}`);
    const file = join(runFolder, "exitcode.txt");
    assert.ok(existsSync(file), `${name} left no exit code file`);
    results.push(`${name}=${readFileSync(file, "utf8").trim()}`);
    assert.equal(existsSync(join(runFolder, "exitcode.part")), false, `${name} left its half-written file`);
  }
  assert.deepEqual(results, ["falls-off-the-end=4", "exits-zero=0", "exits-one=1", "throws=3"]);
});

test("the exit code in the file is read as a number or not at all, and an empty file is not a crash", { skip }, () => {
  const { source } = harness(String.raw`
foreach ($text in $null, '', '  ', ('7' + [char]13 + [char]10), ' 0 ', 'x', '1.5') { '[' + ([string]$text).Trim() + ']=' + $(if ($null -eq (Read-ExitCodeText $text)) { 'none' } else { Read-ExitCodeText $text }) }
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  assert.deepEqual(run.out.trim().split(/\r?\n/), ["[]=none", "[]=none", "[]=none", "[7]=7", "[0]=0", "[x]=none", "[1.5]=none"]);
});

test("the runner always ends with an explicit exit code, never by falling off the end", () => {
  const source = readFileSync(join(scenarios, "Run-WeirScenarios.ps1"), "utf8").trimEnd();
  assert.match(source, /\r?\nexit 0$/, "Run-WeirScenarios.ps1 does not end with exit 0");
  assert.match(source, /if \(\$failed\.Count -gt 0 -or \$missing\.Count -gt 0\) \{ exit 1 \}\r?\nexit 0$/);
});

test("the update scenario compares every Activity entry, paged, not the newest 100 twice", () => {
  const source = readFileSync(join(scenarios, "Weir.Scenarios.Update.ps1"), "utf8");
  assert.doesNotMatch(source, /Get-ActivityItems -Limit 100/);
  assert.match(source, /Get-AllActivityItems/);
  assert.match(source, /Find-ActivityLostOrChanged/);
  assert.match(source, /Save-BuildUnderTestMarker/);
});

test("an invalid -Phases is refused before anything is touched", { skip }, () => {
  const out = mkdtempSync(join(tmpdir(), "weir-scenarios-phases-"));
  const run = powershell([
    "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
    "-RigHost", "no-such-host", "-InstallerPath", join(out, "x", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
    "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-OutDirectory", join(out, "artifacts"), "-Phases", "Update,Soak", "-WhatIf",
  ]);
  assert.notEqual(run.status, 0);
  assert.match(run.err + run.out, /-Phases takes any of Update, WithDeluno, Fresh; it was given 'Soak'/);
  assert.equal(existsSync(join(out, "artifacts")), false);
});
