// tray-supervision and tray-give-up: the real tray with the real server under it. The server is ended by its own process id, the way a crash
// ends it, and its port is taken by another program. The tray brings it back after waits that grow and says why each start failed; when it
// cannot, it gives up once, tells the person, and Restart Weir is the way back. All written for CI: the VM suite never ended a server.
import assert from "node:assert/strict";

import { choose, statusLine } from "./tray-actions.mjs";
import { backoffProblem, failedStarts, lineWith, linesWith } from "./tray-log.mjs";
import { GIVE_UP_CEILING_MS, holdPort, installedAndStopped, PORT, restoreWeir, startTray } from "./tray-world.mjs";
import { balloonLine, balloons, menu, status } from "./tray-words.mjs";
import { ensureSignedIn, wait } from "./windows-install.mjs";

/** The server warns in its log, for every start that follows a failed one, that it is starting again. */
const STARTING_AGAIN = /^Weir is starting again: this is start attempt \d+/;
const WARNING_ROWS_CEILING = 30;

function expectStartingAgainWarnings(world, why) {
  world.logWatch.expectMatching(STARTING_AGAIN, { times: WARNING_ROWS_CEILING, why });
}

/** The failed starts logged after the mark, with the waits between them judged: each lies where the policy puts it, and they grow. */
function expectGrowingWaits(failures, what) {
  const waits = failures.filter((failure) => !failure.gaveUp);
  for (const failure of waits) {
    const problem = backoffProblem(failure.failures, failure.waitSeconds);
    assert.equal(problem, null, `${what}: ${problem}`);
  }
  for (const [index, failure] of failures.entries()) {
    assert.equal(failure.failures, index + 1, `${what}: the ${index + 1}th failure was counted as the ${failure.failures}th`);
  }
}

async function onlyOneServerRuns(world) {
  const servers = (await world.machine.processes()).filter((process) => process.name === "WeirServer.exe");
  assert.equal(servers.length, 1, `${servers.length} servers are running, not one`);
}

/** tray-supervision: a server that is killed three times comes back each time, a different process, after longer waits, with the reason logged. */
export async function traySupervision(world) {
  const { machine } = world;
  expectStartingAgainWarnings(world, "the server is ended on purpose and the tray starts it again");
  await installedAndStopped(world);
  const tray = await startTray(world, ["--port", String(PORT), "--no-browser"]);
  const facts = [];
  let orphan = 0;
  let orphaned = false;
  try {
    let pid = await tray.ready();
    await ensureSignedIn(world);

    for (let kill = 1; kill <= 3; kill++) {
      await machine.killProcess(pid);
      const failures = await wait(world, async () => (failedStarts(await tray.log()).length >= kill ? failedStarts(await tray.log()) : undefined), `the tray to log failed start ${kill}`, 60_000, 500);
      assert.match(failures[kill - 1].reason, /^exited with code \d+ after running /, `the tray says why the server stopped (${failures[kill - 1].reason})`);
      const next = await tray.readyAfter(pid);
      expectGrowingWaits(failedStarts(await tray.log()), `after kill ${kill}`);
      assert.notEqual(next, pid, "the server that came back is the same process");
      pid = next;
      await onlyOneServerRuns(world);
    }
    const log = await tray.log();
    assert.equal(linesWith(log, /Giving up/).length, 0, "the tray gave up although the server came back each time");
    const started = lineWith(log, /^Weir started on attempt \d+, after 3 failed attempts: /);
    assert.ok(started, "the tray does not say that Weir started only on a retry, after three failures");
    assert.equal(await machine.readText(`${machine.paths.runtimeHome}\\current-port.txt`), String(PORT), "current-port.txt names the port the server is on");
    assert.equal(await statusLine(world, tray), status.running(PORT), "the icon says Weir is running again");
    // The tray goes, and its server is left running.
    orphan = pid;
    await machine.killProcess(tray.pid);
    orphaned = true;
    await tray.exited("the tray to end");
    assert.ok((await machine.processes()).some((process) => process.pid === orphan), "the server went with the tray, so there is no orphan to find");
    facts.push(
      `the server (pid ${pid}) was ended by its own process id three times; each time the tray logged why ("exited with code"), waited ${failedStarts(log)
        .map((failure) => `${failure.waitSeconds} s`)
        .join(", ")} (growing, inside the policy's bounds), started another server and Weir answered /ready again, with one server running`,
    );
  } finally {
    if (!orphaned) await tray.end();
  }

  // The tray ends without stopping its server, as a crashed tray does: the next one finds the server it left and stops it.
  const second = await startTray(world, ["--port", String(PORT), "--no-browser"]);
  try {
    await second.line(new RegExp(`^Startup \\(orphaned server check\\): stopped WeirServer\\.exe \\(pid ${orphan}\\)`), { what: "the new tray to stop the server the last one left" });
    const pid = await second.ready();
    assert.notEqual(pid, orphan, "the server the old tray left is still the one running");
    await onlyOneServerRuns(world);
    facts.push(`a tray that ended without stopping its server (pid ${orphan}) left it running, and the next tray stopped it before starting its own`);
    await machine.killProcess(pid);
    const holder = await holdPort(world, PORT);
    let held = true;
    try {
      const failures = await wait(world, async () => (failedStarts(await second.log()).length >= 3 ? failedStarts(await second.log()) : undefined), "the tray to log three failed starts while another program held the port", 120_000, 500);
      assert.ok(failures.slice(1).every((failure) => /code 75/.test(failure.reason)), `the starts that found the port taken say so with the server's own exit code (${failures.map((failure) => failure.reason).join(" | ")})`);
      await holder.release();
      held = false;
      await second.readyAfter(pid);
    } finally {
      if (held) await holder.release();
    }
    const log = await second.log();
    expectGrowingWaits(failedStarts(log), "with the port held");
    assert.equal(linesWith(log, /Giving up/).length, 0, "the tray gave up before the port was free again");
    assert.ok(lineWith(log, /^Weir started on attempt \d+, after \d+ failed attempts: /), "the tray does not say that Weir started only on a retry");
    facts.push("with another program holding the port the restarts failed with the server's own exit code (75) and kept trying, with growing waits, and Weir came up as soon as the port was free");
  } finally {
    await second.end();
    await restoreWeir(world);
  }
  return `${facts.join("; ")}.`;
}

/** tray-give-up: five failures in a row end the trying once, with the red icon and a balloon; Restart Weir brings Weir back with a fresh budget. */
export async function trayGiveUp(world) {
  const { machine } = world;
  expectStartingAgainWarnings(world, "the server is ended on purpose and the tray starts it again");
  await installedAndStopped(world);
  const tray = await startTray(world, ["--port", String(PORT), "--no-browser"]);
  try {
    const pid = await tray.ready();
    await ensureSignedIn(world);
    await machine.killProcess(pid);
    const holder = await holdPort(world, PORT);
    let held = true;
    try {
      await tray.line(/^Start attempt \d+ failed: .*Giving up \(5 of 5 failures allowed within 15 min\)/, { what: "the tray to give up", ceilingMs: GIVE_UP_CEILING_MS });
      const failures = failedStarts(await tray.log());
      assert.equal(failures.length, 5, `the tray made ${failures.length} failed starts, not five`);
      expectGrowingWaits(failures, "before it gave up");
      const gave = linesWith(await tray.log(), /Giving up/);
      assert.equal(gave.length, 1, "the tray gave up more than once");
      assert.ok(gave[0].includes(`Close the program that is using port ${PORT}, or use Change port, then use Restart Weir.`), `the give-up line does not say which port or what to do: ${gave[0]}`);
      const balloon = balloonLine("error", "Weir", balloons.stoppedPortInUse(PORT, 5));
      await tray.line(balloon, { what: "the balloon that tells the person Weir stopped" });
      assert.equal(linesWith(await tray.log(), "Weir stopped and didn't start again").length, 1, "the person was told more than once");
      assert.equal(await statusLine(world, tray), status.stoppedPortInUse(PORT), "the menu does not say why Weir is stopped");
      assert.equal(await world.server.ready(), false, "Weir answers although the tray says it stopped");

      // Restart Weir is the way back, once the port is free.
      await holder.release();
      held = false;
      const mark = await tray.lineCount();
      await world.desktop.clickBalloon(tray.pid);
      await tray.line("Restart: restarting the server on port 9347 (only this PC can connect).", { what: "the click on the balloon to restart the server", after: mark });
      const back = await tray.readyAfter(pid);
      assert.equal(await statusLine(world, tray), status.running(PORT), "the icon does not say Weir is running after Restart Weir");

      // The watchdog is watching again: another crash is met with another start, counted from one.
      const afterRestart = await tray.lineCount();
      await machine.killProcess(back);
      const again = await wait(world, async () => failedStarts((await tray.log()).slice(afterRestart)).at(0), "the tray to log the failed start after Restart Weir", 60_000, 500);
      assert.equal(again.failures, 1, `after Restart Weir the tray counted ${again.failures} failures, not a fresh one`);
      const last = await tray.readyAfter(back);

      // Quit is a decision, not a crash: the server is stopped and not started again.
      const quitMark = await tray.lineCount();
      await choose(world, tray, menu.quit);
      assert.equal(await tray.exited("the tray to end after Quit"), 0, "the tray did not end with exit code 0");
      const ending = (await tray.log()).slice(quitMark);
      assert.ok(ending.some((line) => line.startsWith(`Bundled server host pid=${last} stopped cleanly in`)), "Quit did not stop the server cleanly");
      assert.equal(linesWith(ending, /^Start attempt|^Restart:|Starting bundled server host/).length, 0, "the tray started the server again after Quit");
    } finally {
      if (held) await holder.release();
    }
  } finally {
    await tray.end();
    await restoreWeir(world);
  }
  return (
    `The server was ended and its port taken: the tray tried five times with growing waits, gave up once ("Giving up (5 of 5 failures"), told the person with the balloon "${balloons.stoppedPortInUse(PORT, 5)}", ` +
    `and its menu said "${status.stoppedPortInUse(PORT)}"; after the port was freed, a click on the balloon ("Click to try again") restarted Weir; another crash was met with another start, counted from one (a fresh budget); Quit stopped the server cleanly and nothing started it again.`
  );
}
