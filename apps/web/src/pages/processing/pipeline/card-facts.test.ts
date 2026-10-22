import { describe, expect, it } from "vitest";

import { arrivingNote, identityFacts, placeFact } from "./card-facts";
import { NOW, aFile, lanesOf } from "./pipeline-fixtures";

const arrivingFile = (
  overrides: Parameters<typeof aFile>[2],
  nextLook?: { at: number; interval: number },
) =>
  lanesOf(
    [
      aFile(1, "on_hold", {
        status_reason: "Still being written.",
        ...overrides,
      }),
    ],
    [],
    nextLook ? new Map([[2, nextLook]]) : new Map(),
  ).arriving[0];

describe("what an arriving file is told", () => {
  it("says when Weir looks again, for a wait with no clock of its own", () => {
    const item = arrivingFile({}, { at: NOW + 83_000, interval: 300 });

    expect(arrivingNote(item, NOW)).toBe(
      "Still being written. Weir looks again in 1:23.",
    );
  });

  it("says Weir is looking again now once that moment has come", () => {
    const item = arrivingFile({}, { at: NOW - 1000, interval: 300 });

    expect(arrivingNote(item, NOW)).toBe(
      "Still being written. Weir is looking again now.",
    );
  });

  it("says the wait is over, and Weir is checking, once a file's own hold has run out", () => {
    const item = arrivingFile({
      hold_until: new Date(NOW - 1000).toISOString(),
    });

    expect(arrivingNote(item, NOW)).toBe(
      "Its wait is over. Weir is checking it now.",
    );
  });

  it("says only the server's sentence while a hold is still counting down", () => {
    const item = arrivingFile({
      hold_until: new Date(NOW + 30_000).toISOString(),
    });

    expect(arrivingNote(item, NOW)).toBe("Still being written.");
  });
});

describe("what says which file and work a card is", () => {
  it("gives the source with its workflow, then the file's own name", () => {
    expect(identityFacts("library", "Movies", "A/B/Film.2020.mkv")).toEqual([
      "Library clean · Movies",
      "Film.2020.mkv",
    ]);
  });

  it("gives only the source where there is no file", () => {
    expect(identityFacts("download", "TV", "")).toEqual(["Download · TV"]);
  });

  it("says a place in line as a person does", () => {
    expect(placeFact(1)).toBe("1st in line");
    expect(placeFact(13)).toBe("13th in line");
  });
});

describe("what a file the media manager is still importing is told", () => {
  it("says Weir is checking again now, once Weir's next look has come", () => {
    const [item] = lanesOf(
      [aFile(1, "blocked_upstream", { blocked_by_connection: "Sonarr" })],
      [],
      new Map([[2, { at: NOW - 1000, interval: 300 }]]),
    ).arriving;

    expect(arrivingNote(item, NOW)).toBe(
      "Sonarr is still importing it. Weir is checking again now.",
    );
  });
});
