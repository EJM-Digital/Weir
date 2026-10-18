// Running the windows-install shard: the machine, Weir as a browser sees it, and the record of what each scenario printed and what the
// install logged. The machine is a clean GitHub-hosted runner; nothing here is meant for a PC that has a Weir of its own.
import { randomBytes } from "node:crypto";
import { copyFile, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { ORIGIN } from "../docker-smoke/stack.mjs";
import { WeirClient } from "../docker-smoke/weir-client.mjs";
import { runScenarios } from "./run.mjs";
import { SCENARIOS, watchLogs } from "./windows-install.mjs";

/** The server as an outsider asks it: is it ready, and which version does it report. */
export function httpServer(origin) {
  return {
    async ready() {
      try {
        const answer = await fetch(`${origin}/ready`, { signal: AbortSignal.timeout(3000), headers: { Connection: "close" } });
        return (await answer.json()).ready === true;
      } catch {
        return false;
      }
    },
    async version() {
      const answer = await fetch(`${origin}/openapi.json`, { signal: AbortSignal.timeout(10_000), headers: { Connection: "close" } });
      return (await answer.json()).info.version;
    },
  };
}

/**
 * The version the build under test must report: the one the package build was given (as the Windows package smoke reads it), or, for the
 * placeholder build, the one the server project declares.
 * @param {{given?: string, props: string}} source `props` is the text of apps/server/Directory.Build.props.
 */
export function expectedVersion({ given, props }) {
  const named = (given ?? "").trim().replace(/^v/, "");
  if (named) return named;
  const declared = /<WeirVersion>([^<]+)<\/WeirVersion>/.exec(props)?.[1].trim();
  if (!declared) throw new Error("WeirVersion is not in apps/server/Directory.Build.props, and no version was given.");
  return declared;
}

/** Keeps what the install logged with the record, without the secrets that sit beside it. */
async function keepInstallLogs(machine, logFolder) {
  const kept = join(logFolder, "install-logs");
  await mkdir(kept, { recursive: true });
  const files = [{ from: machine.paths.trayLog, name: "tray-host.log" }];
  for (const file of await machine.files(machine.paths.logsFolder)) files.push({ from: join(machine.paths.logsFolder, file.path), name: `logs-${file.path.replaceAll(/[\/]/g, "-")}` });
  for (const file of files) await copyFile(file.from, join(kept, file.name)).catch((error) => console.error(`::warning::${file.from} could not be kept: ${error.message}`));
}

/**
 * Runs the chosen scenarios on the machine against the Setup. One that fails does not stop the next. Weir is stopped at the end, whatever
 * happened, so the runner is left as it was.
 */
export async function runWindowsShard({ ids, machine, setupPath, version, logFolder }) {
  const recordings = join(logFolder, "recordings");
  await mkdir(recordings, { recursive: true });
  const scratch = await mkdtemp(join(tmpdir(), "weir-scenarios-ci-"));
  const world = {
    machine,
    server: httpServer(ORIGIN),
    weir: new WeirClient(ORIGIN),
    browser: () => new WeirClient(ORIGIN),
    setupPath,
    expectedVersion: version,
    runId: randomBytes(3).toString("hex"),
    scratch,
    watchLogs,
    record: (name, text) => writeFile(join(recordings, name), text),
  };
  try {
    return await runScenarios({ ids, registry: SCENARIOS, world });
  } finally {
    await keepInstallLogs(machine, logFolder).catch((error) => console.error(`::warning::The install's logs could not be kept: ${error.message}`));
    await machine.stopWeir().catch((error) => console.error(`::warning::Weir could not be stopped: ${error.message}`));
    await rm(scratch, { recursive: true, force: true });
  }
}

