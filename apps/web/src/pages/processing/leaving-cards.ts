/**
 * What a card looks like for a moment after its file leaves Waiting, Working or Handing back. A file that
 * finishes, fails or is rejected disappears from those lanes in the same refresh that puts it in Just
 * finished or Needs you, so the card is kept a little longer, drawn with its end state: every step ticked,
 * or the step it stopped at and why. A pass short enough to end before the page ever saw the file in
 * Working goes straight from Waiting to its end, and gets the same card (#852). This works from two
 * consecutive states of the lists, so it needs nothing from the server beyond what the file list already says.
 */
import { useEffect, useMemo, useState } from "react";

import type { ProcessingFile } from "../../lib/processing/files-api";
import { fileReason } from "./file-reason";
import {
  type HandingItem,
  type WaitingItem,
  type WorkingItem,
  type WorkSource,
} from "./processing-model";
import type { FlowStepId } from "./stage-flow-model";

/** How long an ended card stays where it was. */
export const LEAVING_CARD_MS = 2500;
/** How long to wait for the file list to say what became of a card that has left, before letting it go unshown. */
export const OUTCOME_WAIT_MS = 6000;

/** Where a card was last on screen. */
export type LeavingLane = "waiting" | "working" | "handing";

/** A file that has not started has shown no step, so its card starts at the first one. */
const WAITING_STEP: FlowStepId = "checking";

export type LeavingOutcome =
  | { kind: "done" }
  | { kind: "failed" | "rejected"; at: FlowStepId; reason: string };

/** A card as it was last on screen. */
export type ShownCard = {
  key: string;
  lane: LeavingLane;
  source: WorkSource;
  name: string;
  path: string;
  libraryName: string;
  step: FlowStepId;
  file: ProcessingFile;
};

export type LeavingCard = ShownCard & { outcome: LeavingOutcome };

type Departure = {
  card: ShownCard;
  since: number;
  /** Null until the file list says what became of the file. */
  outcome: LeavingOutcome | null;
  /** When the ended card is let go; null until it has an outcome. */
  until: number | null;
};

export type LeavingState = {
  shown: ReadonlyMap<string, ShownCard>;
  departures: readonly Departure[];
};

export const NO_LEAVING_CARDS: LeavingState = {
  shown: new Map(),
  departures: [],
};

/** Rejections come from the checks and the plan, so a card that ended that way never gets past Plan. */
function rejectedAt(step: FlowStepId): FlowStepId {
  return step === "checking" ? "checking" : "plan";
}

/**
 * What became of a file that left the lanes: an outcome to show, `"waiting"` while the list still has it
 * mid-pass or still Waiting (a live progress frame can show a pass running before the list has heard of it, and
 * a pass can end before the list hears of that), or null when it went somewhere that is not an end (on hold for a
 * while).
 */
export function outcomeFor(
  file: ProcessingFile | undefined,
  step: FlowStepId,
): LeavingOutcome | "waiting" | null {
  if (!file) return null;
  switch (file.status) {
    case "processing":
    case "unprocessed":
    case "out_of_schedule":
      return "waiting";
    case "processed":
    case "passed_through":
      return { kind: "done" };
    case "rejected":
      return {
        kind: "rejected",
        at: rejectedAt(step),
        reason: fileReason(file).short,
      };
    case "processing_failed":
      return failed(file, step);
    default:
      return null;
  }
}

function failed(file: ProcessingFile, step: FlowStepId): LeavingOutcome {
  return {
    kind: "failed",
    at: step,
    reason: fileReason(file).short,
  };
}

/** The cards on screen in Waiting, Working and Handing back, one per file. Library cleans have no file to follow. */
export function shownCards(
  waiting: readonly WaitingItem[],
  working: readonly WorkingItem[],
  handing: readonly HandingItem[],
): ShownCard[] {
  const cards: ShownCard[] = [];
  for (const item of waiting) {
    if (item.file) {
      cards.push({
        ...item,
        lane: "waiting",
        step: WAITING_STEP,
        file: item.file,
      });
    }
  }
  for (const [lane, items] of [
    ["working", working],
    ["handing", handing],
  ] as const) {
    for (const item of items) {
      if (item.file) cards.push({ ...item, lane, file: item.file });
    }
  }
  return cards;
}

/**
 * The next state, given the cards on screen now and the file list. A card that was on screen and no longer
 * is becomes a departure; a departure gets its outcome from the file list, and is let go `LEAVING_CARD_MS`
 * after that. Returns `state` itself when nothing changed, so a repeat call costs no render.
 */
export function advance(
  state: LeavingState,
  cards: readonly ShownCard[],
  files: readonly ProcessingFile[],
  now: number,
): LeavingState {
  const current = new Map(cards.map((card) => [card.key, card]));
  const fileById = new Map(files.map((file) => [file.id, file]));
  const known = new Set(state.departures.map((d) => d.card.key));
  const departures: Departure[] = state.departures.filter(
    (d) => !current.has(d.card.key),
  );
  for (const [key, card] of state.shown) {
    if (!current.has(key) && !known.has(key)) {
      departures.push({ card, since: now, outcome: null, until: null });
    }
  }
  const next = departures.flatMap((departure) => {
    const settled = settle(departure, fileById, now);
    return settled ? [settled] : [];
  });
  const unchanged =
    sameCards(state.shown, current) &&
    next.length === state.departures.length &&
    next.every((d, index) => d === state.departures[index]);
  return unchanged ? state : { shown: current, departures: next };
}

/** The departure with its outcome once the file list has one, or null to let it go. */
function settle(
  departure: Departure,
  fileById: ReadonlyMap<number, ProcessingFile>,
  now: number,
): Departure | null {
  if (departure.until != null) {
    return now >= departure.until ? null : departure;
  }
  const outcome = outcomeFor(
    fileById.get(departure.card.file.id),
    departure.card.step,
  );
  if (outcome === "waiting") {
    return now - departure.since > OUTCOME_WAIT_MS ? null : departure;
  }
  return outcome
    ? { ...departure, outcome, until: now + LEAVING_CARD_MS }
    : null;
}

function sameCards(
  a: ReadonlyMap<string, ShownCard>,
  b: ReadonlyMap<string, ShownCard>,
): boolean {
  if (a.size !== b.size) return false;
  for (const [key, card] of b) {
    const before = a.get(key);
    if (!before || before.step !== card.step || before.lane !== card.lane) {
      return false;
    }
  }
  return true;
}

/** The soonest moment `state` changes by itself: an ended card being let go, or a wait for an outcome running out. */
export function nextChangeAt(state: LeavingState): number | null {
  const moments = state.departures.map(
    (d) => d.until ?? d.since + OUTCOME_WAIT_MS + 1,
  );
  return moments.length ? Math.min(...moments) : null;
}

export function endedCards(state: LeavingState): LeavingCard[] {
  return state.departures.flatMap((d) =>
    d.outcome ? [{ ...d.card, outcome: d.outcome }] : [],
  );
}

/** The time the departures are measured against; read as it is when the lanes change, not at a fixed point of a render. */
function clockNow(): number {
  return Date.now();
}

/**
 * The ended cards to draw now. `waiting`, `working` and `handing` are the lanes before any filter, so
 * narrowing the page to one kind of file is never mistaken for a file leaving.
 */
export function useLeavingCards(
  waiting: readonly WaitingItem[],
  working: readonly WorkingItem[],
  handing: readonly HandingItem[],
  files: readonly ProcessingFile[],
): LeavingCard[] {
  const cards = useMemo(
    () => shownCards(waiting, working, handing),
    [waiting, working, handing],
  );
  const [held, setHeld] = useState(NO_LEAVING_CARDS);
  // Worked out while rendering, not in an effect after it: the render that takes a card off the lanes already
  // carries its ended card, so the card is never missing from a commit and its element is kept (#1383).
  const state = advance(held, cards, files, clockNow());
  if (state !== held) setHeld(state);

  useEffect(() => {
    const due = nextChangeAt(state);
    if (due == null) return;
    const timer = window.setTimeout(
      () => setHeld((before) => advance(before, cards, files, Date.now())),
      Math.max(0, due - Date.now()),
    );
    return () => window.clearTimeout(timer);
  }, [state, cards, files]);

  return useMemo(() => endedCards(state), [state]);
}
