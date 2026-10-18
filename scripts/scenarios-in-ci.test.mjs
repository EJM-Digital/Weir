// node --test scripts/scenarios-in-ci.test.mjs
// The parts of the scenarios that run in CI (scripts/scenarios-in-ci.mjs, scripts/scenarios-ci/, docs/exec-plans/active/PLAN-SCENARIOS-IN-CI.md)
// that decide things without Docker: where each VM scenario now runs and that the plan says the same, how the film is made and judged, what
// the logs gained, the scenarios themselves against a small stand-in for Weir and the container, and the workflow that runs them. The Docker
// steps run in CI (ci-packaging.yml).
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { ContainerBox } from "./scenarios-ci/container-box.mjs";
import { filmArguments, filmCommand, cleanedFilmProblems, summarizeProbe } from "./scenarios-ci/film.mjs";
import { catalogIds, linuxShards, loadHomes, problemsWithHomes, scenariosOfShard } from "./scenarios-ci/homes.mjs";
import { LogWatch, newProblemRows } from "./scenarios-ci/log-watch.mjs";
import { exitCode, runScenarios, summaryMarkdown } from "./scenarios-ci/run.mjs";
import { SHARDS, allScenarios } from "./scenarios-ci/shards.mjs";
import { SCENARIOS } from "./scenarios-ci/weir-only.mjs";
import { FakeWorld } from "./scenarios-ci/fake-world.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(join(repository, path), "utf8");
const homes = loadHomes();
const catalog = catalogIds(read("scripts/scenarios/Weir.Scenarios.Catalog.ps1"));

// --- where each scenario runs -------------------------------------------------------------------------------------------

test("every scenario of the VM catalogue has a home, and nothing else has", () => {
  assert.equal(catalog.length, 14);
  assert.deepEqual(problemsWithHomes(homes, catalog), []);
});

test("a home without a reason, an unknown shard or a made-up scenario is a problem", () => {
  const broken = structuredClone(homes);
  broken.scenarios[0].why = "";
  broken.scenarios[1].shard = "no-such-shard";
  broken.scenarios.push({ id: "invented", home: "linux", shard: "weir-only", state: "ported", why: "x" });
  broken.scenarios.push({ id: "weir-only-film", home: "linux", shard: "weir-only", state: "ported", why: "twice" });
  const problems = problemsWithHomes(broken, catalog).join("\n");
  assert.match(problems, new RegExp(`${broken.scenarios[0].id}.*reason`));
  assert.match(problems, /no-such-shard/);
  assert.match(problems, /invented.*not in the catalogue/);
  assert.match(problems, /weir-only-film.*more than once/);
});

test("a scenario that cannot move says why, and has no shard", () => {
  const broken = structuredClone(homes);
  broken.scenarios[0] = { id: broken.scenarios[0].id, home: "not-portable", shard: "weir-only", state: "to-port", why: "" };
  const problems = problemsWithHomes(broken, catalog).join("\n");
  assert.match(problems, /not-portable.*reason/);
  assert.match(problems, /not-portable.*no shard/);
});

test("a scenario's passes in CI need a run, a commit and a ref, and green twice needs two commits on main", () => {
  const broken = structuredClone(homes);
  const [film, again, pause] = ["weir-only-film", "process-again", "pause-resume"].map((id) => broken.scenarios.find((scenario) => scenario.id === id));
  const on = (ref, commit) => ({ run: 1, commit: commit.repeat(40), ref });
  film.state = "green-twice";
  film.passedInCi = [on("main", "a"), on("main", "a"), on("branch", "b")];
  again.passedInCi = [{ run: "x", commit: "abc", ref: "" }];
  pause.state = "to-port";
  pause.passedInCi = [on("main", "c")];
  const problems = problemsWithHomes(broken, catalog).join("\n");
  assert.match(problems, /weir-only-film: green-twice needs passes on main on two different commits \(it has 1\)/);
  assert.match(problems, /process-again: a pass needs its run number/);
  assert.match(problems, /pause-resume: it is to-port but lists runs/);
  film.passedInCi = [on("main", "a"), on("main", "b")];
  assert.doesNotMatch(problemsWithHomes(broken, catalog).join("\n"), /weir-only-film/);
});

test("the plan's table says what the manifest says, row for row", () => {
  const plan = read("docs/exec-plans/active/PLAN-SCENARIOS-IN-CI.md");
  const rows = new Map([...plan.matchAll(/^\| `([a-z-]+)` \| ([a-z-]+) \| ([a-z-]+|none) \| ([a-z-]+) \|/gm)].map((match) => [match[1], match.slice(2)]));
  for (const scenario of homes.scenarios) {
    assert.deepEqual(rows.get(scenario.id), [scenario.home, scenario.shard ?? "none", scenario.state], `the plan's row for ${scenario.id}`);
  }
  assert.equal(rows.size, homes.scenarios.length, "the plan has a row for every scenario and no other");
});

test("every ported Linux scenario has code, and every scenario with code is ported", () => {
  const ported = homes.scenarios.filter((scenario) => scenario.home === "linux" && scenario.state !== "to-port").map((scenario) => scenario.id);
  assert.deepEqual(Object.keys(allScenarios()).sort(), ported.sort());
});

test("every Linux shard with a ported scenario runs exactly its own scenarios", () => {
  for (const shard of linuxShards(homes)) {
    const ported = scenariosOfShard(homes, shard).filter((scenario) => scenario.state !== "to-port").map((scenario) => scenario.id);
    assert.deepEqual(Object.keys(SHARDS[shard]?.scenarios ?? {}).sort(), ported.sort(), shard);
  }
});

test("a shard is its scenarios in catalogue order, and only shards with a ported scenario are in the workflow's matrix", () => {
  assert.deepEqual(scenariosOfShard(homes, "weir-only").filter((scenario) => scenario.state === "ported").map((scenario) => scenario.id), ["weir-only-film", "process-again", "pause-resume", "deleted-queued-file"]);
  const workflow = read(".github/workflows/ci-packaging.yml");
  const listed = /scenarios-linux:[\s\S]*?shard: \[([^\]]*)\]/.exec(workflow)?.[1].split(",").map((name) => name.trim());
  assert.deepEqual(listed, linuxShards(homes));
});

// --- the film -----------------------------------------------------------------------------------------------------------

test("the film has one video, three audio languages and two subtitle tracks, each mapped and tagged", () => {
  const args = filmArguments({ path: "/tmp/x/Film.mkv", seconds: 30 });
  assert.deepEqual(args.slice(0, 8), ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i"]);
  assert.equal(args.filter((arg) => arg.startsWith("sine=frequency=")).length, 3);
  assert.deepEqual(args.filter((arg) => arg.endsWith(".srt")), ["/tmp/x/Film.eng.srt", "/tmp/x/Film.spa.srt"]);
  const maps = args.flatMap((arg, i) => (args[i - 1] === "-map" ? [arg] : []));
  assert.deepEqual(maps, ["0:v", "1:a", "2:a", "3:a", "4:s", "5:s"]);
  for (const tag of ["language=eng", "language=fre", "language=jpn", "language=spa"]) assert.ok(args.includes(tag), tag);
  assert.equal(args.at(-1), "/tmp/x/Film.mkv");
});

test("the film command makes the folder and the subtitle files, runs ffmpeg and takes the subtitle files away", () => {
  const command = filmCommand({ path: "/tmp/x/Film.mkv" });
  assert.match(command, /^mkdir -p '\/tmp\/x' && /);
  assert.match(command, /printf .* > '\/tmp\/x\/Film\.eng\.srt'/);
  assert.match(command, / ffmpeg '-nostdin' /);
  assert.match(command, /rm -f '\/tmp\/x\/Film\.eng\.srt' '\/tmp\/x\/Film\.spa\.srt'$/);
  assert.throws(() => filmCommand({ path: "/tmp/x/Film.mkv", title: "it's" }), /quote/);
});

const probe = (streams, format) => ({ streams, format });
const stream = (codec_type, language) => ({ codec_type, tags: language ? { language } : {} });

test("the film's video bitrate is 3M unless asked for more, so a long film can be as large as a workflow's minimum needs", () => {
  const rate = (options) => { const args = filmArguments({ path: "/tmp/a.mkv", ...options }); return args[args.indexOf("-b:v") + 1]; };
  assert.equal(rate({}), "3M");
  assert.equal(rate({ videoBitrate: "5M" }), "5M");
  assert.match(filmCommand({ path: "/tmp/a.mkv", videoBitrate: "5M" }), /'-b:v' '5M'/);
});

test("a probe is summed up as video count, audio and subtitle languages, length and size", () => {
  const summary = summarizeProbe(
    probe([stream("video"), stream("audio", "eng"), stream("audio", "fre"), stream("subtitle", "spa"), stream("audio")], { duration: "30.04", size: "11000000" }),
  );
  assert.deepEqual(summary, { video: 1, audio: ["eng", "fre", ""], subtitles: ["spa"], seconds: 30.04, bytes: 11_000_000 });
});

const master = { video: 1, audio: ["eng", "fre", "jpn"], subtitles: ["eng", "spa"], seconds: 30, bytes: 11_000_000 };

test("a cleaned film keeps the video and the English audio, loses the rest, is as long and is smaller", () => {
  assert.deepEqual(cleanedFilmProblems(master, { video: 1, audio: ["eng"], subtitles: [], seconds: 30.1, bytes: 6_000_000 }), []);
});

test("every way a cleaned film can be wrong is named", () => {
  const problems = cleanedFilmProblems(master, { video: 0, audio: ["eng", "jpn"], subtitles: ["eng"], seconds: 20, bytes: 11_000_000 }).join("\n");
  assert.match(problems, /video/);
  assert.match(problems, /audio/);
  assert.match(problems, /subtitle/);
  assert.match(problems, /length/);
  assert.match(problems, /smaller/);
});

// --- the logs -----------------------------------------------------------------------------------------------------------

const row = (id, title) => ({ source: "server", id, at: "2026-10-16T10:00:00Z", title });

test("only a warning or error that was not there when the scenario began is new", () => {
  assert.deepEqual(newProblemRows([row(1, "old")], [row(2, "new"), row(1, "old")]), [row(2, "new")]);
  assert.deepEqual(newProblemRows([row(1, "old")], [row(1, "old")]), []);
});

test("a log watch fails with what the logs gained, and when there are more than one page of new ones", async () => {
  const world = new FakeWorld();
  const watch = await LogWatch.begin(world.weir);
  await watch.finish();
  world.logRows.push(row(7, "Something went wrong"));
  await assert.rejects(() => watch.finish(), /Something went wrong/);
  world.logRows.length = 0;
  const crowded = await LogWatch.begin(world.weir);
  world.nextCursor = "more";
  world.logRows.push(row(8, "a"), row(9, "b"));
  await assert.rejects(() => crowded.finish(), /more than one page/);
});

test("a row the scenario said it would cause is let through as many times as it said, and nothing else is", async () => {
  const world = new FakeWorld();
  const watch = await LogWatch.begin(world.weir);
  watch.expect("Sign-in failed", { times: 2, why: "the wrong password is tried twice" });
  world.logRows.push(row(1, "Sign-in failed"), row(2, "Sign-in failed"));
  await watch.finish();
  world.logRows.push(row(3, "Sign-in failed"));
  await assert.rejects(() => watch.finish(), /1 unexpected warning or error \(Sign-in failed\)/);
  world.logRows.length = 0;
  world.logRows.push(row(4, "Sign-in failed"), row(5, "Something else"));
  await assert.rejects(() => watch.finish(), /1 unexpected warning or error \(Something else\)/);
  assert.throws(() => watch.expect("Sign-in failed", {}), /needs a reason/);
});

// --- the scenarios, against a stand-in for Weir and the container ---------------------------------------------------------

async function run(id, world = new FakeWorld()) {
  const results = await runScenarios({ ids: [id], registry: SCENARIOS, world: world.asWorld() });
  return { results, world };
}

test("a film dropped into a Weir-only workflow is cleaned, and the original and its .nfo are left", async () => {
  const { results, world } = await run("weir-only-film");
  assert.equal(results[0].status, "passed", results[0].detail);
  assert.match(results[0].detail, /cleaned/);
  assert.equal(world.workflows.length, 1);
  assert.match(world.workflows[0].name, /^Scenario films [0-9a-f]+$/);
  assert.equal(world.workflows[0].remove_original_after_success, false);
});

test("a film that Weir leaves with every track fails the scenario, and says which", async () => {
  const world = new FakeWorld({ cleaning: (summary) => summary });
  const { results } = await run("weir-only-film", world);
  assert.equal(results[0].status, "failed");
  assert.match(results[0].detail, /audio/);
});

test("a film Weir never cleans fails with what it waited for", async () => {
  const { results } = await run("weir-only-film", new FakeWorld({ neverCleans: true }));
  assert.equal(results[0].status, "failed");
  assert.match(results[0].detail, /Weir to clean Big\.Buck\.Bunny\.2008\/Big\.Buck\.Bunny\.2008\.mkv did not happen within 180 s/);
});

test("a film whose original is touched fails the scenario", async () => {
  const { results } = await run("weir-only-film", new FakeWorld({ removesOriginal: true }));
  assert.equal(results[0].status, "failed");
  assert.match(results[0].detail, /original film is still where it was/);
});

test("Process again skips a file that is already cleaned: nothing queued, no new job, the output as it was", async () => {
  const { results } = await run("process-again");
  assert.equal(results[0].status, "passed", results[0].detail);
  assert.match(results[0].detail, /Skipped: already done \(Retry\.Film\.2017\.mkv\)/);
});

test("Process again that queues the file again fails the scenario", async () => {
  const { results } = await run("process-again", new FakeWorld({ requeue: { requeued: 1, skipped: 0, detail: "Queued." } }));
  assert.equal(results[0].status, "failed");
  assert.match(results[0].detail, /nothing was queued/);
});

test("Process again that rewrites the output fails the scenario", async () => {
  const { results } = await run("process-again", new FakeWorld({ rewritesOnRequeue: true }));
  assert.equal(results[0].status, "failed");
  assert.match(results[0].detail, /not written a second time/);
});

test("a scenario that leaves a new warning in the logs fails although it did what it should", async () => {
  const world = new FakeWorld({ warnsWhileCleaning: "The mover could not set the owner" });
  const { results } = await run("weir-only-film", world);
  assert.equal(results[0].status, "failed");
  assert.match(results[0].detail, /could not set the owner/);
});

test("every scenario chosen runs, whatever the one before it found, and the exit code is 1 for any failure", async () => {
  const world = new FakeWorld({ neverCleans: true });
  const registry = { ...SCENARIOS, "fine-one": async () => "It was fine." };
  const results = await runScenarios({ ids: ["weir-only-film", "fine-one"], registry, world: world.asWorld() });
  assert.deepEqual(results.map((result) => result.status), ["failed", "passed"]);
  assert.equal(exitCode(results), 1);
  assert.equal(exitCode(results.slice(1)), 0);
  assert.equal(exitCode([]), 1, "a run that ran nothing is not a pass");
});

test("a scenario that is not ported cannot be chosen", async () => {
  await assert.rejects(() => runScenarios({ ids: ["fresh-install"], registry: SCENARIOS, world: new FakeWorld().asWorld() }), /fresh-install.*not ported/);
});

test("the summary is a table of each scenario's verdict and time, with what failed spelled out", () => {
  const text = summaryMarkdown([
    { id: "weir-only-film", status: "passed", seconds: 12, detail: "A film was cleaned." },
    { id: "process-again", status: "failed", seconds: 3, detail: "it was answered 500" },
  ], { shard: "weir-only", value: "weir-ci-smoke" });
  assert.match(text, /^### Scenarios: weir-only/);
  assert.match(text, /\| `weir-only-film` \| passed \| 12 s \| A film was cleaned\. \|/);
  assert.match(text, /\| `process-again` \| FAILED \| 3 s \| it was answered 500 \|/);
});

// --- the container ------------------------------------------------------------------------------------------------------

function recordingStack(answers = {}) {
  const calls = [];
  return {
    calls,
    exec: (command) => (calls.push(["exec", command]), answers.exec ?? ""),
    execResult: (command) => (calls.push(["result", command]), answers.result ?? { status: 0, stdout: "", stderr: "", output: "" }),
  };
}

test("the box asks the container's own tools: ffprobe for a file's tracks, sha256sum for its bytes, stat for when it was written", async () => {
  const json = JSON.stringify(probe([stream("video")], { duration: "1", size: "2" }));
  const stack = recordingStack({ result: { status: 0, stdout: json, stderr: "", output: json } });
  const box = new ContainerBox(stack);
  assert.deepEqual(await box.probe("/tmp/a.mkv"), { video: 1, audio: [], subtitles: [], seconds: 1, bytes: 2 });
  assert.deepEqual(stack.calls[0], ["result", "ffprobe -v error -print_format json -show_streams -show_format /tmp/a.mkv"]);
  await box.sha256("/tmp/a.mkv");
  await box.modified("/tmp/a.mkv");
  assert.deepEqual(stack.calls.slice(1).map((call) => call[1]), ["sha256sum /tmp/a.mkv", "stat -c %y /tmp/a.mkv"]);
});

test("a path with a space or a quote is refused rather than run through the shell unquoted", async () => {
  const box = new ContainerBox(recordingStack());
  await assert.rejects(() => box.probe("/tmp/a b.mkv"), /plain path/);
  await assert.rejects(() => box.sha256("/tmp/a;rm.mkv"), /plain path/);
});

test("the box reports a file that is not there as not there, and a probe that fails as a failure", async () => {
  const gone = new ContainerBox(recordingStack({ result: { status: 1, stdout: "", stderr: "", output: "" } }));
  assert.equal(await gone.exists("/tmp/a.mkv"), false);
  const broken = new ContainerBox(recordingStack({ result: { status: 1, stdout: "", stderr: "Invalid data", output: "Invalid data" } }));
  await assert.rejects(() => broken.probe("/tmp/a.mkv"), /Invalid data/);
});

test("a release is a folder holding the film and an .nfo, made as the server's account", async () => {
  const stack = recordingStack();
  await new ContainerBox(stack).dropRelease({ film: "/tmp/m/Film.mkv", folder: "/tmp/w/Name", name: "Name" });
  assert.equal(stack.calls[0][1], "mkdir -p '/tmp/w/Name' && cp '/tmp/m/Film.mkv' '/tmp/w/Name/Name.mkv' && printf 'Scenario release Name\\n' > '/tmp/w/Name/Name.nfo'");
});

// --- the workflow -------------------------------------------------------------------------------------------------------

test("the workflow builds the image under test, runs each shard of the matrix against it and keeps the logs", () => {
  const workflow = read(".github/workflows/ci-packaging.yml");
  const job = /\n  scenarios-linux:\n([\s\S]*?)(?=\n  [a-z0-9-]+:\n|$)/.exec(workflow)?.[1] ?? "";
  assert.match(job, /runs-on: ubuntu-latest/);
  assert.match(job, /timeout-minutes: \d+/);
  assert.match(job, /fail-fast: false/);
  assert.match(job, /tags: weir-ci-smoke/);
  assert.match(job, /node scripts\/scenarios-in-ci\.mjs "\$SHARD" --candidate weir-ci-smoke/);
  assert.match(job, /actions\/upload-artifact@/);
});

test("a change to the scenarios in CI runs them", () => {
  const ci = read(".github/workflows/ci.yml");
  assert.match(ci, /- 'scripts\/scenarios-in-ci\.mjs'/);
  assert.match(ci, /- 'scripts\/scenarios-ci\/\*\*'/);
});
