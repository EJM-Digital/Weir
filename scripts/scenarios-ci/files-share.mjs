// network-share and network-share-hang: a real SMB share on the runner, used by its UNC path as a NAS is. Weir finds the folders and processes a
// film that arrives on it; the share is then taken away (the folders are reported unreachable, nothing is lost, Weir stays responsive) and
// given back (Weir carries on). A share that hangs, as a NAS that has stopped answering does, is made by relaying the SMB client's connection
// through a relay that can stop passing data; a removed share or a stopped service is refused at once and proves a different thing.
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";

import { cleanedFilmProblems } from "./film.mjs";
import { FreezableRelay } from "./files-proxy.mjs";
import { exists, probe, sha256, treeOf } from "./files-media.mjs";
import { masterFilm } from "./files-swap.mjs";
import { freeLetter } from "./files-volumes.mjs";
import { dropRelease, lookAndWait, makeWorkflow, placeRelease, rowFor, tryToLook, waitDone } from "./files-workflow.mjs";
import { expectCleanLogs, filesWorld, newServer, wait } from "./files-world.mjs";
import { json } from "./windows-machine.mjs";

const RESPONSIVE_MS = 8000;
const SMB_PORT = 445;

// --- the share ----------------------------------------------------------------------------------------------------------------------

async function createShare(name, path) {
  await json(`New-SmbShare -Name $env:SCENARIO_NAME -Path $env:SCENARIO_PATH -FullAccess 'Everyone' | Out-Null`, { NAME: name, PATH: path });
}

/** Takes the share away, with the sessions and open files that hold it, the way a NAS that goes off the network does. */
async function removeShare(name) {
  await json(
    `Get-SmbOpenFile | Close-SmbOpenFile -Force -ErrorAction SilentlyContinue
    Get-SmbSession | Close-SmbSession -Force -ErrorAction SilentlyContinue
    if (Get-SmbShare -Name $env:SCENARIO_NAME -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $env:SCENARIO_NAME -Force }`,
    { NAME: name },
  );
}

const hasShare = async (name) => (await json(`ConvertTo-Json -Compress -InputObject ([bool](Get-SmbShare -Name $env:SCENARIO_NAME -ErrorAction SilentlyContinue))`, { NAME: name })) === true;

/** The share's UNC path, by this PC's own name for itself. */
const uncOf = (name, server = "localhost") => `\\\\${server}\\${name}`;

async function makeShare(world, name, backing) {
  await world.machine.ensureFolders(backing, join(backing, "watched"), join(backing, "output"));
  await createShare(name, backing);
  world.cleanups.push(() => removeShare(name));
  assert.ok(await hasShare(name), `the share ${name} was not created`);
  const unc = uncOf(name);
  assert.ok(await exists(join(unc, "watched")), `the share is not reachable by ${unc}`);
  return unc;
}

/** The folders Weir reports it cannot reach, from the file the server keeps for the tray's dot. */
async function unreachableFolders(server) {
  try {
    return JSON.parse(await readFile(join(server.home, "tray-status.json"), "utf8")).unreachable.folders;
  } catch {
    return undefined;
  }
}

const reports = (folders, workflow) => (folders ?? []).some((folder) => folder.includes(workflow.name));

async function waitReported(world, server, workflow, reported) {
  await wait(
    world,
    async () => {
      const folders = await unreachableFolders(server);
      return folders !== undefined && reports(folders, workflow) === reported ? true : undefined;
    },
    `Weir to ${reported ? "report" : "stop reporting"} the folders of ${workflow.name} as unreachable`,
    180_000,
    500,
  );
}

/** How long the System page's own data takes to come back, in milliseconds. */
async function responseTime(weir) {
  const began = performance.now();
  await weir.overview();
  return Math.round(performance.now() - began);
}

async function expectCleanedAt(world, path) {
  const { tools } = await filesWorld(world);
  const master = await masterFilm(world);
  const problems = cleanedFilmProblems(master.summary, await probe(tools, path));
  assert.deepEqual(problems, [], `the film at ${path} is wrong: ${problems.join("; ")}`);
}

// --- network-share ------------------------------------------------------------------------------------------------------------------

/** network-share: folders on a real SMB share are found and used; a share that goes away loses nothing and is reported; a share that comes back is used again. */
export async function networkShare(world) {
  const server = await newServer(world, "share");
  await server.start();
  await server.waitReady();
  const weir = await server.signIn();
  const master = await masterFilm(world);
  const name = `weir-ci-${world.runId}`;
  const backing = join(world.scratch, "share", "backing");
  const unc = await makeShare(world, name, backing);
  const workflow = await makeWorkflow(world, weir, {
    name: "Scenario share",
    watched: join(unc, "watched"),
    work: join(world.scratch, "share", "work"),
    output: join(unc, "output"),
    skipAccessTests: false,
  });
  const facts = [];

  // The share's folders are used by their UNC path.
  const chain = await weir.get(`/processing/libraries/${workflow.id}/folder-chain`);
  await world.record("network-share-folder-chain.json", JSON.stringify(chain, null, 2));
  const first = await dropRelease(world, weir, workflow, "Share.Film.2018", master.path);
  await waitDone(world, weir, workflow, first);
  await expectCleanedAt(world, first.output);
  assert.equal(await sha256(first.film), master.sha256, "the original on the share changed");
  await waitReported(world, server, workflow, false);
  facts.push(`a film dropped on the share by its UNC path was found, cleaned and written back to the share, and the original there was untouched`);

  // The share goes away. A film that arrives meanwhile (in the folder behind the share) is not touched, and the folders are reported.
  world.logWatch.expectMatching(/^Weir can no longer see .*weir-ci-.*watched/, { times: 2, why: "the share is taken away, so Weir says in the logs that it can no longer see the watched folder" });
  world.logWatch.expectMatching(/^The filesystem watcher for Scenario share .* reported an error/, { times: 2, why: "the share is taken away, so the watcher on its folder fails and is restarted" });
  world.logWatch.expectMatching(/^Did not scan Scenario share .*: Weir can't see its watched folder/, { times: 1, why: "Weir is asked to look at the folder while the share is gone, refuses, and says so in the logs" });
  await removeShare(name);
  assert.equal(await exists(join(unc, "watched")), false, "the share is still reachable after it was removed");
  const waiting = await placeRelease({ ...workflow, watched: join(backing, "watched"), output: join(backing, "output") }, "Share.Film.2019", master.path);
  const afterDrop = await responseTime(weir);
  assert.ok(afterDrop < RESPONSIVE_MS, `the System page's data took ${afterDrop} ms while the share was gone`);
  await waitReported(world, server, workflow, true);
  const outputBefore = await treeOf(join(backing, "output"));
  const refused = await tryToLook(weir, workflow);
  assert.equal(refused.status, 400, `asking Weir to look at a folder on a share that is gone was answered ${refused.status}, not refused`);
  assert.match(refused.json?.detail ?? "", /can't see the watched folder/, "the refusal does not say in words that Weir cannot see the watched folder");
  assert.deepEqual(await treeOf(join(backing, "output")), outputBefore, "something was written to the folder behind the removed share");
  assert.equal(await sha256(waiting.film), master.sha256, "the film behind the removed share changed");
  const stillWaiting = await rowFor(weir, workflow, waiting.relative);
  assert.ok(!stillWaiting || !["processed", "passed_through"].includes(stillWaiting.status), "a film on a share that was gone is recorded as done");
  facts.push(`with the share removed Weir reported the folders unreachable, refused to look at the watched folder saying it cannot see it, answered the System page in ${await responseTime(weir)} ms, wrote nothing and left the film alone`);

  // The share comes back and Weir carries on.
  await createShare(name, backing);
  await waitReported(world, server, workflow, false);
  await lookAndWait(world, weir, workflow);
  await waitDone(world, weir, workflow, waiting);
  await expectCleanedAt(world, waiting.output);
  facts.push("when the share came back the folders were no longer reported, and the film that had waited was cleaned and written to it");

  await expectCleanLogs(world, server);
  await server.kill();
  return `${facts.join("; ")}.`;
}

// --- network-share-hang -----------------------------------------------------------------------------------------------------------

/**
 * Windows allows one port per server name, and every use of a UNC path keeps a connection to localhost on the usual port for a while, so the
 * connections are let go of, and the client is asked until it has none, before a mapping on another port is made.
 */
async function dropConnectionsToThisPc(world) {
  await json(`net.exe use * /delete /y | Out-Null`).catch(() => undefined);
  await wait(
    world,
    async () => ((await json(`ConvertTo-Json -Compress -InputObject @(Get-SmbConnection -ErrorAction SilentlyContinue | Where-Object { $_.ServerName -eq 'localhost' }).Count`)) === 0 ? true : undefined),
    "the SMB client to have no connection to localhost",
    60_000,
    1000,
  );
}

/** Maps a drive letter to the share through the relay's port (SMB over an alternative port, Windows Server 2025 and Windows 11 24H2 on). */
async function mapThroughRelay(letter, unc, port) {
  await json(`New-SmbMapping -LocalPath ($env:SCENARIO_LETTER + ':') -RemotePath $env:SCENARIO_UNC -TcpPort ([int]$env:SCENARIO_PORT) | Out-Null`, { LETTER: letter, UNC: unc, PORT: port });
}

async function unmap(letter) {
  await json(`Remove-SmbMapping -LocalPath ($env:SCENARIO_LETTER + ':') -Force -UpdateProfile -ErrorAction SilentlyContinue`, { LETTER: letter });
}

/**
 * network-share-hang: a share that stops answering without refusing is reported unreachable within the probe's time, another workflow keeps
 * working and the System page stays responsive meanwhile; when the share answers again the folders are used again.
 */
export async function networkShareHang(world) {
  const server = await newServer(world, "hang");
  await server.start();
  await server.waitReady();
  const weir = await server.signIn();
  const master = await masterFilm(world);
  const name = `weir-ci-hang-${world.runId}`;
  const backing = join(world.scratch, "hang", "backing");
  await makeShare(world, name, backing);
  const relay = await FreezableRelay.start({ target: { host: "localhost", port: SMB_PORT } });
  world.cleanups.push(() => relay.close());
  // Windows keeps what it has been told about a folder for a while; a hung share must be asked, not remembered.
  await json(`Set-SmbClientConfiguration -DirectoryCacheLifetime 0 -FileInfoCacheLifetime 0 -FileNotFoundCacheLifetime 0 -Force`);
  const inUse = (await json(`ConvertTo-Json -Compress -InputObject @([IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1) })`)) ?? [];
  const letter = freeLetter(inUse);
  await dropConnectionsToThisPc(world);
  await mapThroughRelay(letter, uncOf(name), relay.port);
  world.cleanups.push(() => unmap(letter));
  const mapped = `${letter}:\\`;
  assert.ok(await exists(join(mapped, "watched")), `the share mapped through the relay is not reachable at ${mapped}`);

  const remote = await makeWorkflow(world, weir, {
    name: "Scenario hung share",
    watched: join(mapped, "watched"),
    work: join(world.scratch, "hang", "work-remote"),
    output: join(world.scratch, "hang", "output-remote"),
    skipAccessTests: false,
  });
  const local = await makeWorkflow(world, weir, {
    name: "Scenario local",
    watched: join(world.scratch, "hang", "watched"),
    work: join(world.scratch, "hang", "work"),
    output: join(world.scratch, "hang", "output"),
  });
  const facts = [];

  const before = await dropRelease(world, weir, remote, "Hang.Film.2017", master.path);
  await waitDone(world, weir, remote, before);
  facts.push("a film on the share, reached through the relay, was cleaned");

  relay.freeze();
  try {
    await waitReported(world, server, remote, true);
    facts.push("when the share stopped answering without refusing, Weir reported its folders unreachable");
    const other = await dropRelease(world, weir, local, "Local.Film.2016", master.path);
    await waitDone(world, weir, local, other);
    const hung = await responseTime(weir);
    assert.ok(hung < RESPONSIVE_MS, `the System page's data took ${hung} ms while a share hung`);
    facts.push(`another workflow cleaned a film meanwhile and the System page answered in ${hung} ms`);
  } finally {
    relay.thaw();
  }

  await waitReported(world, server, remote, false);
  const after = await dropRelease(world, weir, remote, "Hang.Film.2020", master.path);
  await waitDone(world, weir, remote, after);
  facts.push("when the share answered again its folders were no longer reported, and the next film on it was cleaned");

  await expectCleanLogs(world, server);
  await server.kill();
  return `${facts.join("; ")}.`;
}
