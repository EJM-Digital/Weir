// What the windows-files scenarios share: the installed server and its ffmpeg, servers of their own on ports of their own, the volumes and
// shares made for a scenario (all taken down when the shard ends), and the library-mode steps that scan, clean and wait.
import assert from "node:assert/strict";
import { dirname, join } from "node:path";

import { pollUntil } from "../docker-smoke/poll.mjs";
import { LogWatch } from "./log-watch.mjs";
import { WeirServerProcess } from "./files-server.mjs";
import { packagedTools } from "./files-media.mjs";
import { Volumes } from "./files-volumes.mjs";
import { installBuildUnderTest } from "./windows-install.mjs";
import { json } from "./windows-machine.mjs";

const FIRST_PORT = 9361;

/**
 * Ends every process that runs from the installed Weir's folder: the server and the ffmpeg it started. A server ended the way a crash ends it
 * leaves its ffmpeg behind, and a process with a file open on a volume keeps that volume's virtual disk from being released.
 */
export function stopProcessesOf(folder) {
  return json(
    `Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($env:SCENARIO_FOLDER + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue`,
    { FOLDER: folder },
  );
}

export const wait = (world, check, what, ceilingMs, intervalMs = 1000) => pollUntil(check, { what, ceilingMs, intervalMs, clock: world.clock });

/** The shard's way of watching the logs: each scenario reads the logs of the servers it starts itself, so the runner has nothing to read. */
export async function watchLogs(world) {
  world.logExpectations = [];
  return {
    expect: (title, options) => world.logExpectations.push({ title, options }),
    expectMatching: (pattern, options) => world.logExpectations.push({ pattern, options }),
    finish: async () => {},
  };
}

/**
 * The installed Weir's server and ffmpeg, the volumes this shard makes and what must be taken down at the end. Installs the build under test
 * when it is not installed; the tray is not started, because these scenarios start the server themselves.
 */
export async function filesWorld(world) {
  if (world.files) return world.files;
  const { machine } = world;
  if (!(await machine.exists(machine.paths.serverExe))) {
    await machine.stopWeir();
    const setup = await installBuildUnderTest(world);
    assert.equal(setup.exitCode, 0, `Setup --silent exited ${setup.exitCode}, not 0`);
  }
  const reported = await machine.serverVersion();
  assert.equal(reported, world.expectedVersion, `WeirServer.exe --version says ${reported}, not the build under test (${world.expectedVersion})`);
  const serverFolder = dirname(machine.paths.serverExe);
  const volumes = new Volumes({ scratch: join(world.scratch, "volumes"), clock: world.clock });
  let port = FIRST_PORT;
  const files = {
    serverExe: machine.paths.serverExe,
    serverFolder,
    tools: packagedTools(serverFolder),
    volumes,
    nextPort: () => port++,
  };
  world.files = files;
  world.cleanups.push(() => volumes.removeAll());
  world.cleanups.push(() => stopProcessesOf(serverFolder));
  return files;
}

/** A server of its own: its own data folder, its own port, ended with the shard whatever happens. */
export async function newServer(world, name) {
  const files = await filesWorld(world);
  const server = new WeirServerProcess({
    exe: files.serverExe,
    home: join(world.scratch, name, "home"),
    port: files.nextPort(),
    output: join(world.scratch, name, "console.log"),
    clock: world.clock,
    baseEnvironment: files.serverEnvironment,
  });
  world.cleanups.push(() => server.kill());
  world.serverConsoles ??= [];
  world.serverConsoles.push({ name, server });
  return server;
}

/** The server's logs gained no warning or error since it began, apart from the rows the scenario said it would cause. */
export async function expectCleanLogs(world, server) {
  const watch = LogWatch.sinceStart(server.weir);
  for (const { title, pattern, options } of world.logExpectations ?? []) {
    if (pattern) watch.expectMatching(pattern, options);
    else watch.expect(title, options);
  }
  await watch.finish();
}

// --- library mode ---------------------------------------------------------------------------------------------------------------

const slashes = (path) => path.replaceAll("\\", "/").toLowerCase();

/** A movie workflow whose library folder is the one given, with the settings library mode reads. */
export async function makeLibrary(world, weir, { name, base, libraryFolder, keepOriginal = false, originalsFolder }) {
  const folders = { watched_folder: join(base, "watched"), work_folder: join(base, "work"), output_folder: join(base, "output") };
  await world.machine.ensureFolders(...Object.values(folders), libraryFolder);
  const created = await weir.createLibrary({ name, media_type: "movie", ...folders, skip_access_tests: true, minimum_free_disk_space_mb: 0 });
  const settings = { library_folders: [libraryFolder], keep_original_after_clean: keepOriginal, ...(originalsFolder ? { originals_folder: originalsFolder } : {}) };
  const saved = await weir.write("PUT", `/processing/libraries/${created.id}/library-settings`, settings);
  assert.equal(saved.status, 200, `saving the library settings was answered ${saved.status}: ${saved.text}`);
  return { id: created.id, libraryFolder };
}

async function libraryFiles(weir, library) {
  return (await weir.get(`/processing/libraries/${library.id}/library-files?page_size=200`)).files;
}

export const rowOf = async (weir, library, path) => (await libraryFiles(weir, library)).find((row) => slashes(row.path) === slashes(path));

/** Asks for a scan and waits until it has found the file, with the status it has once looked at. */
export async function scanUntil(world, weir, library, path, status) {
  const asked = await weir.write("POST", `/processing/libraries/${library.id}/library-scan`, {});
  assert.equal(asked.status, 200, `asking for a scan was answered ${asked.status}: ${asked.text}`);
  return wait(world, async () => {
    const row = await rowOf(weir, library, path);
    return row?.status === status ? row : undefined;
  }, `the scan to find ${path} as ${status}`, 180_000);
}

/** Asks for the file to be cleaned, with the confirmation the dialog asks for. */
export async function requestClean(weir, library, path) {
  const answer = await weir.write("POST", `/processing/libraries/${library.id}/library-files/clean`, { paths: [path], confirm_final_removal: true });
  assert.equal(answer.status, 200, `Clean was answered ${answer.status}: ${answer.text}`);
}
