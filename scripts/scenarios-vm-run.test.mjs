// node --test scripts/scenarios-vm-run.test.mjs
// What the golden-VM scenario suite got wrong on its first run of 1.0.0-rc.19, each pinned with the shape it was found in: the update scenario
// began before Deluno's picker had finished installing Weir, the Deluno scenarios skipped for want of a library and a hand-off that nothing had
// set up (the run now sets them up through Deluno's API), and the recover scenario's deliberate refused sign-in was taken for trouble by the log
// watch. Windows PowerShell exists on Windows only, so elsewhere these tests are skipped.
import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { createServer } from "node:http";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

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

// Deluno's install log as the run found it when the update scenario began: first run under way, Weir, the last component, still being fetched.
const pickerLogWhileInstalling = [
  "2026-10-20T00:44:28.8749696-07:00 first run: the tray icon is shown while Deluno sets itself up.",
  "2026-10-20T00:44:39.3604958-07:00 start with Windows: no.",
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
    "'installing: ' + (Get-PickerState -LogPaths @($log)).State",
    "Show 'waits' { Wait-PickerInstalledWeir -LogPaths @($log) -CeilingSeconds 1 -IntervalMilliseconds 100 }",
    "'ready-asked-while-installing: ' + $script:ReadyCalls.Count",
    "Add-Content -LiteralPath $log -Value $finished",
    "$outcome = Get-PickerState -LogPaths @($log)",
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
  assert.equal(lines[0], "installing: Running");
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

// Deluno's install log as the re-run found it after Deluno was updated over an existing Deluno: Setup ran, no first run, no picker.
const logAfterUpdatingDeluno = [
  "2026-10-20T02:10:58.8392206-07:00 Main starting: args=[--veloapp-install 1.0.0-rc.30.34].",
  "2026-10-20T02:10:58.8525148-07:00 Standard handles at start: in=none, out=none, err=none; now the null device.",
  "2026-10-20T02:10:58.9033151-07:00 after install/update: done; a true first install runs its first-run steps on its first start, a reinstall over existing data does not.",
  "2026-10-20T02:10:59.5874312-07:00 Deluno was running before this install. It starts again once Setup (pid 1776) has exited, unless Setup starts it.",
  "2026-10-20T02:11:02.5233277-07:00 Main starting: args=[--after-setup].",
  "2026-10-20T02:11:02.7063227-07:00 Setup has exited: started Deluno.",
];
const reinstallAsksNothing =
  "2026-10-20T02:10:59.1000000-07:00 first run: this is a reinstall over an existing Deluno (its settings or data are there), so the component picker, the firewall question and the Start with Windows question are not asked again.";

test("the update scenario never waits for a picker that has already finished, never ran, or left no log", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-picker-ran-"));
  const log = join(folder, "install-hook.log");
  const lines = psFile([
    "$ErrorActionPreference = 'Stop'",
    ...dotSource("Lib", "Context", "Update"),
    `$log = '${log}'`,
    "function Show($label, $block) { try { $r = & $block; $label + ': ' + $r } catch { $label + ': THREW ' + $_.Exception.Message } }",
    "$script:ReadyCalls = New-Object System.Collections.Generic.List[int]",
    "function Wait-WeirReady { param([int] $TimeoutSeconds = 120) $script:ReadyCalls.Add($TimeoutSeconds) }",
    "function Settle { $script:ReadyCalls.Clear(); Wait-PickerInstalledWeir -LogPaths @($log) -CeilingSeconds 1 -IntervalMilliseconds 100; 'settled, asked Weir for ' + ($script:ReadyCalls -join ',') }",
    `$installing = ${hereString(pickerLogWhileInstalling)}`,
    `$finished = ${hereString(pickerLogFinished)}`,
    `$updated = ${hereString(logAfterUpdatingDeluno)}`,
    "$firstRun = @($installing -split \"`r?`n\" | Select-Object -First 2)",
    // The picker finished before the scenario began: its own log says so at once.
    "Set-Content -LiteralPath $log -Value ($installing + \"`n\" + $finished)",
    "'finished-earlier-state: ' + (Get-PickerState -LogPaths @($log)).State",
    "Show 'finished-earlier' { Settle }",
    // Deluno updated over an existing Deluno: the picker did not run, so its log never will say it finished.
    "Set-Content -LiteralPath $log -Value $updated",
    "'updated-state: ' + (Get-PickerState -LogPaths @($log)).State",
    "Show 'updated' { Settle }",
    // A reinstall's first run asks nothing, even with the tray icon shown.
    `Set-Content -LiteralPath $log -Value ($firstRun[0] + "\`n${reinstallAsksNothing}")`,
    "'reinstall-state: ' + (Get-PickerState -LogPaths @($log)).State",
    "Show 'reinstall' { Settle }",
    // A log kept from an earlier install that finished says nothing about a picker now at work.
    "Set-Content -LiteralPath $log -Value ($installing + \"`n\" + $finished + \"`n\" + $firstRun[0] + \"`n\" + $firstRun[1])",
    "'second-run-state: ' + (Get-PickerState -LogPaths @($log)).State",
    "Show 'second-run' { Settle }",
    // A log with no first run in it at all (an empty file) is no picker either.
    "Set-Content -LiteralPath $log -Value ''",
    "Show 'empty' { Settle }",
  ]);
  assert.equal(lines[0], "finished-earlier-state: Done");
  assert.equal(lines[1], "finished-earlier: settled, asked Weir for 120");
  assert.equal(lines[2], "updated-state: NotRun");
  assert.equal(lines[3], "updated: settled, asked Weir for 60");
  assert.equal(lines[4], "reinstall-state: NotRun");
  assert.equal(lines[5], "reinstall: settled, asked Weir for 60");
  assert.equal(lines[6], "second-run-state: Running");
  assert.match(lines[7], /^second-run: THREW Timed out after 1 s waiting for Deluno's picker/);
  assert.equal(lines[8], "empty: settled, asked Weir for 60");
});

// --- Deluno set up by the run (#1257) -------------------------------------------------------------------------------------------------

// Windows PowerShell 5.1 run as a child that is waited for without blocking this process, so a server in this process can answer it.
function psFileAsync(lines, { onLine } = {}) {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-vm-run-"));
  const file = join(folder, "test.ps1");
  writeFileSync(file, lines.join("\n"));
  const child = spawn("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file], { stdio: ["pipe", "pipe", "pipe"] });
  let stdout = "";
  let stderr = "";
  child.stdout.on("data", (chunk) => {
    stdout += chunk;
    onLine?.(stdout, child);
  });
  child.stderr.on("data", (chunk) => (stderr += chunk));
  const done = new Promise((resolveRun) => child.on("close", (status) => resolveRun({ status, stdout: stdout.trim().split(/\r?\n/), stderr })));
  return { child, done };
}

// A library as Deluno's manifest lists it (the recorded one, a library nobody has set to refine before import), with the one member /api/libraries
// adds that the setup reads: the processor connection it hands to. No recording of /api/libraries or of the processor routes exists; they are
// shaped as the setup reads them, and the manifest is the recorded shape.
const recordedLibrary = () => {
  const recorded = JSON.parse(
    readFileSync(join(repoRoot, "apps", "server", "tests", "fixtures", "recorded", "deluno-side", "1.0.0-rc.30.33", "real-connection.json"), "utf8"),
  );
  return { ...recorded.exchanges.find((exchange) => exchange.request.path === "/api/integrations/external/manifest").reply.body.libraries[0], processorConnectionId: null };
};

// A Deluno answering the routes the setup uses, keeping what it was asked. `refuse` is the status the library save answers with, instead of accepting it.
async function delunoStandIn({ libraries = [recordedLibrary()], processors = [], refuse = 0 } = {}) {
  const calls = [];
  const state = { libraries: structuredClone(libraries), processors: structuredClone(processors) };
  const server = createServer((request, response) => {
    let text = "";
    request.on("data", (chunk) => (text += chunk));
    request.on("end", () => {
      const body = text ? JSON.parse(text) : null;
      calls.push({ method: request.method, path: request.url, key: request.headers["x-api-key"], body });
      const send = (status, json) => {
        response.writeHead(status, { "content-type": "application/json" });
        response.end(JSON.stringify(json));
      };
      const workflow = /^\/api\/libraries\/([^/]+)\/workflow$/.exec(request.url);
      if (request.method === "GET" && request.url === "/api/integrations/processors/connections") return send(200, state.processors);
      if (request.method === "POST" && request.url === "/api/integrations/processors/connections") {
        const made = { id: "processor-1", name: "Weir on test-machine", provider: body.provider, submissionUrl: body.submissionUrl, isEnabled: body.isEnabled };
        state.processors.push(made);
        return send(200, made);
      }
      if (request.method === "GET" && request.url === "/api/libraries") return send(200, state.libraries);
      if (request.method === "PUT" && workflow) {
        if (refuse) return send(refuse, { title: "This API key does not include the required scope: write." });
        Object.assign(state.libraries.find((library) => library.id === workflow[1]), body);
        return send(200, state.libraries.find((library) => library.id === workflow[1]));
      }
      if (request.method === "GET" && request.url === "/api/integrations/external/manifest") return send(200, { libraries: state.libraries });
      return send(404, { title: "not here" });
    });
  });
  await new Promise((resolveListening) => server.listen(0, "localhost", resolveListening));
  return { calls, state, url: `http://localhost:${server.address().port}`, close: () => server.close() };
}

const setupKey = "deluno_SetupKey0123456789abcdef";

// PowerShell that sets Deluno up against the stand-in, with Weir's side stood in for: its connection to Deluno and the webhook secret it makes.
function setupScript(deluno, body) {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-setup-"));
  return {
    folder,
    lines: [
      "$ErrorActionPreference = 'Stop'",
      ...dotSource("Lib", "Context", "Deluno", "DelunoSetup"),
      `$script:Ctx.DelunoUrl = '${deluno.url}'`,
      `$script:Ctx.DelunoKey = '${setupKey}'`,
      "$script:Ctx.Base = 'http://localhost:5555'",
      `$script:Ctx.MediaRoot = '${join(folder, "media")}'`,
      "$script:Ctx.Session = 'a session'",
      `$script:EvidenceFile = '${join(folder, "evidence.txt")}'`,
      "$script:WebhookSecrets = 0",
      "function Ensure-WeirDelunoConnection { [pscustomobject]@{ Id = 7; How = 'stood in' } }",
      "function Invoke-Weir { param($Session, $Method, $Path, $Body) $script:WebhookSecrets++; [pscustomobject]@{ Status = 200; Text = ''; Json = [pscustomobject]@{ webhook_url_path = '/api/v1/media-managers/webhooks/deluno/7'; header_name = 'X-Webhook-Secret'; webhook_secret = 'whsec-0123456789abcdef' } } }",
      "function Show($label, $block) { try { $r = & $block; $label + ': PASSED ' + $r } catch { $label + ': FAILED ' + $_.Exception.Message } }",
      body,
    ],
  };
}

test("a Deluno with no library that refines before import is set up through its API: Weir as its processor, one library handing back to Weir's folder", { skip }, async () => {
  const deluno = await delunoStandIn();
  try {
    const { folder, lines } = setupScript(deluno, "$set = Initialize-DelunoRefiningLibrary; 'summary: ' + $set.Summary; 'again: ' + [object]::ReferenceEquals($set, (Initialize-DelunoRefiningLibrary))");
    const { status, stdout, stderr } = await psFileAsync(lines).done;
    assert.equal(status, 0, stderr + stdout.join("\n"));

    assert.deepEqual(
      deluno.calls.map((call) => `${call.method} ${call.path}`),
      [
        "GET /api/integrations/processors/connections",
        "POST /api/integrations/processors/connections",
        "GET /api/libraries",
        "PUT /api/libraries/01a17f02bccc732b97a87b112b300bf0/workflow",
        "GET /api/integrations/external/manifest",
      ],
    );
    assert.ok(deluno.calls.every((call) => call.key === setupKey), "every call carries the key from the key file");
    const ready = join(folder, "media", "deluno", "Ready", "Movies");
    assert.deepEqual(deluno.calls[1].body, {
      name: "Weir",
      provider: "generic-webhook",
      submissionUrl: "http://localhost:5555/api/v1/media-managers/webhooks/deluno/7",
      authHeaderName: "X-Webhook-Secret",
      secret: "whsec-0123456789abcdef",
      isEnabled: true,
      healthCheckUrl: "http://localhost:5555/ready",
    });
    assert.deepEqual(deluno.calls[3].body, {
      importWorkflow: "refine-before-import",
      processorConnectionId: "processor-1",
      processorName: "Weir on test-machine",
      processorOutputPath: ready,
      processorTimeoutMinutes: 2880,
      processorFailureMode: "block",
      cleanupMode: "keep-source",
      removeEmptySourceFolders: false,
    });
    assert.ok(existsSync(ready), "Weir's output folder exists");
    assert.match(stdout[0], /^summary: added Weir to Deluno as a processor, with a webhook secret made in Weir; set Deluno's library 'Movies' to refine before import, with 'Weir on test-machine' as its processor, failure mode block and its cleaned films handed back to /);
    assert.equal(stdout[1], "again: True", "done once per run");
    const evidence = readFileSync(join(folder, "evidence.txt"), "utf8");
    assert.match(evidence, /Deluno PUT \/api\/libraries\/01a17f02bccc732b97a87b112b300bf0\/workflow -> 200/, "the record shows the call that set the library up");
    assert.doesNotMatch(evidence, /whsec-0123456789abcdef|deluno_SetupKey/, "neither the key nor the webhook secret is written down");
  } finally {
    deluno.close();
  }
});

test("a library that already refines before import with an enabled Weir processor is left as it is, and no new webhook secret stops the connection working", { skip }, async () => {
  const processor = { id: "processor-9", name: "Weir on test-machine", provider: "generic-webhook", submissionUrl: "http://localhost:5555/api/v1/media-managers/webhooks/deluno/7", isEnabled: true };
  const refining = { ...recordedLibrary(), importWorkflow: "refine-before-import", processorConnectionId: "processor-9", processorName: processor.name, processorOutputPath: "C:\\Weir\\Ready\\Movies" };
  const deluno = await delunoStandIn({ libraries: [refining], processors: [processor] });
  try {
    const { lines } = setupScript(deluno, "$set = Initialize-DelunoRefiningLibrary; 'summary: ' + $set.Summary; 'secrets made: ' + $script:WebhookSecrets");
    const { status, stdout, stderr } = await psFileAsync(lines).done;
    assert.equal(status, 0, stderr + stdout.join("\n"));

    assert.deepEqual(
      deluno.calls.map((call) => `${call.method} ${call.path}`),
      ["GET /api/integrations/processors/connections", "GET /api/libraries", "GET /api/integrations/external/manifest"],
    );
    assert.match(stdout[0], /^summary: used the processor connection 'Weir on test-machine' Deluno already had for Weir; found Deluno's library 'Movies' already refining before import/);
    assert.equal(stdout[1], "secrets made: 0");
  } finally {
    deluno.close();
  }
});

test("Deluno refusing the setup is a failure, and one that names the scopes the key needs; it is never a skip", { skip }, async () => {
  const deluno = await delunoStandIn({ refuse: 403 });
  try {
    const { lines } = setupScript(
      deluno,
      "Show 'setup' { Initialize-DelunoRefiningLibrary | Out-Null }\nfunction Test-DelunoPreconditions { $null }\nShow 'workflows' { $r = Scenario_workflows_from_deluno; if ($r.NotApplicable) { 'NOT APPLICABLE ' + $r.Detail } else { $r } }",
    );
    const { status, stdout, stderr } = await psFileAsync(lines).done;
    assert.equal(status, 0, stderr + stdout.join("\n"));

    const refused = /FAILED Deluno refused to set its library 'Movies' to refine before import \(answered 403: .*required scope: write.*\)\. The key from the key file did not allow it: setting Deluno up through its API needs a key with the scopes read, imports and write\.$/;
    assert.match(stdout[0], new RegExp(`^setup: ${refused.source}`));
    assert.match(stdout[1], new RegExp(`^workflows: ${refused.source}`));
  } finally {
    deluno.close();
  }
});

test("a Deluno with no movie library has nothing to refine films with, and says so rather than waiting", { skip }, async () => {
  const deluno = await delunoStandIn({ libraries: [{ ...recordedLibrary(), mediaType: "tv" }] });
  try {
    const { lines } = setupScript(deluno, "Show 'setup' { Initialize-DelunoRefiningLibrary | Out-Null }");
    const { status, stdout, stderr } = await psFileAsync(lines).done;
    assert.equal(status, 0, stderr + stdout.join("\n"));

    assert.match(stdout[0], /^setup: FAILED The Deluno at http:\/\/localhost:\d+ has no movie library to refine films with/);
    assert.equal(deluno.calls.some((call) => call.method === "PUT"), false, "no library was changed");
  } finally {
    deluno.close();
  }
});

// --- the hand-off the run makes ------------------------------------------------------------------------------------------------------

// A release folder as the run makes one: a film that spans several pieces of a torrent, an .nfo, and a nested extra.
function releaseFolder() {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-release-"));
  const release = join(folder, "Scn1a2b3c.2020.1080p.WEB-DL.x264-WEIR");
  mkdirSync(join(release, "Extras"), { recursive: true });
  writeFileSync(join(release, "Scn1a2b3c.2020.1080p.WEB-DL.x264-WEIR.mkv"), randomBytes(700_000));
  writeFileSync(join(release, "Scn1a2b3c.2020.1080p.WEB-DL.x264-WEIR.nfo"), "Weir scenario release\n");
  writeFileSync(join(release, "Extras", "Featurette.mkv"), Buffer.alloc(2_000_000));
  return { folder, release, name: "Scn1a2b3c.2020.1080p.WEB-DL.x264-WEIR" };
}

test("the torrent the run writes is byte for byte the one the CI shard's release source writes for the same folder", { skip }, async () => {
  const { release, name } = releaseFolder();
  const { makeTorrent } = await import(pathToFileURL(join(repoRoot, "scripts", "scenarios-ci", "with-deluno", "fixture", "torrent.mjs")).href);
  const expected = makeTorrent({ folder: release, name, webSeedBase: "http://localhost:18700/webseed/" });
  const { status, stdout, stderr } = await psFileAsync([
    "$ErrorActionPreference = 'Stop'",
    ...dotSource("Lib", "Context", "DelunoSetup"),
    `$made = New-ReleaseTorrent -Folder '${release}' -Name '${name}' -WebSeedBase 'http://localhost:18700/webseed/'`,
    "'hash: ' + $made.InfoHash",
    "'total: ' + $made.Total",
    "'files: ' + (($made.Files | ForEach-Object { $_.Path + '=' + $_.Size }) -join ',')",
    "'torrent: ' + [Convert]::ToBase64String($made.Torrent)",
  ]).done;
  assert.equal(status, 0, stderr + stdout.join("\n"));

  assert.equal(stdout[0], `hash: ${expected.infohash}`);
  assert.equal(stdout[1], `total: ${expected.total}`);
  assert.equal(stdout[2], `files: ${expected.files.map((file) => `${file.path}=${file.size}`).join(",")}`);
  assert.equal(stdout[3], `torrent: ${expected.torrent.toString("base64")}`);
});

test("the release source serves the torrent and the release's bytes with the ranges a download client asks for, and nothing outside the release", { skip }, async () => {
  const { release, name } = releaseFolder();
  let running;
  const started = new Promise((resolveStarted) => {
    running = psFileAsync(
      [
        "$ErrorActionPreference = 'Stop'",
        ...dotSource("Lib", "Context", "DelunoSetup"),
        `$source = Start-ReleaseSource -Folder '${release}' -Name '${name}'`,
        "'origin: ' + $source.Origin",
        "[Console]::In.ReadLine() | Out-Null",
        "& $source.Stop",
        "'stopped'",
      ],
      {
        onLine: (output) => {
          const found = /origin: (\S+)/.exec(output);
          if (found) resolveStarted(found[1]);
        },
      },
    );
    void running.done.then(() => resolveStarted(null));
  });
  const origin = await started;
  if (origin === null) assert.fail(`the release source did not start: ${(await running.done).stderr}`);
  try {
    const film = readFileSync(join(release, `${name}.mkv`));
    const get = async (path, headers = {}) => {
      const answer = await fetch(`${origin}${path}`, { headers });
      return { status: answer.status, headers: answer.headers, bytes: Buffer.from(await answer.arrayBuffer()) };
    };

    const torrent = await get(`/dl/${name}.torrent`);
    assert.equal(torrent.status, 200);
    assert.equal(torrent.headers.get("content-type"), "application/x-bittorrent");
    assert.match(torrent.bytes.toString("latin1"), new RegExp(`url-list.*${origin.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}/webseed/`));

    const whole = await get(`/webseed/${name}/${name}.mkv`);
    assert.equal(whole.status, 200);
    assert.ok(whole.bytes.equals(film), "the whole file");
    const middle = await get(`/webseed/${name}/${name}.mkv`, { Range: "bytes=10-19" });
    assert.equal(middle.status, 206);
    assert.equal(middle.headers.get("content-range"), `bytes 10-19/${film.length}`);
    assert.ok(middle.bytes.equals(film.subarray(10, 20)));
    const open = await get(`/webseed/${name}/${name}.mkv`, { Range: `bytes=${film.length - 5}-` });
    assert.ok(open.bytes.equals(film.subarray(film.length - 5)));
    const tail = await get(`/webseed/${name}/${name}.mkv`, { Range: "bytes=-5" });
    assert.ok(tail.bytes.equals(film.subarray(film.length - 5)));
    const beyond = await get(`/webseed/${name}/${name}.mkv`, { Range: `bytes=${film.length}-` });
    assert.equal(beyond.status, 416);
    const nested = await get(`/webseed/${name}/Extras/Featurette.mkv`);
    assert.equal(nested.bytes.length, 2_000_000);

    assert.equal((await get(`/webseed/${name}/missing.mkv`)).status, 404);
    assert.equal((await get(`/webseed/${name}/..%5C..%5Cescape.txt`)).status, 404);
    assert.equal((await get("/dl/another-release.torrent")).status, 404);
  } finally {
    running.child.stdin.write("\n");
  }
  const { status, stdout, stderr } = await running.done;
  assert.equal(status, 0, stderr + stdout.join("\n"));
  assert.equal(stdout.at(-1), "stopped");
});

test("with no hand-off made since the build began, the film hand-off scenario makes one before it judges anything, and says what it made", { skip }, () => {
  const scenarios = readFileSync(join(repoRoot, "scripts", "scenarios", "Weir.Scenarios.Deluno.ps1"), "utf8");
  for (const scenario of ["deluno_film_handoff", "deluno_release_with_extra", "deluno_outcomes"]) {
    assert.match(scenarios, new RegExp(`Scenario_${scenario} \\{[\\s\\S]*?\\n {4}(\\$split = )?Get-OrMakeBuildFilmHandoffs`), `${scenario} makes the hand-off it needs`);
  }
  assert.match(scenarios, /Scenario_deluno_resend \{[\s\S]*?New-DelunoFilmHandoff/, "the resend scenario makes the hand-off it sends again");
  assert.doesNotMatch(scenarios, /New-NotApplicable[^\n]*(hand-off|library)/, "no Deluno scenario skips for want of a library or a hand-off");
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

test("the Logs page excuses the Activity entry a deliberate refused sign-in leaves, by the title the page gives it, for that scenario alone", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-expected-page-"));
  const home = join(folder, "home");
  const run = join(folder, "run");
  mkdirSync(join(home, "logs"), { recursive: true });
  mkdirSync(join(run, "evidence"), { recursive: true });
  const refusedAt = (second) => JSON.stringify({ level: "WARNING", message: "auth event: login failed", timestamp: `2026-10-20T09:40:${second}Z` });
  // The two rows System > Logs lists for one refused sign-in, as recorded in the run that found this: the server's own warning, and the
  // Activity entry for the same event.
  const rows = [
    "[pscustomobject]@{ source = 'server'; id = 'server:27'; at = '2026-10-20T09:40:42.719037Z'; title = 'auth event: login failed' }",
    "[pscustomobject]@{ source = 'event'; id = 'event:17'; at = '2026-10-20T09:40:42Z'; title = 'Sign-in failed' }",
  ].join(", ");
  const named = "Add-ExpectedLogRow -Pattern 'auth event: login failed' -PageTitlePattern '^Sign-in failed$' -Why 'the forgotten password is tried once'";
  const lines = psFile([
    "$ErrorActionPreference = 'Stop'",
    ...dotSource("Lib", "Catalog", "Context", "Install", "Tray", "FreshWeir", "WeirOnly"),
    `$script:Ctx.RunFolder = '${run}'`,
    `$script:Ctx.RuntimeHome = '${home}'`,
    `$script:Ctx.InstallRoot = '${join(folder, "install")}'`,
    "$script:Ctx.Base = 'http://localhost:1'",
    "$script:Ctx.KnownLogRows = @()",
    "$script:Ctx.Session = 'a session'",
    "$script:Ctx.Rehearsal = $true",
    "$script:Rows = @()",
    "function Get-WeirJson { param($Session, $Path) [pscustomobject]@{ items = $script:Rows; next_cursor = $null } }",
    "function Show($label, $block) { try { $r = & $block; $label + ': PASSED ' + $r } catch { $label + ': FAILED ' + $_.Exception.Message } }",
    `$server = Join-Path '${home}' 'logs\\weir.log'`,
    `Set-Content -LiteralPath $server -Value '${JSON.stringify({ level: "INFO", message: "started", timestamp: "2026-10-20T09:00:00Z" })}'`,
    "function Begin { $script:Rows = @(); Start-LogWatch; Set-LogWatchPage }",
    // Named with the page title: both rows are excused.
    "Begin",
    named,
    `Add-Content -LiteralPath $server -Value '${refusedAt("10")}'`,
    `$script:Rows = @(${rows})`,
    "Show 'named' { Complete-LogWatch }",
    // Named without it: the Activity entry is still seen, as it was in the run.
    "Begin",
    "Add-ExpectedLogRow -Pattern 'auth event: login failed' -Why 'the forgotten password is tried once'",
    `Add-Content -LiteralPath $server -Value '${refusedAt("20")}'`,
    `$script:Rows = @(${rows})`,
    "Show 'page-title-not-named' { Complete-LogWatch }",
    // The next scenario has no excuse.
    "Begin",
    `Add-Content -LiteralPath $server -Value '${refusedAt("30")}'`,
    "Show 'next-scenario' { Complete-LogWatch }",
    // A different Activity entry is not excused by the sign-in's title, nor is that title excused in the server log file.
    "Begin",
    named,
    `$script:Rows = @(${rows}, [pscustomobject]@{ source = 'event'; id = 'event:18'; at = '2026-10-20T09:41:00Z'; title = 'Sign-in failed again' })`,
    `Add-Content -LiteralPath $server -Value '${refusedAt("40")}'`,
    "Show 'another-entry' { Complete-LogWatch }",
    "Begin",
    named,
    `Add-Content -LiteralPath $server -Value '${JSON.stringify({ level: "WARNING", message: "Sign-in failed", timestamp: "2026-10-20T09:42:00Z" })}'`,
    "Show 'file-title' { Complete-LogWatch }",
  ]);
  assert.match(lines[0], /^named: PASSED .*named as caused on purpose: the forgotten password is tried once/);
  assert.match(lines[1], /^page-title-not-named: FAILED System > Logs gained no unexpected warning or error \(found 1: Sign-in failed\)$/);
  assert.match(lines[2], /^next-scenario: FAILED the server log file gained no unexpected warning or error \(found 1: WARNING auth event: login failed\)$/);
  assert.match(lines[3], /^another-entry: FAILED System > Logs gained no unexpected warning or error \(found 1: Sign-in failed again\)$/);
  assert.match(lines[4], /^file-title: FAILED the server log file gained no unexpected warning or error \(found 1: WARNING Sign-in failed\)$/);
});
