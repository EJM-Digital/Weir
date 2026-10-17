// The shipped compose.yaml run as a stack: the image under test on its named volume, with the commands the Docker smokes and the
// scenarios in CI give it. Only ever runs on a Linux machine with Docker, from the repository root.
import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { composeOverride } from "./override.mjs";
import { run, runForStatus, shellQuote } from "./shell.mjs";

/** Where the stack's Weir answers: compose.yaml publishes its port on this machine. */
export const ORIGIN = "http://localhost:9347";
// An entrypoint that wrongly accepts a start would run the server, which never ends: give a refusal this long to come.
const REFUSAL_CEILING_MS = 120_000;

/** The shipped compose.yaml, run with the image under test. The file is never copied or edited; a scenario may lay an override over it. */
export class Stack {
  #image = "";
  #logFolder;
  #scratch;
  #files = ["compose.yaml"];
  #extraFiles = [];
  #environment = {};
  #alsoLogged = [];
  #overrides = 0;
  #stage = 0;

  /** @param {string} scratch A folder on this machine for the override files. */
  constructor(logFolder, scratch) {
    this.#logFolder = logFolder;
    this.#scratch = scratch;
  }

  #command(subcommand, files = [...this.#files, ...this.#extraFiles]) {
    return `docker compose ${files.map((file) => `-f ${shellQuote(file)}`).join(" ")} ${subcommand}`;
  }

  #compose(subcommand) {
    return run(this.#command(subcommand), { env: { ...this.#environment, WEIR_DOCKER_IMAGE: this.#image } });
  }

  pull(image) {
    run(`docker pull ${image}`);
  }

  /**
   * Creates the stack. `--pull never` for the image CI built (it is in no registry); `always` for a published one, as an operator's `up`
   * would pull it. `layers` (see composeOverride) are laid over the shipped file for this and the stack's later commands, and so are the
   * `files` of services that run beside Weir; `environment` is what those files read their images and settings from.
   * @param {{files?: string[], environment?: Record<string, string>, services?: string[]}} [beside] `services` are the ones whose logs are kept too.
   */
  up(image, pull, layers = null, { files = [], environment = {}, services = [] } = {}) {
    this.#use(image, layers);
    this.#extraFiles = files;
    this.#environment = environment;
    this.#alsoLogged = services;
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
    const files = ["compose.yaml", ...this.#extraFiles];
    run(this.#command("down --volumes --remove-orphans", files), { env: { ...this.#environment, WEIR_DOCKER_IMAGE: this.#image || "weir-ci-smoke" }, allowFailure: true });
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
    return runForStatus(this.#command(`exec -T -u weir ${flags} weir ${command}`), { env: { ...this.#environment, WEIR_DOCKER_IMAGE: this.#image } });
  }

  /** What a service (the server, unless named) has logged since the stack was created; empty when there is no container to ask. */
  logs(service = "weir") {
    return run(this.#command(`logs --no-color ${service}`), { env: { ...this.#environment, WEIR_DOCKER_IMAGE: this.#image || "weir-ci-smoke" }, allowFailure: true });
  }

  /** Copies a path out of the container (running or stopped) into a folder on this machine. */
  copyOut(containerPath, hostFolder) {
    mkdirSync(hostFolder, { recursive: true });
    run(`docker cp ${this.#compose("ps -a -q weir").trim()}:${containerPath} ${hostFolder}`);
  }

  /**
   * Saves the server's log for the artifact the workflow uploads, numbered in the order the stack ran, named for its image, and the log of
   * each service that runs beside it, named for the service.
   */
  keepLogs() {
    const logs = this.logs();
    if (!logs) return;
    this.#stage += 1;
    mkdirSync(this.#logFolder, { recursive: true });
    const image = this.#image.replace(/[^A-Za-z0-9.-]+/g, "-");
    const stage = String(this.#stage).padStart(2, "0");
    writeFileSync(join(this.#logFolder, `${stage}-${image}.log`), logs);
    for (const service of this.#alsoLogged) writeFileSync(join(this.#logFolder, `${stage}-${service}.log`), this.logs(service));
  }
}
