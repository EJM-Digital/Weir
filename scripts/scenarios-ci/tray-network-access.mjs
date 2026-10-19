// network-access-from-weir: who can reach Weir, chosen where a person chooses it, in System > About, instead of from the tray's menu. The page's call
// is a PUT of the scope with the confirmation token (WeirClient sends what the web app sends); the tray takes the choice from the file Weir
// saves, asks Windows for the firewall rule for real (the runner is an administrator), restarts the server for the network, and Weir's status
// follows. Written for CI: the VM suite only chose from the menu, and the web page's progress needs the tray and Windows Firewall.
import assert from "node:assert/strict";

import { installedAndStopped, PORT, restoreWeir, startTray } from "./tray-world.mjs";
import { ensureSignedIn, wait } from "./windows-install.mjs";

const CHANGE_CEILING_MS = 180_000;

/** The status Weir gives System > About, or undefined while the server is restarting and does not answer. */
async function statusOf(weir) {
  try {
    return await weir.get("/suite/network-access");
  } catch {
    return undefined;
  }
}

const loopback = (addresses) => addresses.every((address) => ["127.0.0.1", "::1"].includes(address));
// Every interface is "0.0.0.0", or "::" when it listens on both families.
const beyondThisPc = (addresses) => addresses.some((address) => ["0.0.0.0", "::"].includes(address));

/** Waits for the status to say what the page waits for, and returns it with the sentences the page showed on the way. */
async function followUntil(world, reached, what) {
  const seen = [];
  const status = await wait(
    world,
    async () => {
      const now = await statusOf(world.weir);
      if (now && !seen.includes(now.summary)) seen.push(now.summary);
      return now && reached(now) ? now : undefined;
    },
    what,
    CHANGE_CEILING_MS,
    500,
  );
  return { status, seen };
}

/** network-access-from-weir: choosing the network in System > About makes the firewall rule and opens the server to it; choosing this PC closes it again. */
export async function networkAccessFromWeir(world) {
  const { machine, weir } = world;
  await installedAndStopped(world);
  if (await machine.firewallRuleExists()) await machine.runTray(["--remove-firewall"]);
  const tray = await startTray(world, ["--port", String(PORT), "--no-browser"]);
  const facts = [];
  try {
    const pid = await tray.ready();
    await ensureSignedIn(world);
    assert.ok(loopback(await machine.listeningAddresses(PORT)), "Weir listens beyond this PC before anything was chosen");
    const before = await weir.get("/suite/network-access");
    assert.equal(before.state, "this_pc_only", `System > About says ${before.state} before anything was chosen, not this_pc_only`);
    assert.equal(before.pending_scope, null, "a choice is waiting before anything was chosen");

    const chosen = await weir.write("PUT", "/suite/network-access", { scope: "network" });
    assert.equal(chosen.status, 200, `choosing the network was answered ${chosen.status}: ${chosen.text}`);
    assert.equal(chosen.json.pending_scope ?? chosen.json.scope, "network", "the answer to the choice does not name the network");

    const { status: reachable, seen } = await followUntil(world, (status) => status.state === "allowed" && status.scope === "network" && status.pending_scope === null, "System > About to say the network can reach Weir");
    assert.ok(reachable.addresses.length > 0, "the status gives no address for other devices to use");
    assert.equal(await machine.firewallRuleExists(), true, "Windows Firewall has no rule for Weir although System > About says the network can reach it");
    const next = await tray.readyAfter(pid);
    assert.notEqual(next, pid, "the server was not restarted for the network");
    const open = await machine.listeningAddresses(PORT);
    assert.ok(beyondThisPc(open), `Weir listens on ${open.join(", ")}, not on every interface`);
    facts.push(
      `PUT /suite/network-access with scope network and the confirmation token was answered 200; the status then read "${seen.join('" then "')}"; the Windows Firewall rule exists, the server (pid ${pid}) was replaced by one (pid ${next}) that listens on ${open.join(", ")}, and the status gives ${reachable.addresses.length} address(es) for other devices`,
    );

    const closed = await weir.write("PUT", "/suite/network-access", { scope: "this_pc_only" });
    assert.equal(closed.status, 200, `limiting Weir to this PC was answered ${closed.status}: ${closed.text}`);
    const { status: limited } = await followUntil(world, (status) => status.state === "this_pc_only" && status.pending_scope === null, "System > About to say only this PC can reach Weir");
    assert.equal(limited.scope, "this_pc_only");
    await tray.readyAfter(next);
    assert.ok(loopback(await machine.listeningAddresses(PORT)), "Weir still listens beyond this PC after it was limited to this PC");
    facts.push("choosing this PC only restarted the server again and it listens on this PC alone");
  } finally {
    await tray.end();
    await machine.removeFromDataFolder("lan-access");
    if (await machine.firewallRuleExists()) await machine.runTray(["--remove-firewall"]);
    await restoreWeir(world);
  }
  return `${facts.join("; ")}.`;
}
