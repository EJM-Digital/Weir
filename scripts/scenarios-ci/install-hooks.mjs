// install-hooks: Setup run over a Weir, the way another program installs a new version. Velopack ends what runs from the install, then runs the
// tray's after-install hook, which logs that it ran, ends only the processes of this install, and arranges for Weir to start again once Setup has
// exited, but only when Weir was running. Written for CI: the VM suite installed Weir over nothing.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir } from "node:fs/promises";
import { join } from "node:path";

import { choose } from "./tray-actions.mjs";
import { lineWith, linesWith } from "./tray-log.mjs";
import { menu } from "./tray-words.mjs";
import { expectRowsOfAServerEndedOnPurpose, installedAndStopped, PORT, restoreWeir, RunningTray, startTray } from "./tray-world.mjs";
import { ensureSignedIn, expectBuildUnderTest, START_CEILING_MS, waitReady, wait } from "./windows-install.mjs";

/** The lines the tray writes in its log for an install, as apps/tray/Weir.Tray/Program.cs, InstallProcesses.cs and RestartAfterSetup.cs word them. */
export const hookLines = {
  afterInstall: /^Velopack: after install v\S+$/,
  stopped: /^Velopack after install v\S+: stopped (Weir|WeirServer)(?:\.exe)? \(pid (\d+)\) at (.+?)(?: cleanly)?\.$/,
  left: (pid) => new RegExp(`^Velopack after install v\\S+: left Weir \\(pid ${pid}\\) running: .+ is not part of this install \\(`),
  willStart: /^Weir was running before this install\. It starts again once Setup \(pid \d+\) has exited, unless Setup starts it\.$/,
  leftStopped: "Weir was not running from this install before Setup, so it is left stopped.",
};

// A program that lives for ten minutes at most; each wait of the scenario ends the moment it has what it waits for.
const OTHER_WEIR_LIFETIME_MS = 600_000;

/**
 * Another program called Weir.exe, run from a folder of its own: what a portable copy or a developer build is to a PC that also has the
 * install. It is a copy of the Node that runs the scenario, so it holds the name and nothing else of Weir; it is ended by its process id.
 */
async function startOtherWeir(world) {
  const folder = join(world.scratch, "another-weir");
  await mkdir(folder, { recursive: true });
  const executable = join(folder, "Weir.exe");
  await copyFile(process.execPath, executable);
  const child = spawn(executable, ["-e", `setTimeout(() => {}, ${OTHER_WEIR_LIFETIME_MS})`], { detached: true, stdio: "ignore", windowsHide: true });
  child.unref();
  await wait(world, async () => ((await world.machine.isRunning(child.pid)) ? true : undefined), "the other Weir.exe to run", START_CEILING_MS, 250);
  return { pid: child.pid, executable };
}

/** What the after-install hook stopped, from the lines it logged. */
export function stoppedByTheHook(lines) {
  return lines.flatMap((line) => {
    const found = hookLines.stopped.exec(line);
    return found ? [{ name: found[1], pid: Number(found[2]), path: found[3] }] : [];
  });
}

/** Whether the path is inside the folder, without regard to case. */
function isInside(path, folder) {
  return path.toLowerCase().startsWith(`${folder.toLowerCase()}\\`);
}

/** install-hooks: Setup --silent over a running Weir brings it back and ends only its own; over a Weir that was quit, it leaves it stopped. */
export async function installHooks(world) {
  const { machine } = world;
  expectRowsOfAServerEndedOnPurpose(world, "Setup ends the running server before the new version is installed");
  await installedAndStopped(world);
  // The start that follows Setup is given no port of its own: it finds the one the person saved.
  await machine.writeToDataFolder("port.txt", String(PORT));
  const tray = await startTray(world, ["--port", String(PORT), "--no-browser"]);
  let other;
  const facts = [];
  try {
    const serverBefore = await tray.ready();
    await ensureSignedIn(world);
    other = await startOtherWeir(world);
    const ownBefore = [tray.pid, serverBefore];

    let mark = await tray.lineCount();
    const setup = await machine.install(world.setupPath);
    assert.equal(setup.exitCode, 0, `Setup --silent over the running Weir exited ${setup.exitCode}, not 0`);
    let lines = (await tray.log()).slice(mark);
    assert.ok(lineWith(lines, hookLines.afterInstall), "the tray log does not show the after-install hook ran");
    const stopped = stoppedByTheHook(lines);
    for (const one of stopped) {
      assert.ok(ownBefore.includes(one.pid), `the hook stopped ${one.name} (pid ${one.pid}), which is neither the tray (${tray.pid}) nor the server (${serverBefore}) this install was running`);
      assert.ok(isInside(one.path, machine.paths.installRoot), `the hook stopped ${one.path}, which is not inside the install (${machine.paths.installRoot})`);
    }
    assert.ok(lineWith(lines, hookLines.left(other.pid)), `the hook does not say it left the Weir.exe (pid ${other.pid}) that runs from outside the install`);
    assert.equal(await machine.isRunning(other.pid), true, `the Weir.exe that runs from ${other.executable} was ended by Setup or its hook`);
    for (const pid of ownBefore) assert.equal(await machine.isRunning(pid), false, `process ${pid} of the install that was running before Setup is still running`);
    assert.ok(lineWith(lines, hookLines.willStart), "the hook does not say Weir starts again once Setup has exited");

    await waitReady(world);
    await expectBuildUnderTest(world);
    await ensureSignedIn(world);
    const after = await machine.processes();
    const trayAfter = after.find((process) => process.name === "Weir.exe");
    const serverAfter = after.find((process) => process.name === "WeirServer.exe");
    assert.ok(trayAfter && trayAfter.pid !== tray.pid, "the tray that came back after Setup is not a new one");
    assert.ok(serverAfter && serverAfter.pid !== serverBefore && serverAfter.parentPid === trayAfter.pid, "the server after Setup is not a new one started by the new tray");
    assert.equal(after.filter((process) => process.name === "Weir.exe").length, 1, "more than one tray runs after Setup");
    facts.push(
      `Setup --silent over a running Weir exited 0 in ${setup.seconds} s; the hook logged that it ran${stopped.length > 0 ? ` and stopped ${stopped.map((one) => `${one.name} (pid ${one.pid})`).join(" and ")}, both of the install` : " (Setup had already ended the install's processes, so it had none to stop)"}, ` +
        `left the Weir.exe (pid ${other.pid}) that runs from another folder running, and arranged the restart: the tray (pid ${tray.pid}) and server (pid ${serverBefore}) were gone and a new tray (pid ${trayAfter.pid}) and server (pid ${serverAfter.pid}) answered /ready on port ${PORT} with no browser`,
    );

    // Quit is an orderly end, which clears the mark: Setup over a Weir that was quit leaves it quit.
    const quit = await runningTrayAt(world, trayAfter.pid);
    await choose(world, quit, menu.quit);
    await quit.exited("the tray to end after Quit");
    mark = await tray.lineCount();
    const again = await machine.install(world.setupPath);
    assert.equal(again.exitCode, 0, `Setup --silent over the quit Weir exited ${again.exitCode}, not 0`);
    lines = (await tray.log()).slice(mark);
    assert.ok(lineWith(lines, hookLines.afterInstall), "the tray log does not show the after-install hook ran the second time");
    assert.equal(linesWith(lines, hookLines.willStart).length, 0, "the hook arranged a start of a Weir that had been quit");
    assert.ok(lineWith(lines, hookLines.leftStopped), "the hook does not say it left a Weir that was not running stopped");
    assert.deepEqual(await machine.processes(), [], "Weir runs although it was quit before Setup");
    assert.equal(await machine.isRunning(other.pid), true, "the Weir.exe that runs from another folder was ended by the second Setup");
    facts.push("Setup --silent over a Weir that had been quit exited 0, the hook said it left Weir stopped, and nothing of the install ran");
  } finally {
    if (other) await machine.killProcess(other.pid);
    await machine.removeFromDataFolder("port.txt");
    await restoreWeir(world);
  }
  return `${facts.join("; ")}.`;
}

/** A handle on a tray that is already running, for the menu: the scenarios choose items through its process id. */
async function runningTrayAt(world, pid) {
  const mark = (await world.machine.readText(world.machine.paths.trayLog)).length;
  return new RunningTray(world, { pid, exit: Promise.resolve(0) }, mark);
}
