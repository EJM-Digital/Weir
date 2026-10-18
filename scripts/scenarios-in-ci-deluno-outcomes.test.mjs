// The with-deluno scenarios about what a hand-off ends in (a release with an extra, a send again, the outcomes, a release Weir fails, rejects or finds
// deleted), judged against stand-ins for Deluno, qBittorrent, the recording proxy and Weir: each must pass when the applications agree and fail,
// saying what, for each place they can disagree. The real applications are the stack in CI.
import assert from "node:assert/strict";
import { test } from "node:test";

import { runScenarios } from "./scenarios-ci/run.mjs";
import { SHARDS } from "./scenarios-ci/shards.mjs";
import { nothingWasOwed } from "./scenarios-ci/with-deluno/outcomes.mjs";
import { WORKFLOW_SETTINGS, workflowSave } from "./scenarios-ci/with-deluno/failure-support.mjs";

function fakeClock() {
  let now = 0;
  return { now: () => now, sleep: async (ms) => void (now += ms) };
}

const RELEASE = "Scna1b2c3Film.2020.1080p.WEB-DL.x264-WEIR";
const OUTPUT = `/weir/Ready/Movies/${RELEASE}`;
const WORKFLOW = { id: 3, name: "Movies", folders_synced_from_connection_id: 7, manager_connection_ids: [7], watched_folder: "/downloads/completed/Movies", output_folder: "/weir/Ready/Movies", min_file_size_mb: 50, failure_policy: "pass_through", media_type: "movie" };

/**
 * A stack in which the film goes through and everything agrees. `wrong` changes one thing.
 * @param {object} [wrong]
 */
function stack(wrong = {}) {
  const calls = [];
  const kept = [];
  const files = new Set([`/downloads/completed/Movies/${RELEASE}/${RELEASE}.mkv`, `/downloads/completed/Movies/${RELEASE}/Extras/Featurette.mkv`]);
  let paused = false;
  let torrentAsks = 0;
  let logReads = 0;
  const handoff = {
    id: "h1",
    releaseName: RELEASE,
    mediaType: "movies",
    status: "completed",
    importJobId: "job-1",
    outputPath: OUTPUT,
    createdUtc: "2026-10-18T10:00:00Z",
    sourceFiles: [`/downloads/completed/Movies/${RELEASE}/${RELEASE}.mkv`],
    outcome: { outcome: "imported", delivery: "delivered", attempts: 1, lastAttemptUtc: new Date().toISOString(), processorSaid: "Weir recorded it." },
    ...wrong.handoff,
  };
  const row = { id: 1, library_id: 3, relative_path: `${RELEASE}/${RELEASE}.mkv`, status: "processed", handback: { output_path: `${OUTPUT}/${RELEASE}.mkv`, outcome: "imported", settled_at: "t1", released_at: "t2" }, ...wrong.row };
  const extraRows = wrong.extraRows ?? [];
  const reports = wrong.reports ?? [];
  const activity = wrong.activity ?? [];
  const workflow = { ...WORKFLOW };
  const weir = {
    write: async (method, path, fields) => {
      calls.push(`${method} ${path} ${JSON.stringify(fields ?? {})}`);
      if (path === "/pause") {
        paused = fields.paused;
        return { status: 200, json: { paused }, text: "" };
      }
      if (path.startsWith("/processing/libraries/") && method === "PUT") {
        Object.assign(workflow, fields);
        return { status: wrong.saveRefused ? 400 : 200, json: { ...workflow }, text: wrong.saveRefused ? "no" : "" };
      }
      return { status: 200, json: {}, text: "" };
    },
    get: async (path) => {
      if (path.startsWith("/system/log")) return { items: ++logReads > 1 ? (wrong.logRows ?? []) : [], next_cursor: null };
      if (path === "/processing/libraries") return [workflow];
      if (path.startsWith("/activity/recent")) return { items: path.includes("event_type=processing.file_rejected") ? activity.filter((item) => item.event_type === "processing.file_rejected") : activity };
      if (path.startsWith("/processing/files?limit=1&")) return { status_counts: { processing_failed: 0, rejected: 0, ...wrong.counts }, files: [] };
      if (path.startsWith("/processing/files")) return { files: wrong.noFile ? extraRows : [row, ...extraRows] };
      throw new Error(`The stand-in has no route ${path}`);
    },
  };
  const exchanges = {
    where: async (route, test) => (route === "to-deluno" ? reports.filter(test) : (wrong.toWeir ?? [{ request: { method: "POST", body: { releaseName: RELEASE } }, reply: { status: 200 } }]).filter(test)),
  };
  const delunoApi = {
    call: async (method, path) => (calls.push(`deluno ${method} ${path}`), wrong.sendAgain ?? { status: 200, json: { message: "sent", outcome: { ...handoff.outcome, attempts: 2, lastAttemptUtc: new Date().toISOString() } }, text: "" }),
    must: async (method, path) => {
      calls.push(`deluno ${method} ${path}`);
      if (path.startsWith("/api/integrations/processors/handoffs")) return wrong.handoffs ?? [handoff];
      if (path === "/api/blocked-releases") return wrong.noBlocklist ? [] : [{ id: "b1", releaseName: RELEASE }];
      throw new Error(`The stand-in has no Deluno route ${path}`);
    },
  };
  const deluno = {
    connectWeir: async () => ({ id: 7, how: "connected Weir to Deluno" }),
    makeFilm: async (options) => (calls.push(`makeFilm ${JSON.stringify({ ...options, box: undefined })}`), { title: "Scna1b2c3Film", year: options.year, release: RELEASE, infohash: "abc123", size: 56_000_000, url: "http://fixture:8765/x.torrent" }),
    sendFilm: async () => {},
    handoffOf: async () => handoff,
    keepConversation: (made, recording) => void kept.push(`${recording.state} ${made.id}`),
    qbittorrent: { torrent: async () => (wrong.torrentGone && ++torrentAsks > 1 ? undefined : { progress: 1, amount_left: 0, size: 56_000_000 }) },
    exchanges,
    deluno: delunoApi,
  };
  const box = {
    exists: async (path) => files.has(path) && !wrong.extraGone,
    sizeOf: async () => wrong.extraSize ?? 2_000_000,
    findNamed: async () => wrong.writtenExtras ?? [],
    removeTree: async (path) => {
      for (const file of [...files]) if (file.startsWith(path)) files.delete(file);
    },
  };
  return { calls, kept, world: { weir, deluno, box, runId: "a1b2c3", clock: fakeClock() }, handoff, workflow, isPaused: () => paused };
}

const run = (id, made) => runScenarios({ ids: [id], registry: SHARDS["with-deluno"].scenarios, world: made.world }).then(([result]) => result);
const good = (id, wrong) => {
  const made = stack(wrong);
  return run(id, made).then((result) => ({ result, made }));
};

// --- a release with an extra --------------------------------------------------------------------------------------------

test("a release with an extra: Deluno names only the film, the extra stays in the download, and Weir writes nothing for it", async () => {
  const { result, made } = await good("deluno-release-with-extra");
  assert.equal(result.status, "passed", result.detail);
  assert.match(result.detail, /naming only the film/);
  assert.deepEqual(made.kept, ["real-release-with-extra h1"]);
  assert.ok(made.calls.some((call) => call.startsWith("makeFilm") && call.includes("Featurette.mkv")), "the release was made with an extra");
});

test("a release with an extra fails when Deluno names the extra, when it is gone, changed, written to the output or not left alone", async () => {
  const named = await good("deluno-release-with-extra", { handoff: { sourceFiles: [`/d/${RELEASE}.mkv`, "/d/Extras/Featurette.mkv"] } });
  assert.match(named.result.detail, /names the one file at or above the minimum/);
  const gone = await good("deluno-release-with-extra", { extraGone: true });
  assert.match(gone.result.detail, /is still in the release folder where qBittorrent left it/);
  const changed = await good("deluno-release-with-extra", { extraSize: 10 });
  assert.match(changed.result.detail, /as it was downloaded/);
  const written = await good("deluno-release-with-extra", { writtenExtras: ["/weir/Ready/Movies/Featurette.mkv"] });
  assert.match(written.result.detail, /wrote nothing named for the extra/);
  const worked = await good("deluno-release-with-extra", { extraRows: [{ id: 9, library_id: 3, relative_path: `${RELEASE}/Extras/Featurette.mkv`, status: "processing_failed" }] });
  assert.match(worked.result.detail, /left it alone as skipped/);
  const needs = await good("deluno-release-with-extra", { counts: { processing_failed: 1 } });
  assert.match(needs.result.detail, /nothing in 'Movies' needs the person/);
});

// --- send again ---------------------------------------------------------------------------------------------------------

test("a send again: Deluno asks Weir again, Weir takes it, and its file stays as it was", async () => {
  const { result, made } = await good("deluno-resend");
  assert.equal(result.status, "passed", result.detail);
  assert.ok(made.calls.includes("deluno POST /api/integrations/processors/handoffs/h1/outcome/send-again"));
});

test("a send again fails when Deluno will not, when Weir refuses it, when no attempt was made or when it raises a failure", async () => {
  const refused = await good("deluno-resend", { sendAgain: { status: 404, json: { message: "nothing recorded" }, text: "" } });
  assert.match(refused.result.detail, /Deluno sent the outcome again \(answered 404: nothing recorded\)/);
  const delivery = await good("deluno-resend", { sendAgain: { status: 200, json: { outcome: { outcome: "imported", delivery: "refused", attempts: 1, lastAttemptUtc: new Date().toISOString(), processorSaid: "no" } }, text: "" } });
  assert.match(delivery.result.detail, /never refused \(it reads refused: no\)/);
  const stale = await good("deluno-resend", { sendAgain: { status: 200, json: { outcome: { outcome: "imported", delivery: "delivered", attempts: 1, lastAttemptUtc: "2020-01-01T00:00:00Z" } }, text: "" } });
  assert.match(stale.result.detail, /really sent it to Weir just now/);
});

// --- outcomes -----------------------------------------------------------------------------------------------------------

test("outcomes: none is pending, refused or given up, and Weir's answer is Deluno's", async () => {
  const { result } = await good("deluno-outcomes");
  assert.equal(result.status, "passed", result.detail);
  assert.match(result.detail, /1 of Weir's files carry the same answer as Deluno/);
});

test("outcomes fail for one Deluno could not deliver, and for one Weir recorded differently", async () => {
  const pending = await good("deluno-outcomes", { handoff: { outcome: { outcome: "imported", delivery: "pending", attempts: 3, processorSaid: "Weir is down" } } });
  assert.match(pending.result.detail, /delivery pending, not pending, refused, never received or given up \(Deluno said: Weir is down\)/);
  const other = { id: "h2", releaseName: "Other.Film.2019", mediaType: "movies", status: "completed", outputPath: "/weir/Ready/Movies/Other.Film.2019", outcome: { outcome: "imported", delivery: "delivered", attempts: 1 } };
  const otherRow = { id: 5, library_id: 3, relative_path: "Other.Film.2019/Other.Film.2019.mkv", status: "processed", handback: { output_path: "/weir/Ready/Movies/Other.Film.2019/Other.Film.2019.mkv", outcome: "not_imported" } };
  const differs = await good("deluno-outcomes", { handoffs: [stack().handoff, other], extraRows: [otherRow] });
  assert.match(differs.result.detail, /Deluno says imported and Weir says not_imported/);
});

test("a closing outcome for a hand-off that handed no file back is nothing owed; any other give-up is not", () => {
  const outcome = { outcome: "not-imported", delivery: "given-up", attempts: 0, processorSaid: "No file was handed back, so there was nothing to tell it" };
  const handoff = { outputPath: "" };
  assert.equal(nothingWasOwed(handoff, { ...outcome, processorSaid: "no file was handed back, so there was nothing to tell it." }, []), true);
  assert.equal(nothingWasOwed(handoff, outcome, []), false, "Deluno's words are lower case");
  assert.equal(nothingWasOwed({ outputPath: OUTPUT }, { ...outcome, processorSaid: "no file was handed back, so there was nothing to tell it" }, []), false);
  assert.equal(nothingWasOwed(handoff, { ...outcome, processorSaid: "the connection was removed" }, []), false);
  assert.equal(nothingWasOwed(handoff, { ...outcome, attempts: 2, processorSaid: "no file was handed back, so there was nothing to tell it" }, []), false);
});

// --- a release Weir fails -----------------------------------------------------------------------------------------------

const failedHandoff = { status: "failed", failureMessage: "Weir reports failed: Weir can't read this file yet.", outcome: null, outputPath: null };
const failedReport = (body = {}, reply = { status: 202 }) => ({ request: { method: "POST", path: "/api/integrations/processors/events", body: { handoffId: "h1", status: "failed", message: "Weir can't read this file yet.", failureClass: "preflight", disposition: "held", ...body } }, reply });
const failedRow = { id: 2, library_id: 3, relative_path: `${RELEASE}/${RELEASE}.mkv`, status: "processing_failed", handback: null };
const failedStack = (wrong = {}) => ({ handoff: failedHandoff, row: failedRow, reports: [failedReport()], ...wrong });

test("a release Weir cannot read: Deluno shows the hand-off failed in Weir's words and the download is left", async () => {
  const { result, made } = await good("deluno-failed-release", failedStack());
  assert.equal(result.status, "passed", result.detail);
  assert.deepEqual(made.kept, ["real-failed-unreadable h1"]);
  assert.ok(made.calls.some((call) => call.startsWith("makeFilm") && call.includes('"zeros":true')), "the release's file is zeros");
});

test("a release Weir cannot read fails the scenario when Deluno shows other words, when Weir called it finished or showed it cleaned", async () => {
  const words = await good("deluno-failed-release", failedStack({ handoff: { ...failedHandoff, failureMessage: "something else" } }));
  assert.match(words.result.detail, /failed in Weir's words/);
  const finished = await good("deluno-failed-release", failedStack({ reports: [failedReport(), failedReport({ status: "completed" })] }));
  assert.match(finished.result.detail, /never reported the unreadable file as finished/);
  const cleaned = await good("deluno-failed-release", failedStack({ row: { ...failedRow, status: "processed" } }));
  assert.match(cleaned.result.detail, /shows the file as not cleaned/);
  const refused = await good("deluno-failed-release", failedStack({ reports: [failedReport({}, { status: 409 })] }));
  assert.match(refused.result.detail, /Deluno accepted Weir's failed report \(it answered 409\)/);
});

test("the warnings a release Weir fails logs are expected when they are about its film, and any other is not", async () => {
  const about = { source: "weir", id: 1, at: "t", title: "Weir could not read Scna1b2c3Film.2020.1080p.WEB-DL.x264-WEIR.mkv" };
  const expected = await good("deluno-failed-release", failedStack({ logRows: [about] }));
  assert.equal(expected.result.status, "passed", expected.result.detail);
  assert.match(expected.result.detail, /The logs gained only what the scenario expected: Weir could not read Scna1b2c3Film/);
  const other = await good("deluno-failed-release", failedStack({ logRows: [about, { source: "weir", id: 2, at: "t", title: "Disk is nearly full" }] }));
  assert.equal(other.result.status, "failed");
  assert.match(other.result.detail, /gained 1 unexpected warning or error \(Disk is nearly full\)/);
});

// --- a release Weir rejects ---------------------------------------------------------------------------------------------

const rejectedReport = (body = {}, reply = { status: 202 }) => failedReport({ disposition: "rejected", sourceRemoved: false, failureClass: "preflight", message: "This file contains no video.", ...body }, reply);
const rejectedActivity = (detail = `Deluno accepted that this release is bad and can find a different one. Deluno will remove the download; Weir left it in place.`) => [{ event_type: "processing.file_rejected", title: `${RELEASE}.mkv was rejected so a different release can be found`, detail }];
const rejectedStack = (wrong = {}) => ({ torrentGone: true, handoff: { ...failedHandoff, failureMessage: "rejected" }, row: { ...failedRow, status: "rejected" }, reports: [rejectedReport()], activity: rejectedActivity(), ...wrong });

test("a release Weir rejects: reported rejected with the download left, Weir says so, Deluno refuses it and forgets the torrent, and the policy is put back", async () => {
  const { result, made } = await good("deluno-rejected-release", rejectedStack());
  assert.equal(result.status, "passed", result.detail);
  assert.deepEqual(made.kept, ["real-rejected-audio-only h1"]);
  assert.ok(made.calls.some((call) => call.startsWith("makeFilm") && call.includes('"content":"audio-only"')));
  const saves = made.calls.filter((call) => call.startsWith("PUT /processing/libraries/3"));
  assert.equal(saves.length, 2);
  assert.match(saves[0], /"failure_policy":"reject"/);
  assert.match(saves[1], /"failure_policy":"pass_through"/);
  assert.equal(made.workflow.failure_policy, "pass_through");
});

test("a release Weir rejects fails the scenario when the report is not a rejection, says the source was removed, or Activity or Deluno disagree; the policy is still put back", async () => {
  const held = await good("deluno-rejected-release", rejectedStack({ reports: [rejectedReport({ disposition: "held" })] }));
  assert.match(held.result.detail, /Weir reported the release rejected \(it said disposition held\)/);
  const removed = await good("deluno-rejected-release", rejectedStack({ reports: [rejectedReport({ sourceRemoved: true })] }));
  assert.match(removed.result.detail, /did not remove the download/);
  const silent = await good("deluno-rejected-release", rejectedStack({ activity: rejectedActivity("Weir rejected it.") }));
  assert.match(silent.result.detail, /says Deluno will remove the download and Weir left it in place/);
  assert.equal(silent.made.workflow.failure_policy, "pass_through");
  const kept = await good("deluno-rejected-release", rejectedStack({ noBlocklist: true }));
  assert.match(kept.result.detail, /Deluno to refuse .* did not happen/);
});

// --- a download deleted before Weir cleans it ---------------------------------------------------------------------------

const goneReport = (body = {}) => failedReport({ failureClass: "source_gone", message: "The download is no longer there, so Weir had nothing to do.", disposition: undefined, sourceRemoved: false, ...body });
const goneStack = (wrong = {}) => ({ handoff: { ...failedHandoff, failureMessage: "Weir reports failed for it: The download is no longer there, so Weir had nothing to do." }, row: failedRow, reports: [goneReport()], ...wrong });

test("a download deleted while Weir holds the hand-off: after Weir resumes it reports the download gone and Deluno shows it failed in those words", async () => {
  const { result, made } = await good("deluno-deleted-before-cleaning", goneStack());
  assert.equal(result.status, "passed", result.detail);
  assert.deepEqual(made.kept, ["real-deleted-before-cleaning h1"]);
  const pauses = made.calls.filter((call) => call.startsWith("PUT /pause"));
  assert.match(pauses[0], /"paused":true/);
  assert.match(pauses.at(-1), /"paused":false/);
  assert.equal(made.isPaused(), false);
});

test("a deleted download fails the scenario when Weir called it something else or Deluno disagrees, and Weir is resumed all the same", async () => {
  const other = await good("deluno-deleted-before-cleaning", goneStack({ reports: [goneReport({ failureClass: "preflight" })] }));
  assert.match(other.result.detail, /Weir reported the download gone \(failure class preflight/);
  assert.equal(other.made.isPaused(), false, "a failed scenario does not leave Weir paused");
  const words = await good("deluno-deleted-before-cleaning", goneStack({ handoff: { ...failedHandoff, failureMessage: "else" } }));
  assert.match(words.result.detail, /failed in Weir's words/);
  const never = await good("deluno-deleted-before-cleaning", goneStack({ toWeir: [] }));
  assert.match(never.result.detail, /Weir to take in Deluno's hand-off of .* did not happen/);
});

// --- saving a workflow --------------------------------------------------------------------------------------------------

test("a workflow is saved with the settings the save takes and none of the bookkeeping it reads back, and a change on top", () => {
  const read = { ...Object.fromEntries(WORKFLOW_SETTINGS.map((name) => [name, `v-${name}`])), id: 3, display_order: 1, manager_coverage: "x", updated_at: "t", folders_synced_from_connection_id: 7 };
  const saved = workflowSave(read, { failure_policy: "reject" });
  assert.deepEqual(Object.keys(saved).sort(), [...WORKFLOW_SETTINGS].sort());
  assert.equal(saved.failure_policy, "reject");
  assert.equal(saved.name, "v-name");
  assert.equal("id" in saved, false);
});
