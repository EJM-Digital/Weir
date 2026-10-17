// The scenarios of the windows-install shard, run on a clean GitHub-hosted Windows runner against the Setup this commit built: a fresh
// install (ported from the VM's fresh-install), the recovery command of the installed server (recover-locked-out-account), and what the VM
// suite never covered, an uninstall that leaves the data alone (uninstall-keeps-the-data). Each makes what it needs: Weir installed, running
// and signed in to, and reads nothing an earlier scenario left except the account the run holds.
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { join } from "node:path";

import { pollUntil } from "../docker-smoke/poll.mjs";
import { parseSetupCode } from "../docker-smoke/server-log.mjs";
import { LOG_RETENTION_DAYS, expectKept, pick } from "../docker-smoke/smoke-data.mjs";
import { csrfSignedWith } from "./csrf-signature.mjs";
import { LogWatch } from "./log-watch.mjs";

export const PORT = 9347;
const USERNAME = "scenario-admin";
const SYSTEM_SID = "S-1-5-18";
const ADMINISTRATORS_SID = "S-1-5-32-544";
const SECRET_FILES = ["session.secret", "credentials.secret"];
const MINIMUM_SECRET_LENGTH = 32;
const WARNINGS_OF_A_SERVER_WITHOUT_SECRETS = [
  "WEIR_SESSION_SECRET is not set",
  "WEIR_CREDENTIALS_SECRET is not set",
  "WEIR_CREDENTIALS_SECRET is set but shorter",
];
// What a start of Weir takes at most; every wait ends the moment its condition holds, so these are hang guards.
export const START_CEILING_MS = 120_000;
const UNINSTALL_CEILING_MS = 120_000;

/** Fails with each thing found when there is any. */
function expectNone(found, what) {
  assert.equal(found.length, 0, `${what}: ${found.join("; ")}`);
}

export const wait = (world, check, what, ceilingMs, intervalMs = 1000) => pollUntil(check, { what, ceilingMs, intervalMs, clock: world.clock });

// --- Weir installed, running and signed in to ---------------------------------------------------------------------------------

export async function waitReady(world) {
  await wait(world, async () => ((await world.server.ready()) ? true : undefined), `Weir to answer /ready on port ${PORT}`, START_CEILING_MS);
}

export async function expectBuildUnderTest(world) {
  const reported = await world.server.version();
  assert.equal(reported, world.expectedVersion, `the running server reports ${reported}, not the build under test (${world.expectedVersion})`);
  return reported;
}

/** Starts the installed tray; every start of Weir in a scenario is a start of the build under test. */
export async function startInstalledWeir(world) {
  const pid = world.machine.startTray(PORT);
  await waitReady(world);
  await expectBuildUnderTest(world);
  return pid;
}

/**
 * Runs Setup --silent, then tells the tray to only announce updates. Weir checks for updates when it starts, and on a runner that can reach the
 * release feed the build under test (a placeholder version, older than every release) would download the newest release and replace itself
 * the next time it starts; what a scenario judges is the build, so it must stay.
 */
export async function installBuildUnderTest(world) {
  const setup = await world.machine.install(world.setupPath);
  assert.equal(setup.exitCode, 0, `Setup --silent exited ${setup.exitCode}, not 0`);
  await world.machine.keepUpdatesFromInstalling();
  return setup;
}

/** Installs the build under test if it is not what is installed, and starts it if it is not running. */
async function ensureInstalledAndRunning(world) {
  const { machine } = world;
  if (await world.server.ready()) return expectBuildUnderTest(world);
  if (!(await machine.exists(machine.paths.serverExe))) {
    await machine.stopWeir();
    await installBuildUnderTest(world);
  }
  if ((await machine.processes()).every((process) => process.name !== "Weir.exe")) machine.startTray(PORT);
  await waitReady(world);
  return expectBuildUnderTest(world);
}

/** Creates the first account the way a person does when Weir offers to, or signs in as the one this run made. */
export async function ensureSignedIn(world) {
  const { weir, machine } = world;
  const status = await weir.bootstrapStatus();
  if (status.bootstrap_allowed) {
    const setupCode = status.requires_setup_code ? parseSetupCode(await machine.serverLogs()) : null;
    assert.ok(!status.requires_setup_code || setupCode, "Weir asked for a setup code but its log does not carry one");
    const account = { username: USERNAME, password: `Pw-${randomBytes(18).toString("hex")}` };
    await weir.bootstrap(account.username, account.password, setupCode);
    world.account = account;
    return;
  }
  assert.ok(world.account, "Weir has an account this run did not make, so there is no way to sign in");
  if ((await weir.signedInAs()) !== world.account.username) await weir.login(world.account.username, world.account.password);
}

export async function ensureWeir(world) {
  await ensureInstalledAndRunning(world);
  await ensureSignedIn(world);
}

/**
 * What every scenario of this shard is judged on besides its own checks: the logs gained no warning or error. A scenario that begins with
 * no Weir answering (a fresh install) has nothing in its logs yet, so what they hold when it ends is what it gained.
 */
export async function watchLogs(world) {
  if (await world.server.ready()) {
    await ensureSignedIn(world);
    return LogWatch.begin(world.weir);
  }
  const watch = new LogWatch(world.weir, []);
  return {
    expect: (title, options) => watch.expect(title, options),
    finish: async () => {
      await ensureSignedIn(world);
      await watch.finish();
    },
  };
}

// --- fresh-install ------------------------------------------------------------------------------------------------------------

/**
 * The two secrets the tray made in the data folder: each long enough, readable by SYSTEM, the administrators and the installing account
 * and nobody else, with no list inherited from the folder, and different from each other. Returns them by file name.
 */
async function secretsMadeByTheInstall(world) {
  const { machine } = world;
  const account = await machine.account();
  const allowed = new Set([SYSTEM_SID, ADMINISTRATORS_SID, account.sid]);
  const secrets = {};
  for (const name of SECRET_FILES) {
    const path = machine.secretPath(name);
    assert.ok(await machine.exists(path), `${name} was not made in the data folder`);
    secrets[name] = (await machine.readText(path)).trim();
    assert.ok(secrets[name].length >= MINIMUM_SECRET_LENGTH, `${name} is ${secrets[name].length} characters, shorter than ${MINIMUM_SECRET_LENGTH}`);
    const access = await machine.accessList(path);
    assert.ok(access.protected, `${name} takes its access list from its folder instead of having its own`);
    const others = access.rules.filter((rule) => rule.allow && !allowed.has(rule.sid)).map((rule) => rule.name);
    assert.deepEqual(others, [], `${name} can be read by ${others.join(", ")}, not only by SYSTEM, the administrators and ${account.name}`);
    assert.ok(
      access.rules.some((rule) => rule.allow && rule.sid === account.sid),
      `${name} does not let the installing account (${account.name}) read it`,
    );
  }
  assert.notEqual(secrets["session.secret"], secrets["credentials.secret"], "the session secret and the credentials secret are the same");
  return secrets;
}

/** The running server was started with those secrets: it signs with the session secret, and it logged no complaint about either. */
async function expectServerUsesSecrets(world, secrets) {
  const token = (await world.weir.get("/auth/csrf")).csrf_token;
  assert.ok(csrfSignedWith(token, secrets["session.secret"]), "the token the server signed does not verify with the secret in session.secret");
  const log = await world.machine.serverLogs();
  for (const complaint of WARNINGS_OF_A_SERVER_WITHOUT_SECRETS) {
    assert.ok(!log.includes(complaint), `the server's log says "${complaint}"`);
  }
}

/** fresh-install: Setup --silent on a clean machine, then the installed Weir started the way an unattended caller starts it. */
async function freshInstall(world) {
  const { machine } = world;
  await machine.stopWeir();
  const setup = await installBuildUnderTest(world);
  assert.ok(await machine.exists(machine.paths.trayExe), `Weir.exe is not installed at ${machine.paths.trayExe}`);
  assert.ok(await machine.exists(machine.paths.serverExe), `WeirServer.exe is not installed at ${machine.paths.serverExe}`);

  const trayPid = await startInstalledWeir(world);
  const reported = await expectBuildUnderTest(world);
  const fileVersion = await machine.serverVersion();
  assert.equal(fileVersion, world.expectedVersion, `WeirServer.exe --version says ${fileVersion}`);

  const running = await machine.processes();
  const tray = running.find((process) => process.pid === trayPid);
  assert.ok(tray, "the tray is not running");
  assert.equal(tray.hasWindow, false, "the tray opened a window");
  assert.ok(
    running.some((process) => process.name === "WeirServer.exe" && process.parentPid === trayPid),
    "the tray did not start the server as its child",
  );

  const addresses = await machine.listeningAddresses(PORT);
  const strangers = addresses.filter((address) => !["127.0.0.1", "::1"].includes(address));
  assert.ok(addresses.includes("127.0.0.1") && strangers.length === 0, `port ${PORT} listens on ${addresses.join(", ")}, not on this PC only`);

  const trayLog = await machine.readText(machine.paths.trayLog);
  assert.ok(trayLog.includes(`Using port ${PORT} from --port`), "the tray log does not say it used the port it was given");
  assert.ok(trayLog.includes(`Weir is healthy on http://127.0.0.1:${PORT}/`), "the tray log does not say Weir is healthy");

  const secrets = await secretsMadeByTheInstall(world);
  await expectServerUsesSecrets(world, secrets);
  return (
    `Setup --silent exited 0 in ${setup.seconds} s; Weir ${reported} answered /ready; tray pid ${trayPid} with the server as its child and no window; ` +
    `listening on ${addresses.join(", ")} only; session.secret and credentials.secret are ${MINIMUM_SECRET_LENGTH}+ characters, readable only by SYSTEM, the administrators and the installing account, ` +
    "and the server signs with the session secret."
  );
}

// --- recover-locked-out-account ---------------------------------------------------------------------------------------------------

function expectRecoverSaid(reset, { username, ended }) {
  assert.ok(reset.stdout.includes(`Password reset for '${username}'.`), "recover does not say the password was reset");
  assert.ok(reset.stdout.includes("The account is active and has the admin role."), "recover does not say the account is an active admin");
  const words = ended === 1 ? "1 signed-in session was ended" : `${ended} signed-in sessions were ended`;
  assert.ok(new RegExp(`^${words}\\W+sign in again with the new password\\.\\s*$`, "m").test(reset.stdout), `recover does not say ${words}`);
  assert.ok(reset.stderr.includes("--password stays in your shell history"), "recover does not warn that --password stays in the shell history");
  return words;
}

/** The installed command, run as a person's console runs it with nothing set; what it printed is kept with the record. */
async function runRecover(world, name, args) {
  const result = await world.machine.recover(args);
  await world.record(`recover-${name}.txt`, `exit code: ${result.exitCode}\n--- stdout ---\n${result.stdout}\n--- stderr ---\n${result.stderr}\n`);
  return result;
}

/** recover-locked-out-account: the installed WeirServer.exe recover finds Weir's data folder by itself and resets the only password. */
async function recoverLockedOutAccount(world) {
  await ensureWeir(world);
  const { machine, weir } = world;
  const { username, password: forgotten } = world.account;
  const recovered = `Rc-${randomBytes(16).toString("hex")}-Aa1`;

  const settings = await weir.suiteSettings();
  const original = settings.log_retention_days;
  const retention = original === 17 ? 18 : 17;
  await weir.saveSuiteSettings({ app_timezone: settings.app_timezone, log_retention_days: retention });
  const other = world.browser();
  await other.login(username, forgotten);
  const ended = (await weir.activeSessions()).length;
  assert.ok(ended >= 2, `two browsers are signed in before the reset (${ended} sessions)`);

  let words;
  try {
    await machine.stopWeir();

    const help = await runRecover(world, "help", ["--help"]);
    assert.equal(help.exitCode, 0, `recover --help exited ${help.exitCode}`);
    for (const wanted of ["usage: Weir recover", "--username USERNAME", "--password PASSWORD", "--list"]) {
      assert.ok(help.stdout.includes(wanted), `recover --help does not mention ${wanted}`);
    }

    const list = await runRecover(world, "list", ["--list"]);
    assert.equal(list.exitCode, 0, `recover --list exited ${list.exitCode}: ${list.stderr}`);
    assert.ok(
      list.stdout.includes(`Accounts in ${machine.paths.database}:`),
      `with nothing set, the command did not find the data folder the installed server uses (${machine.paths.database}): ${list.stdout}`,
    );
    assert.ok(new RegExp(`^\\s+${username}\\s+role=admin\\s+active\\s*$`, "m").test(list.stdout), `recover --list does not show ${username} as an active admin`);

    const reset = await runRecover(world, "recover", ["--username", username, "--password", recovered]);
    assert.equal(reset.exitCode, 0, `recover exited ${reset.exitCode}: ${reset.stderr}`);
    words = expectRecoverSaid(reset, { username, ended });
  } finally {
    // Whatever happened, the next scenario finds Weir running.
    if ((await machine.processes()).every((process) => process.name !== "Weir.exe")) await startInstalledWeir(world);
  }

  for (const earlier of [weir, other]) {
    assert.equal(await earlier.signedInAs(), null, "a session from before the reset is still accepted");
  }
  // Weir writes a warning, and Activity a line, for a sign-in it refuses; this one is refused on purpose.
  for (const title of ["auth event: login failed", "Sign-in failed"]) {
    world.logWatch.expect(title, { why: "the forgotten password is tried once, to see that it is refused" });
  }
  const withForgotten = await world.browser().loginStatus(username, forgotten);
  assert.equal(withForgotten, 401, `the forgotten password was answered ${withForgotten}, not refused`);

  world.account = { username, password: recovered };
  weir.forgetSession();
  await weir.login(username, recovered);
  assert.equal(await weir.signedInAs(), username, `the new password does not sign in as ${username}`);
  const kept = await weir.suiteSettings();
  assert.equal(kept.log_retention_days, retention, `the saved setting changed: log_retention_days is ${kept.log_retention_days}, not ${retention}`);
  const said = (await weir.activity("auth.password_changed")).filter((item) => item.title === "Password recovered from the server");
  assert.equal(said.length, 1, `Activity holds ${said.length} entries titled "Password recovered from the server", not 1`);

  await weir.saveSuiteSettings({ app_timezone: settings.app_timezone, log_retention_days: original });
  return (
    `WeirServer.exe recover, run from the install with nothing set, found ${machine.paths.database}, listed ${username}, reset the password and said ${words}; ` +
    `the forgotten password is refused (401), the new one signs in, ${ended} earlier sessions are ended, the saved setting is kept and Activity says the password was recovered from the server.`
  );
}

// --- uninstall-keeps-the-data -----------------------------------------------------------------------------------------------------------

/** What an uninstall did to the data folder, in words: a file gone, a file that is new, or one whose bytes changed. The tray's own log may grow. */
export function dataFolderProblems(before, after, { mayGrow = [] } = {}) {
  const now = new Map(after.map((file) => [file.path, file]));
  const problems = [];
  for (const file of before) {
    const found = now.get(file.path);
    if (!found) problems.push(`${file.path} is gone`);
    else if (mayGrow.includes(file.path)) {
      if (found.bytes < file.bytes) problems.push(`${file.path} is shorter than it was`);
    } else if (found.sha256 !== file.sha256) problems.push(`${file.path} changed`);
  }
  const was = new Set(before.map((file) => file.path));
  for (const file of after) if (!was.has(file.path)) problems.push(`${file.path} is new`);
  return problems;
}

async function makeWorkflow(world) {
  const base = join(world.scratch, "workflow");
  const folders = { watched_folder: join(base, "watched"), work_folder: join(base, "work"), output_folder: join(base, "output") };
  await world.machine.ensureFolders(...Object.values(folders));
  const created = await world.weir.createLibrary({ name: `Scenario uninstall ${world.runId}`, media_type: "movie", ...folders, skip_access_tests: true });
  const settings = await world.weir.suiteSettings();
  await world.weir.saveSuiteSettings({ app_timezone: settings.app_timezone, log_retention_days: LOG_RETENTION_DAYS });
  return { ...world.account, library: pick(created) };
}

/** What a person would look for on the PC: the shortcuts, Run entry and running mark that Weir leaves. */
async function traces(machine) {
  return { shortcuts: await machine.shortcuts(), runEntry: await machine.runEntry(), mark: await machine.trayMark() };
}

async function expectUninstalled(world) {
  const { machine } = world;
  const uninstall = await machine.uninstall();
  assert.equal(uninstall.exitCode, 0, `the uninstaller exited ${uninstall.exitCode}, not 0`);
  await wait(
    world,
    async () => ((await machine.exists(machine.paths.trayExe)) ? undefined : true),
    "the uninstaller to remove Weir.exe",
    UNINSTALL_CEILING_MS,
  );
  expectNone((await machine.processes()).map((process) => `${process.name} (pid ${process.pid})`), "the uninstaller left Weir running");
  const left = await traces(machine);
  expectNone(
    [...left.shortcuts, ...(left.runEntry ? [`the Run entry ${left.runEntry}`] : []), ...(left.mark ? ["the running mark in the registry"] : [])],
    "the uninstaller left these behind",
  );
}

async function reinstallAndSignIn(world, kept) {
  const { machine, weir } = world;
  await installBuildUnderTest(world);
  await startInstalledWeir(world);
  weir.forgetSession();
  await expectKept({ client: weir, kept, sessionKept: false });
}

async function secretsOf(world) {
  return Object.fromEntries(await Promise.all(SECRET_FILES.map(async (name) => [name, await world.machine.readText(world.machine.secretPath(name))])));
}

/**
 * uninstall-keeps-the-data: Weir's account, workflow, setting, database, logs, backups and secrets are where they were after the uninstaller
 * has run, once with Weir stopped (every byte the same) and once with Weir running (the uninstaller stops it first and deletes nothing), and a
 * reinstall finds the account, the workflow and the setting, with the very same secrets.
 */
async function uninstallKeepsTheData(world) {
  const { machine } = world;
  await ensureWeir(world);
  const kept = await makeWorkflow(world);
  const secrets = await secretsOf(world);
  const home = machine.paths.runtimeHome;

  // Weir stopped: nothing in the data folder may differ, but the line the tray logs that it was uninstalled.
  assert.ok((await machine.trayMark()) !== null, "a running tray leaves no mark in the registry, so there is nothing for the uninstall to remove");
  await machine.stopWeir();
  const stopped = await machine.files(home, { hash: true });
  assert.ok(stopped.some((file) => file.path === join("data", "weir.sqlite3")), "the data folder holds no database");
  await expectUninstalled(world);
  const afterStopped = await machine.files(home, { hash: true });
  expectNone(dataFolderProblems(stopped, afterStopped, { mayGrow: ["tray-host.log"] }), "the uninstall changed the data folder");
  await reinstallAndSignIn(world, kept);
  assert.deepEqual(await secretsOf(world), secrets, "the reinstall replaced a secret");

  // Weir running: the uninstaller stops it first, and nothing in the data folder is deleted.
  const shortcuts = await machine.shortcuts();
  const running = await machine.files(home);
  await expectUninstalled(world);
  const afterRunning = await machine.files(home);
  const gone = running.filter((file) => !afterRunning.some((now) => now.path === file.path)).map((file) => file.path);
  expectNone(gone, "the uninstall deleted files from the data folder");
  assert.ok((afterRunning.find((file) => file.path === join("data", "weir.sqlite3"))?.bytes ?? 0) > 0, "the database is gone or empty after the uninstall");
  assert.ok((await machine.readText(machine.paths.trayLog)).includes("Velopack: before uninstall"), "the tray log does not show the uninstall hook ran");
  await reinstallAndSignIn(world, kept);
  assert.deepEqual(await secretsOf(world), secrets, "the second reinstall replaced a secret");

  return (
    `Uninstalled twice, once with Weir stopped (the ${stopped.length} files of the data folder unchanged but the tray's own log) and once with it running (the uninstaller stopped it, ${running.length} files still there); ` +
    `${shortcuts.length} shortcuts, the Run entry and the running mark were gone each time; each reinstall found the account, the workflow "${kept.library.name}" and the saved setting, and session.secret and credentials.secret were the same bytes.`
  );
}

/** The scenarios of the windows-install shard, by id. */
export const SCENARIOS = {
  "fresh-install": freshInstall,
  "recover-locked-out-account": recoverLockedOutAccount,
  "uninstall-keeps-the-data": uninstallKeepsTheData,
};
