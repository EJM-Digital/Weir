// node --test scripts/scenarios.test.mjs
// Weir's VM scenario suite (scripts/scenarios, docs/release.md "Proof before tagging"): every PowerShell file parses in
// Windows PowerShell 5.1, every scenario in the catalog has code and is described in the docs, and the plan modes list the
// scenarios without touching a machine. Windows PowerShell exists on Windows only, so elsewhere these tests are skipped.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { combineRecords, parseRecord, run as runCombine } from "./combine-golden-path-records.mjs";
import { repository } from "./release/temporary-repository.mjs";
import { passedVersion } from "./verify-golden-path-for-release.mjs";

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
  assert.equal(entries.length, 14);
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
    "deluno-outcomes", "fresh-install", "first-visit-account", "weir-only-film", "process-again",
    "pause-resume", "deleted-queued-file", "update-requests", "recover-locked-out-account",
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

test("the recover helper refuses to start the installed server without an explicit data folder", { skip }, () => {
  const run = psFile(String.raw`
$ErrorActionPreference = 'Stop'
. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'
. '${join(scenarios, "Weir.Scenarios.Context.ps1")}'
. '${join(scenarios, "Weir.Scenarios.Recover.ps1")}'
$script:Ctx.InstallRoot = Join-Path ([IO.Path]::GetTempPath()) 'weir-no-such-install'
foreach ($folder in $null, '', '   ') {
    try { Invoke-InstalledRecover -Arguments @('--list') -DataFolder $folder | Out-Null; 'STARTED' }
    catch { 'REFUSED|' + $_.Exception.Message }
}
`);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.equal(lines.length, 3);
  for (const line of lines) assert.match(line, /^REFUSED\|Invoke-InstalledRecover refuses to run without an explicit data folder/);
});

test("clearing WEIR_HOME for the recover helper is refused anywhere but the scenario VM", { skip }, () => {
  const run = psFile(String.raw`
$ErrorActionPreference = 'Stop'
. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'
. '${join(scenarios, "Weir.Scenarios.Context.ps1")}'
. '${join(scenarios, "Weir.Scenarios.Recover.ps1")}'
# This machine, as it is: no token from the orchestrator, no marker. If the guard let it through the helper would start this path.
$script:Ctx.InstallRoot = Join-Path ([IO.Path]::GetTempPath()) 'weir-no-such-install'
try { Invoke-InstalledRecover -Arguments @('--list') -UseDefaultDataFolder | Out-Null; 'STARTED' }
catch { 'LOCAL|' + $_.Exception.Message }

# Even handed a token, with no marker in the VM's place, this machine is refused.
$script:Ctx.VmToken = 'abc123'
try { Invoke-InstalledRecover -Arguments @('--list') -UseDefaultDataFolder | Out-Null; 'STARTED' }
catch { 'TOKEN|' + $_.Exception.Message }

# The guard's judgement, on the facts of a machine that is the VM and of machines that differ in one thing each.
$vm = [pscustomobject]@{ Token = 'abc123'; MarkerToken = 'abc123'; MarkerComputer = 'GOLDEN-VM'; MarkerHost = 'rig-host.example.test'; Computer = 'GOLDEN-VM'; Manufacturer = 'Microsoft Corporation'; Model = 'Virtual Machine' }
'VM|' + [string](Get-ScenarioVmRefusal -Facts $vm)
foreach ($change in @{ Token = '' }, @{ MarkerToken = '' }, @{ MarkerToken = 'older' }, @{ MarkerComputer = 'OTHER-PC' }, @{ Computer = 'RIG-HOST' ; MarkerComputer = 'RIG-HOST' }, @{ Manufacturer = 'Dell Inc.' ; Model = 'XPS' }, @{ Model = 'Standard PC' }) {
    $facts = $vm.PSObject.Copy()
    foreach ($name in $change.Keys) { $facts.$name = $change[$name] }
    'OTHER|' + (Get-ScenarioVmRefusal -Facts $facts)
}
# This machine's own facts, read for real, are not the VM's.
'REAL|' + [string](Get-ScenarioVmRefusal -Facts (Get-ScenarioVmFacts))
`);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  const refusal = /^(LOCAL|TOKEN)\|Invoke-InstalledRecover refuses to clear WEIR_HOME outside the scenario VM/;
  assert.match(lines[0], refusal);
  assert.match(lines[0], /no -VmToken/);
  assert.match(lines[1], refusal);
  assert.match(lines[1], /no marker at C:\\golden\\weir\\scenario-vm\.json/);
  assert.equal(lines[2], "VM|", "the facts of the scenario VM pass the guard");
  const others = lines.slice(3, 10);
  assert.equal(others.length, 7);
  for (const line of others) assert.match(line, /^OTHER\|\S/, `a machine that differs from the VM passed: ${line}`);
  assert.match(lines[10], /^REAL\|\S/, "this machine passed the guard");
});

test("the orchestrator leaves the marker and the token in the VM that the recover helper's guard reads", () => {
  const orchestrator = readFileSync(join(scenarios, "Invoke-WeirScenarios.ps1"), "utf8");
  const helper = readFileSync(join(scenarios, "Weir.Scenarios.Recover.ps1"), "utf8");
  const runner = readFileSync(join(scenarios, "Run-WeirScenarios.ps1"), "utf8");
  const markerPath = "C:\\golden\\weir\\scenario-vm.json";
  assert.ok(orchestrator.includes(markerPath) && helper.includes(markerPath), "the orchestrator and the guard name different marker files");
  assert.match(orchestrator, /vm_computer = \$env:COMPUTERNAME/, "the marker is not written with the VM's own computer name");
  assert.match(orchestrator, /VmToken = \$vmToken/, "the token is not given to the run");
  assert.match(orchestrator, /@\(\$wrapper, \$vmToken, \$RigHost\)/, "the marker is not written with the token and the host");
  assert.match(runner, /\$script:Ctx\.VmToken = \$VmToken/, "the run does not keep the token it was given");
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

// --- every scenario runs alone (#981) ------------------------------------------------------------------------------------------

test("no scenario of its own is left for the logs: every scenario ends with the log check", { skip }, () => {
  const ids = plan().map((entry) => entry.Id);
  assert.ok(!ids.some((id) => id.startsWith("logs-clean")), "a stand-alone logs scenario reads the warnings of the whole machine");
  const runner = readFileSync(join(scenarios, "Run-WeirScenarios.ps1"), "utf8");
  assert.match(runner, /Start-LogWatch\s+\$detail = & \$function/, "the runner notes the logs before a scenario begins");
  assert.match(runner, /\$logs = Complete-LogWatch/, "the runner judges what the logs gained once a scenario has passed");
});

// Reads every Scenario_ function with PowerShell's own parser and says what it finds that stops it running alone.
const scenarioIndependence = String.raw`
$ErrorActionPreference = 'Stop'
. '${join(scenarios, "Weir.Scenarios.Catalog.ps1")}'
$ids = @($ScenarioCatalog | ForEach-Object { $_.Id })
$phaseOf = @{}
foreach ($entry in $ScenarioCatalog) { $phaseOf[$entry.Id] = $entry.Phase }
$found = 0
foreach ($file in Get-ChildItem -LiteralPath '${scenarios}' -Filter 'Weir.Scenarios.*.ps1') {
    $tokens = $null; $errors = $null
    $tree = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    $functions = $tree.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -like 'Scenario_*' }, $true)
    foreach ($function in $functions) {
        $found++
        $id = $function.Name.Substring('Scenario_'.Length).Replace('_', '-')
        # 1. It writes no state of the run's context: whatever it needs is made by the helpers it calls, for itself.
        $assignments = $function.Body.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)
        foreach ($assignment in $assignments) {
            $left = $assignment.Left
            if ($left -is [System.Management.Automation.Language.MemberExpressionAst] -and $left.Expression -is [System.Management.Automation.Language.VariableExpressionAst] -and $left.Expression.VariablePath.UserPath -eq 'script:Ctx') {
                'WRITES|' + $id + '|' + $left.Member.Value
            }
        }
        # 2. It calls no other scenario.
        $commands = $function.Body.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
        foreach ($command in $commands) {
            $name = $command.GetCommandName()
            if ($name -and $name -like 'Scenario_*') { 'CALLS|' + $id + '|' + $name }
        }
        # 3. It names no other scenario.
        $strings = $function.Body.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true)
        foreach ($string in $strings) {
            if ($ids -contains $string.Value -and $string.Value -ne $id) { 'NAMES|' + $id + '|' + $string.Value }
        }
        # 4. A Fresh scenario makes its own Weir and account.
        if ($phaseOf[$id] -eq 'Fresh' -and $id -ne 'fresh-install') {
            $setups = @($commands | Where-Object { $_.GetCommandName() -in 'Initialize-FreshScenario', 'Ensure-FreshWeir' })
            if ($setups.Count -eq 0) { 'NOSETUP|' + $id }
        }
    }
}
'FOUND|' + $found
`;

test("no scenario names another, calls another, or reads state another left: each one makes its own", { skip }, () => {
  const run = psFile(scenarioIndependence);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  const found = lines.pop();
  assert.deepEqual(lines, [], `these scenarios depend on another or write the run's shared state:\n${lines.join("\n")}`);
  assert.equal(found, "FOUND|14", "every scenario of the catalog was read");
});

// The shared helpers, dot-sourced with a context pointing at a temporary place, for the log watch and Fresh setup tests.
function freshHarness(body, { rehearsal = false } = {}) {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-fresh-"));
  const home = join(folder, "home");
  const run = join(folder, "run");
  mkdirSync(join(home, "logs"), { recursive: true });
  mkdirSync(join(run, "evidence"), { recursive: true });
  const lines = [
    "$ErrorActionPreference = 'Stop'",
    ...["Lib", "Catalog", "Context", "Install", "Tray", "FreshWeir", "WeirOnly"].map((name) => `. '${join(scenarios, `Weir.Scenarios.${name}.ps1`)}'`),
    `$script:Ctx.RunFolder = '${run}'`,
    `$script:Ctx.RuntimeHome = '${home}'`,
    `$script:Ctx.InstallRoot = '${join(folder, "install")}'`,
    `$script:Ctx.MediaRoot = '${join(folder, "media")}'`,
    "$script:Ctx.Base = 'http://localhost:1'",
    "$script:Ctx.Version = '1.0.0-rc.99'",
    "$script:Ctx.ShortSha = 'abcdef0'",
    "$script:Ctx.KnownLogRows = @()",
    "$script:Ctx.Session = $null",
    `$script:Ctx.Rehearsal = $${rehearsal ? "true" : "false"}`,
    body,
  ];
  return { source: lines.join("\n"), folder, home };
}

const serverLine = (level, message, at = "2026-10-11T19:00:00Z") => JSON.stringify({ level, message, timestamp: at });

test("the log watch judges only what a scenario added: warnings that were there when it began are not its", { skip }, () => {
  const { source } = freshHarness(String.raw`
$script:Rows = @([pscustomobject]@{ source = 'server'; id = 7; at = '2026-10-11T18:00:00'; title = 'An old warning' })
$script:More = $false
function Get-WeirJson { param($Session, $Path) [pscustomobject]@{ items = $script:Rows; next_cursor = $(if ($script:More) { 'next' } else { $null }) } }
function Show($label, $block) { try { $r = & $block; $label + ': PASSED ' + $r } catch { $label + ': FAILED ' + $_.Exception.Message } }
$server = Join-Path $script:Ctx.RuntimeHome 'logs\weir.log'
$tray = Join-Path $script:Ctx.RuntimeHome 'tray-host.log'
Set-Content -LiteralPath $server -Value @('{"level":"INFO","message":"started","timestamp":"2026-10-11T17:00:00Z"}', '{"level":"WARNING","message":"an old server warning","timestamp":"2026-10-11T17:30:00Z"}')
Set-Content -LiteralPath $tray -Value @('[2026-10-11 17:00:00] Weir started', '[2026-10-11 17:10:00] Could not do an old thing')

$script:Ctx.Session = 'a session'
Start-LogWatch; Set-LogWatchPage
Show 'nothing-new' { Complete-LogWatch }

Start-LogWatch; Set-LogWatchPage
Add-Content -LiteralPath $server -Value '{"level":"ERROR","message":"a new server error","timestamp":"2026-10-11T20:00:00Z"}'
Show 'new-server' { Complete-LogWatch }

Start-LogWatch; Set-LogWatchPage
Add-Content -LiteralPath $tray -Value '[2026-10-11 20:05:00] Could not start a new thing'
Show 'new-tray' { Complete-LogWatch }

Start-LogWatch; Set-LogWatchPage
$script:Rows = @([pscustomobject]@{ source = 'event'; id = 9; at = '2026-10-11T20:10:00'; title = 'A new page warning' }) + $script:Rows
Show 'new-page' { Complete-LogWatch }

$script:Ctx.KnownLogRows = @([pscustomobject]@{ Pattern = 'new page warning'; Why = 'the scenario provoked it' })
Start-LogWatch; Set-LogWatchPage
$script:Rows = @([pscustomobject]@{ source = 'event'; id = 10; at = '2026-10-11T20:11:00'; title = 'Another new page warning' }) + $script:Rows
Show 'named-on-purpose' { Complete-LogWatch }
$script:Ctx.KnownLogRows = @()

Start-LogWatch; Set-LogWatchPage
$script:More = $true
$script:Rows = @(1..100 | ForEach-Object { [pscustomobject]@{ source = 'event'; id = 100 + $_; at = '2026-10-11T21:00:00'; title = 'warning ' + $_ } })
Show 'more-than-a-page' { Complete-LogWatch }
$script:More = $false

$script:Ctx.Session = $null
Start-LogWatch
Show 'no-account' { Complete-LogWatch }
$script:Ctx.LogWatch = $null
Show 'never-started' { Complete-LogWatch }
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.match(lines[0], /^nothing-new: PASSED Logs since it began: no unexpected warning or error on System > Logs, in the server log file or in the tray log\.$/);
  assert.match(lines[1], /^new-server: FAILED the server log file gained no unexpected warning or error \(found 1: ERROR a new server error\)$/);
  assert.match(lines[2], /^new-tray: FAILED the tray log gained no line reporting trouble \(found 1: \[2026-10-11 20:05:00\] Could not start a new thing\)$/);
  assert.match(lines[3], /^new-page: FAILED System > Logs gained no unexpected warning or error \(found 1: A new page warning\)$/);
  assert.match(lines[4], /^named-on-purpose: PASSED .*named as caused on purpose: the scenario provoked it/);
  assert.match(lines[5], /^more-than-a-page: FAILED System > Logs holds more new warnings and errors than one page shows/);
  assert.match(lines[6], /^no-account: PASSED Logs since it began: no unexpected warning or error in the server log file or the tray log \(there was no account/);
  assert.match(lines[7], /^never-started: FAILED The scenario ended with no log watch to judge/);
});

test("a scenario that begins with no account judges only the Logs rows written after it began; one that signs in notes the page first", { skip }, () => {
  const { source } = freshHarness(String.raw`
$script:Rows = @([pscustomobject]@{ source = 'event'; id = 3; at = '2026-10-11T18:00:00Z'; title = 'A warning from the previous build' })
function Get-WeirJson { param($Session, $Path) [pscustomobject]@{ items = $script:Rows; next_cursor = $null } }
function Show($label, $block) { try { $r = & $block; $label + ': PASSED' } catch { $label + ': FAILED ' + $_.Exception.Message } }
Set-Content -LiteralPath (Join-Path $script:Ctx.RuntimeHome 'logs\weir.log') -Value '{"level":"INFO","message":"started","timestamp":"2026-10-11T17:00:00Z"}'
Set-Content -LiteralPath (Join-Path $script:Ctx.RuntimeHome 'tray-host.log') -Value '[2026-10-11 17:00:00] Weir started'
# Began with no session; the account was made and signed in to inside the scenario (no Set-LogWatchPage). The old warning was
# written before the scenario began (the machine was not restored), so it is not the scenario's.
Start-LogWatch
$script:Ctx.Session = 'a session'
Show 'old-warning-before-the-mark' { Complete-LogWatch }
# A warning written after the scenario began is.
Start-LogWatch
$script:Rows = @([pscustomobject]@{ source = 'event'; id = 4; at = [DateTime]::UtcNow.AddMinutes(1).ToString('o'); title = 'A warning the build wrote just now' }) + $script:Rows
Show 'new-warning-after-the-mark' { Complete-LogWatch }
# An old warning whose time cannot be read is counted, never excused.
Start-LogWatch
$script:Rows = @([pscustomobject]@{ source = 'event'; id = 5; at = 'not a time'; title = 'A warning with no readable time' })
Show 'unreadable-time' { Complete-LogWatch }
$script:Rows = @([pscustomobject]@{ source = 'event'; id = 3; at = '2026-10-11T18:00:00Z'; title = 'A warning from the previous build' })
# Signed in at the start of the scenario (Initialize-WeirSession notes the page): what was there is not the scenario's.
Start-LogWatch
Set-LogWatchPage
Show 'began-signed-in' { Complete-LogWatch }
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.equal(lines[0], "old-warning-before-the-mark: PASSED");
  assert.match(lines[1], /^new-warning-after-the-mark: FAILED System > Logs gained no unexpected warning or error \(found 1: A warning the build wrote just now\)$/);
  assert.match(lines[2], /^unreadable-time: FAILED System > Logs gained no unexpected warning or error \(found 1: A warning with no readable time\)$/);
  assert.equal(lines[3], "began-signed-in: PASSED");
});

test("a scenario that counts Logs rows itself counts only those written since it began: old warnings are not its, a new one is", { skip }, () => {
  const { source } = freshHarness(String.raw`
$script:Rows = @([pscustomobject]@{ source = 'event'; id = 3; at = '2026-10-11T18:00:00Z'; title = 'A warning from the previous build' })
$script:More = $false
function Get-WeirJson { param($Session, $Path) [pscustomobject]@{ items = $script:Rows; next_cursor = $(if ($script:More) { 'next' } else { $null }) } }
function Show($label, $block) { try { $r = & $block; $label + ': ' + $r } catch { $label + ': FAILED ' + $_.Exception.Message } }
$began = Get-SecondMark
Show 'old-only' { @(Get-LogPageProblems -Since $began).Count }
$script:Rows = @([pscustomobject]@{ source = 'event'; id = 4; at = [DateTime]::UtcNow.AddMinutes(1).ToString('o'); title = 'A new warning' }) + $script:Rows
Show 'new-one' { (@(Get-LogPageProblems -Since $began) | ForEach-Object { $_.title }) -join ',' }
$script:More = $true
$script:Rows = @(1..100 | ForEach-Object { [pscustomobject]@{ source = 'event'; id = 100 + $_; at = [DateTime]::UtcNow.AddMinutes(1).ToString('o'); title = 'warning ' + $_ } })
Show 'page-full-of-new' { @(Get-LogPageProblems -Since $began).Count }
$script:Rows = @(1..99 | ForEach-Object { [pscustomobject]@{ source = 'event'; id = 100 + $_; at = '2026-10-11T18:00:00Z'; title = 'old ' + $_ } }) + @([pscustomobject]@{ source = 'event'; id = 500; at = '2026-10-11T18:00:00Z'; title = 'old' })
Show 'page-full-of-old' { @(Get-LogPageProblems -Since $began).Count }
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.equal(lines[0], "old-only: 0");
  assert.equal(lines[1], "new-one: A new warning");
  assert.match(lines[2], /^page-full-of-new: FAILED Logs lists more than 100 warnings and errors since this scenario began/);
  assert.equal(lines[3], "page-full-of-old: 0");
});

test("-Scenarios resolves ids in run order, takes commas and wildcards, and names the nearest ids for a mistyped one", { skip }, () => {
  const { source } = freshHarness(String.raw`
function Pick($requested) { try { (Resolve-ScenarioSelection -Requested $requested -Catalog $ScenarioCatalog) -join ',' } catch { 'ERROR ' + $_.Exception.Message } }
'one=' + (Pick @('process-again'))
'comma=' + (Pick @('pause-resume,process-again'))
'separate=' + (Pick @('pause-resume', 'process-again'))
'wildcard=' + (Pick @('deluno-*'))
'twice=' + (Pick @('process-again', 'process-again'))
'none=[' + (Pick @()) + ']'
'typo=' + (Pick @('process-agin'))
'phase-name=' + (Pick @('Fresh'))
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.deepEqual(lines.slice(0, 6), [
    "one=process-again",
    "comma=process-again,pause-resume",
    "separate=process-again,pause-resume",
    "wildcard=deluno-film-handoff,deluno-release-with-extra,deluno-resend,deluno-outcomes",
    "twice=process-again",
    "none=[]",
  ]);
  assert.match(lines[6], /^typo=ERROR -Scenarios names 'process-agin', which is no scenario\. The nearest ids are: process-again, [a-z-]+, [a-z-]+\.$/);
  assert.match(lines[7], /^phase-name=ERROR -Scenarios names 'Fresh', which is no scenario\. The nearest ids are: [a-z-]+, [a-z-]+, [a-z-]+\.$/);
});

test("the account is kept, forgotten or reset from what Weir says and what the run holds", { skip }, () => {
  const { source } = freshHarness(String.raw`
foreach ($exists in $false, $true) { foreach ($held in $false, $true) { foreach ($none in $false, $true) {
    'exists=' + $exists + ' held=' + $held + ' noAccount=' + $none + ' -> ' + (Resolve-AccountAction -AccountExists $exists -Held $held -NoAccount $none)
} } }
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  assert.deepEqual(run.out.trim().split(/\r?\n/), [
    "exists=False held=False noAccount=False -> keep",
    "exists=False held=False noAccount=True -> keep",
    "exists=False held=True noAccount=False -> forget",
    "exists=False held=True noAccount=True -> forget",
    "exists=True held=False noAccount=False -> reset",
    "exists=True held=False noAccount=True -> reset",
    "exists=True held=True noAccount=False -> keep",
    "exists=True held=True noAccount=True -> reset",
  ]);
});

test("a Fresh scenario starts from a Weir whose account state it knows, and a rehearsal never resets someone else's Weir", { skip }, () => {
  const { source } = freshHarness(String.raw`
$script:Offered = $true
function Wait-WeirReady { param($TimeoutSeconds) }
function Get-WeirJson { param($Session, $Path) [pscustomobject]@{ bootstrap_allowed = $script:Offered } }
function Show($label, $block) { try { & $block | Out-Null; $label + ': ok ' + $(if ($script:Ctx.ContainsKey('Username')) { 'holds-account' } else { 'no-account-held' }) } catch { $label + ': FAILED ' + $_.Exception.Message } }
Show 'offered' { Ensure-FreshWeir }
Show 'offered-and-asked-for-none' { Ensure-FreshWeir -NoAccount }
$script:Ctx.Username = 'scenario-admin'; $script:Ctx.Password = 'x'; $script:Ctx.Session = 'a session'
Show 'account-gone-since' { Ensure-FreshWeir }
$script:Offered = $false
$script:Ctx.Username = 'scenario-admin'; $script:Ctx.Password = 'x'
Show 'account-held' { Ensure-FreshWeir }
Show 'account-held-but-none-wanted' { Ensure-FreshWeir -NoAccount }
$script:Ctx.Remove('Username')
Show 'account-not-held' { Ensure-FreshWeir }
`, { rehearsal: true });
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const lines = run.out.trim().split(/\r?\n/);
  assert.equal(lines[0], "offered: ok no-account-held");
  assert.match(lines[1], /^offered-and-asked-for-none: ok no-account-held$/);
  assert.equal(lines[2], "account-gone-since: ok no-account-held");
  assert.equal(lines[3], "account-held: ok holds-account");
  assert.match(lines[4], /^account-held-but-none-wanted: FAILED A rehearsal is pointed at a Weir somebody else started, so it cannot set that Weir back to having no account/);
  assert.match(lines[5], /^account-not-held: FAILED A rehearsal is pointed at a Weir somebody else started/);
});

test("a workflow is named for the run and its folders sit under the run's id, so a rerun is never refused as a duplicate", { skip }, () => {
  const { source } = freshHarness(String.raw`
$script:Created = New-Object System.Collections.Generic.List[string]
function Get-WeirJson { param($Session, $Path, $Method, $Body) $script:Created.Add($Body.name + '|' + $Body.watched_folder); [pscustomobject]@{ id = $script:Created.Count } }
$script:Ctx.Session = 'a session'
$root = Join-Path $script:Ctx.MediaRoot 'films'
$script:Ctx.RunId = 'aaaaaa'
$first = New-ScenarioWorkflow -Name 'Scenario films' -Root $root
$other = New-ScenarioWorkflow -Name 'Scenario again' -Root (Join-Path $script:Ctx.MediaRoot 'again')
$script:Ctx.RunId = 'bbbbbb'
$rerun = New-ScenarioWorkflow -Name 'Scenario films' -Root $root
'names=' + ($first.Name, $other.Name, $rerun.Name -join ',')
'distinct=' + (@($first.Name, $other.Name, $rerun.Name | Select-Object -Unique).Count)
'folders=' + (@($first.Watched, $rerun.Watched | Select-Object -Unique).Count)
'under-run-id=' + $first.Watched.EndsWith('films\aaaaaa\watched') + ',' + $rerun.Watched.EndsWith('films\bbbbbb\watched')
'run-id-is-made=' + ((Get-RunId) -eq 'bbbbbb')
$script:Ctx.Remove('RunId')
'made-when-missing=' + ((Get-RunId) -match '^[0-9a-f]{6}$')
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  assert.deepEqual(run.out.trim().split(/\r?\n/), [
    "names=Scenario films aaaaaa,Scenario again aaaaaa,Scenario films bbbbbb",
    "distinct=3",
    "folders=2",
    "under-run-id=True,True",
    "run-id-is-made=True",
    "made-when-missing=True",
  ]);
});

test("a record of chosen scenarios lists every other scenario as not chosen, never as failed", { skip }, () => {
  const { source } = freshHarness(String.raw`
$results = @([pscustomobject]@{ Id = 'process-again'; Status = 'passed'; Detail = 'it did'; Seconds = 3 })
$path = Join-Path $script:Ctx.RunFolder 'record.md'
$others = @($ScenarioCatalog | Where-Object { $_.Id -ne 'process-again' } | ForEach-Object { $_.Id })
Write-ScenarioRecord -Path $path -Catalog $ScenarioCatalog -Results $results -Facts @{ Version = '1.0.0-rc.99'; ShortSha = 'abcdef0' } -NotChosenIds $others
$text = Get-Content -LiteralPath $path -Raw
'chosen=' + ($text -match '\| Process again \| passed \|')
'not-chosen=' + ([regex]::Matches($text, '\| not chosen \|').Count)
'failed-rows=' + ([regex]::Matches($text, '\| failed \|').Count)
$text -split "\r?\n" | Where-Object { $_ -like '**1 of 14 scenarios passed*' }
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  assert.deepEqual(run.out.trim().split(/\r?\n/), ["chosen=True", "not-chosen=13", "failed-rows=0", "**1 of 14 scenarios passed; 0 failed; 13 not run.**"]);
});

// --- the orchestrator and the runner take -Scenarios ------------------------------------------------------------------------

test("every scenario can be chosen alone: -WhatIf lists that scenario only, in its own phase, and sets no status", { skip }, () => {
  for (const entry of plan()) {
    const out = whatIf("-Scenarios", entry.Id);
    const listed = out.split(/\r?\n/).filter((line) => /^ {5}- /.test(line));
    assert.deepEqual(listed, [`     - ${entry.Title}`], `${entry.Id} alone lists exactly itself`);
    assert.match(out, new RegExp(`Scenarios chosen: ${entry.Id}\\. Each runs alone`));
    assert.match(out, new RegExp(`Run-WeirScenarios\\.ps1 -Phase ${entry.Phase} -Scenarios ${entry.Id} as a one-shot`));
    assert.match(out, /No golden-path status is set, whatever the result/);
    assert.doesNotMatch(out, /set the golden-path status on/);
    for (const phase of ["Update", "WithDeluno", "Fresh"]) {
      const named = new RegExp(`^Phase ${phase} \\(`, "m").test(out);
      assert.equal(named, phase === entry.Phase, `${entry.Id}: ${phase} ${named ? "is" : "is not"} in the run`);
    }
    if (entry.Phase === "Fresh") assert.match(out, /restore checkpoint 'clean'/, `${entry.Id} starts from the clean checkpoint`);
    else assert.doesNotMatch(out, /restore checkpoint 'clean'/, `${entry.Id} runs on the VM as Deluno left it`);
  }
});

test("-Scenarios chooses several scenarios across phases, and -Phases may only name phases they are in", { skip }, () => {
  const both = whatIf("-Scenarios", "update-over-installed-weir,process-again");
  assert.match(both, /Phases in this invocation: Update, Fresh/);
  assert.match(both, /-Phase Update -Scenarios update-over-installed-weir as a one-shot/);
  assert.match(both, /-Phase Fresh -Scenarios process-again as a one-shot/);
  const withPhases = whatIf("-Phases", "Fresh", "-Scenarios", "process-again,pause-resume");
  assert.match(withPhases, /-Phase Fresh -Scenarios process-again,pause-resume as a one-shot/);
  const out = mkdtempSync(join(tmpdir(), "weir-scenarios-outside-"));
  const run = powershell([
    "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
    "-RigHost", "no-such-host", "-InstallerPath", join(out, "x", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
    "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-OutDirectory", join(out, "artifacts"), "-Phases", "Update", "-Scenarios", "process-again", "-WhatIf",
  ]);
  assert.notEqual(run.status, 0);
  assert.match(run.err + run.out, /-Scenarios names process-again \(phase Fresh\), outside -Phases Update/);
  assert.equal(existsSync(join(out, "artifacts")), false);
});

test("-NoRestore runs chosen Fresh scenarios on the machine as it stands: no checkpoint restore, the VM checked, and it needs -Scenarios", { skip }, () => {
  const restoring = whatIf("-Scenarios", "process-again");
  assert.match(restoring, /restore checkpoint 'clean' of VM/);
  const kept = whatIf("-Scenarios", "process-again", "-NoRestore");
  assert.doesNotMatch(kept, /restore checkpoint 'clean' of VM/);
  assert.match(kept, /Phase Fresh \(last, NO restore, -NoRestore\)/);
  assert.match(kept, /start it if it is not running, and wait for PowerShell Direct; restore nothing\. The first run must have restored checkpoint 'clean'/);
  assert.match(kept, /-Phase Fresh -Scenarios process-again as a one-shot/);
  const out = mkdtempSync(join(tmpdir(), "weir-scenarios-norestore-"));
  const run = powershell([
    "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
    "-RigHost", "no-such-host", "-InstallerPath", join(out, "x", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
    "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-OutDirectory", join(out, "artifacts"), "-NoRestore", "-WhatIf",
  ]);
  assert.notEqual(run.status, 0);
  assert.match(run.err + run.out, /-NoRestore needs -Scenarios/);
  assert.equal(existsSync(join(out, "artifacts")), false);
});

test("-NoRestore refuses a machine whose Weir is not the build under test unless a chosen scenario installs it, naming both builds", { skip }, () => {
  const script = [
    `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
    "$sha = '0123456789abcdef0123456789abcdef01234567'",
    "function Show($label, $refusal) { $label + '=' + $(if ($refusal) { $refusal } else { 'runs' }) }",
    "Show 'same' (Get-NoRestoreRefusal -Found '1.0.0-rc.19+0123456' -Version '1.0.0-rc.19' -CommitSha $sha -ScenarioIds 'first-visit-account')",
    "Show 'older' (Get-NoRestoreRefusal -Found '1.0.0-rc.18+89abcde' -Version '1.0.0-rc.19' -CommitSha $sha -ScenarioIds 'first-visit-account')",
    "Show 'othercommit' (Get-NoRestoreRefusal -Found '1.0.0-rc.19+89abcde' -Version '1.0.0-rc.19' -CommitSha $sha -ScenarioIds 'first-visit-account')",
    "Show 'nocommit' (Get-NoRestoreRefusal -Found '1.0.0-rc.19' -Version '1.0.0-rc.19' -CommitSha $sha -ScenarioIds 'first-visit-account')",
    "Show 'none' (Get-NoRestoreRefusal -Found '' -Version '1.0.0-rc.19' -CommitSha $sha -ScenarioIds 'first-visit-account')",
    "Show 'installs' (Get-NoRestoreRefusal -Found '1.0.0-rc.18+89abcde' -Version '1.0.0-rc.19' -CommitSha $sha -ScenarioIds 'process-again','fresh-install')",
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.equal(run.status, 0, run.err);
  const lines = Object.fromEntries(run.out.trim().split(/\r?\n/).map((line) => [line.slice(0, line.indexOf("=")), line.slice(line.indexOf("=") + 1)]));
  assert.equal(lines.same, "runs");
  assert.equal(lines.installs, "runs", "fresh-install installs the build, so the Weir found does not matter");
  const plain = /^-NoRestore would run on the Weir already on the machine, which is (.+), not the build under test \(1\.0\.0-rc\.19, commit 0123456\)\. None of the chosen scenarios installs the build\. Include fresh-install in -Scenarios, or run without -NoRestore\.$/;
  assert.match(lines.older, plain);
  assert.match(lines.older, /which is 1\.0\.0-rc\.18\+89abcde,/);
  assert.match(lines.othercommit, plain);
  assert.match(lines.nocommit, plain);
  assert.match(lines.none, /which is no Weir at all, not the build under test/);
});

test("-NoRestore reads the installed Weir on the VM and prints it before refusing or running", () => {
  const source = readFileSync(join(scenarios, "Invoke-WeirScenarios.ps1"), "utf8");
  assert.match(source, /function Use-VmWithoutRestore[\s\S]*Get-NoRestoreRefusal[\s\S]*\r?\n\}/);
  assert.match(source, /Weir on \$VmName before the run: /);
});

test("a run of chosen scenarios given no -OutDirectory gets a record folder of its own per run; a full run and an explicit -OutDirectory keep theirs", { skip }, () => {
  const bare = (...extra) => {
    const run = powershell([
      "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
      "-RigHost", "no-such-host", "-InstallerPath", join(tmpdir(), "x", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
      "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-WhatIf", ...extra,
    ]);
    assert.equal(run.status, 0, run.err || run.out);
    return run.out;
  };
  const chosen = bare("-Scenarios", "process-again");
  assert.match(chosen, /weir-scenarios-1\.0\.0-rc\.99-0123456-only-process-again-\d{8}T\d{6}Z/);
  const full = bare("-Phases", "Update");
  assert.match(full, /weir-scenarios-1\.0\.0-rc\.99-0123456(?!-)/);
  assert.doesNotMatch(full, /weir-scenarios-1\.0\.0-rc\.99-0123456-\d{8}T/);
  assert.match(whatIf("-Scenarios", "process-again"), /weir-scenarios-1\.0\.0-rc\.99-0123456-only-process-again(?!-\d{8}T)/);
});

test("a mistyped scenario id is refused before anything is touched, naming the nearest ids", { skip }, () => {
  const out = mkdtempSync(join(tmpdir(), "weir-scenarios-typo-"));
  const run = powershell([
    "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
    "-RigHost", "no-such-host", "-InstallerPath", join(out, "x", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
    "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-OutDirectory", join(out, "artifacts"), "-Scenarios", "process-agin", "-WhatIf",
  ]);
  assert.notEqual(run.status, 0);
  assert.match(run.err + run.out, /-Scenarios names 'process-agin', which is no scenario\. The nearest ids are: process-again/);
  assert.equal(existsSync(join(out, "artifacts")), false);
  const inVm = powershell(["-File", join(scenarios, "Run-WeirScenarios.ps1"), "-Plan", "-Scenarios", "process-agin"]);
  assert.notEqual(inVm.status, 0);
  assert.match(inVm.err + inVm.out, /which is no scenario/);
});

test("the runner takes -Scenarios (the name Deluno's suite uses) and no longer -Only, and the orchestrator hands each phase its own ids", () => {
  const runner = readFileSync(join(scenarios, "Run-WeirScenarios.ps1"), "utf8");
  assert.match(runner, /\[string\[\]\] \$Scenarios/);
  assert.doesNotMatch(runner, /\$Only\b/);
  const orchestrator = readFileSync(join(scenarios, "Invoke-WeirScenarios.ps1"), "utf8");
  assert.match(orchestrator, /\[string\[\]\] \$Scenarios/);
  assert.match(orchestrator, /\$arguments\.Scenarios = /);
});

// --- combining a full run with the re-runs of what it failed ---------------------------------------------------------------

const TITLES = ["Fresh install", "First visit", "Pause and resume", "Update buttons"];
const COMMIT = "a".repeat(40);

// A record as Write-ScenarioRecord writes it: a heading, the facts, then one line per scenario.
function record({ version = "3.2.17", commit = COMMIT, scriptsCommit, chosen, results }) {
  const facts = { Commit: commit, Rehearsal: "no", ...(scriptsCommit ? { "Scripts commit": scriptsCommit } : {}), ...(chosen ? { "Scenarios chosen": chosen.join(", ") } : {}) };
  const rows = TITLES.map((title, index) => {
    const status = results[title] ?? (chosen ? "not chosen" : "failed");
    return `| ${index + 1} | ${title} | ${status} | ${status === "not chosen" ? 0 : 12} | ${status === "passed" ? "it worked" : status === "not chosen" ? "Not chosen for this run." : "it did not"} |`;
  });
  return [`# Weir scenario suite ${version}, commit ${commit.slice(0, 7)}`, "", ...Object.entries(facts).map(([key, value]) => `- **${key}:** ${value}`), "", "| # | Scenario | Result | Seconds | Saw |", "| --- | --- | --- | --- | --- |", ...rows, ""].join("\n");
}

const everyScenarioPasses = Object.fromEntries(TITLES.map((title) => [title, "passed"]));

// Three real commits: A, then B (a script changed), then C (a server file changed).
function scriptHistory() {
  const repo = repository();
  const a = repo.commit({ "apps/server/src/Weir.Core/Plan.cs": "a", "scripts/scenarios/Weir.Scenarios.Lib.ps1": "a" }, "A");
  const b = repo.commit({ "scripts/scenarios/Weir.Scenarios.Lib.ps1": "b" }, "B");
  const c = repo.commit({ "apps/server/src/Weir.Core/Plan.cs": "c" }, "C");
  return { git: repo.git, a, b, c };
}

const combine = (git, full, ...reruns) =>
  combineRecords({ full: { name: "full.md", text: full }, reruns: reruns.map((text, index) => ({ name: `rerun-${index + 1}.md`, text })), git });

test("a full run that failed two scenarios and their re-runs on the same build combine into one record, and the status is set after it", () => {
  const { git, a, b } = scriptHistory();
  const files = {
    "full.md": record({ scriptsCommit: a, results: { ...everyScenarioPasses, "First visit": "failed", "Pause and resume": "failed" } }),
    "first.md": record({ scriptsCommit: b, chosen: ["first-visit-account"], results: { "First visit": "passed" } }),
    "second.md": record({ scriptsCommit: b, chosen: ["pause-resume"], results: { "Pause and resume": "passed" } }),
  };
  const calls = [];
  const written = {};
  const status = runCombine(["records/full.md", "records/first.md", "records/second.md", "--out", "records/combined.md"], {
    git,
    read: (path) => files[basename(path)],
    write: (path, text) => { written[basename(path)] = text; },
    setStatus: (call) => calls.push({ ...call, recordWritten: "combined.md" in written }),
    log: () => {},
    error: (line) => assert.fail(line),
  });

  assert.equal(status, 0);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].recordWritten, true, "the record is written before the status is set");
  assert.equal(calls[0].sha, COMMIT);
  assert.match(calls[0].description, /combined/);
  assert.equal(passedVersion({ description: calls[0].description }), "3.2.17", "the release gate can still read which version the pass was run for");
  const text = written["combined.md"];
  for (const name of ["full.md", "first.md", "second.md"]) assert.ok(text.includes(name), `the combined record names ${name}`);
  assert.match(text, /\*\*4 of 4 scenarios passed, 2 of them on a re-run alone\.\*\*/);
  assert.equal([...text.matchAll(/ \| passed \| /g)].length, 4, "every scenario reads passed");
});

test("a failed scenario with no re-run is refused, naming the scenario, and nothing is written or set", () => {
  const { git, a, b } = scriptHistory();
  const files = {
    "full.md": record({ scriptsCommit: a, results: { ...everyScenarioPasses, "First visit": "failed", "Pause and resume": "failed" } }),
    "one.md": record({ scriptsCommit: b, chosen: ["first-visit-account"], results: { "First visit": "passed" } }),
  };
  const result = combine(git, files["full.md"], files["one.md"]);
  assert.equal(result.ok, false);
  assert.match(result.problems.join("\n"), /No re-run is given for 1 scenario the full run did not pass: Pause and resume \(failed\)/);

  const lines = [];
  const status = runCombine(["full.md", "one.md"], {
    git,
    read: (path) => files[basename(path)],
    write: () => assert.fail("a refused combine writes no record"),
    setStatus: () => assert.fail("a refused combine sets no status"),
    log: () => {},
    error: (line) => lines.push(line),
  });
  assert.equal(status, 1);
  assert.match(lines.join("\n"), /Not combined\. The golden-path status is left as it was\./);
});

test("a re-run made for a different build is refused, whether the version or the commit differs", () => {
  const { git, a, b } = scriptHistory();
  const full = record({ scriptsCommit: a, results: { ...everyScenarioPasses, "First visit": "failed" } });
  const otherVersion = record({ version: "3.2.18", scriptsCommit: b, chosen: ["first-visit-account"], results: { "First visit": "passed" } });
  const otherCommit = record({ commit: "b".repeat(40), scriptsCommit: b, chosen: ["first-visit-account"], results: { "First visit": "passed" } });
  for (const rerun of [otherVersion, otherCommit]) {
    const result = combine(git, full, rerun);
    assert.equal(result.ok, false);
    assert.match(result.problems.join("\n"), /rerun-1\.md was made for 3\.2\.1[78]\+[0-9a-f]{7} \(commit [0-9a-f]{40}\), and full\.md for 3\.2\.17\+aaaaaaa .*: a different build/);
  }
});

test("a scenario whose latest re-run failed is refused, and a later passing re-run of it is accepted", () => {
  const { git, a, b } = scriptHistory();
  const full = record({ scriptsCommit: a, results: { ...everyScenarioPasses, "First visit": "failed" } });
  const failed = record({ scriptsCommit: b, chosen: ["first-visit-account"], results: { "First visit": "failed" } });
  const passed = record({ scriptsCommit: b, chosen: ["first-visit-account"], results: { "First visit": "passed" } });
  const refused = combine(git, full, failed);
  assert.equal(refused.ok, false);
  assert.match(refused.problems.join("\n"), /First visit did not pass when re-run alone: failed in rerun-1\.md/);
  assert.equal(combine(git, full, failed, passed).ok, true);
  assert.equal(combine(git, full, passed, failed).ok, false, "the last re-run decides");
});

test("a change to a file that ships between the scripts of the full run and of a re-run is refused; a change to scripts alone is not", () => {
  const { git, a, b, c } = scriptHistory();
  const failedAt = (commit) => record({ scriptsCommit: commit, results: { ...everyScenarioPasses, "First visit": "failed" } });
  const rerunAt = (commit) => record({ scriptsCommit: commit, chosen: ["first-visit-account"], results: { "First visit": "passed" } });
  assert.equal(combine(git, failedAt(a), rerunAt(b)).ok, true, "only a script changed");
  const refused = combine(git, failedAt(a), rerunAt(c));
  assert.equal(refused.ok, false);
  assert.match(refused.problems.join("\n"), /1 file that ships changed: apps\/server\/src\/Weir\.Core\/Plan\.cs\. A change to a file that ships needs a full run\./);
  assert.equal(combine(git, failedAt(c), rerunAt(a)).ok, false, "the same change is found whichever record is older");
});

test("a record that is not a full run, passed everything, or does not say where its scripts ran from is refused", () => {
  const { git, a } = scriptHistory();
  const failedFull = record({ scriptsCommit: a, results: { ...everyScenarioPasses, "First visit": "failed" } });
  const rerun = record({ scriptsCommit: a, chosen: ["first-visit-account"], results: { "First visit": "passed" } });
  assert.match(combine(git, rerun, rerun).problems.join("\n"), /full\.md is a run of chosen scenarios, not a full run/);
  assert.match(combine(git, failedFull, failedFull).problems.join("\n"), /rerun-1\.md is a full run, not a re-run/);
  assert.match(combine(git, record({ scriptsCommit: a, results: everyScenarioPasses }), rerun).problems.join("\n"), /passed every scenario, so there is nothing to combine/);
  const unsaid = record({ chosen: ["first-visit-account"], results: { "First visit": "passed" } });
  assert.match(combine(git, failedFull, unsaid).problems.join("\n"), /rerun-1\.md does not say which commit its scenario scripts ran from/);
  assert.match(combine(git, "not a record", rerun).problems.join("\n"), /full\.md is not a scenario record/);
});

test("the combiner reads a record exactly as Write-ScenarioRecord writes it", { skip }, () => {
  const { source } = freshHarness(String.raw`
$results = @([pscustomobject]@{ Id = 'process-again'; Status = 'passed'; Detail = 'it did | really'; Seconds = 3 })
$path = Join-Path $script:Ctx.RunFolder 'record.md'
$others = @($ScenarioCatalog | Where-Object { $_.Id -ne 'process-again' } | ForEach-Object { $_.Id })
Write-ScenarioRecord -Path $path -Catalog $ScenarioCatalog -Results $results -Facts @{ Version = '3.2.17'; ShortSha = 'abcdef0'; Commit = ('a' * 40); 'Scripts commit' = ('b' * 40); 'Scenarios chosen' = 'process-again' } -NotChosenIds $others
Get-Content -LiteralPath $path -Raw
`);
  const run = psFile(source);
  assert.equal(run.status, 0, run.err + run.out);
  const parsed = parseRecord("record.md", run.out);
  assert.deepEqual([parsed.version, parsed.shortSha, parsed.commit, parsed.scriptsCommit, parsed.subset], ["3.2.17", "abcdef0", COMMIT, "b".repeat(40), true]);
  assert.equal(parsed.rows.length, 14);
  assert.deepEqual(parsed.rows.find((row) => row.status === "passed"), { title: "Process again", status: "passed", detail: "it did / really" });
});

test("the orchestrator writes which commit its scenario scripts ran from into the record, and says so when the working tree has changes", () => {
  const source = readFileSync(join(scenarios, "Invoke-WeirScenarios.ps1"), "utf8");
  assert.match(source, /\$facts\['Scripts commit'\] = /);
  assert.match(source, /status --porcelain/);
  assert.match(source, /\+uncommitted/);
});
