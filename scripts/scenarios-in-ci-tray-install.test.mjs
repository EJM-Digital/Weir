// node --test scripts/scenarios-in-ci-tray-install.test.mjs
// What the windows-tray shard's install-hooks and tray-portable scenarios decide without a Windows PC: the words they expect against the C# and
// the web source that write them, and how they read the hook's lines. Setup, the tray and Windows Firewall are the windows-tray job of ci-packaging.yml.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { hookLines, stoppedByTheHook } from "./scenarios-ci/install-hooks.mjs";
import { linesAfter } from "./scenarios-ci/tray-log.mjs";
import { CANNOT_UPDATE_DETAIL, NOT_INSTALLED_LINE } from "./scenarios-ci/tray-portable.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(join(repository, path), "utf8");

const program = read("apps/tray/Weir.Tray/Program.cs");
const processes = read("apps/tray/Weir.Tray/InstallProcesses.cs");
const restart = read("apps/tray/Weir.Tray/RestartAfterSetup.cs");

test("the lines the install-hooks scenario looks for are written by the tray's source", () => {
  assert.ok(program.includes('TrayLog.Write($"Velopack: after install v{v}")'));
  assert.ok(program.includes('KillRunningProcesses($"Velopack after install v{v}")'));
  assert.ok(processes.includes("{why}: stopped {server.Name} (pid {server.Process.Id}) at {server.Executable} cleanly."));
  assert.ok(processes.includes("{why}: stopped {own.Name} (pid {own.Process.Id}) at {own.Executable}."));
  assert.ok(processes.includes("{why}: left {name} (pid {proc.Id}) running: {executable ?? \"unknown path\"} is not part of this install ({root})."));
  assert.ok(restart.includes("Weir was running before this install. It starts again once Setup (pid {setup}) has exited, unless Setup starts it."));
  assert.ok(restart.includes(`"${hookLines.leftStopped}"`));
  assert.ok(processes.includes('private const string TrayName = "Weir";'));
});

test("the hook's lines are read as the tray writes them", () => {
  const lines = linesAfter(
    [
      "[2026-10-19 10:00:01] Velopack: after install v1.0.0-rc.19",
      "[2026-10-19 10:00:01] Velopack after install v1.0.0-rc.19: stopped WeirServer (pid 4120) at C:\\Users\\runner\\AppData\\Local\\Weir\\current\\server\\WeirServer.exe cleanly.",
      "[2026-10-19 10:00:02] Velopack after install v1.0.0-rc.19: stopped Weir (pid 3300) at C:\\Users\\runner\\AppData\\Local\\Weir\\current\\Weir.exe.",
      "[2026-10-19 10:00:02] Velopack after install v1.0.0-rc.19: left Weir (pid 5588) running: D:\\scratch\\another-weir\\Weir.exe is not part of this install (C:\\Users\\runner\\AppData\\Local\\Weir\\current).",
      "[2026-10-19 10:00:02] Weir was running before this install. It starts again once Setup (pid 2210) has exited, unless Setup starts it.",
      "",
    ].join("\n"),
  );
  assert.ok(lines.some((line) => hookLines.afterInstall.test(line)));
  assert.deepEqual(stoppedByTheHook(lines), [
    { name: "WeirServer", pid: 4120, path: "C:\\Users\\runner\\AppData\\Local\\Weir\\current\\server\\WeirServer.exe" },
    { name: "Weir", pid: 3300, path: "C:\\Users\\runner\\AppData\\Local\\Weir\\current\\Weir.exe" },
  ]);
  assert.ok(lines.some((line) => hookLines.left(5588).test(line)));
  assert.ok(!lines.some((line) => hookLines.left(3300).test(line)), "a process the hook stopped is not one it left");
  assert.ok(lines.some((line) => hookLines.willStart.test(line)));
});

test("the portable scenario expects the words the tray and the page use", () => {
  assert.ok(read("apps/tray/Weir.Tray/TrayUpdates.cs").includes(`"${NOT_INSTALLED_LINE}"`));
  assert.ok(read("apps/web/src/pages/system/tabs/about/update-actions.tsx").includes(CANNOT_UPDATE_DETAIL));
  assert.ok(read("apps/server/src/Weir.Api/Endpoints/SuiteUpdateEndpoints.cs").includes(`"${CANNOT_UPDATE_DETAIL}"`));
});
