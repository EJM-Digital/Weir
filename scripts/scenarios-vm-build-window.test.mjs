// node --test scripts/scenarios-vm-build-window.test.mjs
// A scenario that replaces the running Weir with the build under test (the update scenario, a fresh install over a Weir that is there) is judged
// on the logs from where the build under test began, not from where the scenario began: the previous release goes on logging until the install
// stops it, and what it logs is not the build's. The run of 1.0.0-rc.21 that found this counted rc.20's own stall and poster-lookup warnings
// (logged before "Weir 1.0.0-rc.21+e59bbed is starting process=7592") as the build's. Windows PowerShell exists on Windows only, so elsewhere
// these tests are skipped.
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

function psFile(lines) {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-build-window-run-"));
  const file = join(folder, "test.ps1");
  writeFileSync(file, lines.join("\n"));
  const run = spawnSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr + run.stdout);
  return run.stdout.trim().split(/\r?\n/);
}

const dotSource = (...names) => names.map((name) => `. '${join(scenarios, `Weir.Scenarios.${name}.ps1`)}'`);
// A PowerShell variable holding text exactly as it is (apostrophes included).
const variable = (name, ...text) => [`$${name} = @'`, ...text, "'@"].join("\n");

const entry = (level, message, timestamp) => JSON.stringify({ timestamp, level, logger: "Weir.Test", message });
const stall = entry("WARNING", "Weir's server has been slow to get to work for 5 s, so pages may answer slowly.", "2026-10-28T08:02:44.569219Z");
const poster = entry("WARNING", "Weir could not reach the metadata service for posters (timed out).", "2026-10-28T08:02:55.166697Z");
const oldStart = entry("INFO", "Weir 1.0.0-rc.20 is starting process=6260 home=C:\\ProgramData\\Weir", "2026-10-28T07:51:15.444605Z");
const newStart = entry("INFO", "Weir 1.0.0-rc.21+e59bbed is starting process=7592 home=C:\\ProgramData\\Weir", "2026-10-28T08:04:18.021733Z");
const otherCommit = entry("INFO", "Weir 1.0.0-rc.21+0000000 is starting process=7000 home=C:\\ProgramData\\Weir", "2026-10-28T08:03:30.000000Z");
const afterStart = entry("WARNING", "Weir could not reach the metadata service for posters (timed out).", "2026-10-28T08:05:00.000000Z");
const trayHead = "[2026-10-28 08:01:00] Weir started";
const trayPid = "[2026-10-28 08:04:19] Bundled server host pid=7592";
const trayEarly = "[2026-10-28 08:03:00] Could not reach the old server";
const trayLate = "[2026-10-28 08:05:00] Could not reach the server";
const pageRow = (id, at, title) => `[pscustomobject]@{ source = 'server'; id = '${id}'; at = '${at}'; title = '${title}' }`;

test("an update is judged from the build under test's own start line: the previous release's warnings are information, the build's are judged, no start line fails", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-build-window-"));
  const home = join(folder, "home");
  const run = join(folder, "run");
  mkdirSync(join(home, "logs"), { recursive: true });
  mkdirSync(join(run, "evidence"), { recursive: true });
  const lines = psFile([
    "$ErrorActionPreference = 'Stop'",
    ...dotSource("Lib", "Catalog", "Context", "Install", "Tray", "FreshWeir", "WeirOnly"),
    `$script:Ctx.RunFolder = '${run}'`,
    `$script:Ctx.RuntimeHome = '${home}'`,
    `$script:Ctx.InstallRoot = '${join(folder, "install")}'`,
    "$script:Ctx.Base = 'http://localhost:1'",
    "$script:Ctx.KnownLogRows = @()",
    "$script:Ctx.Version = '1.0.0-rc.21'",
    "$script:Ctx.ShortSha = 'e59bbed'",
    "$script:Ctx.Rehearsal = $false",
    "$script:Rows = @()",
    "function Get-WeirJson { param($Session, $Path) [pscustomobject]@{ items = $script:Rows; next_cursor = $null } }",
    "function Show($label, $block) { try { $r = & $block; $label + ': PASSED ' + $r } catch { $label + ': FAILED ' + $_.Exception.Message } }",
    `$server = Join-Path '${home}' 'logs\\weir.log'`,
    `$tray = Join-Path '${home}' 'tray-host.log'`,
    variable("oldStart", oldStart),
    variable("newStart", newStart),
    variable("otherCommit", otherCommit),
    variable("stall", stall),
    variable("poster", poster),
    variable("afterStart", afterStart),
    variable("trayHead", trayHead),
    variable("trayPid", trayPid),
    variable("trayEarly", trayEarly),
    variable("trayLate", trayLate),
    // The previous release running, the watch begun, and the scenario saying it replaces that Weir.
    "function Begin { param([switch] $Page) $script:Rows = @(); $script:Ctx.Session = $null; Set-Content -LiteralPath $server -Value $oldStart; Set-Content -LiteralPath $tray -Value $trayHead; Start-LogWatch; if ($Page) { $script:Ctx.Session = 'a session'; Set-LogWatchPage }; Use-BuildUnderTestLogWindow }",
    // The run that found this: the previous release's warnings, then the build starts, then nothing.
    "Begin; Add-Content -LiteralPath $server -Value $stall, $poster, $newStart; Add-Content -LiteralPath $tray -Value $trayPid",
    "Show 'previous-release' { Complete-LogWatch }",
    // A warning the build under test logs after it started is still judged.
    "Begin; Add-Content -LiteralPath $server -Value $stall, $newStart, $afterStart; Add-Content -LiteralPath $tray -Value $trayPid",
    "Show 'build-warning' { Complete-LogWatch }",
    // No start line of the build (another commit's is not it).
    "Begin; Add-Content -LiteralPath $server -Value $stall, $otherCommit; Add-Content -LiteralPath $tray -Value $trayPid",
    "Show 'no-start' { Complete-LogWatch }",
    // A start line, but the tray does not say it started that process.
    "Begin; Add-Content -LiteralPath $server -Value $newStart",
    "Show 'no-tray-line' { Complete-LogWatch }",
    // The tray log: trouble before the build's server started is the previous release's, trouble after is the build's.
    "Begin; Add-Content -LiteralPath $server -Value $newStart; Add-Content -LiteralPath $tray -Value $trayEarly, $trayPid",
    "Show 'tray-previous' { Complete-LogWatch }",
    "Begin; Add-Content -LiteralPath $server -Value $newStart; Add-Content -LiteralPath $tray -Value $trayPid, $trayLate",
    "Show 'tray-build' { Complete-LogWatch }",
    // System > Logs: a row written before the build began is the previous release's; one written from its first second on is judged.
    `Begin -Page; Add-Content -LiteralPath $server -Value $stall, $newStart; Add-Content -LiteralPath $tray -Value $trayPid; $script:Rows = @(${pageRow("server:1", "2026-10-28T08:02:44.569219Z", "slow to get to work")})`,
    "Show 'page-previous' { Complete-LogWatch }",
    `Begin -Page; Add-Content -LiteralPath $server -Value $stall, $newStart; Add-Content -LiteralPath $tray -Value $trayPid; $script:Rows = @(${pageRow("server:1", "2026-10-28T08:02:44.569219Z", "slow to get to work")}, ${pageRow("server:2", "2026-10-28T08:04:18.500000Z", "poster lookup timed out")})`,
    "Show 'page-build' { Complete-LogWatch }",
    // A scenario that replaced nothing is judged from where it began, as before.
    "Set-Content -LiteralPath $server -Value $oldStart; Set-Content -LiteralPath $tray -Value $trayHead; Start-LogWatch; Add-Content -LiteralPath $server -Value $stall",
    "Show 'replaced-nothing' { Complete-LogWatch }",
    "$script:Ctx.LogWatch = $null",
    "Show 'no-watch' { Use-BuildUnderTestLogWindow }",
  ]);
  assert.match(
    lines[0],
    /^previous-release: PASSED .*judged from the moment the build under test began \(1\.0\.0-rc\.21\+e59bbed, process 7592, logged at 2026-10-28T08:04:18\.\d+Z\)\. For information, not judged, the previous release logged 2 warning or error line\(s\) before that: server log: WARNING Weir's server has been slow .* \| server log: WARNING Weir could not reach the metadata service for posters/,
  );
  assert.match(lines[1], /^build-warning: FAILED the server log file gained no unexpected warning or error \(found 1: WARNING Weir could not reach the metadata service for posters \(timed out\)\.\)$/);
  assert.match(lines[2], /^no-start: FAILED the server log has no line saying that Weir 1\.0\.0-rc\.21 \(e59bbed\) began .*since the scenario installed it over the running Weir/);
  assert.match(lines[3], /^no-tray-line: FAILED the tray log has no line saying that it started process 7592, the server of the build under test/);
  assert.match(lines[4], /^tray-previous: PASSED .*tray log: \[2026-10-28 08:03:00\] Could not reach the old server/);
  assert.match(lines[5], /^tray-build: FAILED the tray log gained no line reporting trouble \(found 1: \[2026-10-28 08:05:00\] Could not reach the server\)$/);
  assert.match(lines[6], /^page-previous: PASSED .*System > Logs: slow to get to work/);
  assert.match(lines[7], /^page-build: FAILED System > Logs gained no unexpected warning or error \(found 1: poster lookup timed out\)$/);
  assert.match(lines[8], /^replaced-nothing: FAILED the server log file gained no unexpected warning or error \(found 1: WARNING Weir's server has been slow/);
  assert.match(lines[9], /^no-watch: FAILED The build under test was named as replacing the running Weir with no log watch running/);
});

test("every scenario that installs the build under test over, or after stopping, a running Weir says so before it installs", () => {
  for (const [file, before] of [
    ["Update", /Use-BuildUnderTestLogWindow\r?\n\s*\$update = Invoke-WeirSetup/],
    ["FreshWeir", /Use-BuildUnderTestLogWindow\r?\n\s*\$update = Invoke-WeirSetup/],
    ["FreshWeir", /Use-BuildUnderTestLogWindow\r?\n\s*Stop-InstalledWeir\r?\n\s*\$setup = Invoke-WeirSetup/],
    ["Install", /Use-BuildUnderTestLogWindow\r?\n\s*Stop-InstalledWeir\r?\n\s*\$setup = Invoke-WeirSetup/],
  ]) {
    assert.match(readFileSync(join(scenarios, `Weir.Scenarios.${file}.ps1`), "utf8"), before, `${file} names that it replaces the running Weir before it installs`);
  }
});
