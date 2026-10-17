// A stand-in for a clean Windows runner and the Weir installed on it, small enough to read, for testing what the windows-install scenarios
// decide: it installs and uninstalls, makes the tray's secrets, answers the routes the scenarios use, runs the recovery command the way the
// real one behaves, and can be told to go wrong in the ways the scenarios must catch. The real ones are the runner and the Setup, in CI.
import { createHash, createHmac } from "node:crypto";
import { join, relative } from "node:path";

const SYSTEM = { sid: "S-1-5-18", name: "NT AUTHORITY\\SYSTEM" };
const ADMINISTRATORS = { sid: "S-1-5-32-544", name: "BUILTIN\\Administrators" };
const USERS = { sid: "S-1-5-32-545", name: "BUILTIN\\Users" };
const RUNNER = { sid: "S-1-5-21-1-2-3-1001", name: "RUNNER\\runneradmin" };
const VERSION = "3.4.0";

const urlSafe = (bytes) => bytes.toString("base64").replaceAll("=", "").replaceAll("+", "-").replaceAll("/", "_");

/** The token the server issues: `value.timestamp.signature`, HMAC-SHA1 under a key derived from the secret. */
function signCsrf(secret) {
  const key = createHash("sha1").update(`weir-csrf-v1signer${secret}`, "utf8").digest();
  const signed = `csrf.anonymous.${urlSafe(Buffer.from([0x6a, 0x01]))}`;
  return `${signed}.${urlSafe(createHmac("sha1", key).update(signed, "utf8").digest())}`;
}

/**
 * @param {object} [behaviour]
 * @param {number} [behaviour.secretLength] How long the tray makes its secrets.
 * @param {boolean} [behaviour.sameSecrets] The two secrets are one.
 * @param {boolean} [behaviour.secretsInherit] The secrets take their access list from the folder.
 * @param {boolean} [behaviour.usersReadSecrets] The Users group may read the secrets.
 * @param {boolean} [behaviour.serverIgnoresSecret] The server signs with some other secret.
 * @param {string} [behaviour.serverLogLine] A line the server logs when it starts.
 * @param {string[]} [behaviour.listensOn] Where the server listens.
 * @param {boolean} [behaviour.trayShowsWindow]
 * @param {number} [behaviour.setupExit] What Setup exits with.
 * @param {string} [behaviour.reportedVersion] What the server reports as its version.
 * @param {boolean} [behaviour.uninstallDeletesData]
 * @param {boolean} [behaviour.uninstallEditsDatabase]
 * @param {boolean} [behaviour.uninstallLeavesShortcut]
 * @param {boolean} [behaviour.uninstallLeavesMark]
 * @param {boolean} [behaviour.uninstallLeavesRunning]
 * @param {boolean} [behaviour.reinstallReplacesSecrets]
 * @param {boolean} [behaviour.reinstallForgetsAccount]
 * @param {boolean} [behaviour.recoverLooksElsewhere] The recovery command opens some other data folder.
 * @param {boolean} [behaviour.recoverChangesNothing] It says it reset the password and did not.
 * @param {boolean} [behaviour.recoverKeepsSessions]
 * @param {string} [behaviour.warnsWhenStarted] A warning the server logs when it starts.
 */
export class FakeWindows {
  logRows = [];
  recordings = new Map();
  calls = [];
  #behaviour;
  #now = 0;
  #pids = 100;
  #secrets = 0;
  #installed = false;
  #tray = null;
  #files = new Map();
  #db = null;
  #sessions = new Set();
  #shortcuts = [];
  #mark = null;

  constructor(behaviour = {}) {
    this.#behaviour = behaviour;
  }

  clock = {
    now: () => this.#now,
    sleep: async (ms) => {
      this.#now += ms;
    },
  };

  #home = join("C:", "ProgramData", "Weir");
  #install = join("C:", "Users", "runneradmin", "AppData", "Local", "Weir");
  #running = () => this.#tray !== null;
  #rel = (path) => relative(this.#home, path);
  #put = (name, content) => this.#files.set(name, content);

  paths = {
    installRoot: this.#install,
    updater: join(this.#install, "Update.exe"),
    trayExe: join(this.#install, "current", "Weir.exe"),
    serverExe: join(this.#install, "current", "server", "WeirServer.exe"),
    runtimeHome: this.#home,
    database: join(this.#home, "data", "weir.sqlite3"),
    trayLog: join(this.#home, "tray-host.log"),
    logsFolder: join(this.#home, "logs"),
  };

  // --- the world the scenarios are run in -----------------------------------------------------------------------------------------

  /** What runScenarios is given. */
  world() {
    return {
      machine: this.machine,
      server: this.server,
      weir: this.browser(),
      browser: () => this.browser(),
      setupPath: "C:\\setup\\Weir-win-Setup.exe",
      expectedVersion: VERSION,
      runId: "ab12cd",
      scratch: join("C:", "scratch"),
      clock: this.clock,
      record: (name, text) => this.recordings.set(name, text),
    };
  }

  server = {
    ready: async () => this.#running(),
    version: async () => this.#behaviour.reportedVersion ?? VERSION,
  };

  /** A browser with no session. */
  browser() {
    return new FakeBrowser(this);
  }

  // --- the machine -------------------------------------------------------------------------------------------------------------------

  machine = {
    paths: this.paths,
    install: async (setup) => {
      this.calls.push(`install ${setup}`);
      const exitCode = this.#behaviour.setupExit ?? 0;
      if (exitCode === 0) {
        this.#installed = true;
        this.#shortcuts = [join("C:", "Users", "runneradmin", "Start Menu", "Programs", "Weir.lnk")];
        if (this.#behaviour.reinstallReplacesSecrets && this.#files.has("session.secret")) this.#files.delete("session.secret");
        if (this.#behaviour.reinstallForgetsAccount) this.#db = null;
      }
      return { exitCode, seconds: 4 };
    },
    uninstall: async () => {
      this.calls.push("uninstall");
      if (this.#running() && !this.#behaviour.uninstallLeavesRunning) this.#stop();
      this.#installed = false;
      if (!this.#behaviour.uninstallLeavesShortcut) this.#shortcuts = [];
      if (!this.#behaviour.uninstallLeavesMark) this.#mark = null;
      this.#put("tray-host.log", `${this.#files.get("tray-host.log") ?? ""}Velopack: before uninstall\n`);
      if (this.#behaviour.uninstallDeletesData) {
        this.#files.clear();
        this.#db = null;
      }
      if (this.#behaviour.uninstallEditsDatabase) this.#put(join("data", "weir.sqlite3"), "edited by the uninstaller");
      return { exitCode: 0 };
    },
    startTray: (port) => {
      this.calls.push(`startTray ${port}`);
      const trayPid = ++this.#pids;
      this.#tray = { trayPid, serverPid: ++this.#pids };
      this.#mark = { processId: trayPid, executable: this.paths.trayExe };
      this.#start();
      return trayPid;
    },
    processes: async () =>
      this.#tray
        ? [
            { name: "Weir.exe", pid: this.#tray.trayPid, parentPid: 1, hasWindow: this.#behaviour.trayShowsWindow ?? false },
            { name: "WeirServer.exe", pid: this.#tray.serverPid, parentPid: this.#tray.trayPid, hasWindow: false },
          ]
        : [],
    stopWeir: async () => {
      this.calls.push("stopWeir");
      this.#stop();
    },
    listeningAddresses: async () => (this.#running() ? (this.#behaviour.listensOn ?? ["127.0.0.1"]) : []),
    account: async () => RUNNER,
    accessList: async (path) => {
      const rules = [SYSTEM, ADMINISTRATORS, RUNNER, ...(this.#behaviour.usersReadSecrets ? [USERS] : [])].map((who) => ({ ...who, allow: true, rights: "FullControl" }));
      return { protected: !this.#behaviour.secretsInherit, rules, path };
    },
    shortcuts: async () => [...this.#shortcuts],
    runEntry: async () => null,
    trayMark: async () => this.#mark,
    serverVersion: async () => this.#behaviour.reportedVersion ?? VERSION,
    recover: async (args) => this.#recover(args),
    secretPath: (name) => join(this.#home, name),
    ensureFolders: async () => undefined,
    exists: async (path) => path === this.paths.trayExe || path === this.paths.serverExe ? this.#installed : this.#files.has(this.#rel(path)),
    readText: async (path) => this.#files.get(this.#rel(path)) ?? "",
    files: async (folder, { hash = false } = {}) =>
      [...this.#files]
        .map(([path, content]) => ({ path, bytes: content.length, ...(hash ? { sha256: createHash("sha256").update(content).digest("hex") } : {}) }))
        .toSorted((a, b) => a.path.localeCompare(b.path)),
    serverLogs: async () => this.#files.get(join("logs", "weir.log")) ?? "",
  };

  #stop() {
    this.#tray = null;
  }

  /** The tray's first start makes the secrets; the server then starts with them and opens its database. */
  #start() {
    const length = this.#behaviour.secretLength ?? 64;
    const made = (kind) => `${kind}-${++this.#secrets}-`.padEnd(length, "x");
    if (!this.#files.has("session.secret")) this.#put("session.secret", made("session"));
    if (!this.#files.has("credentials.secret")) this.#put("credentials.secret", this.#behaviour.sameSecrets ? this.#files.get("session.secret") : made("credentials"));
    this.#put(join("data", "weir.sqlite3"), this.#files.get(join("data", "weir.sqlite3")) ?? "database");
    this.#put("tray-host.log", `${this.#files.get("tray-host.log") ?? ""}Using port 9347 from --port\nWeir is healthy on http://127.0.0.1:9347/\n`);
    this.#put(join("logs", "weir.log"), `${this.#files.get(join("logs", "weir.log")) ?? ""}${this.#behaviour.serverLogLine ?? "Weir started"}\n`);
    this.#db ??= { account: null, settings: { app_timezone: "UTC", log_retention_days: 30 }, libraries: [], activity: [], logs: [] };
    if (this.#behaviour.warnsWhenStarted) this.#db.logs.push({ source: "server", id: this.#db.logs.length + 1, at: this.#now, title: this.#behaviour.warnsWhenStarted });
    this.logRows = this.#db.logs;
  }

  #recover(args) {
    const done = (exitCode, stdout = "", stderr = "") => ({ exitCode, stdout, stderr });
    if (args.includes("--help")) return done(0, "usage: Weir recover [--username USERNAME] [--password PASSWORD] [--list]\n  --username USERNAME\n  --password PASSWORD\n  --list\n");
    const database = this.#behaviour.recoverLooksElsewhere ? join("C:", "Users", "runneradmin", "Weir", "data", "weir.sqlite3") : this.paths.database;
    if (args.includes("--list")) return done(0, `Accounts in ${database}:\n  ${this.#db.account.username}  role=admin  active\n`);
    const ended = this.#sessions.size;
    const password = args[args.indexOf("--password") + 1];
    if (!this.#behaviour.recoverChangesNothing) this.#db.account.password = password;
    if (!this.#behaviour.recoverKeepsSessions) this.#sessions.clear();
    this.#db.activity.push({ event_type: "auth.password_changed", title: "Password recovered from the server" });
    const words = ended === 1 ? "1 signed-in session was ended" : `${ended} signed-in sessions were ended`;
    return done(0, `Password reset for '${this.#db.account.username}'.\nThe account is active and has the admin role.\n${words}; sign in again with the new password.\n`, "--password stays in your shell history.\n");
  }

  // --- the server's side of the routes ------------------------------------------------------------------------------------------------

  get db() {
    if (!this.#running()) throw new Error("Weir did not answer: connection refused");
    return this.#db;
  }

  get sessions() {
    return this.#sessions;
  }

  csrfToken() {
    return signCsrf(this.#behaviour.serverIgnoresSecret ? "another secret entirely" : this.#files.get("session.secret"));
  }
}

class FakeBrowser {
  #world;
  #cookie = null;

  constructor(world) {
    this.#world = world;
  }

  forgetSession() {
    this.#cookie = null;
  }

  #open(username) {
    this.#cookie = `session-${username}-${Math.random().toString(16).slice(2)}`;
    this.#world.sessions.add(this.#cookie);
  }

  async bootstrapStatus() {
    return { bootstrap_allowed: this.#world.db.account === null, requires_setup_code: false, reason: this.#world.db.account ? "admin_already_exists" : "none" };
  }

  async bootstrap(username, password) {
    this.#world.db.account = { username, password };
    this.#open(username);
  }

  async login(username, password) {
    if ((await this.loginStatus(username, password)) !== 200) throw new Error("signing in: expected HTTP 200, got 401");
  }

  async loginStatus(username, password) {
    const { account } = this.#world.db;
    if (!account || account.username !== username || account.password !== password) return 401;
    this.#open(username);
    return 200;
  }

  async signedInAs() {
    return this.#cookie && this.#world.sessions.has(this.#cookie) && this.#world.db.account ? this.#world.db.account.username : null;
  }

  async activeSessions() {
    return [...this.#world.sessions].map((id) => ({ id }));
  }

  async suiteSettings() {
    return { ...this.#world.db.settings };
  }

  async saveSuiteSettings(fields) {
    Object.assign(this.#world.db.settings, fields);
  }

  async activity(eventType) {
    return this.#world.db.activity.filter((item) => item.event_type === eventType);
  }

  async createLibrary(fields) {
    const library = { id: this.#world.db.libraries.length + 1, ...fields };
    this.#world.db.libraries.push(library);
    return library;
  }

  async libraries() {
    return [...this.#world.db.libraries];
  }

  async get(path) {
    if (path === "/auth/csrf") return { csrf_token: this.#world.csrfToken() };
    if (path.startsWith("/system/log")) return { items: [...this.#world.db.logs], next_cursor: null };
    throw new Error(`the stand-in does not answer ${path}`);
  }
}

export { VERSION };
