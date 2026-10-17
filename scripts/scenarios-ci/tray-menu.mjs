// tray-menu: every item of the tray's menu, pressed through Windows UI Automation on the real installed tray, with the effect on the real server,
// on the files the tray and the server share, on the PC (the clipboard, File Explorer, the registry, the browser) and in tray-host.log, which
// keeps every balloon the tray showed. Written for CI: the VM suite never pressed a menu item.
import assert from "node:assert/strict";

import { choose, closeMenu, menuProblems, openMenu, runningMenu, statusLine } from "./tray-actions.mjs";
import { closeBrowsers, browserOn, expectNoBrowser, iconText, installedAndStopped, PORT, restoreWeir, serverOn, startTray } from "./tray-world.mjs";
import { balloonLine, balloons, menu, status } from "./tray-words.mjs";
import { ensureSignedIn, wait } from "./windows-install.mjs";
import { linesWith } from "./tray-log.mjs";

const UPDATE_CEILING_MS = 180_000;
const sameFolder = (a, b) => a.replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase() === b.replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase();

/** The version the menu's version line shows: without the build metadata after a plus sign. */
export const menuVersion = (version) => version.split("+")[0];

async function expectFolderOpened(world, folder, what) {
  const { machine } = world;
  await wait(world, async () => ((await machine.explorerFolders()) ?? []).some((open) => sameFolder(open, folder)) || undefined, `File Explorer to show the ${what} (${folder})`, 60_000, 500);
  await machine.closeExplorerFolders();
}

/** The person presses the menu's items one after the other, and what each should do has happened. */
export async function trayMenu(world) {
  const { machine, desktop, weir } = world;
  const facts = [];
  await installedAndStopped(world);
  // A start the person made: no --silent and no --no-browser, on a desktop, so the tray tells them with a balloon.
  const tray = await startTray(world, ["--port", String(PORT)]);
  try {
    // The icon is up at once; the server may not have answered yet.
    const first = await iconText(world);
    assert.match(first, /^Weir - /, `the icon of Weir says "${first}"`);
    await tray.ready();
    await ensureSignedIn(world);
    await tray.line(balloonLine("info", "Weir", balloons.running(PORT)), { what: "the tray to tell the person Weir is running" });
    await wait(world, async () => ((await iconText(world)) === status.running(PORT) ? true : undefined), "the icon to say Weir is running", 120_000, 1000);
    facts.push(`the icon was in the notification area ("${first}") and then said "${status.running(PORT)}", and the tray told the person Weir was running`);
    await expectNoBrowser(world, "although the start was only announced", PORT);

    // The menu as the standard lays it out.
    const items = await openMenu(world, tray);
    const problems = menuProblems(runningMenu({ port: PORT, version: menuVersion(world.expectedVersion) }), items);
    assert.deepEqual(problems, [], `the menu is not the one the standard describes: ${problems.join("; ")}`);
    const toggle = items.find((item) => item.name === menu.startWithWindows);
    assert.equal(toggle.toggle, "Off", `Start with Windows is a check item that starts off (it is ${toggle.toggle})`);
    facts.push(`the menu held its ${items.length} items in order, with Weir v${menuVersion(world.expectedVersion)} and Start with Windows as a check item that starts off (menu opened by ${desktop.route})`);
    await closeMenu(world, tray);

    // Copy address.
    await choose(world, tray, menu.copyAddress);
    await wait(world, async () => ((await desktop.clipboardText()).trim() === `http://localhost:${PORT}` ? true : undefined), "the clipboard to hold the address", 30_000, 500);
    facts.push(`Copy address put http://localhost:${PORT} on the clipboard`);

    // Pause processing, and Resume processing.
    await choose(world, tray, menu.pause);
    await tray.line("Pause processing asked for from the tray menu.", { what: "the tray to log the pause request" });
    await wait(world, async () => ((await weir.get("/pause")).paused ? true : undefined), "Weir to pause on the tray's request", 60_000, 500);
    await wait(world, async () => ((await statusLine(world, tray)) === status.paused ? true : undefined), `the menu to say "${status.paused}"`, 60_000, 500);
    await openMenu(world, tray);
    assert.ok((await desktop.menu(tray.pid)).items.some((item) => item.name === menu.resume && item.enabled), "the menu offers Resume processing while paused");
    await closeMenu(world, tray);
    await choose(world, tray, menu.resume);
    await tray.line("Resume processing asked for from the tray menu.", { what: "the tray to log the resume request" });
    await wait(world, async () => ((await weir.get("/pause")).paused ? undefined : true), "Weir to resume on the tray's request", 60_000, 500);
    facts.push("Pause processing paused the real server and the menu then said Paused and offered Resume processing; Resume processing resumed it");

    // The folders, and Report a problem (which shows the logs).
    await choose(world, tray, menu.openDataFolder);
    await expectFolderOpened(world, machine.paths.runtimeHome, "data folder");
    await choose(world, tray, menu.openLogsFolder);
    await expectFolderOpened(world, machine.paths.logsFolder, "logs folder");
    await choose(world, tray, menu.reportProblem);
    await expectFolderOpened(world, machine.paths.logsFolder, "logs folder for Report a problem");
    facts.push("Open data folder, Open logs folder and Report a problem... each opened File Explorer on the right folder");

    // Start with Windows: a real check item, the menu stays open, and the Run entry follows it.
    await choose(world, tray, menu.startWithWindows);
    await wait(world, async () => ((await machine.runEntry()) ? true : undefined), "the Run entry to be made", 30_000, 500);
    const turnedOn = (await desktop.menu(tray.pid))?.items.find((item) => item.name === menu.startWithWindows);
    assert.equal(turnedOn?.toggle, "On", "the menu stayed open and shows Start with Windows on");
    await desktop.invoke(tray.pid, menu.startWithWindows);
    await wait(world, async () => ((await machine.runEntry()) ? undefined : true), "the Run entry to be taken away", 30_000, 500);
    assert.equal((await desktop.menu(tray.pid))?.items.find((item) => item.name === menu.startWithWindows)?.toggle, "Off", "the menu shows Start with Windows off again");
    await closeMenu(world, tray);
    facts.push("Start with Windows made the Run entry and stayed open showing it on, and took the entry away again");

    // Check for updates: the check runs, and an update found is announced with a balloon whose click opens System > About.
    await choose(world, tray, menu.checkForUpdates);
    await tray.line("Checking for updates...", { what: "the tray to check for updates" });
    const offer = await tray.line(/^Balloon \(info\) Weir Update: Version \S+ is available\./, { what: "the tray to announce the update it found", ceilingMs: UPDATE_CEILING_MS });
    const offered = /Version (\S+) is available/.exec(offer)[1];
    assert.equal(offer, balloonLine("info", "Weir Update", balloons.updateAvailable(offered)), "the update balloon says what the tray's words say");
    await desktop.clickBalloon(tray.pid);
    await tray.line("Opening Weir in browser on port 9347 (source=tray-update-balloon)", { what: "the click on the update balloon to open Weir" });
    const aboutPage = await browserOn(world, PORT, "/system?tab=about");
    facts.push(`Check for updates found v${offered}, the balloon said so, and a click on it opened ${aboutPage.commandLine.match(/http\S+/)[0]}`);
    await closeBrowsers(world, PORT);

    // Open Weir, and the click on the icon.
    await choose(world, tray, menu.openWeir);
    await tray.line("Opening Weir in browser on port 9347 (source=tray)", { what: "Open Weir to open the browser" });
    const opened = await browserOn(world, PORT);
    assert.ok(opened.commandLine.includes(`http://127.0.0.1:${PORT}/`), `the browser was opened on ${opened.commandLine}`);
    await closeBrowsers(world, PORT);
    const before = (await linesWith(await tray.log(), "Opening Weir in browser")).length;
    assert.equal((await desktop.clickIcon("double")).found, true, "the icon was found to click");
    await tray.line("(source=tray-click)", { what: "a click on the icon to open Weir", after: 0 });
    const gesture = (await tray.log()).filter((line) => line.includes("source=tray-click") || line.includes("source=tray-dblclick"));
    assert.equal(gesture.filter((line) => line.startsWith("Opening Weir")).length, 1, `a click and a double-click in one gesture open one window (${gesture.join(" | ")})`);
    assert.ok((await linesWith(await tray.log(), "Opening Weir in browser")).length > before, "the click opened a browser");
    await browserOn(world, PORT);
    await closeBrowsers(world, PORT);
    facts.push("Open Weir opened http://127.0.0.1:9347/, and one click-and-double-click gesture on the icon opened one window");

    // Restart Weir: a clean stop, a new server on the same port, the icon starting in between.
    const oldPid = await tray.serverPid();
    const mark = await tray.lineCount();
    await choose(world, tray, menu.restart);
    await tray.line("Restart Weir chosen.", { what: "the tray to log the restart" });
    await iconText(world, { startingOnly: true });
    const newPid = await tray.readyAfter(oldPid);
    const stopping = (await tray.log()).slice(mark);
    assert.ok(stopping.some((line) => line.startsWith(`Bundled server host pid=${oldPid} stopped cleanly in`)), "the old server was asked to stop and stopped by itself");
    assert.ok(!stopping.some((line) => line.includes("killing it")), "no server was killed");
    await wait(world, async () => ((await iconText(world)) === status.running(PORT) ? true : undefined), "the icon to say Weir is running again", 120_000, 1000);
    facts.push(`Restart Weir stopped server ${oldPid} cleanly, started ${newPid} on the same port, and the icon said Starting meanwhile`);

    // Quit: the server is stopped by its own stop request, the tray ends, and the icon goes.
    const quitMark = await tray.lineCount();
    await choose(world, tray, menu.quit);
    const code = await tray.exited("the tray to end after Quit");
    assert.equal(code, 0, `the tray ended with exit code ${code}`);
    const ending = (await tray.log()).slice(quitMark);
    assert.ok(ending.some((line) => line === "Quit requested from tray icon"), "the tray logged the Quit");
    assert.ok(ending.some((line) => line.startsWith(`Bundled server host pid=${newPid} stopped cleanly in`)), "Quit stopped the server cleanly");
    assert.ok(!ending.some((line) => line.includes("killing it") || line.startsWith("Start attempt")), "Quit did not kill the server or start it again");
    assert.equal(await serverOn(PORT).ready(), false, "the server is gone");
    await wait(world, async () => ((await desktop.icons()).length === 0 ? true : undefined), "the icon to leave the notification area", 60_000, 500);
    facts.push("Quit stopped the server cleanly, ended the tray with exit code 0 and took the icon away");
  } finally {
    await closeBrowsers(world, PORT);
    await restoreWeir(world);
  }
  if (desktop.routeReason) facts.push(`the taskbar could not be used (${desktop.routeReason}), so the menu was opened by the message the notification area sends`);
  return `${facts.join("; ")}.`;
}
