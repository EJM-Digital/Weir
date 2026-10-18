// Running the windows-install shard: the machine, Weir as a browser sees it, and the record of what each scenario printed and what the
// install logged. The machine is a clean GitHub-hosted runner; nothing here is meant for a PC that has a Weir of its own.
import { randomBytes } from "node:crypto";
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { REPOSITORY_URL } from "../repository.mjs";
import { ORIGIN } from "../docker-smoke/stack.mjs";
import { WeirClient } from "../docker-smoke/weir-client.mjs";
import { GithubRedirect } from "./github-redirect.mjs";
import { GithubStandIn } from "./github-stand-in.mjs";
import { runScenarios } from "./run.mjs";
import { TrayDesktop } from "./tray-desktop.mjs";

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
async function keepInstallLogs(machine, logFolder, name = "install-logs") {
  const kept = join(logFolder, name);
  await mkdir(kept, { recursive: true });
  const files = [
    { from: machine.paths.trayLog, name: "tray-host.log" },
    { from: join(machine.paths.runtimeHome, "server-host.log"), name: "server-host.log" },
  ];
  for (const file of await machine.files(machine.paths.logsFolder)) files.push({ from: join(machine.paths.logsFolder, file.path), name: `logs-${file.path.replaceAll(/[\/]/g, "-")}` });
  for (const file of files) await copyFile(file.from, join(kept, file.name)).catch((error) => console.error(`::warning::${file.from} could not be kept: ${error.message}`));
}

/** The release the shard updates to, from the `releases.win.json` vpk wrote: its full package and the delta against the release. */
export function packagesOf(releasesJson) {
  const assets = JSON.parse(releasesJson).Assets;
  const full = assets.find((asset) => asset.Type === "Full");
  const delta = assets.find((asset) => asset.Type === "Delta");
  if (!full || !delta) throw new Error("The feed of the build under test needs a full package and a delta package; it holds " + assets.map((asset) => asset.FileName).join(", "));
  return { full, delta };
}

/**
 * Starts the stand-in for GitHub's release feed and sends the PC's requests for github.com to it. Returns the stand-in and what ends it all.
 * @param {{feedFolder: string, previousFeed: string, previousVersion: string, version: string, scratch: string}} options
 */
async function startGithub({ feedFolder, previousFeed, previousVersion, version, scratch }) {
  const feed = new GithubStandIn({
    folder: feedFolder,
    current: { tag: `v${version}`, releasesJson: await readFile(join(feedFolder, "releases.win.json"), "utf8") },
    previous: { tag: `v${previousVersion}`, releasesJson: await readFile(previousFeed, "utf8") },
    updated: new Date().toISOString().replace(/\.\d+Z$/, "Z"),
  });
  const redirect = new GithubRedirect();
  const stop = async () => {
    await redirect.undo().catch((error) => console.error(`::warning::The PC could not be put back as it was: ${error.message}`));
    await feed.close();
  };
  try {
    const certificate = await redirect.trustCertificate(scratch);
    await feed.listen({ tls: certificate, port: 443 });
    await redirect.redirect();
    const answer = await redirect.fetchAsThePcDoes(`${REPOSITORY_URL}/releases.atom`);
    if (answer.status !== 200 || !answer.text.includes(`/releases/tag/v${version}`)) {
      throw new Error(`The PC's request for github.com did not reach the stand-in (status ${answer.status}).`);
    }
    feed.requests.length = 0;
  } catch (error) {
    await stop();
    throw error;
  }
  return { feed, stop };
}

/**
 * Runs the chosen scenarios of a shard (`registry` and `watchLogs` are its module's) on the machine against the Setup. One that fails does not stop the next. Weir is stopped at the end, whatever
 * happened, so the runner is left as it was. The windows-update shard also gets `update`: the feed of the build under test, the release to
 * install first and its Setup.
 * @param {{update?: {feedFolder: string, previousSetup: string, previousFeed: string, previousVersion: string}}} options
 */
export async function runWindowsShard({ ids, registry: scenarios, watchLogs, machine, setupPath, version, logFolder, update: updating }) {
  const recordings = join(logFolder, "recordings");
  await mkdir(recordings, { recursive: true });
  const scratch = await mkdtemp(join(tmpdir(), "weir-scenarios-ci-"));
  const world = {
    machine,
    desktop: new TrayDesktop(),
    server: httpServer(ORIGIN),
    weir: new WeirClient(ORIGIN),
    browser: () => new WeirClient(ORIGIN),
    setupPath,
    expectedVersion: version,
    runId: randomBytes(3).toString("hex"),
    scratch,
    cleanups: [],
    watchLogs,
    record: (name, text) => writeFile(join(recordings, name), text),
  };
  let github = null;
  let registry = scenarios;
  try {
    if (updating) {
      const releasesJson = await readFile(join(updating.feedFolder, "releases.win.json"), "utf8");
      github = await startGithub({ ...updating, version, scratch });
      Object.assign(world, { feed: github.feed, feedFolder: updating.feedFolder, previousSetup: updating.previousSetup, previousVersion: updating.previousVersion, release: packagesOf(releasesJson) });
      registry = Object.fromEntries(
        Object.entries(scenarios).map(([id, scenario]) => [
          id,
          async (given) => {
            try {
              return await scenario(given);
            } finally {
              await keepInstallLogs(machine, join(logFolder, "scenario-logs"), id).catch((error) => console.error(`::warning::The logs of ${id} could not be kept: ${error.message}`));
              await given.record(`github-requests-${id}.json`, JSON.stringify(github.feed.requests, null, 2));
            }
          },
        ]),
      );
    }
    return await runScenarios({ ids, registry, world });
  } finally {
    await github?.stop();
    await keepInstallLogs(machine, logFolder).catch((error) => console.error(`::warning::The install's logs could not be kept: ${error.message}`));
    await machine.stopWeir().catch((error) => console.error(`::warning::Weir could not be stopped: ${error.message}`));
    await rm(scratch, { recursive: true, force: true });
  }
}
