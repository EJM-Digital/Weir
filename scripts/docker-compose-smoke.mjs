#!/usr/bin/env node
// Starts Weir the way the Docker install page tells people to, with the compose.yaml in this repository as it ships (its cap_drop, its
// capabilities and the UID/GID entrypoint), on its named volume, and proves the volume's contents survive.
//
//   restart  PLAT-14: create the first account, a workflow and a saved setting; `docker compose restart`; then `down` and `up` again
//            (the volume stays). The account signs in and the workflow and setting are as they were.
//   upgrade  PLAT-16: the same on the previous release's image, then `down` and the build under test on the same volume. It opens the
//            database, migrates it when the build's schema is newer, and leaves a copy of the old database in backups/pre-update.
//            A release with a predecessor whose image cannot be pulled fails; only the first release has nothing to upgrade from.
//   recover  PLAT-20: create the first account, a workflow and a saved setting; run the recovery command the way the Docker page tells a
//            locked-out person to (`docker exec -u weir -e WEIR_HOME=/data/weir weir /opt/weir/Weir recover`). The password is reset, the
//            forgotten one is refused, the new one signs in (also after a restart), the earlier session is ended, the workflow and setting
//            are kept, and what the command printed is kept in the logs folder.
//
// Usage: node scripts/docker-compose-smoke.mjs <restart|upgrade|recover> --candidate <local image>
//                                              [--releasing <version being released>] [--previous <image>] [--logs <folder>]
// `--releasing` picks the previous release as the newest one older than it; without it, the newest published release is the previous
// one. Releases come from `gh api` (GH_TOKEN, GITHUB_REPOSITORY); `--previous <image>` names the image directly.
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { parseArgs } from "node:util";

import { headRevision } from "./docker-smoke/database.mjs";
import { previousRelease } from "./docker-smoke/previous-image.mjs";
import { recoverResetsPassword } from "./docker-smoke/recover.mjs";
import { restartKeepsData, upgradeKeepsData } from "./docker-smoke/scenarios.mjs";
import { WeirClient } from "./docker-smoke/weir-client.mjs";

const ORIGIN = "http://localhost:9347";
const SCHEMA_MIGRATOR = "apps/server/src/Weir.Infrastructure/Sqlite/SchemaMigrator.cs";

/** Runs a command and returns how it ended, without judging it. */
function runResult(command, { env = {} } = {}) {
  const result = spawnSync("sh", ["-c", command], { encoding: "utf8", env: { ...process.env, ...env }, maxBuffer: 64 * 1024 * 1024 });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function run(command, { env = {}, allowFailure = false } = {}) {
  const result = runResult(command, { env });
  if (result.status !== 0 && !allowFailure) {
    throw new Error(`${command} exited ${result.status}\n${result.stdout}${result.stderr}`);
  }
  return `${result.stdout}${result.stderr}`;
}

/** The shipped compose.yaml, run with the image under test. The file is never copied or edited. */
class Stack {
  #image = "";
  #logFolder;
  #stage = 0;

  constructor(logFolder) {
    this.#logFolder = logFolder;
  }

  #compose(command) {
    return run(`docker compose -f compose.yaml ${command}`, { env: { WEIR_DOCKER_IMAGE: this.#image } });
  }

  pull(image) {
    run(`docker pull ${image}`);
  }

  // `--pull never` for the image CI built (it is in no registry); `always` for a published one, as an operator's `up` would pull it.
  up(image, pull) {
    this.#image = image;
    run(`docker compose -f compose.yaml up -d --pull ${pull}`, { env: { WEIR_DOCKER_IMAGE: image } });
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
    run("docker compose -f compose.yaml down --volumes", { env: { WEIR_DOCKER_IMAGE: this.#image || "weir-ci-smoke" }, allowFailure: true });
  }

  waitHealthy() {
    const container = this.#compose("ps -a -q weir").trim();
    const wait = spawnSync(process.execPath, ["scripts/wait-for-health.mjs", "--url", `${ORIGIN}/health`, "--container", container], {
      stdio: "inherit",
    });
    if (wait.status !== 0) throw new Error("Weir did not become healthy.");
  }

  /** Runs a command in the container as the account the server runs as. */
  exec(command) {
    return this.#compose(`exec -T -u weir weir ${command}`);
  }

  /** Like `exec`, with variables set for the command (`docker exec -e`), and its exit code, output and errors kept apart whatever the exit code. */
  execResult(command, variables = {}) {
    const flags = Object.entries(variables).map(([name, value]) => `-e ${name}=${value}`).join(" ");
    return runResult(`docker compose -f compose.yaml exec -T -u weir ${flags} weir ${command}`, { env: { WEIR_DOCKER_IMAGE: this.#image } });
  }

  /** What the server has logged since the stack was created; empty when there is no container to ask. */
  logs() {
    return run("docker compose -f compose.yaml logs --no-color weir", { env: { WEIR_DOCKER_IMAGE: this.#image || "weir-ci-smoke" }, allowFailure: true });
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
  const repository = process.env.GITHUB_REPOSITORY || "jampat000/Weir";
  const listing = run(`gh api --paginate repos/${repository}/releases --jq '.[] | select(.draft | not) | .tag_name'`);
  return listing.split(/\r?\n/).filter(Boolean);
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
  if (!["restart", "upgrade", "recover"].includes(scenario) || !values.candidate) {
    console.error("Usage: node scripts/docker-compose-smoke.mjs <restart|upgrade|recover> --candidate <image> [--releasing <version>] [--previous <image>]");
    process.exit(2);
  }

  const stack = new Stack(join(values.logs, scenario));
  const client = new WeirClient(ORIGIN);
  const scratch = mkdtempSync(join(tmpdir(), "weir-docker-smoke-"));
  stack.removeEverything();
  try {
    if (scenario === "restart") {
      await restartKeepsData({ stack, client, candidate: values.candidate });
      console.log("The account, workflow and setting survived a restart and a down and up of the shipped compose.yaml.");
      return;
    }

    if (scenario === "recover") {
      const recovered = await recoverResetsPassword({ stack, client, candidate: values.candidate, recording: join(values.logs, "recover", "printed") });
      console.log(
        `The documented recover command reset the password in the running container: the forgotten one is refused, the new one signs in (also after a restart), ${recovered.sessionsEnded} earlier session(s) ended, the workflow and setting kept.`,
      );
      return;
    }

    const previous = values.previous
      ? { version: values.previous, image: values.previous }
      : previousRelease(publishedReleaseTags(), values.releasing);
    if (!previous) {
      console.log("::notice::There is no earlier release to upgrade from, so there is nothing to upgrade.");
      return;
    }
    const upgraded = await upgradeKeepsData({
      stack,
      client,
      candidate: values.candidate,
      previous,
      headRevision: headRevision(readFileSync(SCHEMA_MIGRATOR, "utf8")),
      scratch,
    });
    console.log(
      `Upgraded from ${upgraded.from} (database at ${upgraded.revisionBefore}) with the account, workflow and setting kept` +
        (upgraded.migrated ? ", the database migrated and the copy saved first." : "; the database needed no migration."),
    );
  } catch (error) {
    console.error(`::error::${error.message}`);
    console.error(`::group::Container log\n${stack.logs()}\n::endgroup::`);
    process.exitCode = 1;
  } finally {
    stack.keepLogs();
    stack.removeEverything();
    rmSync(scratch, { recursive: true, force: true });
  }
}

await main();
