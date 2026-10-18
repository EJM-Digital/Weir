// What the windows-files shard decides, tested without a Windows PC: the plan of what a swap leaves behind, the diskpart scripts, the reading of
// Windows Firewall's rule, and the relay that makes a share hang. The real runner, the real volumes and the real server are the windows-files
// job of ci-packaging.yml.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createConnection, createServer } from "node:net";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import { filmArguments } from "./scenarios-ci/film.mjs";
import { FreezableRelay } from "./scenarios-ci/files-proxy.mjs";
import { FILM_PATH, KEPT_BESIDE_PATH, POINTS, commits, filesAfterRecovery, filesWhenStopped } from "./scenarios-ci/files-swap-plan.mjs";
import { createScript, detachScript, freeLetter } from "./scenarios-ci/files-volumes.mjs";
import { lanAddresses, ruleProblems } from "./scenarios-ci/files-network.mjs";
import { loadHomes, scenariosOfShard } from "./scenarios-ci/homes.mjs";
import { SCENARIOS } from "./scenarios-ci/windows-files.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(join(repository, path), "utf8");
const homes = loadHomes();

test("every ported windows-files scenario has code, in the order the shard runs them", () => {
  const ported = scenariosOfShard(homes, "windows-files").filter((scenario) => scenario.state !== "to-port").map((scenario) => scenario.id);
  assert.deepEqual(Object.keys(SCENARIOS), ported);
});

test("the swap's points are the ones the server and the contract suite name, in the same order", () => {
  const enumText = read("apps/server/src/Weir.Infrastructure/LibraryMode/SwapProgress.cs").split("public enum SwapPoint")[1].split("}")[0];
  const server = [...enumText.matchAll(/^\s+([A-Z][A-Za-z]+),?\s*$/gm)].map((match) => match[1]);
  assert.deepEqual(POINTS, server);
  const contract = [...read("apps/server/tests/Weir.Contract.Tests/Jobs/SwapCrashPlan.cs").matchAll(/^\s+"([A-Z][A-Za-z]+)",$/gm)].map((match) => match[1]);
  assert.deepEqual(POINTS, contract.slice(0, POINTS.length));
});

test("a swap ended before the commit leaves the original, and from the commit on the cleaned film", () => {
  assert.deepEqual(POINTS.filter(commits), ["CleanedCopyInPlace", "CommittedRecorded", "BackupDealtWith", "FinishedRecorded"]);
});

test("the files a swap leaves at each point are the contract suite's plan", () => {
  const none = { keepOriginal: false };
  assert.deepEqual(filesWhenStopped("Started", none), { library: [FILM_PATH], elsewhere: [] });
  assert.deepEqual(filesWhenStopped("CopyChecked", none).library, ["Film (2020)/Film (2020).mkv", "Film (2020)/Film (2020).weir-tmp.mkv"]);
  assert.deepEqual(filesWhenStopped("OriginalMovedAside", none).library, ["Film (2020)/Film (2020).weir-bak.mkv", "Film (2020)/Film (2020).weir-tmp.mkv"]);
  assert.deepEqual(filesWhenStopped("CommittedRecorded", none).library, ["Film (2020)/Film (2020).mkv", "Film (2020)/Film (2020).weir-bak.mkv"]);
  assert.deepEqual(filesWhenStopped("FinishedRecorded", none).library, [FILM_PATH]);
});

test("the place for a kept original is reserved from the commit's record, beside the film or on the other volume", () => {
  const beside = { keepOriginal: true };
  assert.equal(filesWhenStopped("CopyChecked", beside).library.includes(KEPT_BESIDE_PATH), false);
  assert.ok(filesWhenStopped("CommitRecorded", beside).library.includes(KEPT_BESIDE_PATH));
  const away = { keepOriginal: true, keptElsewhere: true };
  assert.deepEqual(filesWhenStopped("CommitRecorded", away).elsewhere, [FILM_PATH]);
  assert.equal(filesWhenStopped("CommitRecorded", away).library.includes(KEPT_BESIDE_PATH), false);
});

test("once Weir has started again the film is there, and its kept original only if the swap had committed", () => {
  assert.deepEqual(filesAfterRecovery("CopyChecked", { keepOriginal: true }), { library: [FILM_PATH], elsewhere: [] });
  assert.deepEqual(filesAfterRecovery("CleanedCopyInPlace", { keepOriginal: true }), { library: [FILM_PATH, KEPT_BESIDE_PATH].sort(), elsewhere: [] });
  assert.deepEqual(filesAfterRecovery("CleanedCopyInPlace", { keepOriginal: true, keptElsewhere: true }), { library: [FILM_PATH], elsewhere: [FILM_PATH] });
  assert.deepEqual(filesAfterRecovery("FinishedRecorded", { keepOriginal: false }), { library: [FILM_PATH], elsewhere: [] });
  assert.throws(() => filesWhenStopped("Nowhere", { keepOriginal: false }), /not a point of a swap/);
});

test("the diskpart script makes a fixed disk, formats it NTFS and gives it its letter; a bad letter, size or path is refused", () => {
  const script = createScript({ file: "C:\\run\\volume-Z.vhdx", sizeMb: 128, letter: "Z", label: "WeirCI" });
  assert.deepEqual(script.trim().split("\r\n"), [
    'create vdisk file="C:\\run\\volume-Z.vhdx" maximum=128 type=fixed',
    'select vdisk file="C:\\run\\volume-Z.vhdx"',
    "attach vdisk",
    "create partition primary",
    'format fs=ntfs quick label="WeirCI"',
    "assign letter=Z",
  ]);
  assert.deepEqual(detachScript({ file: "C:\\run\\volume-Z.vhdx" }).trim().split("\r\n"), ['select vdisk file="C:\\run\\volume-Z.vhdx"', "detach vdisk"]);
  assert.throws(() => createScript({ file: "C:\\a.vhdx", sizeMb: 128, letter: "zz", label: "x" }), /not a drive letter/);
  assert.throws(() => createScript({ file: "C:\\a.vhdx", sizeMb: 4, letter: "Z", label: "x" }), /too small/);
  assert.throws(() => createScript({ file: 'C:\\a".vhdx', sizeMb: 64, letter: "Z", label: "x" }), /quote/);
});

test("a drive letter is the first from the end of the alphabet that no drive has", () => {
  assert.equal(freeLetter(["C", "D"]), "Z");
  assert.equal(freeLetter(["C", "z", "Y"]), "X");
  assert.throws(() => freeLetter([..."ZYXWVUTSRQPONM"]), /taken/);
});

test("the film's bitrate can be chosen, and is 3M unless it is", () => {
  const rate = (args) => args[args.indexOf("-b:v") + 1];
  assert.equal(rate(filmArguments({ path: "x.mkv" })), "3M");
  assert.equal(rate(filmArguments({ path: "x.mkv", videoBitrate: "6M" })), "6M");
});

const rule = { enabled: "True", direction: "Inbound", action: "Allow", profile: "Any", program: "C:\\Weir\\current\\server\\WeirServer.exe", protocol: "TCP" };
const exe = "c:\\weir\\current\\server\\weirserver.exe";

test("Weir's firewall rule is right when it is one enabled inbound TCP allow rule for the installed server on every profile", () => {
  assert.deepEqual(ruleProblems([rule], exe), []);
  assert.deepEqual(ruleProblems([{ ...rule, profile: "Domain, Private, Public" }], exe), []);
});

test("every way the rule can be wrong is named", () => {
  assert.match(ruleProblems([], exe)[0], /0 rules named Weir/);
  assert.match(ruleProblems([rule, rule], exe)[0], /2 rules/);
  assert.match(ruleProblems([{ ...rule, enabled: "False" }], exe).join(), /not enabled/);
  assert.match(ruleProblems([{ ...rule, direction: "Outbound" }], exe).join(), /Outbound, not Inbound/);
  assert.match(ruleProblems([{ ...rule, action: "Block" }], exe).join(), /Block, not Allow/);
  assert.match(ruleProblems([{ ...rule, profile: "Domain, Private" }], exe).join(), /does not cover the Public network profile/);
  assert.match(ruleProblems([{ ...rule, profile: "Private" }], exe).join(), /Domain and Public network profiles/);
  assert.match(ruleProblems([{ ...rule, program: "C:\\Other\\app.exe" }], exe).join(), /program other than the installed server/);
  assert.match(ruleProblems([{ ...rule, protocol: "UDP" }], exe).join(), /UDP, not TCP/);
});

test("this PC's network addresses leave out loopback, self-assigned and IPv6 ones", () => {
  const interfaces = {
    Loopback: [{ family: "IPv4", address: "127.0.0.1", internal: true }],
    Ethernet: [
      { family: "IPv4", address: "192.0.2.10", internal: false },
      { family: "IPv6", address: "fe80::1", internal: false },
    ],
    Lonely: [{ family: "IPv4", address: "169.254.3.4", internal: false }],
  };
  assert.deepEqual(lanAddresses(interfaces), ["192.0.2.10"]);
});

/** A server that answers what it is sent, upper-cased. */
async function echoServer() {
  const server = createServer((socket) => socket.on("data", (chunk) => socket.write(String(chunk).toUpperCase())));
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return server;
}

const connect = async (port) => {
  const socket = createConnection({ host: "127.0.0.1", port });
  await new Promise((resolve) => socket.once("connect", resolve));
  return socket;
};

const nextData = (socket) => new Promise((resolve) => socket.once("data", (chunk) => resolve(String(chunk))));

test("a relay passes data in both directions, holds a frozen connection open without passing anything, and passes what waited once thawed", async () => {
  const target = await echoServer();
  const relay = await FreezableRelay.start({ target: { host: "127.0.0.1", port: target.address().port } });
  const socket = await connect(relay.port);
  try {
    const first = nextData(socket);
    socket.write("one");
    assert.equal(await first, "ONE");

    relay.freeze();
    assert.equal(relay.frozen, true);
    let heard = null;
    socket.once("data", (chunk) => (heard = String(chunk)));
    socket.write("two");
    // A connection opened while it is frozen is accepted and passes nothing either.
    const late = await connect(relay.port);
    late.write("late");
    assert.equal(socket.destroyed, false);
    assert.equal(heard, null);

    const answered = new Promise((resolve) => socket.once("data", (chunk) => resolve(String(chunk))));
    relay.thaw();
    assert.equal(await answered, "TWO");
    late.destroy();
  } finally {
    socket.destroy();
    await relay.close();
    await new Promise((resolve) => target.close(resolve));
  }
});
