import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ProcessingFile } from "../../lib/processing/files-api";
import {
  LEAVING_CARD_MS,
  NO_LEAVING_CARDS,
  OUTCOME_WAIT_MS,
  advance,
  endedCards,
  nextChangeAt,
  outcomeFor,
  shownCards,
  useLeavingCards,
  type ShownCard,
} from "./leaving-cards";
import type { WaitingItem, WorkingItem } from "./processing-model";

const START = 1_000_000;

function file(
  id: number,
  status: ProcessingFile["status"],
  overrides: Partial<ProcessingFile> = {},
): ProcessingFile {
  return {
    id,
    status,
    library_id: 1,
    library_name: "TV",
    relative_path: `Show.S01E0${id}.mkv`,
    status_reason: "",
    ...overrides,
  } as ProcessingFile;
}

function shown(id: number, overrides: Partial<ShownCard> = {}): ShownCard {
  return {
    key: `file-${id}`,
    lane: "working",
    source: "download",
    name: `Show S01E0${id}`,
    path: `Show.S01E0${id}.mkv`,
    libraryName: "TV",
    step: "write",
    file: file(id, "processing"),
    ...overrides,
  };
}

describe("outcomeFor", () => {
  it("is done for a file that was processed or passed through", () => {
    expect(outcomeFor(file(1, "processed"), "write")).toEqual({ kind: "done" });
    expect(outcomeFor(file(1, "passed_through"), "write")).toEqual({
      kind: "done",
    });
  });

  it("marks a failed file at the step it was on, with a few words on the kind of failure", () => {
    const failed = file(1, "processing_failed", {
      failure_class: "execution",
      status_reason: "ffmpeg stopped. It said the disk was full.",
    });

    expect(outcomeFor(failed, "write")).toEqual({
      kind: "failed",
      at: "write",
      reason: "Writing stopped · original kept",
    });
  });

  it("says something plain when a failure gave no reason", () => {
    expect(outcomeFor(file(1, "processing_failed"), "verify")).toEqual({
      kind: "failed",
      at: "verify",
      reason: "Couldn't finish · original kept",
    });
  });

  it("treats a hold as no end", () => {
    expect(outcomeFor(file(1, "on_hold"), "write")).toBeNull();
  });

  it("shows a rejection on Checking or Plan, whatever step the card had reached", () => {
    const rejected = file(1, "rejected", {
      status_reason: "It has no video.",
    });

    expect(outcomeFor(rejected, "checking")).toMatchObject({
      kind: "rejected",
      at: "checking",
      reason: "Rejected",
    });
    expect(outcomeFor(rejected, "write")).toMatchObject({
      kind: "rejected",
      at: "plan",
    });
  });

  it("waits while the list still has the file mid-pass, and gives nothing for a file it does not have", () => {
    expect(outcomeFor(file(1, "processing"), "write")).toBe("waiting");
    expect(outcomeFor(undefined, "write")).toBeNull();
  });

  it("waits while the list still calls the file Waiting, as it does for a pass that has not reached it yet", () => {
    expect(outcomeFor(file(1, "unprocessed"), "write")).toBe("waiting");
    expect(outcomeFor(file(1, "out_of_schedule"), "write")).toBe("waiting");
  });

  it("gives nothing for a file that went on hold to try again later", () => {
    expect(outcomeFor(file(1, "on_hold"), "write")).toBeNull();
  });
});

describe("advance", () => {
  const before = advance(NO_LEAVING_CARDS, [shown(1)], [], START);

  it("keeps a card that has left, once the list says it finished", () => {
    const state = advance(before, [], [file(1, "processed")], START + 1000);

    expect(endedCards(state)).toMatchObject([
      { key: "file-1", outcome: { kind: "done" } },
    ]);
  });

  it("lets the card go LEAVING_CARD_MS after its outcome is known", () => {
    const ended = advance(before, [], [file(1, "processed")], START + 1000);

    const early = advance(
      ended,
      [],
      [file(1, "processed")],
      START + 1000 + LEAVING_CARD_MS - 1,
    );
    const late = advance(
      ended,
      [],
      [file(1, "processed")],
      START + 1000 + LEAVING_CARD_MS,
    );

    expect(endedCards(early)).toHaveLength(1);
    expect(endedCards(late)).toHaveLength(0);
  });

  it("does not restart the clock when the list refreshes while the card is kept", () => {
    const ended = advance(before, [], [file(1, "processed")], START + 1000);

    const refreshed = advance(ended, [], [file(1, "processed")], START + 2000);

    expect(nextChangeAt(refreshed)).toBe(START + 1000 + LEAVING_CARD_MS);
  });

  it("lets a card without an outcome go once the wait for one has run out", () => {
    const waiting = advance(before, [], [file(1, "processing")], START + 500);

    expect(nextChangeAt(waiting)).toBe(START + 500 + OUTCOME_WAIT_MS + 1);
  });

  it("has nothing to change by itself when no card is leaving", () => {
    expect(nextChangeAt(NO_LEAVING_CARDS)).toBeNull();
  });

  it("waits for a list that still has the file mid-pass, then shows it once it has moved on", () => {
    const waiting = advance(before, [], [file(1, "processing")], START + 500);
    expect(endedCards(waiting)).toHaveLength(0);

    const settled = advance(
      waiting,
      [],
      [file(1, "processing_failed")],
      START + 900,
    );

    expect(endedCards(settled)).toMatchObject([
      { outcome: { kind: "failed", at: "write" } },
    ]);
  });

  it("shows the end of a pass that finished before the list stopped calling the file Waiting", () => {
    const waiting = advance(before, [], [file(1, "unprocessed")], START + 500);
    expect(endedCards(waiting)).toHaveLength(0);

    const settled = advance(waiting, [], [file(1, "processed")], START + 900);

    expect(endedCards(settled)).toMatchObject([{ outcome: { kind: "done" } }]);
  });

  it("gives up on a list that never says what became of the file", () => {
    const waiting = advance(before, [], [file(1, "processing")], START + 500);

    const gaveUp = advance(
      waiting,
      [],
      [file(1, "processing")],
      START + OUTCOME_WAIT_MS + 1000,
    );

    expect(gaveUp.departures).toHaveLength(0);
  });

  it("shows nothing for a file that went back to another lane", () => {
    const state = advance(before, [], [file(1, "on_hold")], START + 500);

    expect(endedCards(state)).toHaveLength(0);
    expect(state.departures).toHaveLength(0);
  });

  describe("a card that leaves Waiting", () => {
    const fromWaiting = advance(
      NO_LEAVING_CARDS,
      [shown(1, { lane: "waiting", step: "checking" })],
      [file(1, "unprocessed")],
      START,
    );

    it("ends with every step ticked when the file went straight to processed", () => {
      const state = advance(
        fromWaiting,
        [],
        [file(1, "processed")],
        START + 500,
      );

      expect(endedCards(state)).toMatchObject([
        { lane: "waiting", outcome: { kind: "done" } },
      ]);
    });

    it("ends at the first step when the file failed without ever being seen working", () => {
      const state = advance(
        fromWaiting,
        [],
        [file(1, "processing_failed", { status_reason: "It would not open." })],
        START + 500,
      );

      expect(endedCards(state)).toMatchObject([
        {
          outcome: {
            kind: "failed",
            at: "checking",
            reason: "Couldn't finish · original kept",
          },
        },
      ]);
    });

    it("shows nothing when the file went on hold", () => {
      const state = advance(fromWaiting, [], [file(1, "on_hold")], START + 500);

      expect(state.departures).toHaveLength(0);
    });

    it("shows nothing when the file simply moved on to Working", () => {
      const state = advance(
        fromWaiting,
        [shown(1)],
        [file(1, "processing")],
        START + 500,
      );

      expect(state.departures).toHaveLength(0);
    });
  });

  it("shows nothing for a card that only moved from Working to Handing back", () => {
    const state = advance(
      before,
      [shown(1, { lane: "handing", step: "verify" })],
      [file(1, "processing")],
      START + 500,
    );

    expect(state.departures).toHaveLength(0);
  });

  it("drops a card that comes back before its end is shown", () => {
    const waiting = advance(before, [], [file(1, "processing")], START + 500);

    const back = advance(
      waiting,
      [shown(1)],
      [file(1, "processing")],
      START + 900,
    );

    expect(back.departures).toHaveLength(0);
  });

  it("returns the same state when nothing has changed", () => {
    const same = advance(before, [shown(1)], [], START + 500);

    expect(same).toBe(before);
  });

  it("keeps one ended card per file when two leave at once", () => {
    const two = advance(NO_LEAVING_CARDS, [shown(1), shown(2)], [], START);

    const state = advance(
      two,
      [],
      [file(1, "processed"), file(2, "processing_failed")],
      START + 500,
    );

    expect(endedCards(state).map((card) => card.outcome.kind)).toEqual([
      "done",
      "failed",
    ]);
  });
});

describe("shownCards", () => {
  it("follows files, not library cleans, and tags each with its lane", () => {
    const working = {
      key: "file-1",
      source: "download",
      name: "One",
      path: "one.mkv",
      libraryName: "TV",
      step: "write",
      file: file(1, "processing"),
    } as WorkingItem;
    const clean = { ...working, key: "job-9", file: null } as WorkingItem;

    const cards = shownCards([], [working, clean], []);

    expect(cards.map((card) => [card.key, card.lane])).toEqual([
      ["file-1", "working"],
    ]);
  });

  it("follows a waiting file from the first step, and not a waiting library clean", () => {
    const waiting = {
      key: "file-2",
      source: "download",
      name: "Two",
      path: "two.mkv",
      libraryName: "TV",
      file: file(2, "unprocessed"),
    } as WaitingItem;
    const clean = { ...waiting, key: "job-9", file: null } as WaitingItem;

    const cards = shownCards([waiting, clean], [], []);

    expect(cards.map((card) => [card.key, card.lane, card.step])).toEqual([
      ["file-2", "waiting", "checking"],
    ]);
  });
});

describe("useLeavingCards", () => {
  beforeEach(() => {
    vi.useFakeTimers({ now: START });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function working(id: number): WorkingItem {
    const card = shown(id);
    return { ...card, percent: 50 } as unknown as WorkingItem;
  }

  it("keeps a finished card for about two and a half seconds, then lets it drop", () => {
    const one = working(1);
    const { result, rerender } = renderHook(
      ({ items, files }) => useLeavingCards([], items, [], files),
      {
        initialProps: {
          items: [one],
          files: [file(1, "processing")],
        },
      },
    );
    expect(result.current).toHaveLength(0);

    rerender({ items: [], files: [file(1, "processed")] });
    expect(result.current).toMatchObject([
      { key: "file-1", outcome: { kind: "done" } },
    ]);

    act(() => {
      vi.advanceTimersByTime(LEAVING_CARD_MS);
    });
    expect(result.current).toHaveLength(0);
  });

  it("shows a failure at the step it failed on", () => {
    const one = working(1);
    const { result, rerender } = renderHook(
      ({ items, files }) => useLeavingCards([], items, [], files),
      { initialProps: { items: [one], files: [file(1, "processing")] } },
    );

    rerender({
      items: [],
      files: [
        file(1, "processing_failed", {
          status_reason: "The new file would not play.",
        }),
      ],
    });

    expect(result.current).toMatchObject([
      {
        outcome: {
          kind: "failed",
          at: "write",
          reason: "Couldn't finish · original kept",
        },
      },
    ]);
  });
});
