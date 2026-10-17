#!/usr/bin/env node
// Starts Weir the way the Docker install page tells people to, with the compose.yaml in this repository as it ships (its cap_drop, its
// capabilities and the UID/GID entrypoint), on its named volume, and proves what the install promises.
//
//   restart      PLAT-14: create the first account, a workflow and a saved setting; `docker compose restart`; then `down` and `up` again
//                (the volume stays). The account signs in and the workflow and setting are as they were.
//   upgrade      PLAT-16: the same on the previous release's image, then `down` and the build under test on the same volume. It opens the
//                database, migrates it when the build's schema is newer, and leaves a copy of the old database in backups/pre-update.
//                A release with a predecessor whose image cannot be pulled fails; only the first release has nothing to upgrade from.
//   recover      PLAT-20: create the first account, a workflow and a saved setting; run the recovery command the way the Docker page tells a
//                locked-out person to (`docker exec -u weir -e WEIR_HOME=/data/weir weir /opt/weir/Weir recover`). The password is reset, the
//                forgotten one is refused, the new one signs in (also after a restart), the earlier session is ended, the workflow and setting
//                are kept, and what the command printed is kept in the logs folder.
//   entrypoint   The four below, one after the other, each on a stack of its own.
//   identity     SET-E39, PLAT-15: WEIR_PUID and WEIR_PGID remap the weir account; the server runs as it; the data volume and the two
//                generated secrets belong to it; the secrets are the same after a restart and a new container.
//   data-folder  SET-E39, PLAT-15: a data folder bind-mounted from the host, which Docker made as root, is handed to the ids given, so the
//                user who gave them reads the database and secrets; and handed on to other ids with the database kept.
//   refusals     SET-E39, PLAT-15: ids of 0, ids that are not numbers, a short session secret and malformed settings end the start.
//   media        SET-E38, PLAT-15, FL-29: files passed through into a bind-mounted output folder arrive owned by the ids given, with the
//                output modes; the settings for the watched and work folders change nothing there.
//   arm64        PLAT-13: the linux/arm64 image, under QEMU, answers /health, carries working tools and creates the first account.
//
// Usage: node scripts/docker-compose-smoke.mjs <scenario> --candidate <local image>
//                                              [--releasing <version being released>] [--previous <image>] [--logs <folder>]
// `--releasing` picks the previous release as the newest one older than it; without it, the newest published release is the previous
// one. Releases come from `gh api` (GH_TOKEN, GITHUB_REPOSITORY); `--previous <image>` names the image directly.
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { parseArgs } from "node:util";

import { arm64Starts } from "./docker-smoke/arm64-scenario.mjs";
import { headRevision } from "./docker-smoke/database.mjs";
import { refusesBadSettings, remapsAndKeepsSecrets, takesOverTheDataFolder } from "./docker-smoke/identity-scenarios.mjs";
import { deliversIntoMediaFolder } from "./docker-smoke/media-folder-scenario.mjs";
import { composeOverride } from "./docker-smoke/override.mjs";
import { previousRelease } from "./docker-smoke/previous-image.mjs";
import { recoverResetsPassword } from "./docker-smoke/recover.mjs";
import { restartKeepsData, upgradeKeepsData } from "./docker-smoke/scenarios.mjs";
import { run, runForStatus, shellQuote } from "./docker-smoke/shell.mjs";
import { WeirClient } from "./docker-smoke/weir-client.mjs";
import { REPOSITORY } from "./repository.mjs";

const ORIGIN = "http://localhost:9347";
const SCHEMA_MIGRATOR = "apps/server/src/Weir.Infrastructure/Sqlite/SchemaMigrator.cs";
// An entrypoint that wrongly accepts a start would run the server, which never ends: give a refusal this long to come.
const REFUSAL_CEILING_MS = 120_000;
const ENTRYPOINT_SCENARIOS = ["identity", "data-folder", "refusals", "media"];
const SCENARIOS = ["restart", "upgrade", "recover", "entrypoint", ...ENTRYPOINT_SCENARIOS, "arm64"];

/** The shipped compose.yaml, run with the image under test. The file is never copied or edited; a scenario may lay an override over it. */
class Stack {
  #image = "";
  #logFolder;
  #scratch;
  #files = ["compose.yaml"];
  #overrides = 0;
  #stage = 0;

  /** @param {string} scratch A folder on this machine for the override files. */
  constructor(logFolder, scratch) {
    this.#logFolder = logFolder;
    this.#scratch = scratch;
  }

  #command(subcommand, files = this.#files) {
    return `docker compose ${files.map((file) => `-f ${shellQuote(file)}`).join(" ")} ${subcommand}`;
  }

  #compose(subcommand) {
    return run(this.#command(subcommand), { env: { WEIR_DOCKER_IMAGE: this.#image } });
  }

  pull(image) {
    run(`docker pull ${image}`);
  }

  /**
   * Creates the stack. `--pull never` for the image CI built (it is in no registry); `always` for a published one, as an operator's `up`
   * would pull it. `layers` (see composeOverride) are laid over the shipped file for this and the stack's later commands.
   */
  up(image, pull, layers = null) {
    this.#use(image, layers);
    this.#compose(`up -d --pull ${pull}`);
  }

  /** Makes this image, with these layers over the shipped file, the stack the following commands are for. */
  #use(image, layers) {
    this.#image = image;
    this.#files = ["compose.yaml"];
    if (layers) {
      this.#overrides += 1;
      const override = join(this.#scratch, `override-${this.#overrides}.yml`);
      writeFileSync(override, composeOverride(layers));
      this.#files.push(override);
    }
  }

  /**
   * One container from the service as it ships, with these variables added, run to its end without publishing the port. A start the
   * entrypoint refuses ends in seconds; `status` is null when it was still running at the ceiling.
   */
  startOnce(image, environment) {
    // `run` has no --pull option, and the shipped file says to pull always: the image CI built is in no registry.
    this.#use(image, { pullPolicy: "never" });
    const variables = Object.entries(environment).map(([name, value]) => `-e ${shellQuote(`${name}=${value}`)}`);
    return runForStatus(this.#command(`run --rm -T ${variables.join(" ")} weir`), { env: { WEIR_DOCKER_IMAGE: image }, timeoutMs: REFUSAL_CEILING_MS });
  }

  restart() {
    this.#compose("restart");
  }

  stop() {
    this.keepLogs();
    this.#compose("stop");
  }

  down() {
    this.keepLogs();
    this.#compose("down");
  }

  /** The stack and its volume, so the next scenario starts on a new one. */
  removeEverything() {
    run(this.#command("down --volumes --remove-orphans", ["compose.yaml"]), { env: { WEIR_DOCKER_IMAGE: this.#image || "weir-ci-smoke" }, allowFailure: true });
  }

  /**
   * Empties a folder the server's account made files in, which this user cannot: a container that runs as root does it, as it would
   * for an operator. Without an image the stack never started, so nothing was put there.
   */
  emptyAsRoot(folder) {
    if (!this.#image) return;
    run(`docker run --rm --entrypoint find -v ${shellQuote(folder)}:/target ${shellQuote(this.#image)} /target -mindepth 1 -delete`, { allowFailure: true });
  }

  /**
   * Waits for /health. The defaults suit a native start; an emulated one is given more attempts of its own.
   * @param {{attempts?: number, intervalSeconds?: number, attemptTimeoutSeconds?: number}} [wait]
   */
  waitHealthy({ attempts = 30, intervalSeconds = 2, attemptTimeoutSeconds = 5 } = {}) {
    const container = this.#compose("ps -a -q weir").trim();
    const wait = spawnSync(
      process.execPath,
      [
        "scripts/wait-for-health.mjs",
        ...["--url", `${ORIGIN}/health`, "--container", container],
        ...["--attempts", String(attempts), "--interval-seconds", String(intervalSeconds), "--attempt-timeout-seconds", String(attemptTimeoutSeconds)],
      ],
      { stdio: "inherit" },
    );
    if (wait.status !== 0) throw new Error("Weir did not become healthy.");
  }

  /** Runs a command in the container as the account the server runs as. */
  exec(command) {
    return this.execAs("weir", command);
  }

  /** Runs a command in the container as the given user (a name or a uid). */
  execAs(user, command) {
    return this.#compose(`exec -T -u ${shellQuote(user)} weir sh -c ${shellQuote(command)}`);
  }

  /** Like `exec`, with variables set for the command (`docker exec -e`), and its exit code, output and errors kept apart whatever the exit code. */
  execResult(command, variables = {}) {
    const flags = Object.entries(variables).map(([name, value]) => `-e ${shellQuote(`${name}=${value}`)}`).join(" ");
    return runForStatus(this.#command(`exec -T -u weir ${flags} weir ${command}`), { env: { WEIR_DOCKER_IMAGE: this.#image } });
  }

  /** What the server has logged since the stack was created; empty when there is no container to ask. */
  logs() {
    return run(this.#command("logs --no-color weir"), { env: { WEIR_DOCKER_IMAGE: this.#image || "weir-ci-smoke" }, allowFailure: true });
  }

  /** Copies a path out of the container (running or stopped) into a folder on this machine. */
  copyOut(containerPath, hostFolder) {
    mkdirSync(hostFolder, { recursive: true });
    run(`docker cp ${this.#compose("ps -a -q weir").trim()}:${containerPath} ${hostFolder}`);
  }

  /** Saves the server's log for the artifact the workflow uploads, numbered in the order the stack ran, named for its image. */
  keepLogs() {
    const logs = this.logs();
    if (!logs) return;
    this.#stage += 1;
    mkdirSync(this.#logFolder, { recursive: true });
    const image = this.#image.replace(/[^A-Za-z0-9.-]+/g, "-");
    writeFileSync(join(this.#logFolder, `${String(this.#stage).padStart(2, "0")}-${image}.log`), logs);
  }
}

function publishedReleaseTags() {
  const repository = process.env.GITHUB_REPOSITORY || REPOSITORY;
  const listing = run(`gh api --paginate repos/${repository}/releases --jq '.[] | select(.draft | not) | .tag_name'`);
  return listing.split(/\r?\n/).filter(Boolean);
}

async function upgrade(world, { releasing, previous, scratch }) {
  const from = previous ? { version: previous, image: previous } : previousRelease(publishedReleaseTags(), releasing);
  if (!from) {
    console.log("::notice::There is no earlier release to upgrade from, so there is nothing to upgrade.");
    return "There is no earlier release, so there was nothing to upgrade.";
  }
  const upgraded = await upgradeKeepsData({
    ...world,
    previous: from,
    headRevision: headRevision(readFileSync(SCHEMA_MIGRATOR, "utf8")),
    scratch,
  });
  return (
    `Upgraded from ${upgraded.from} (database at ${upgraded.revisionBefore}) with the account, workflow and setting kept` +
    (upgraded.migrated ? ", the database migrated and the copy saved first." : "; the database needed no migration.")
  );
}

/** Runs one scenario on a stack of its own; returns what it showed, or throws what it found wrong. */
async function runScenario(scenario, { candidate, releasing, previous, logs }) {
  const scratch = mkdtempSync(join(tmpdir(), "weir-docker-smoke-"));
  const stack = new Stack(join(logs, scenario), scratch);
  const world = { stack, client: new WeirClient(ORIGIN), candidate };
  stack.removeEverything();
  try {
    switch (scenario) {
      case "restart":
        await restartKeepsData(world);
        return "The account, workflow and setting survived a restart and a down and up of the shipped compose.yaml.";
      case "upgrade":
        return await upgrade(world, { releasing, previous, scratch });
      case "recover": {
        const recovered = await recoverResetsPassword({ ...world, recording: join(logs, "recover", "printed") });
        return `The documented recover command reset the password in the running container: the forgotten one is refused, the new one signs in (also after a restart), ${recovered.sessionsEnded} earlier session(s) ended, the workflow and setting kept.`;
      }
      case "identity":
        await remapsAndKeepsSecrets(world);
        return "WEIR_PUID and WEIR_PGID remapped the account, the volume and both secrets belong to it, and the secrets outlived a restart and a new container.";
      case "data-folder":
        await takesOverTheDataFolder({ ...world, dataFolder: join(scratch, "weir-data") });
        return "A bind-mounted data folder Docker made as root was handed to the ids given, then to other ids, with the database kept.";
      case "refusals":
        refusesBadSettings(world);
        return "Ids of 0, ids that are not numbers, a short session secret and malformed settings each ended the start with a reason.";
      case "media":
        await deliversIntoMediaFolder({ ...world, mediaFolder: join(scratch, "media") });
        return "Files passed through into a bind-mounted output folder arrived owned by the ids given, with the output modes.";
      case "arm64":
        await arm64Starts(world);
        return "The arm64 image answered /health, ran its tools and created the first account.";
      default:
        throw new Error(`There is no scenario "${scenario}".`);
    }
  } catch (error) {
    console.error(`::group::Container log of ${scenario}\n${stack.logs()}\n::endgroup::`);
    throw error;
  } finally {
    stack.keepLogs();
    stack.removeEverything();
    stack.emptyAsRoot(scratch);
    rmSync(scratch, { recursive: true, force: true });
  }
}

async function main() {
  const { values, positionals } = parseArgs({
    allowPositionals: true,
    options: {
      candidate: { type: "string" },
      releasing: { type: "string", default: "" },
      previous: { type: "string" },
      logs: { type: "string", default: "artifacts/docker-compose-smoke" },
    },
  });
  const [scenario] = positionals;
  if (!SCENARIOS.includes(scenario) || !values.candidate) {
    console.error(`Usage: node scripts/docker-compose-smoke.mjs <${SCENARIOS.join("|")}> --candidate <image> [--releasing <version>] [--previous <image>]`);
    process.exit(2);
  }

  // The entrypoint scenarios all run, whatever the one before them found, so one run reports every failure.
  for (const name of scenario === "entrypoint" ? ENTRYPOINT_SCENARIOS : [scenario]) {
    try {
      console.log(await runScenario(name, values));
    } catch (error) {
      console.error(`::error::${name}: ${error.message}`);
      process.exitCode = 1;
    }
  }
}

await main();
