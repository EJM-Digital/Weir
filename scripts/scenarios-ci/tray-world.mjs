// What every scenario of the windows-tray shard does with the tray it starts: the tray in a known state of the data folder, a handle on the
// process with its log and its server, and the waits that end the moment the tray has said what the scenario waits for.
import assert from "node:assert/strict";
import { watch } from "node:fs";
import net from "node:net";

import { ORIGIN } from "../docker-smoke/stack.mjs";
import { WeirClient } from "../docker-smoke/weir-client.mjs";
import { httpServer } from "./windows-shard.mjs";
import { ensureSignedIn, installBuildUnderTest, PORT, START_CEILING_MS, startInstalledWeir, wait } from "./windows-install.mjs";
import { lineWith, linesAfter, linesWith, serverProcessIds } from "./tray-log.mjs";

/** The files the tray and the server leave in the data folder that decide how the next start goes. */
const START_STATE = ["port.txt", "current-port.txt", "lan-access", "pause-request.json", "update-check-now", "update-download-now", "update-apply-now"];
export const BALLOON_CEILING_MS = 60_000;
export const STOP_CEILING_MS = 90_000;
export const GIVE_UP_CEILING_MS = 300_000;
const BROWSER_CEILING_MS = 60_000;

/** The address of Weir on a port, as a person types it and as the tray opens it. */
export const originOf = (port) => `http://localhost:${port}`;
export const serverOn = (port) => httpServer(originOf(port));

/** Installs the build under test when it is not installed, and stops whatever of it is running; the data folder is left as the earlier scenarios made it. */
export async function installedAndStopped(world) {
  const { machine } = world;
  if (!(await machine.exists(machine.paths.serverExe))) {
    await machine.stopWeir();
    await installBuildUnderTest(world);
  } else {
    await machine.stopWeir();
  }
  await machine.keepUpdatesFromInstalling({ onlyWhenAsked: true });
  await machine.removeFromDataFolder(...START_STATE);
}

/** Weir running on the shard's port with the account this run made, as the next scenario and the log watch expect to find it. */
export async function restoreWeir(world) {
  await world.machine.stopWeir();
  await startInstalledWeir(world);
  await ensureSignedIn(world);
}

/** A tray the scenario started: its process, and what it has written to tray-host.log since. */
export class RunningTray {
  #world;
  #mark;

  constructor(world, { pid, exit }, mark) {
    this.#world = world;
    this.#mark = mark;
    this.pid = pid;
    this.exit = exit;
  }

  /** What the tray has logged since it started, one line each, without time stamps. */
  async log() {
    return linesAfter(await this.#world.machine.readText(this.#world.machine.paths.trayLog), this.#mark);
  }

  /** The point in the log from here on: lines logged after this call. */
  async markNow() {
    this.#mark = (await this.#world.machine.readText(this.#world.machine.paths.trayLog)).length;
  }

  /** Waits for a line that holds the text or matches the pattern, and returns it. `after` lines to skip: those already seen. */
  async line(wanted, { what, ceilingMs = START_CEILING_MS, after = 0 }) {
    return wait(this.#world, async () => lineWith((await this.log()).slice(after), wanted), what ?? `the tray to log ${wanted}`, ceilingMs, 500);
  }

  /** How many lines the log holds now. */
  async lineCount() {
    return (await this.log()).length;
  }

  async linesHolding(wanted) {
    return linesWith(await this.log(), wanted);
  }

  /** The id of the server process the tray started last. */
  async serverPid() {
    const pids = serverProcessIds(await this.log());
    assert.ok(pids.length > 0, "the tray has not logged a server process id yet");
    return pids.at(-1);
  }

  /** Waits until the server answers /ready on the port, and returns the process id of the server the tray started last. */
  async ready(port = PORT) {
    await wait(this.#world, async () => ((await serverOn(port).ready()) ? true : undefined), `Weir to answer /ready on port ${port}`, START_CEILING_MS, 500);
    return this.serverPid();
  }

  /** Waits for a server that is not the one with this id to answer, and returns its id. */
  async readyAfter(previousPid, port = PORT) {
    return wait(
      this.#world,
      async () => {
        const pid = serverProcessIds(await this.log()).at(-1);
        return pid !== undefined && pid !== previousPid && (await serverOn(port).ready()) ? pid : undefined;
      },
      `a new server to answer /ready on port ${port}`,
      START_CEILING_MS,
      500,
    );
  }

  /** Waits for the tray process to end, and returns its exit code. */
  async exited(what, ceilingMs = STOP_CEILING_MS) {
    return wait(this.#world, async () => ((await this.#world.machine.isRunning(this.pid)) ? undefined : this.exit), what, ceilingMs, 500);
  }

  /** Ends this tray and its server, by process id, whatever state it is in. */
  async end() {
    const { machine } = this.#world;
    await machine.stopWeir();
  }
}

/** Starts the installed tray with the arguments, and notes where its log stood. */
export async function startTray(world, args, env) {
  const { machine } = world;
  const mark = (await machine.exists(machine.paths.trayLog)) ? (await machine.readText(machine.paths.trayLog)).length : 0;
  return new RunningTray(world, machine.startTrayWith({ args, env }), mark);
}

/** A client of Weir on another port, signed in as the account this run made. */
export async function clientOn(world, port) {
  const client = new WeirClient(originOf(port));
  await client.login(world.account.username, world.account.password);
  return client;
}

export { ORIGIN, PORT };

/**
 * Waits for the icon of Weir in the notification area (the hidden icons are opened when it is in them; the icons of trays ended by force are
 * cleared first) and for it to say what `wanted` accepts. Returns the hover text. On failure it says what the icons said meanwhile.
 */
export async function iconSays(world, wanted = () => true, what = "the icon of Weir in the notification area", ceilingMs = START_CEILING_MS) {
  const seen = new Set();
  return wait(
    world,
    async () => {
      const found = await world.desktop.icons();
      for (const icon of found) seen.add(icon.name);
      if (found.length === 0) {
        await world.desktop.showHidden();
        return undefined;
      }
      if (found.length > 1) {
        await world.desktop.hoverIcons();
        return undefined;
      }
      return wanted(found[0].name) ? found[0].name : undefined;
    },
    what,
    ceilingMs,
    500,
  ).catch((error) => {
    throw new Error(`${error.message} The icons said: ${[...seen].join(" | ") || "nothing"}.`, { cause: error });
  });
}

/** The hover text of the icon, once it is there. */
export const iconText = (world, { startingOnly = false } = {}) =>
  iconSays(world, (text) => !startingOnly || text.includes("Starting"), startingOnly ? "the icon to say Weir is starting" : "the icon of Weir in the notification area");

/** Waits for a browser to be started on this address, and returns it: { pid, name, commandLine }. */
export async function browserOn(world, port, path = "/") {
  const url = `http://127.0.0.1:${port}${path}`;
  return wait(
    world,
    async () => (await world.machine.browsersShowing(url))?.[0],
    `a browser to be started on ${url}`,
    BROWSER_CEILING_MS,
    500,
  );
}

/** The browsers started on any address of Weir's ports, ended by their process ids, so the next check starts clean. */
export async function closeBrowsers(world, ...ports) {
  for (const port of ports) {
    for (const browser of (await world.machine.browsersShowing(`127.0.0.1:${port}`)) ?? []) await world.machine.killProcess(browser.pid);
  }
}

/** Fails when a browser was started on any of Weir's addresses: nothing but a click opens one. */
export async function expectNoBrowser(world, why, ...ports) {
  for (const port of ports) {
    const found = (await world.machine.browsersShowing(`127.0.0.1:${port}`)) ?? [];
    assert.deepEqual(found.map((browser) => browser.commandLine), [], `a browser was started on port ${port} ${why}`);
  }
}

/**
 * Another program listening on the port, as a person's other software would: the server and the tray find it taken. It waits for the port to
 * be free first (a server that has just ended lets go of it a moment later) and returns an object to let it go.
 */
export async function holdPort(world, port) {
  const listener = net.createServer();
  const connections = new Set();
  listener.on("connection", (socket) => {
    connections.add(socket);
    socket.on("close", () => connections.delete(socket));
  });
  await wait(
    world,
    () =>
      new Promise((resolve) => {
        listener.once("error", () => resolve(undefined));
        listener.listen(port, "127.0.0.1", () => resolve(true));
      }),
    `port ${port} to be free for another program to take`,
    START_CEILING_MS,
    100,
  );
  return {
    port,
    // Whatever connected to it (the tray looking for its server) is let go too, or the port is not free until that is closed.
    release: () =>
      new Promise((resolve) => {
        listener.close(() => resolve());
        for (const socket of connections) socket.destroy();
      }),
  };
}

/**
 * Does something the moment the tray logs a line, without waiting for the next look: a file watch on the log, so a scenario can take a port in
 * the gap between the tray's saying it will use it and the server's trying. `stop` ends the watch; `done` settles with the action's result.
 */
export function atLogLine(world, tray, wanted, action, skip = 0, ceilingMs = START_CEILING_MS) {
  let stopped = false;
  let watcher;
  let timer;
  const done = new Promise((resolve, reject) => {
    let running = false;
    // A hang guard: the line never came.
    timer = setTimeout(() => {
      stopped = true;
      watcher.close();
      reject(new Error(`the tray did not log ${wanted} within ${ceilingMs / 1000} s.`));
    }, ceilingMs);
    const look = async () => {
      if (stopped || running) return;
      running = true;
      try {
        if (lineWith((await tray.log()).slice(skip), wanted) !== undefined) {
          stopped = true;
          watcher.close();
          clearTimeout(timer);
          resolve(await action());
        }
      } catch (error) {
        stopped = true;
        watcher.close();
        clearTimeout(timer);
        reject(error);
      } finally {
        running = false;
      }
    };
    watcher = watch(world.machine.paths.runtimeHome, (_, name) => {
      if (name === "tray-host.log") void look();
    });
    // The line may have been logged between the mark and the watch.
    void look();
  });
  return {
    done,
    stop: () => {
      stopped = true;
      clearTimeout(timer);
      watcher.close();
    },
  };
}
