// deluno-outcomes, ported from scripts/scenarios/Weir.Scenarios.Deluno.ps1: what Deluno recorded as the outcome of each hand-off agrees with what
// Weir recorded for the file. Every hand-off in the stack is this run's own, so none is set aside as the previous release's.
import assert from "node:assert/strict";

import { followedFilm } from "./film-handoff.mjs";
import { weirFiles, weirFilesFor } from "./handoffs.mjs";

const NOT_TOLD = ["pending", "refused", "never-received", "given-up"];
const NOTHING_TO_TELL = /no file was handed back, so there was nothing to tell it/;

/**
 * Deluno writes a closing outcome for a hand-off that handed no file back: not-imported, given up with no attempt, in its own words that there
 * was nothing to tell Weir. It is the one given-up that is no failure to tell: Weir was owed nothing and never held a copy. Any other reason for
 * giving up (a removed connection, a processor that does not ask to be told, a week without an answer) is judged as the failure it is.
 */
export function nothingWasOwed(handoff, outcome, weirCopies) {
  return (
    outcome.outcome === "not-imported" &&
    outcome.delivery === "given-up" &&
    Number(outcome.attempts) === 0 &&
    !String(handoff.outputPath ?? "").trim() &&
    NOTHING_TO_TELL.test(String(outcome.processorSaid ?? "")) &&
    weirCopies.length === 0
  );
}

export async function delunoOutcomes(world) {
  await followedFilm(world);
  const withOutcome = (await world.deluno.deluno.must("GET", "/api/integrations/processors/handoffs?take=200")).filter((handoff) => handoff.outcome);
  assert.ok(withOutcome.length > 0, "Deluno has recorded an outcome for at least one hand-off");
  const files = await weirFiles(world);

  let owedNothing = 0;
  let compared = 0;
  for (const handoff of withOutcome) {
    const { outcome } = handoff;
    const copies = weirFilesFor(handoff, files);
    if (nothingWasOwed(handoff, outcome, copies)) {
      owedNothing += 1;
      continue;
    }
    assert.ok(!NOT_TOLD.includes(outcome.delivery), `Deluno's outcome for ${handoff.releaseName} is ${outcome.outcome}, delivery ${outcome.delivery}, not pending, refused, never received or given up (Deluno said: ${outcome.processorSaid})`);
    if (outcome.delivery !== "delivered") continue;
    assert.ok(copies.length >= 1, `Weir holds the file Deluno's outcome (${outcome.outcome}) for ${handoff.releaseName} is about`);
    for (const copy of copies) {
      const weir = copy.handback?.outcome;
      assert.equal(weir, outcome.outcome, `${copy.relative_path}: Deluno says ${outcome.outcome} and Weir says ${weir ?? "nothing"}`);
      compared += 1;
    }
  }
  assert.ok(compared >= 1, "at least one outcome Deluno delivered could be compared with Weir's");
  return `Read the outcomes of ${withOutcome.length} hand-off(s) in Deluno: none pending, refused, never received or given up on (${owedNothing} closed with nothing owed because no file was handed back); ${compared} of Weir's files carry the same answer as Deluno.`;
}
