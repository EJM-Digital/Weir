// deluno-resend, ported from scripts/scenarios/Weir.Scenarios.Deluno.ps1: Deluno sends the outcome of an imported hand-off to Weir again, as a person
// asks it to ("Tell it again"). Weir takes the repeat, keeps the one imported answer and the file it already settled, and logs no failure. Both
// sides are read over HTTP: Deluno's own record of the delivery, and Weir's file.
import assert from "node:assert/strict";

import { followedFilm } from "./film-handoff.mjs";
import { weirFilesFor, weirFiles } from "./handoffs.mjs";

const SETTLED_DELIVERIES = ["delivered", "settled"];
/** The applications share this machine's clock; this much is allowed for how Deluno rounds the time it writes. */
const CLOCK_SLACK_MS = 5000;
const FAILED_RESULTS = ["failed", "error"];
/** How a record of Weir's file reads, to compare before and after: its answer and when it was settled and released. */
const stateOf = (copy) => `${copy.id}|${copy.handback?.outcome}|${copy.handback?.settled_at}|${copy.handback?.released_at}`;
const failuresIn = async (world) => (await world.weir.get("/activity/recent?limit=100")).items.filter((item) => FAILED_RESULTS.includes(item.result)).length;

export async function delunoResend(world) {
  const { film, handoff, copies } = await followedFilm(world);
  const before = copies.map(stateOf).sort();
  const failuresBefore = await failuresIn(world);

  // Deluno answers with its own record of the outcome after the attempt: { message, outcome: { outcome, delivery, attempts, lastAttemptUtc, processorSaid } }.
  const asked = Date.now();
  const sent = await world.deluno.deluno.call("POST", `/api/integrations/processors/handoffs/${handoff.id}/outcome/send-again`);
  assert.equal(sent.status, 200, `Deluno sent the outcome again (answered ${sent.status}: ${sent.json?.message ?? sent.text.slice(0, 300)})`);
  const record = sent.json?.outcome;
  assert.ok(record, "Deluno answered with its record of the outcome it sent");
  assert.equal(record.outcome, "imported", `the outcome Deluno sent again is imported (it reads ${record.outcome})`);
  assert.ok(Number(record.attempts) >= 1 && Date.parse(record.lastAttemptUtc) >= asked - CLOCK_SLACK_MS, `Deluno really sent it to Weir just now (attempt ${record.attempts} at ${record.lastAttemptUtc ?? "no time"}; asked at ${new Date(asked).toISOString()})`);
  assert.ok(SETTLED_DELIVERIES.includes(record.delivery), `Weir took the repeat: Deluno records it as delivered or settled, never refused (it reads ${record.delivery}: ${record.processorSaid})`);

  const after = await world.deluno.handoffOf(film.release);
  assert.ok(SETTLED_DELIVERIES.includes(after.outcome?.delivery), `Deluno's own list reads the repeat as delivered or settled (it reads ${after.outcome?.delivery})`);

  const afterCopies = weirFilesFor(after, await weirFiles(world)).filter((copy) => copies.some((earlier) => earlier.id === copy.id));
  assert.deepEqual(afterCopies.map(stateOf).sort(), before, "Weir kept the one imported answer and the file it had already settled, unchanged");
  assert.equal(await failuresIn(world), failuresBefore, "the repeat raised no failure in Weir's Activity");
  return `Deluno sent the outcome for ${film.release} again; Weir took it (Deluno reads the delivery as ${after.outcome.delivery}, attempt ${record.attempts}), and Weir's file stayed imported and settled as it was, with no failure.`;
}
