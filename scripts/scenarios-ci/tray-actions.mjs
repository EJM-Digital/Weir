// What a person does at the tray, as the scenarios ask for it: open the menu, read it, choose an item. Each step waits for what it needs to
// have happened (the icon, the menu, the item enabled) and ends the moment it has.
import assert from "node:assert/strict";

import { wait } from "./windows-install.mjs";

const MENU_CEILING_MS = 60_000;
const ITEM_CEILING_MS = 30_000;
const POLL_MS = 500;

/** The menu of the tray as the standard lays it out, in order, with the state of each item while Weir runs (docs/tray-standard.md). */
export function runningMenu({ port, version, address = `http://localhost:${port}` }) {
  return [
    { name: "Open Weir", enabled: true },
    { name: `Weir - Running at ${address}`, enabled: false },
    { name: "Pause processing", enabled: true },
    { name: "Restart Weir", enabled: true },
    { name: "Copy address", enabled: true },
    { name: "Allow other devices on your network...", enabled: true },
    { name: "Only allow this PC", enabled: false },
    { name: `Change port (${port})...`, enabled: true },
    { name: "Open data folder", enabled: true },
    { name: "Open logs folder", enabled: true },
    { name: "Start with Windows", enabled: true },
    { name: "Check for updates", enabled: true },
    { name: `Weir v${version}`, enabled: false },
    { name: "Report a problem...", enabled: true },
    { name: "Quit Weir", enabled: true },
  ];
}

/** How the menu differs from the one expected, in words; nothing when it is the same, item for item. */
export function menuProblems(expected, actual) {
  const problems = [];
  if (actual.length !== expected.length) problems.push(`the menu has ${actual.length} items, not ${expected.length}: ${actual.map((item) => item.name).join(" | ")}`);
  for (const [index, want] of expected.entries()) {
    const got = actual[index];
    if (!got) continue;
    if (got.name !== want.name) problems.push(`item ${index + 1} says "${got.name}", not "${want.name}"`);
    else if (got.enabled !== want.enabled) problems.push(`"${got.name}" is ${got.enabled ? "enabled" : "disabled"}, not ${want.enabled ? "enabled" : "disabled"}`);
  }
  return problems;
}

const TASKBAR_CEILING_MS = 45_000;
const APPEAR_CEILING_MS = 8_000;

async function openByRoute(world, tray, ceilingMs) {
  const { desktop } = world;
  return wait(
    world,
    async () => {
      const open = await desktop.menu(tray.pid);
      if (open) return open.items;
      const done = await desktop.openMenu(tray.pid);
      // The hidden icons opened first; the next look finds the icon in them.
      if (done.found === false) return undefined;
      return wait(world, async () => (await desktop.menu(tray.pid))?.items, "the menu to appear", APPEAR_CEILING_MS, 400).catch(() => undefined);
    },
    `the menu of the tray to open (${desktop.route})`,
    ceilingMs,
    POLL_MS,
  );
}

/**
 * Opens the menu of the tray and returns its items. It is a right click on the icon in the notification area; if the hosted desktop will not
 * let the icon be reached that way, the same click is sent to the tray as the message the notification area sends it, once, with what was
 * found kept in the record.
 */
export async function openMenu(world, tray) {
  const { desktop } = world;
  if (desktop.route === "message") return openByRoute(world, tray, MENU_CEILING_MS);
  try {
    return await openByRoute(world, tray, TASKBAR_CEILING_MS);
  } catch (error) {
    desktop.route = "message";
    desktop.routeReason = error.message;
    await world.record("taskbar-tree.json", JSON.stringify(await desktop.dump("taskbar", 8).catch((failure) => ({ failed: failure.message })), null, 1));
    return openByRoute(world, tray, MENU_CEILING_MS);
  }
}

/** Closes the menu, if one is open. */
export async function closeMenu(world, tray) {
  if (await world.desktop.menu(tray.pid)) await world.desktop.closeMenu();
  await wait(world, async () => ((await world.desktop.menu(tray.pid)) ? undefined : true), "the menu to close", ITEM_CEILING_MS, POLL_MS);
}

/** Chooses an item of the menu by the words on it: opens the menu, waits for the item to be enabled, and clicks it. */
export async function choose(world, tray, name, { click = false } = {}) {
  await openMenu(world, tray);
  await wait(
    world,
    async () => {
      const item = (await world.desktop.menu(tray.pid))?.items.find((entry) => entry.name === name);
      assert.ok(item, `the menu has no item "${name}"`);
      return item.enabled ? item : undefined;
    },
    `"${name}" to be enabled`,
    ITEM_CEILING_MS,
    POLL_MS,
  );
  await (click ? world.desktop.clickItem(tray.pid, name) : world.desktop.invoke(tray.pid, name));
}

/** The status line of the menu, the words the icon shows when the pointer rests on it. */
export async function statusLine(world, tray) {
  const items = await openMenu(world, tray);
  const line = items[1].name;
  await closeMenu(world, tray);
  return line;
}

/** Waits until the status line of the menu says this, and returns it; on failure it says what the menu said last. */
export async function statusIs(world, tray, expected, what, ceilingMs = 120_000) {
  let last = "nothing";
  return wait(
    world,
    async () => {
      last = await statusLine(world, tray);
      return last === expected ? last : undefined;
    },
    what,
    ceilingMs,
    1000,
  ).catch((error) => {
    throw new Error(`${error.message} The menu said: ${last}.`, { cause: error });
  });
}
