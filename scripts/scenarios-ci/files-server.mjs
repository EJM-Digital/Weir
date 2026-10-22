// A Weir server started straight from the install, with a data folder of its own, for the scenarios that need to end it, start it again, or
// point it at a drive that is not C:. It is the installed WeirServer.exe, so it finds the ffmpeg the package carries beside it. The tray is not
// involved; what the tray adds (the firewall rule, the restart for network access) is the network-access scenario's, on the installed Weir.
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { closeSync, openSync } from "node:fs";
import { mkdir, readFile } from "node:fs/promises";
import { dirname, join } from "node:path";

import { pollUntil } from "../docker-smoke/poll.mjs";
import { parseSetupCode } from "../docker-smoke/server-log.mjs";
import { WeirClient } from "../docker-smoke/weir-client.mjs";
import { consoleEnvironment } from "./windows-machine.mjs";
import { allFiles } from "./files-media.mjs";

const READY_CEILING_MS = 120_000;
const EXIT_CEILING_MS = 30_000;
const USERNAME = "scenario-admin";

export class WeirServerProcess {
  #child = null;
  #exited = null;
  #account = null;

  /**
   * @param {{exe: string, home: string, port: number, output: string, clock?: object, baseEnvironment?: object}} options `output` is the file the server's own console is written to; `baseEnvironment` is added to every start.
   */
  constructor({ exe, home, port, output, clock, baseEnvironment = {} }) {
    this.exe = exe;
    this.home = home;
    this.port = port;
    this.output = output;
    this.clock = clock;
    this.baseEnvironment = baseEnvironment;
    this.origin = `http://localhost:${port}`;
    this.weir = new WeirClient(this.origin);
    this.secrets = { session: randomBytes(24).toString("hex"), credentials: randomBytes(24).toString("hex") };
  }

  get pid() {
    return this.#child?.pid ?? null;
  }

  get running() {
    return this.#child !== null && this.#exited === null;
  }

  /**
   * Starts the server. `development` is what lets a test hold a swap (WEIR_LIBRARY_SWAP_HOLD_AT is refused in production);
   * every other start is the production start of a real install. Returns the process id.
   */
  async start({ environment = "production", workers = 1, extra = {} } = {}) {
    if (this.running) throw new Error(`The server on port ${this.port} is already running (pid ${this.pid}).`);
    await mkdir(this.home, { recursive: true });
    await mkdir(dirname(this.output), { recursive: true });
    const env = {
      ...consoleEnvironment(process.env),
      WEIR_HOME: this.home,
      WEIR_ENV: environment,
      WEIR_WEB_DIST: join(dirname(this.exe), "web-dist"),
      WEIR_SESSION_SECRET: this.secrets.session,
      WEIR_CREDENTIALS_SECRET: this.secrets.credentials,
      WEIR_SESSION_COOKIE_SECURE: "false",
      WEIR_PROCESSING_WORKER_COUNT: String(workers),
      ...this.baseEnvironment,
      ...extra,
    };
    const out = openSync(this.output, "a");
    try {
      const child = spawn(this.exe, ["--port", String(this.port)], { env, cwd: dirname(this.exe), stdio: ["ignore", out, out], windowsHide: true });
      this.#child = child;
      this.#exited = null;
      child.on("exit", (code) => {
        this.#exited = { code };
      });
      child.on("error", (error) => {
        this.#exited = { code: null, error };
      });
    } finally {
      closeSync(out);
    }
    this.weir.forgetSession();
    return this.pid;
  }

  /** Waits until the server answers /ready, or fails at once if it has ended. */
  async waitReady() {
    await pollUntil(
      async () => {
        if (this.#exited) throw new Error(`The server on port ${this.port} ended (${JSON.stringify(this.#exited)}) before it was ready. Its console:\n${await this.console()}`);
        return (await this.#answersReady()) ? true : undefined;
      },
      { what: `the server on port ${this.port} to answer /ready`, ceilingMs: READY_CEILING_MS, intervalMs: 500, clock: this.clock },
    );
  }

  async #answersReady() {
    try {
      const answer = await fetch(`${this.origin}/ready`, { signal: AbortSignal.timeout(3000), headers: { Connection: "close" } });
      return (await answer.json()).ready === true;
    } catch {
      return false;
    }
  }

  /** Ends the server the way a crash or a power cut does, by its process id, and waits until it has gone. */
  async kill() {
    const child = this.#child;
    if (!child || this.#exited) return;
    child.kill();
    await pollUntil(async () => (this.#exited ? true : undefined), { what: `the server (pid ${child.pid}) to end`, ceilingMs: EXIT_CEILING_MS, intervalMs: 100, clock: this.clock });
  }

  /** What the server wrote to its console and to its log files. */
  async console() {
    const read = (path) => readFile(path, "utf8").catch(() => "");
    const logs = await allFiles(join(this.home, "logs"));
    return [await read(this.output), ...(await Promise.all(logs.map(read)))].join("\n");
  }

  /** Creates the first account the way a person does, or signs in as the one this server already has. Returns the client. */
  async signIn() {
    const weir = this.weir;
    if (this.#account) {
      if ((await weir.signedInAs()) !== this.#account.username) await weir.login(this.#account.username, this.#account.password);
      return weir;
    }
    const status = await weir.bootstrapStatus();
    if (!status.bootstrap_allowed) throw new Error("The server already has an account that this run did not make.");
    const setupCode = status.requires_setup_code ? parseSetupCode(await this.console()) : null;
    if (status.requires_setup_code && !setupCode) throw new Error("The server asked for a setup code but its log does not carry one.");
    const account = { username: USERNAME, password: `Pw-${randomBytes(18).toString("hex")}` };
    await weir.bootstrap(account.username, account.password, setupCode);
    this.#account = account;
    return weir;
  }

  /** After a restart the session is gone, the account is not. */
  async signInAgain() {
    this.weir.forgetSession();
    return this.signIn();
  }
}
