import { describe, expect, it } from "vitest";

import type { FinishedFile } from "../../lib/activity/processing-outcome";
import type { ArrivingItem, WorkingItem } from "./processing-model";
import {
  ago,
  clock,
  finishedLine,
  finishedNote,
  isJustNow,
  ordinal,
  readRate,
  removedTrackWords,
  ringFraction,
  ringLabel,
  ringState,
  runningFor,
  speedWords,
  timeLeft,
  timeRemaining,
  workingFigures,
} from "./processing-words";

function finished(overrides: Partial<FinishedFile> = {}): FinishedFile {
  return {
    id: 1,
    source: "download",
    kind: "cleaned",
    relativePath: "Show.S01E01.mkv",
    libraryId: 1,
    savedBytes: null,
    removedAudio: 0,
    removedSubtitles: 0,
    sentence: null,
    finishedAt: "2026-10-02T10:00:00Z",
    ...overrides,
  };
}

describe("what the Activity stream adds under a library clean with no sentence", () => {
  it("says nothing", () => {
    expect(finishedNote(finished({ source: "library", sentence: null }))).toBe(
      "",
    );
  });
});

describe("how a finished file's outcome reads", () => {
  it("says a file that stopped couldn't finish, and what became of the original", () => {
    expect(finishedLine(finished({ kind: "failed" }))).toBe(
      "Couldn't finish · original kept",
    );
    expect(finishedLine(finished({ kind: "passed" }))).toBe(
      "Couldn't finish · passed through",
    );
  });
});

describe("what the Activity stream adds under an entry", () => {
  it("adds only what the entry's own words do not already say", () => {
    expect(finishedNote(finished({ kind: "failed" }))).toBe("Original kept");
    expect(finishedNote(finished({ kind: "passed" }))).toBe(
      "Couldn't finish it",
    );
    expect(
      finishedNote(
        finished({
          kind: "rejected",
          sentence:
            "Rejected: None of its audio tracks are in English, and ...",
        }),
      ),
    ).toBe("By your rules");
  });

  it("keeps what a cleaned download saved and removed", () => {
    expect(
      finishedNote(
        finished({ savedBytes: 318 * 1024 * 1024, removedAudio: 2 }),
      ),
    ).toBe("Saved 318 MB · removed 2 audio");
  });

  it("reads a library clean's removals from its own entry, and says nothing when there are none to read", () => {
    const library = (sentence: string) =>
      finished({ source: "library", kind: "cleaned", sentence });

    expect(
      finishedNote(
        library(
          "Cleaned X.mkv: removed 2 audio tracks and 1 subtitle track. The original was kept at D:/Keep/X.mkv.",
        ),
      ),
    ).toBe("removed 2 audio tracks and 1 subtitle track");
    expect(finishedNote(library("Cleaned X.mkv."))).toBe("");
  });
});

describe("how a place in line reads", () => {
  it("says first, second, third and the rest with the right ending", () => {
    expect([0, 1, 2, 3, 10, 11, 12, 20, 21].map(ordinal)).toEqual([
      "1st",
      "2nd",
      "3rd",
      "4th",
      "11th",
      "12th",
      "13th",
      "21st",
      "22nd",
    ]);
  });
});

describe("how long a pass has still to run", () => {
  it("says seconds under a minute and a half, then minutes, then hours", () => {
    expect(timeRemaining(41)).toBe("41 s");
    expect(timeRemaining(300)).toBe("5 min");
    expect(timeRemaining(3 * 3600)).toBe("3 h");
  });

  it("adds that it is what is left, and says nothing for an estimate it does not have", () => {
    expect(timeLeft(300)).toBe("5 min left");
    expect(timeLeft(null)).toBe("");
    expect(timeRemaining(null)).toBe("");
  });
});

describe("the tracks a pass took out", () => {
  it.each([
    [0, 0, []],
    [2, 0, ["2 audio"]],
    [0, 1, ["1 subtitle"]],
    [1, 4, ["1 audio", "4 subtitles"]],
  ])(
    "with %i audio and %i subtitles reads %j",
    (removedAudio, removedSubtitles, words) => {
      expect(removedTrackWords({ removedAudio, removedSubtitles })).toEqual(
        words,
      );
    },
  );

  it.each([
    ["saved nothing and removed nothing", {}, "Cleaned"],
    ["saved space", { savedBytes: 5 * 1024 * 1024 }, "Saved 5.0 MB"],
    [
      "removed tracks without saving space",
      { removedAudio: 2 },
      "Cleaned · removed 2 audio",
    ],
    [
      "saved space and removed tracks",
      { savedBytes: 5 * 1024 * 1024, removedSubtitles: 3 },
      "Saved 5.0 MB · removed 3 subtitles",
    ],
    ["was already clean", { kind: "already" }, "Already clean"],
    [
      "has a sentence of its own",
      { sentence: "Rejected: no English audio." },
      "Rejected: no English audio.",
    ],
  ] as const)("reads a file that %s", (_name, overrides, words) => {
    expect(finishedLine(finished(overrides))).toBe(words);
  });
});

describe("how long ago something happened", () => {
  const NOW = Date.parse("2026-10-02T12:00:00Z");
  const at = (secondsAgo: number) =>
    new Date(NOW - secondsAgo * 1000).toISOString();

  it.each([
    [10, "just now"],
    [44, "just now"],
    [60, "1 min ago"],
    [600, "10 min ago"],
    [5399, "90 min ago"],
    [7200, "2 h ago"],
  ])("reads %i seconds ago as %s", (secondsAgo, words) => {
    expect(ago(at(secondsAgo), NOW)).toBe(words);
  });

  it("reads a moment that has not come yet as just now, and an unreadable one as nothing", () => {
    expect(ago(at(-30), NOW)).toBe("just now");
    expect(ago("not a time", NOW)).toBe("");
    expect(isJustNow("not a time", NOW)).toBe(false);
    expect(isJustNow(at(5), NOW)).toBe(true);
  });
});

describe("the numbers a running pass reports", () => {
  it.each([
    ["1.26e+03x", "1,260×"],
    ["35.2x", "35×"],
    ["0.8x", "0.8×"],
    ["2.0x", "2×"],
    ["0x", null],
    ["fast", null],
    ["", null],
    [null, null],
    [undefined, null],
  ])("reads the speed %j as %j", (raw, words) => {
    expect(speedWords(raw)).toBe(words);
  });

  it.each([
    ["from the size, how far along and how long", 1000, 50, 10, 50],
    ["without a size", null, 50, 10, null],
    ["without a percentage", 1000, null, 10, null],
    ["without any time passing", 1000, 50, 0, null],
    ["in less than a second", 1000, 50, 0.5, null],
    ["before it has begun", 1000, 0, 10, null],
    ["capped at the whole file", 1000, 250, 10, 100],
  ])("reads the rate %s", (_name, size, percent, elapsed, rate) => {
    expect(readRate(size, percent, elapsed)).toBe(rate);
  });

  it.each([
    [245, "4:05"],
    [3723, "1:02:03"],
    [0, "0:00"],
    [-1, ""],
    [Number.NaN, ""],
    [null, ""],
    [undefined, ""],
  ])("shows %j seconds on a clock as %j", (seconds, words) => {
    expect(clock(seconds)).toBe(words);
  });

  it.each([
    [41, "41 s"],
    [120, "2 min"],
    [134, "2 min 14 s"],
    [3600, "1 h"],
    [3900, "1 h 5 min"],
    [-1, ""],
    [Number.POSITIVE_INFINITY, ""],
    [null, ""],
    [undefined, ""],
  ])("says %j seconds of running as %j", (seconds, words) => {
    expect(runningFor(seconds)).toBe(words);
  });

  it.each([
    [58, "58s"],
    [540, "9m"],
    [7200, "2h"],
    [0, "0s"],
  ])("fits %i seconds in the ring as %s", (seconds, label) => {
    expect(ringLabel(seconds)).toBe(label);
  });
});

describe("how a file's wait looks in its ring", () => {
  const waiting = (changes: Partial<ArrivingItem>): ArrivingItem =>
    ({
      holdUntil: null,
      holdTotal: null,
      nextLook: null,
      ...changes,
    }) as ArrivingItem;

  it.each([
    [null, "unknown"],
    [30, "counting"],
    [0, "checking"],
    [-4, "checking"],
  ])("is %j seconds left, so the ring is %s", (left, state) => {
    expect(ringState(left)).toBe(state);
  });

  it.each([
    ["has come", waiting({ holdUntil: 1, holdTotal: 100 }), 0, 1],
    [
      "is known only by its own hold",
      waiting({ holdUntil: 1, holdTotal: 100 }),
      25,
      0.75,
    ],
    [
      "is known only by the next look at its library",
      waiting({ nextLook: { interval: 200 } as ArrivingItem["nextLook"] }),
      50,
      0.75,
    ],
    ["has no length to measure", waiting({}), 50, null],
    [
      "has a hold of no length",
      waiting({ holdUntil: 1, holdTotal: 0 }),
      50,
      null,
    ],
    [
      "is not known at all",
      waiting({ holdUntil: 1, holdTotal: 100 }),
      null,
      null,
    ],
    [
      "is longer than the hold",
      waiting({ holdUntil: 1, holdTotal: 100 }),
      250,
      0,
    ],
  ])("fills the ring when the wait %s", (_name, item, left, fraction) => {
    expect(ringFraction(item, left)).toBe(fraction);
  });
});

describe("a running pass's figures", () => {
  const working = (
    file: Record<string, unknown> | null,
    changes: Partial<WorkingItem> = {},
  ) =>
    ({
      file,
      percent: null,
      speed: null,
      ...changes,
    }) as unknown as WorkingItem;

  it("reads every figure the server has given", () => {
    expect(
      workingFigures(
        working(
          {
            size_bytes: 1_000_000,
            progress_elapsed_seconds: 10,
            duration_seconds: 600,
          },
          { percent: 50, speed: "2.5x" },
        ),
      ),
    ).toEqual({
      speed: "2.5×",
      reading: "48.8 KB/s",
      through: "5:00 of 10:00",
      running: "10 s",
    });
  });

  it("leaves out every figure the server has not given", () => {
    expect(workingFigures(working(null))).toEqual({
      speed: null,
      reading: null,
      through: null,
      running: null,
    });
  });

  it("does not go past the end of the file when the pass reports more than a whole", () => {
    expect(
      workingFigures(working({ duration_seconds: 600 }, { percent: 130 }))
        .through,
    ).toBe("10:00 of 10:00");
  });
});
