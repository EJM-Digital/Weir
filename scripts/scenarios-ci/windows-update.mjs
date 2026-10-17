// The scenarios of the windows-update shard, run on a clean GitHub-hosted Windows runner. Each installs the newest published release of Weir,
// with its own account, workflow, setting and a saved connection, and updates it to the build under test through the tray's own update code:
// the tray checks, downloads, verifies and applies, and the new version comes up on its own. What the tray asks GitHub for is answered by a
// stand-in made from the package this commit built (github-stand-in.mjs), which the PC's hosts file points github.com at.
//
// The build under test is built again under a version just above the release (update-versions.mjs): an update only goes to a newer version.
import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { basename, dirname, join } from "node:path";

import { pollUntil } from "../docker-smoke/poll.mjs";
import { LOG_RETENTION_DAYS, expectKept, pick } from "../docker-smoke/smoke-data.mjs";
import { LogWatch } from "./log-watch.mjs";
import { SabnzbdStandIn } from "./sabnzbd-stand-in.mjs";
import { PORT, ensureSignedIn, waitReady } from "./windows-install.mjs";

const SECRET_FILES = ["session.secret", "credentials.secret"];
const IDLE_MINUTES = 5;
// Hang guards: each wait ends the moment what it waits for has happened.
const STEP_CEILING_MS = 180_000;
const APPLY_CEILING_MS = 300_000;
const IDLE_CEILING_MS = (IDLE_MINUTES + 10) * 60_000;
// A periodic check must never fire during a scenario; the checks it wants are the start-up check or a person's click.
const NEVER_CHECK_MINUTES = 10_080;
const ACTIVITY_FIELDS = ["created_at", "event_type", "module", "title", "detail", "trigger", "result", "library_id", "relative_path", "run_key"];

const wait = (world, check, what, ceilingMs, intervalMs = 1000) => pollUntil(check, { what, ceilingMs, intervalMs, clock: world.clock });

// --- the log of the System > Logs page ----------------------------------------------------------------------------------------

/**
 * The watch of System > Logs for a scenario that installs Weir itself: it begins once the release is up and the account is signed in
 * (`world.logWatch.begin()`), so what counts is what the update and the new version logged.
 */
export function watchLogs(world) {
  let watch = null;
  const expected = [];
  return {
    begin: async () => {
      watch = await LogWatch.begin(world.weir);
      for (const [title, options] of expected) watch.expect(title, options);
    },
    expect: (title, options) => (watch ? watch.expect(title, options) : expected.push([title, options])),
    finish: async () => {
      assert.ok(watch, "the scenario never began watching System > Logs");
      await ensureSignedIn(world);
      await watch.finish();
    },
  };
}

// --- reading the tray ---------------------------------------------------------------------------------------------------------------

const trayLog = (world) => world.machine.readText(world.machine.paths.trayLog);

/** How much of the tray's log there is now, to look only at what it says from here on. */
async function logMark(world) {
  return (await trayLog(world)).length;
}

async function trayLogSince(world, mark) {
  return (await trayLog(world)).slice(mark);
}

async function waitForTrayLine(world, text, mark, ceilingMs = STEP_CEILING_MS) {
  return wait(
    world,
    async () => (await trayLogSince(world, mark)).split(/\r?\n/).find((line) => line.includes(text)),
    `the tray to log "${text}"`,
    ceilingMs,
  );
}

const updateState = (world) => world.weir.get("/suite/update-state");

function waitForState(world, matches, what, ceilingMs = STEP_CEILING_MS) {
  return wait(
    world,
    async () => {
      const state = await updateState(world);
      return matches(state) ? state : undefined;
    },
    what,
    ceilingMs,
  );
}

/** Presses one of System > About's buttons, as the page does: the server writes the tray's request file. */
async function press(world, route) {
  const answer = await world.weir.write("POST", route, {});
  assert.equal(answer.status, 200, `${route} was answered ${answer.status}, not 200: ${answer.text.slice(0, 200)}`);
}

// --- the Weir a scenario starts from ------------------------------------------------------------------------------------------------

/**
 * Installs the newest published release on a clean machine, tells its tray what to do about updates, starts it and makes the first account.
 * @param {{feed: string, mode: "Auto"|"DownloadOnly"|"NotifyOnly", checkOnStartup: boolean}} options
 *   `feed` is what GitHub shows (github-stand-in.mjs): it is set before the tray starts, because a tray that checks at start asks at once.
 */
async function startPreviousRelease(world, { feed, mode, checkOnStartup }) {
  const { machine, weir } = world;
  await machine.removeEverything();
  world.account = undefined;
  weir.forgetSession();
  world.feed.use(feed);

  const setup = await machine.install(world.previousSetup);
  assert.equal(setup.exitCode, 0, `Setup --silent of the release ${world.previousVersion} exited ${setup.exitCode}, not 0`);
  await machine.writeUpdateSettings({ mode, checkOnStartup, checkIntervalMinutes: NEVER_CHECK_MINUTES });
  const trayPid = machine.startTray(PORT);
  await waitReady(world);
  const reported = await world.server.version();
  assert.equal(reported, world.previousVersion, `the installed release reports ${reported}, not ${world.previousVersion}`);
  await ensureSignedIn(world);
  await world.logWatch.begin();
  return { trayPid, setupSeconds: setup.seconds };
}

async function secretsOf(world) {
  return Object.fromEntries(await Promise.all(SECRET_FILES.map(async (name) => [name, await world.machine.readText(world.machine.secretPath(name))])));
}

/** The Activity entries Weir lists, all of them, newest first. */
async function allActivity(weir) {
  const items = [];
  let before = null;
  for (let page = 0; page < 500; page += 1) {
    const answer = await weir.get(`/activity/recent?limit=100&with_total=false${before ? `&before_id=${before}` : ""}`);
    items.push(...answer.items);
    if (!answer.has_more || answer.items.length === 0) return items;
    before = answer.items.at(-1).id;
  }
  throw new Error("Activity holds more entries than this check pages through.");
}

/** The entries of `before` that `after` does not hold exactly as they were; entries only in `after` (what happened since) are not a finding. */
export function activityProblems(before, after) {
  const now = new Map(after.map((item) => [String(item.id), item]));
  const problems = [];
  for (const item of before) {
    const found = now.get(String(item.id));
    if (!found) problems.push(`#${item.id} "${item.title}" is gone`);
    else {
      const changed = ACTIVITY_FIELDS.filter((field) => JSON.stringify(item[field] ?? null) !== JSON.stringify(found[field] ?? null));
      if (changed.length > 0) problems.push(`#${item.id} "${item.title}" changed (${changed.join(", ")})`);
    }
  }
  return problems;
}

/**
 * What the release is used for before the update, and what it must still hold afterwards: the account (signed in), a workflow with its
 * folders, a saved setting, a connection to a download client with its API key stored, the two secrets and Activity as it stands.
 */
async function useThePreviousRelease(world, id) {
  const { weir, machine } = world;
  const base = join(world.scratch, `${id}-${world.runId}`);
  const folders = { watched_folder: join(base, "watched"), work_folder: join(base, "work"), output_folder: join(base, "output") };
  await machine.ensureFolders(...Object.values(folders));
  const created = await weir.createLibrary({ name: `Scenario update ${id} ${world.runId}`, media_type: "movie", ...folders, skip_access_tests: true });
  const settings = await weir.suiteSettings();
  await weir.saveSuiteSettings({ app_timezone: settings.app_timezone, log_retention_days: LOG_RETENTION_DAYS });

  const apiKey = randomBytes(12).toString("hex");
  const downloadClient = new SabnzbdStandIn(apiKey);
  const port = await downloadClient.listen();
  const connection = await weir.write("POST", "/download-clients/connections", { kind: "sabnzbd", base_url: `http://localhost:${port}`, api_key: apiKey });
  assert.equal(connection.status, 201, `saving the download client connection was answered ${connection.status}: ${connection.text.slice(0, 200)}`);
  const connectionId = connection.json.id;
  await expectConnectionWorks(world, connectionId, "before the update");

  return {
    downloadClient,
    connectionId,
    kept: { ...world.account, library: pick(created) },
    secrets: await secretsOf(world),
    activity: await allActivity(weir),
  };
}

async function expectConnectionWorks(world, connectionId, when) {
  const answer = await world.weir.write("POST", `/download-clients/connections/${connectionId}/test`, {});
  assert.equal(answer.status, 200, `testing the download client connection ${when} was answered ${answer.status}: ${answer.text.slice(0, 200)}`);
  assert.equal(answer.json.ok, true, `the download client connection does not work ${when}: ${answer.json.detail}`);
}

// --- what an update must have done ----------------------------------------------------------------------------------------------------

async function waitForBuildUnderTest(world) {
  await wait(
    world,
    async () => {
      try {
        return (await world.server.ready()) && (await world.server.version()) === world.expectedVersion ? true : undefined;
      } catch {
        return undefined;
      }
    },
    `Weir to answer on the build under test (${world.expectedVersion})`,
    APPLY_CEILING_MS,
  );
}

/** The old release is still the one running: the same version answers, and the tray and server it was started with are still there. */
async function expectPreviousReleaseStillRunning(world, before) {
  const reported = await world.server.version();
  assert.equal(reported, world.previousVersion, `Weir reports ${reported}; the release ${world.previousVersion} should still be running`);
  const running = (await world.machine.processes()).map((process) => process.pid);
  assert.ok(running.includes(before.trayPid), "the tray the release was started with is no longer running");
}

/**
 * Weir after the update, judged against what it held before: the build under test answers, started by the update and not by hand, with a
 * tray and a server that are not the old ones and no window; the account is still signed in, the workflow and setting are as saved, the
 * secrets are the same bytes, the saved API key still works, Activity is as it was, and the copy of the data taken before the update is
 * named by System overview.
 */
async function expectUpdatedWithDataKept(world, before, { oldProcesses }) {
  const { machine, weir } = world;
  await waitForBuildUnderTest(world);
  const fileVersion = await machine.serverVersion();
  assert.equal(fileVersion, world.expectedVersion, `WeirServer.exe --version says ${fileVersion}`);

  const running = await machine.processes();
  const tray = running.find((process) => process.name === "Weir.exe");
  const server = running.find((process) => process.name === "WeirServer.exe");
  assert.ok(tray && server, "the updated Weir has no tray and server running");
  assert.equal(tray.hasWindow, false, "the updated tray opened a window");
  assert.ok(!oldProcesses.some((old) => old.pid === tray.pid || old.pid === server.pid), "the tray or server that answers is one that was running before the update");
  assert.equal(server.parentPid, tray.pid, "the new tray did not start the new server as its child");
  const addresses = await machine.listeningAddresses(PORT);
  assert.ok(addresses.includes("127.0.0.1") && addresses.every((address) => ["127.0.0.1", "::1"].includes(address)), `port ${PORT} listens on ${addresses.join(", ")}, not on this PC only`);

  await expectKept({ client: weir, kept: before.kept, sessionKept: true });
  assert.deepEqual(await secretsOf(world), before.secrets, "the update replaced a secret");
  await expectConnectionWorks(world, before.connectionId, "after the update");
  const problems = activityProblems(before.activity, await allActivity(weir));
  assert.deepEqual(problems.slice(0, 5), [], `Activity entries from before the update are not as they were (${problems.length})`);

  const backup = await wait(
    world,
    async () => (await weir.overview()).last_update_backup ?? undefined,
    "System overview to name the copy of the data saved for the update",
    STEP_CEILING_MS,
  );
  assert.equal(backup.from_version, world.previousVersion, `the copy says it was taken at ${backup.from_version}`);
  assert.equal(backup.to_version, world.expectedVersion, `the copy says it was taken for ${backup.to_version}`);
  const copy = (await machine.files(dirname(backup.path))).find((file) => file.path === basename(backup.path));
  assert.ok(copy && copy.bytes > 0, `the copy ${backup.path} is missing or empty`);
  return { tray, backup };
}

const oldRelease = (world) => `Weir ${world.previousVersion}`;

// --- update-over-installed-weir ------------------------------------------------------------------------------------------------------

/**
 * update-over-installed-weir: the update an installed Weir takes through System > About. With the tray told only to say that an update
 * exists, a feed with nothing newer is checked (nothing found), then one with the build under test (found, nothing downloaded by itself), then
 * the person downloads it (a delta against the release, which Velopack verifies by hash and rebuilds into the full package) and restarts
 * to apply it. Weir comes back on the build under test by itself, and everything it held is still there.
 */
async function updateOverInstalledWeir(world) {
  const { machine, feed } = world;
  const id = "update-over-installed-weir";
  const { trayPid } = await startPreviousRelease(world, { feed: "current", mode: "NotifyOnly", checkOnStartup: false });
  const before = await useThePreviousRelease(world, id);
  const oldProcesses = await machine.processes();
  await waitForState(world, (state) => state.tray_running === true, "the tray to say it is alive");

  // Nothing newer is published: the check finds nothing and says so.
  let mark = await logMark(world);
  await press(world, "/suite/check-update");
  await waitForTrayLine(world, "Check-now flag detected", mark);
  await waitForTrayLine(world, "No update available.", mark);
  const nothing = await waitForState(world, (state) => state.state === "idle", "the check to come to an answer");
  assert.equal(nothing.pending_version, null, `the check offers ${nothing.pending_version} although nothing newer is published`);
  assert.equal(nothing.downloaded, false);

  // The build under test is published: the check finds it, and in Notify only mode that is all that happens.
  feed.use("newer");
  mark = await logMark(world);
  await press(world, "/suite/check-update");
  await waitForTrayLine(world, `Update available: v${world.expectedVersion}`, mark);
  const found = await waitForState(world, (state) => state.state === "idle" && state.pending_version !== null, "the check to say what it found");
  assert.equal(found.pending_version, world.expectedVersion, `the check offers ${found.pending_version}, not ${world.expectedVersion}`);
  assert.equal(found.downloaded, false, "Notify only downloaded the update by itself");
  assert.ok(!(await trayLogSince(world, mark)).includes("Downloading update"), "Notify only started a download by itself");

  // The person downloads it: the tray asks GitHub for the delta only, verifies it, and keeps the full package it rebuilds.
  const asked = feed.requests.length;
  mark = await logMark(world);
  await press(world, "/suite/download-update");
  await waitForTrayLine(world, "Download-now flag detected", mark);
  await waitForTrayLine(world, "Update downloaded successfully.", mark);
  const downloaded = await waitForState(world, (state) => state.downloaded === true, "the update to be reported as downloaded");
  assert.equal(downloaded.pending_version, world.expectedVersion);
  assert.deepEqual(feed.packagesAskedFor(asked), [world.release.delta.FileName], "the tray did not take just the delta package");
  const kept = (await machine.files(machine.paths.packagesFolder, { hash: true })).find((file) => file.path === world.release.full.FileName);
  assert.ok(kept, `${world.release.full.FileName} is not in ${machine.paths.packagesFolder} after the download`);
  assert.equal(kept.sha256.toLowerCase(), world.release.full.SHA256.toLowerCase(), "the full package rebuilt from the delta is not the one the feed names");
  await expectPreviousReleaseStillRunning(world, { trayPid });

  // The person restarts to apply it.
  mark = await logMark(world);
  await press(world, "/suite/apply-update");
  await waitForTrayLine(world, "Apply-now flag detected - applying update and restarting.", mark);
  await waitForTrayLine(world, `Asking the server to save a copy of Weir's data before updating to v${world.expectedVersion}`, mark);
  await waitForTrayLine(world, "The server saved a copy of Weir's data.", mark);
  await waitForTrayLine(world, `Applying update v${world.expectedVersion} and restarting...`, mark);
  const { tray, backup } = await expectUpdatedWithDataKept(world, before, { oldProcesses });
  await waitForTrayLine(world, `Velopack: after update to v${world.expectedVersion}`, mark);

  return (
    `${oldRelease(world)} was installed on a clean machine and used (an account, a workflow, a saved setting and a download client connection). With the tray told only to say that an update exists, ` +
    `a check with nothing newer published found nothing, a check with the build published found ${world.expectedVersion} and downloaded nothing; Download update took only ${world.release.delta.FileName} ` +
    `and rebuilt ${world.release.full.FileName} with the hash the feed names; Restart and apply saved a copy of the data, applied it, and Weir came back by itself as ${world.expectedVersion} ` +
    `(new tray pid ${tray.pid}, no window, this PC only); the session still worked, the workflow, setting and ${before.activity.length} Activity entries were as they were, the two secrets were the same bytes, ` +
    `the saved API key still worked, and System overview names the copy ${basename(backup.path)}.`
  );
}

// --- update-installs-when-idle -----------------------------------------------------------------------------------------------------------

/**
 * update-installs-when-idle: in Automatic mode the tray checks when it starts, downloads the full package (the feed offers no delta), says it
 * installs once Weir has been idle for 5 minutes, leaves the release running until then, and applies the update by itself when Weir has been
 * idle that long.
 */
async function updateInstallsWhenIdle(world) {
  const { machine, feed } = world;
  const { trayPid } = await startPreviousRelease(world, { feed: "newer-full", mode: "Auto", checkOnStartup: true });
  const before = await useThePreviousRelease(world, "update-installs-when-idle");
  const oldProcesses = await machine.processes();

  // The tray's log was made new by this scenario's install, so all of it is this scenario's.
  const start = 0;
  await waitForTrayLine(world, "Update downloaded successfully.", start);
  await waitForTrayLine(world, `installing once Weir has been idle for ${IDLE_MINUTES} minutes`, start);
  assert.deepEqual(feed.packagesAskedFor(), [world.release.full.FileName], "the tray did not take just the full package");
  await expectPreviousReleaseStillRunning(world, { trayPid });

  await waitForTrayLine(world, `Weir has been idle for ${IDLE_MINUTES} minutes; installing update v${world.expectedVersion} now.`, start, IDLE_CEILING_MS);
  await waitForTrayLine(world, `Applying update v${world.expectedVersion} and restarting...`, start);
  const { tray, backup } = await expectUpdatedWithDataKept(world, before, { oldProcesses });

  return (
    `In Automatic mode the tray checked when it started, took ${world.release.full.FileName} and said it installs once Weir has been idle for ${IDLE_MINUTES} minutes; ` +
    `${oldRelease(world)} was still the one running until then, and once Weir had been idle that long it applied the update by itself and Weir came back as ${world.expectedVersion} ` +
    `(new tray pid ${tray.pid}, no window) with the account, workflow, setting, secrets, saved API key and Activity as they were, and the copy ${basename(backup.path)} saved first.`
  );
}

// --- update-left-waiting-installs-at-start --------------------------------------------------------------------------------------------------

/**
 * update-left-waiting-installs-at-start: in Download only mode the tray downloads the update and leaves it waiting (Weir does not restart
 * by itself); when the tray is ended without quitting, as a Windows sign-out does, the next start installs the waiting update before the
 * server starts.
 */
async function updateLeftWaitingInstallsAtStart(world) {
  const { machine } = world;
  const { trayPid } = await startPreviousRelease(world, { feed: "newer", mode: "DownloadOnly", checkOnStartup: true });
  const before = await useThePreviousRelease(world, "update-left-waiting-installs-at-start");
  const oldProcesses = await machine.processes();

  await waitForState(world, (state) => state.downloaded === true, "the update to be downloaded", STEP_CEILING_MS);
  assert.ok(!(await trayLog(world)).includes("installing update"), "Download only installed the update by itself");
  await expectPreviousReleaseStillRunning(world, { trayPid });

  // The tray is ended the way a sign-out ends it, with the update downloaded and nothing installed.
  await machine.stopWeir();
  const mark = await logMark(world);
  machine.startTray(PORT);
  await waitForTrayLine(world, `Update v${world.expectedVersion} was left waiting to install. Installing it before the server starts.`, mark);
  const { tray, backup } = await expectUpdatedWithDataKept(world, before, { oldProcesses });
  await waitForTrayLine(world, `Velopack: after update to v${world.expectedVersion}`, mark);
  assert.ok(await machine.exists(join(machine.paths.runtimeHome, "update-start-attempt")), "the tray did not record that it tried the update at start");

  return (
    `In Download only mode the tray downloaded ${world.expectedVersion} and left ${oldRelease(world)} running; with the tray ended by force, the next start logged that the update was left waiting, ` +
    `installed it before the server started, and Weir came back as ${world.expectedVersion} (new tray pid ${tray.pid}, no window) with the account, workflow, setting, secrets, saved API key and Activity as they were; ` +
    `System overview names the copy ${basename(backup.path)}.`
  );
}

// --- update-failure-leaves-the-old-weir -----------------------------------------------------------------------------------------------------

/**
 * update-failure-leaves-the-old-weir: a package that was changed after its hash was recorded is refused when downloaded, and GitHub being down
 * fails the check; each says why in plain words, applies nothing, and leaves the release running with its tray and server. When the feed is
 * right again the same download goes through.
 */
async function updateFailureLeavesTheOldWeir(world) {
  const { feed } = world;
  const { trayPid } = await startPreviousRelease(world, { feed: "corrupt", mode: "NotifyOnly", checkOnStartup: false });
  const oldProcesses = await world.machine.processes();
  await waitForState(world, (state) => state.tray_running === true, "the tray to say it is alive");

  // A package that does not match its hash.
  let mark = await logMark(world);
  await press(world, "/suite/check-update");
  await waitForTrayLine(world, `Update available: v${world.expectedVersion}`, mark);
  await waitForState(world, (state) => state.state === "idle" && state.pending_version === world.expectedVersion, "the check to say what it found");
  mark = await logMark(world);
  await press(world, "/suite/download-update");
  await waitForTrayLine(world, "Update download failed:", mark);
  const refused = await waitForState(world, (state) => state.state === "failed", "the download to be reported as failed");
  expectPlainFailure(refused.failure, "download the update");
  assert.equal(refused.downloaded, false);
  assert.ok(!(await world.machine.files(world.machine.paths.packagesFolder)).some((file) => file.path === world.release.full.FileName), "a package that failed its hash was kept as the update");
  const apply = await world.weir.write("POST", "/suite/apply-update", {});
  assert.equal(apply.status, 409, `Restart and apply with nothing downloaded was answered ${apply.status}, not 409`);
  await expectPreviousReleaseStillRunning(world, { trayPid });

  // GitHub is down.
  feed.use("down");
  mark = await logMark(world);
  await press(world, "/suite/check-update");
  await waitForTrayLine(world, "Update check failed:", mark);
  const down = await waitForState(world, (state) => state.state === "failed" && state.failure !== refused.failure, "the check to be reported as failed");
  expectPlainFailure(down.failure, "reach GitHub");
  await expectPreviousReleaseStillRunning(world, { trayPid });

  // The feed is right again: the same steps go through.
  feed.use("newer");
  await press(world, "/suite/check-update");
  await waitForState(world, (state) => state.state === "idle" && state.pending_version === world.expectedVersion && state.failure === null, "the check to find the update again");
  await press(world, "/suite/download-update");
  await waitForState(world, (state) => state.downloaded === true, "the update to download from the right feed");
  const survivors = (await world.machine.processes()).filter((process) => oldProcesses.some((old) => old.pid === process.pid));
  assert.equal(survivors.length, oldProcesses.length, "the tray or server of the release stopped while the update failed");

  return (
    `${oldRelease(world)} was told to say that updates exist. A package served with one byte changed after its hash was recorded was refused ("${refused.failure}"), nothing was kept as the update, ` +
    `and Restart and apply was refused with 409; with GitHub down the check failed ("${down.failure}"). The release kept running with the same tray and server each time, and once the feed was right again the check found ${world.expectedVersion} and the download went through.`
  );
}

/** A failure the page shows is a sentence of plain words that names what failed, not an exception. */
export function expectPlainFailure(failure, mentions) {
  assert.ok(typeof failure === "string" && failure.length > 0, "the failure is not explained");
  assert.match(failure, /^Weir could not /, `the failure does not start in plain words: ${failure}`);
  assert.ok(failure.includes(mentions), `the failure does not say it could not ${mentions}: ${failure}`);
  assert.doesNotMatch(failure, /Exception|Velopack|Checksum|SHA|\bat [A-Z]\w+\./, `the failure shows what the code threw: ${failure}`);
}

/** The scenarios of the windows-update shard, by id, in the order they run. */
export const SCENARIOS = {
  "update-over-installed-weir": updateOverInstalledWeir,
  "update-installs-when-idle": updateInstallsWhenIdle,
  "update-left-waiting-installs-at-start": updateLeftWaitingInstallsAtStart,
  "update-failure-leaves-the-old-weir": updateFailureLeavesTheOldWeir,
};
