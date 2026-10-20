// node --test scripts/scenarios-vm-run.test.mjs
// What the golden-VM scenario suite got wrong on its first run of 1.0.0-rc.19, each pinned with the shape it was found in: the update scenario
// began before Deluno's picker had finished installing Weir, the Deluno workflows scenario waited on a Deluno nobody had set up, and the
// recover scenario's deliberate refused sign-in was taken for trouble by the log watch. Windows PowerShell exists on Windows only, so
// elsewhere these tests are skipped.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const scenarios = join(repoRoot, "scripts", "scenarios");
const hasWindowsPowerShell = process.platform === "win32" && spawnSync("powershell.exe", ["-NoProfile", "-Command", "exit 0"]).status === 0;
const skip = hasWindowsPowerShell ? false : "Windows PowerShell is not available";

// Runs PowerShell source from a file in Windows PowerShell 5.1, the version the VM has.
function psFile(lines) {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-vm-run-"));
  const file = join(folder, "test.ps1");
  writeFileSync(file, lines.join("\n"));
  const run = spawnSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr + run.stdout);
  return run.stdout.trim().split(/\r?\n/);
}

const dotSource = (...names) => names.map((name) => `. '${join(scenarios, `Weir.Scenarios.${name}.ps1`)}'`);
const hereString = (lines) => ["@'", ...lines, "'@"].join("\n");

// --- the update scenario and Deluno's picker ------------------------------------------------------------------------------------------

// Deluno's picker log as the run found it when the update scenario began: Weir, the last component, still being fetched.
const pickerLogWhileInstalling = [
  "2026-10-20T00:48:13.7980094-07:00 NZBGet: SHA-256 matches the vendor's published checksum",
  "2026-10-20T00:48:31.6892678-07:00 NZBGet: categories and download folders set up.",
  "2026-10-20T00:48:43.0267392-07:00 Weir: SHA-256 matches the vendor's published checksum",
  "2026-10-20T00:48:43.0277267-07:00 Weir: installer Weir-win-Setup.exe sha256 80F03C7FF26FD167A910E9DB92458C1A1EAC5808D69D88B518708C7C488FD112",
];
// What the picker wrote when it had finished, seventy seconds later.
const pickerLogFinished = [
  "2026-10-20T00:49:56.7998758-07:00 component 'qbittorrent': Installed: qBittorrent installed and set up with Movies and TV categories and your download folders.",
  "2026-10-20T00:49:56.8014207-07:00 component 'nzbget': Installed: NZBGet installed and set up with Movies and TV categories and your download folders.",
  "2026-10-20T00:49:56.8019863-07:00 component 'weir': Installed: Weir installed and is ready.",
];
const weirDownloadFailed = "2026-10-20T00:49:56.8019863-07:00 component 'weir': DownloadFailed: Weir could not be downloaded.";
const onlyQbittorrent = "2026-10-20T00:49:56.7998758-07:00 component 'qbittorrent': Installed: qBittorrent installed.";

test("the update scenario waits for Deluno's picker to say it is done, not for sixty seconds of Weir answering", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-picker-"));
  const log = join(folder, "install-hook.log");
  const lines = psFile([
    "$ErrorActionPreference = 'Stop'",
    ...dotSource("Lib", "Context", "Update"),
    `$log = '${log}'`,
    "function Show($label, $block) { try { $r = & $block; $label + ': ' + $r } catch { $label + ': THREW ' + $_.Exception.Message } }",
    "$script:ReadyCalls = New-Object System.Collections.Generic.List[int]",
    "function Wait-WeirReady { param([int] $TimeoutSeconds = 120) $script:ReadyCalls.Add($TimeoutSeconds) }",
    `$installing = ${hereString(pickerLogWhileInstalling)}`,
    `$finished = ${hereString(pickerLogFinished)}`,
    "Set-Content -LiteralPath $log -Value $installing",
    "'installing: ' + [string]($null -eq (Get-PickerOutcome -LogPaths @($log)))",
    "Show 'waits' { Wait-PickerInstalledWeir -LogPaths @($log) -CeilingSeconds 1 -IntervalMilliseconds 100 }",
    "'ready-asked-while-installing: ' + $script:ReadyCalls.Count",
    "Add-Content -LiteralPath $log -Value $finished",
    "$outcome = Get-PickerOutcome -LogPaths @($log)",
    "'finished: ' + $outcome.WeirInstalled + ' | ' + $outcome.Said",
    "Show 'proceeds' { Wait-PickerInstalledWeir -LogPaths @($log) -CeilingSeconds 1 -IntervalMilliseconds 100; 'ok' }",
    "'ready-asked-after: ' + ($script:ReadyCalls -join ',')",
    `Set-Content -LiteralPath $log -Value ($installing + "\`n${weirDownloadFailed}")`,
    "Show 'weir-failed' { Wait-PickerInstalledWeir -LogPaths @($log) -CeilingSeconds 1 -IntervalMilliseconds 100 }",
    `Set-Content -LiteralPath $log -Value ($installing + "\`n${onlyQbittorrent}")`,
    "Show 'weir-not-asked-for' { Wait-PickerInstalledWeir -LogPaths @($log) -CeilingSeconds 1 -IntervalMilliseconds 100 }",
    "$script:ReadyCalls.Clear()",
    "Show 'no-picker' { Wait-PickerInstalledWeir -LogPaths @(); 'ok' }",
    "'ready-asked-no-picker: ' + ($script:ReadyCalls -join ',')",
  ]);
  assert.equal(lines[0], "installing: True");
  assert.match(lines[1], /^waits: THREW Timed out after 1 s waiting for Deluno's picker to finish installing the components it was asked for\.$/);
  assert.equal(lines[2], "ready-asked-while-installing: 0");
  assert.match(lines[3], /^finished: True \| 2026-10-20T00:49:56\.8019863-07:00 component 'weir': Installed: Weir installed and is ready\.$/);
  assert.equal(lines[4], "proceeds: ok");
  assert.equal(lines[5], "ready-asked-after: 120");
  assert.match(lines[6], /^weir-failed: THREW Deluno's picker finished without installing Weir: .*component 'weir': DownloadFailed/);
  assert.match(lines[7], /^weir-not-asked-for: THREW Deluno's picker finished without installing Weir: .*component 'qbittorrent'/);
  assert.equal(lines[8], "no-picker: ok");
  assert.equal(lines[9], "ready-asked-no-picker: 60");
  const scenario = readFileSync(join(scenarios, "Weir.Scenarios.Update.ps1"), "utf8");
  assert.match(scenario, /Scenario_update_over_installed_weir \{[\s\S]*?\n {4}Wait-PickerInstalledWeir\n/, "the scenario waits for the picker first");
  assert.doesNotMatch(scenario, /Scenario_update_over_installed_weir \{[\s\S]*?\n {4}Wait-WeirReady -TimeoutSeconds 60/);
});

// --- workflows set up from Deluno -------------------------------------------------------------------------------------------------------

test("workflows from Deluno: a Deluno with no library set to refine before import has nothing to set up, so the scenario is not applicable, not a timeout", { skip }, () => {
  const recorded = JSON.parse(
    readFileSync(join(repoRoot, "apps", "server", "tests", "fixtures", "recorded", "deluno-side", "1.0.0-rc.30.33", "real-connection.json"), "utf8"),
  );
  const manifest = recorded.exchanges.find((exchange) => exchange.request.path === "/api/integrations/external/manifest").reply.body;
  assert.deepEqual(
    manifest.libraries.map((library) => library.importWorkflow),
    ["standard"],
    "the recorded Deluno is one nobody has set a library to refine before import on",
  );
  const refining = structuredClone(manifest);
  refining.libraries[0].importWorkflow = "refine-before-import";
  const lines = psFile([
    "$ErrorActionPreference = 'Stop'",
    ...dotSource("Lib", "Context", "Deluno"),
    "$script:Ctx.DelunoUrl = 'http://localhost:7879'",
    "function Test-DelunoPreconditions { $null }",
    "function Invoke-Deluno { param($Method, $Path) [pscustomobject]@{ Status = 200; Json = ($script:Manifest | ConvertFrom-Json); Text = $script:Manifest } }",
    "function Ensure-WeirDelunoConnection { throw 'PAST-THE-CHECK' }",
    "function Show($scenario) { try { $r = & $scenario; if ($r.NotApplicable) { 'NA: ' + $r.Detail } else { 'PASSED: ' + $r } } catch { 'FAILED: ' + $_.Exception.Message } }",
    `$script:Manifest = ${hereString([JSON.stringify(manifest)])}`,
    "Show { Scenario_workflows_from_deluno }",
    `$script:Manifest = ${hereString([JSON.stringify(refining)])}`,
    "Show { Scenario_workflows_from_deluno }",
  ]);
  assert.match(
    lines[0],
    /^NA: The Deluno at http:\/\/localhost:7879 lists 1 library\(ies\) and none is set to refine before import, so Weir has no workflow to set up from it\. Deluno's own suite sets its libraries up/,
  );
  assert.equal(lines[1], "FAILED: PAST-THE-CHECK");
});

// --- the log watch and a refused sign-in made on purpose ---------------------------------------------------------------------------------

test("a sign-in a scenario makes on purpose is named for that scenario alone: the log watch excuses it there and nowhere else", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-expected-"));
  const home = join(folder, "home");
  const run = join(folder, "run");
  mkdirSync(join(home, "logs"), { recursive: true });
  mkdirSync(join(run, "evidence"), { recursive: true });
  const refused = (at) => JSON.stringify({ level: "WARNING", message: "auth event: login failed", timestamp: at });
  const lines = psFile([
    "$ErrorActionPreference = 'Stop'",
    ...dotSource("Lib", "Catalog", "Context", "Install", "Tray", "FreshWeir", "WeirOnly"),
    `$script:Ctx.RunFolder = '${run}'`,
    `$script:Ctx.RuntimeHome = '${home}'`,
    `$script:Ctx.InstallRoot = '${join(folder, "install")}'`,
    "$script:Ctx.Base = 'http://localhost:1'",
    "$script:Ctx.KnownLogRows = @()",
    "$script:Ctx.Session = $null",
    "$script:Ctx.Rehearsal = $true",
    "function Show($label, $block) { try { $r = & $block; $label + ': PASSED ' + $r } catch { $label + ': FAILED ' + $_.Exception.Message } }",
    `$server = Join-Path '${home}' 'logs\\weir.log'`,
    `$tray = Join-Path '${home}' 'tray-host.log'`,
    `Set-Content -LiteralPath $server -Value '${JSON.stringify({ level: "INFO", message: "started", timestamp: "2026-10-11T17:00:00Z" })}'`,
    "Set-Content -LiteralPath $tray -Value '[2026-10-11 17:00:00] Weir started'",
    "Start-LogWatch",
    `Add-Content -LiteralPath $server -Value '${refused("2026-10-11T20:00:00Z")}'`,
    "Show 'unnamed' { Complete-LogWatch }",
    "Start-LogWatch",
    "Add-ExpectedLogRow -Pattern 'auth event: login failed' -Why 'the forgotten password is tried once'",
    `Add-Content -LiteralPath $server -Value '${refused("2026-10-11T20:01:00Z")}'`,
    "Show 'named' { Complete-LogWatch }",
    "Start-LogWatch",
    `Add-Content -LiteralPath $server -Value '${refused("2026-10-11T20:02:00Z")}'`,
    "Show 'next-scenario' { Complete-LogWatch }",
    "Start-LogWatch",
    "Add-ExpectedLogRow -Pattern 'auth event: login failed' -Why 'the forgotten password is tried once'",
    `Add-Content -LiteralPath $server -Value '${JSON.stringify({ level: "ERROR", message: "something else broke", timestamp: "2026-10-11T20:03:00Z" })}'`,
    "Show 'something-else' { Complete-LogWatch }",
    "$script:Ctx.LogWatch = $null",
    "Show 'no-watch' { Add-ExpectedLogRow -Pattern 'x' -Why 'y' }",
  ]);
  assert.match(lines[0], /^unnamed: FAILED the server log file gained no unexpected warning or error \(found 1: WARNING auth event: login failed\)$/);
  assert.match(lines[1], /^named: PASSED .*named as caused on purpose: the forgotten password is tried once/);
  assert.match(lines[2], /^next-scenario: FAILED the server log file gained no unexpected warning or error \(found 1: WARNING auth event: login failed\)$/);
  assert.match(lines[3], /^something-else: FAILED the server log file gained no unexpected warning or error \(found 1: ERROR something else broke\)$/);
  assert.match(lines[4], /^no-watch: FAILED A log row was named as caused on purpose with no log watch running/);
  const recover = readFileSync(join(scenarios, "Weir.Scenarios.Recover.ps1"), "utf8");
  assert.match(
    recover,
    /Add-ExpectedLogRow -Pattern 'auth event: login failed'[^\n]*\n\s*\$withForgotten = Invoke-Weir/,
    "the recover scenario names the refused sign-in before it makes it",
  );
});
