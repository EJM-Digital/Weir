// The person at the desktop: the tray's icon, its menu and its windows, reached through Windows UI Automation (tray-ui.ps1), and the click on
// a balloon, which the notification area sends as a message. Nothing here waits; the scenarios own every wait, so each ends the moment what it
// waits for has happened.
import { execFile, spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const SCRIPT = fileURLToPath(new URL("./tray-ui.ps1", import.meta.url));
const COMMAND_CEILING_MS = 120_000;

function shellArguments(args, sta) {
  return ["-NoProfile", "-NonInteractive", ...(sta ? ["-STA"] : []), "-ExecutionPolicy", "Bypass", ...args];
}

function run(args, { sta = false } = {}) {
  return new Promise((resolve, reject) => {
    execFile("powershell.exe", shellArguments(args, sta), { windowsHide: true, timeout: COMMAND_CEILING_MS, maxBuffer: 32 * 1024 * 1024 }, (error, stdout, stderr) => {
      if (error) reject(new Error(`powershell ${args.join(" ")} failed: ${error.message}\n${stderr}`.trim(), { cause: error }));
      else resolve(stdout.trim());
    });
  });
}

const scriptArguments = (command, options) => ["-File", SCRIPT, command, ...Object.entries(options).flatMap(([name, value]) => [`-${name}`, String(value)])];

async function ui(command, options = {}) {
  const text = await run(scriptArguments(command, options));
  return text ? JSON.parse(text) : null;
}

export class TrayDesktop {
  /** How the menu is opened: a right click on the icon in the notification area (`taskbar`), or the message that click sends (`message`). */
  route = "taskbar";

  /** Why the taskbar could not be used, when the menu is opened by message instead. */
  routeReason = null;

  session() {
    return ui("session");
  }

  /** Weir's icon in the notification area, or in the hidden icons when they are open: `[{name, bounds, area}]`, empty when it is not there. */
  icons() {
    return ui("icons");
  }

  /** Opens the hidden icons, unless they are open already. */
  showHidden() {
    return ui("show-hidden");
  }

  /** Right-clicks the icon (opening the hidden icons first when it is in them), or sends the click's message. Returns what was done. */
  openMenu(trayProcessId) {
    return ui("open-menu", { TrayProcessId: trayProcessId, Via: this.route });
  }

  /** Clicks the icon once, or twice in one gesture; `{found: false}` when the icon is not at hand. */
  clickIcon(clicks = "single") {
    return ui("click-icon", { Clicks: clicks });
  }

  /** The open menu of the tray process (`{items: [{name, enabled, toggle}]}`), or null when none is open. */
  menu(trayProcessId) {
    return ui("menu", { TrayProcessId: trayProcessId });
  }

  /** Clicks (or toggles) the item with exactly this text, and returns once the tray has handled the click. */
  invoke(trayProcessId, item) {
    return ui("invoke", { TrayProcessId: trayProcessId, Item: item });
  }

  /**
   * Clicks an item whose handler does not return until the person has answered a window (Change port), so the click is left running; the
   * returned object ends it. The click is over once the window is closed.
   */
  invokeAndLeave(trayProcessId, item) {
    const child = spawn("powershell.exe", shellArguments(scriptArguments("invoke", { TrayProcessId: trayProcessId, Item: item }), false), { stdio: "ignore", windowsHide: true });
    const ended = new Promise((resolve) => child.on("exit", resolve));
    return { ended, stop: () => child.kill() };
  }

  closeMenu() {
    return ui("close-menu");
  }

  /** The window the tray has open (`{title, texts, edits, buttons, options}`), or null. */
  window(trayProcessId) {
    return ui("window", { TrayProcessId: trayProcessId });
  }

  setWindowText(trayProcessId, value) {
    return ui("window-set", { TrayProcessId: trayProcessId, Value: value });
  }

  pressInWindow(trayProcessId, item) {
    return ui("window-press", { TrayProcessId: trayProcessId, Item: item });
  }

  /** Sends the tray the click Windows sends for the balloon on screen; the tray does what that balloon offered. */
  clickBalloon(trayProcessId) {
    return ui("click-balloon", { TrayProcessId: trayProcessId });
  }

  /** The text of the notifications on screen. */
  balloons() {
    return ui("balloons");
  }

  /** The tree of the taskbar or the desktop, for a failure's record. */
  dump(root = "desktop", depth = 4) {
    return ui("dump", { Root: root, Depth: depth });
  }

  /** What the clipboard holds as text. */
  clipboardText() {
    return run(["-Command", "Get-Clipboard -Raw"], { sta: true });
  }
}
