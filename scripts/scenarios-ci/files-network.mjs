// network-access: the setting "Other devices on your network" flipped on a real Windows machine through the API a person's System page uses.
// The installed tray restarts the server and Windows Firewall gets Weir's rule; the port is then open beyond this PC and answers on the
// machine's own network address, and flipping back closes it. What one machine cannot show is said in the scenario's result: that Windows
// Firewall lets a packet from another machine in (traffic from a PC to its own address never meets the firewall), so the rule itself is read.
import assert from "node:assert/strict";
import { networkInterfaces } from "node:os";
import { join } from "node:path";

import { LogWatch } from "./log-watch.mjs";
import { PORT, ensureInstalledAndRunning, ensureSignedIn, wait } from "./windows-install.mjs";
import { json } from "./windows-machine.mjs";

const RULE_NAME = "Weir";
const ALL_PROFILES = ["Domain", "Private", "Public"];
const FLIP_CEILING_MS = 240_000;

/** What is wrong with the firewall rule(s) named Weir for the server program, in words; nothing when it is the rule Weir means to make. */
export function ruleProblems(rules, serverExe) {
  if (rules.length !== 1) return [`Windows Firewall has ${rules.length} rules named ${RULE_NAME}, not one`];
  const [rule] = rules;
  const problems = [];
  if (String(rule.enabled).toLowerCase() !== "true") problems.push("the rule is not enabled");
  if (rule.direction !== "Inbound") problems.push(`the rule is ${rule.direction}, not Inbound`);
  if (rule.action !== "Allow") problems.push(`the rule's action is ${rule.action}, not Allow`);
  const profiles = rule.profile === "Any" ? ALL_PROFILES : String(rule.profile).split(",").map((name) => name.trim());
  const missing = ALL_PROFILES.filter((profile) => !profiles.includes(profile));
  if (missing.length > 0) problems.push(`the rule does not cover the ${missing.join(" and ")} network profile${missing.length > 1 ? "s" : ""}`);
  if (String(rule.program).toLowerCase() !== serverExe.toLowerCase()) problems.push("the rule is for a program other than the installed server");
  if (rule.protocol !== "TCP") problems.push(`the rule is for ${rule.protocol}, not TCP`);
  return problems;
}

/** The IPv4 addresses of this PC's network adapters other than loopback and self-assigned ones, as the operating system reports them. */
export function lanAddresses(interfaces = networkInterfaces()) {
  return Object.values(interfaces)
    .flat()
    .filter((entry) => entry && entry.family === "IPv4" && !entry.internal && !entry.address.startsWith("169.254."))
    .map((entry) => entry.address);
}

const rulesNamedWeir = () =>
  json(
    `$rules = @(Get-NetFirewallRule -DisplayName 'Weir' -ErrorAction SilentlyContinue)
    ConvertTo-Json -Compress -Depth 4 -InputObject @($rules | ForEach-Object {
      [pscustomobject]@{
        enabled = [string]$_.Enabled
        direction = [string]$_.Direction
        action = [string]$_.Action
        profile = [string]$_.Profile
        program = [string](Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $_).Program
        protocol = [string](Get-NetFirewallPortFilter -AssociatedNetFirewallRule $_).Protocol
      }
    })`,
  ).then((rules) => rules ?? []);

const blockRulesFor = (serverExe) =>
  json(
    `ConvertTo-Json -Compress -InputObject @(Get-NetFirewallApplicationFilter -Program $env:SCENARIO_PROGRAM -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object { $_.Action -eq 'Block' -and $_.Enabled -eq 'True' } | ForEach-Object { $_.DisplayName })`,
    { PROGRAM: serverExe },
  ).then((names) => names ?? []);

const removeRules = () => json(`Get-NetFirewallRule -DisplayName 'Weir' -ErrorAction SilentlyContinue | Remove-NetFirewallRule`);

/** Asks the address for Weir's /ready; true when Weir answers there, false when nothing does. */
async function answersAt(address) {
  try {
    const answer = await fetch(`http://${address}:${PORT}/ready`, { signal: AbortSignal.timeout(5000), headers: { Connection: "close" } });
    return (await answer.json()).ready === true;
  } catch {
    return false;
  }
}

async function status(weir) {
  try {
    return await weir.get("/suite/network-access");
  } catch {
    return undefined;
  }
}

const waitFor = (world, weir, what, accepts) => wait(world, async () => {
  const current = await status(weir);
  return current && accepts(current) ? current : undefined;
}, what, FLIP_CEILING_MS, 1000);

async function choose(weir, scope) {
  const answer = await weir.write("PUT", "/suite/network-access", { scope });
  assert.equal(answer.status, 200, `choosing ${scope} was answered ${answer.status}: ${answer.text}`);
  return answer.json;
}

const beyondThisPc = (addresses) => addresses.some((address) => ["0.0.0.0", "::"].includes(address));
const onlyThisPc = (addresses) => addresses.length > 0 && addresses.every((address) => ["127.0.0.1", "::1"].includes(address));

/** network-access: other devices on flips the listener open and Windows Firewall's rule appears for every profile; this PC only shuts the port again. */
export async function networkAccess(world) {
  const { machine, weir } = world;
  await ensureInstalledAndRunning(world);
  await ensureSignedIn(world);
  const logs = LogWatch.sinceStart(weir);
  const lan = lanAddresses();
  assert.ok(lan.length > 0, "the runner has no network address other than its own to reach Weir on");
  const [address] = lan;
  const facts = [];

  // This PC only: the port listens on loopback, the machine's own network address gets no answer, and no rule has been made.
  const first = await weir.get("/suite/network-access");
  assert.equal(first.scope, "this_pc_only", `Weir starts as ${first.scope}, not this_pc_only`);
  assert.equal(first.pending_scope, null, "a change is already waiting");
  assert.ok(onlyThisPc(await machine.listeningAddresses(PORT)), `port ${PORT} listens beyond this PC before anything was allowed`);
  assert.equal(await answersAt(address), false, "Weir answers on the machine's network address while it is for this PC only");
  const hadRule = (await rulesNamedWeir()).length > 0;
  await removeRules();
  assert.deepEqual(await rulesNamedWeir(), [], "the Weir rule could not be removed before the test");
  facts.push(`for this PC only, port ${PORT} listened on loopback alone and the machine's network address got no answer (a Weir firewall rule was ${hadRule ? "there, and removed" : "not there"} to begin with)`);

  // Other devices: the choice is saved, the tray asks Windows for the rule and restarts the server for the network.
  const asked = await choose(weir, "network");
  assert.equal(asked.pending_scope, "network", "the choice for other devices is not waiting to be applied");
  const open = await waitFor(world, weir, "Weir to listen for other devices and Windows Firewall to allow it", (current) => current.scope === "network" && current.pending_scope === null && current.state === "allowed");
  assert.equal(open.firewall, "allowed", `System says Windows Firewall is ${open.firewall}`);
  assert.ok(await machine.exists(join(machine.paths.runtimeHome, "lan-access")), "the saved choice (lan-access) is not in the data folder");
  assert.equal((await machine.readText(join(machine.paths.runtimeHome, "lan-access"))).trim(), "on", "lan-access does not say on");
  const listening = await machine.listeningAddresses(PORT);
  assert.ok(beyondThisPc(listening), `port ${PORT} listens on ${listening.join(", ")}, not beyond this PC`);
  assert.equal(await blockRulesFor(machine.paths.serverExe).then((names) => names.length), 0, "an enabled block rule for the server is in place");
  const problems = ruleProblems(await rulesNamedWeir(), machine.paths.serverExe);
  assert.deepEqual(problems, [], `Windows Firewall's rule for Weir: ${problems.join("; ")}`);
  assert.ok(open.addresses.length > 0, "System lists no address to open Weir on from another device");
  facts.push("choosing other devices through the API had the tray restart the server for the network, and System reported the firewall as allowing it");
  facts.push(`the port listens beyond this PC, and Windows Firewall has one enabled inbound TCP allow rule named ${RULE_NAME} for the installed server on the Domain, Private and Public profiles, with no block rule for it`);

  // The machine's own network address now reaches Weir: the sign-in page and the API.
  assert.equal(await answersAt(address), true, "Weir does not answer on the machine's network address");
  const page = await fetch(`http://${address}:${PORT}/`, { signal: AbortSignal.timeout(10_000), headers: { Connection: "close" } });
  assert.equal(page.status, 200, `the sign-in page was answered ${page.status} on the network address`);
  assert.match(page.headers.get("content-type") ?? "", /text\/html/, "the page on the network address is not HTML");
  const reported = await (await fetch(`http://${address}:${PORT}/openapi.json`, { signal: AbortSignal.timeout(10_000), headers: { Connection: "close" } })).json();
  assert.equal(reported.info.version, world.expectedVersion, "the network address answers with another version");
  facts.push("the machine's own network address answered with the sign-in page and the build under test");

  // This PC only again: the server restarts for loopback and the port is shut. Weir's rule stays, as designed: it is the listener that shuts the port.
  const back = await choose(weir, "this_pc_only");
  assert.equal(back.pending_scope, "this_pc_only", "the choice for this PC only is not waiting to be applied");
  await waitFor(world, weir, "Weir to listen for this PC only again", (current) => current.scope === "this_pc_only" && current.pending_scope === null);
  assert.equal((await machine.readText(join(machine.paths.runtimeHome, "lan-access"))).trim(), "off", "lan-access does not say off");
  const closed = await machine.listeningAddresses(PORT);
  assert.ok(onlyThisPc(closed), `port ${PORT} listens on ${closed.join(", ")} after this PC only`);
  assert.equal(await answersAt(address), false, "Weir still answers on the machine's network address after this PC only");
  assert.equal((await rulesNamedWeir()).length, 1, "Weir's firewall rule was removed by flipping back; it is meant to stay");
  facts.push("choosing this PC only again closed the port to the network address, and left the firewall rule in place");

  await logs.finish();
  return (
    `${facts.join("; ")}. Not proved on one machine: that a packet from a second machine passes Windows Firewall (traffic from this PC to its own address ` +
    "does not meet the firewall), which the rule's profiles, program and action stand for."
  );
}
