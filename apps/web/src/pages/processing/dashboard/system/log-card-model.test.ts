import { describe, expect, it } from "vitest";

import {
  PULSE_MS,
  defaultFilter,
  emptyLogWords,
  isFresh,
  levelOf,
  lineFromEntry,
  lineFromFrame,
  logSummary,
  logSummaryWords,
  mergeLines,
  shownLines,
  todayCounts,
  type LogLine,
} from "./log-card-model";

const NOW = Date.parse("2026-10-02T10:00:00Z");

function line(overrides: Partial<LogLine> & { at: number }): LogLine {
  return {
    key: `${overrides.at}|info|${overrides.message ?? "m"}`,
    level: "info",
    message: "m",
    arrivedAt: null,
    ...overrides,
  };
}

describe("levelOf", () => {
  it.each([
    ["ERROR", "error"],
    ["Critical", "error"],
    ["WARNING", "warning"],
    ["warn", "warning"],
    ["INFO", "info"],
    ["Information", "info"],
    ["DEBUG", "info"],
  ])("reads %s as %s", (raw, level) => {
    expect(levelOf(raw)).toBe(level);
  });
});

describe("lineFromEntry and lineFromFrame", () => {
  it("reads a log entry's time and level", () => {
    const parsed = lineFromEntry({
      timestamp: "2026-10-02T09:59:30Z",
      level: "WARNING",
      message: "Radarr was slow.",
    });
    expect(parsed).toMatchObject({
      at: Date.parse("2026-10-02T09:59:30Z"),
      level: "warning",
      message: "Radarr was slow.",
      arrivedAt: null,
    });
  });

  it("skips an entry whose time cannot be read", () => {
    expect(
      lineFromEntry({ timestamp: "never", level: "ERROR", message: "x" }),
    ).toBeNull();
  });

  it("marks a line from the stream as arriving when the page was told", () => {
    const parsed = lineFromFrame(
      { at: "2026-10-02T10:00:00Z", level: "ERROR", message: "Disk full." },
      NOW + 5,
    );
    expect(parsed).toMatchObject({ level: "error", arrivedAt: NOW + 5 });
  });

  it("gives the same line the same key from the log and from the stream", () => {
    const fromLog = lineFromEntry({
      timestamp: "2026-10-02T10:00:00Z",
      level: "ERROR",
      message: "Disk full.",
    });
    const fromStream = lineFromFrame(
      { at: "2026-10-02T10:00:00Z", level: "ERROR", message: "Disk full." },
      NOW,
    );
    expect(fromLog?.key).toBe(fromStream?.key);
  });
});

describe("mergeLines", () => {
  it("lists every line once, newest first", () => {
    const merged = mergeLines([
      [line({ at: 1, message: "a" }), line({ at: 3, message: "c" })],
      [line({ at: 2, message: "b" }), line({ at: 3, message: "c" })],
    ]);
    expect(merged.map((entry) => entry.message)).toEqual(["c", "b", "a"]);
  });

  it("keeps the live version of a line that was read from the log afterwards", () => {
    const live = line({ at: 5, message: "x", arrivedAt: NOW });
    const read = line({ at: 5, message: "x" });
    const [kept] = mergeLines([[live], [read]]);
    expect(kept.arrivedAt).toBe(NOW);
  });

  it("keeps no more than the limit", () => {
    const many = Array.from({ length: 10 }, (_, index) =>
      line({ at: index, message: `m${index}` }),
    );
    expect(mergeLines([many], 4)).toHaveLength(4);
  });
});

describe("shownLines", () => {
  const lines = [
    line({ at: 3, level: "error", message: "e" }),
    line({ at: 2, level: "warning", message: "w" }),
    line({ at: 1, level: "info", message: "i" }),
  ];

  it("shows everything for All", () => {
    expect(shownLines(lines, "all")).toHaveLength(3);
  });

  it("shows the errors and warnings together for Problems, leaving the information out", () => {
    expect(shownLines(lines, "problems").map((entry) => entry.message)).toEqual(
      ["e", "w"],
    );
  });

  it("shows only errors, or only warnings", () => {
    expect(shownLines(lines, "error").map((entry) => entry.message)).toEqual([
      "e",
    ]);
    expect(shownLines(lines, "warning").map((entry) => entry.message)).toEqual([
      "w",
    ]);
  });
});

describe("a line's key", () => {
  it("is the same for the line the stream brought and the one the log read returns, so the line keeps its element", () => {
    // The server writes one timestamp (to the microsecond, "Z" for UTC) to both, and the stream trims the message.
    const streamed = lineFromFrame(
      {
        at: "2026-10-02T09:59:58.123456Z",
        level: "WARNING",
        message: "Disk is nearly full",
      },
      NOW,
    );
    const read = lineFromEntry({
      timestamp: "2026-10-02T09:59:58.123456Z",
      level: "WARNING",
      message: " Disk is nearly full\n",
    });

    expect(streamed?.key).toBe(read?.key);
    expect(mergeLines([[streamed!], [read!]])).toHaveLength(1);
  });
});

describe("defaultFilter", () => {
  it("is the problems when there have been errors or warnings today", () => {
    expect(defaultFilter({ errors: 2, warnings: 0 })).toBe("problems");
    expect(defaultFilter({ errors: 0, warnings: 1 })).toBe("problems");
  });

  it("is everything when today has had none", () => {
    expect(defaultFilter({ errors: 0, warnings: 0 })).toBe("all");
  });
});

describe("isFresh", () => {
  it("pulses a line that has just arrived, and not one read from the log", () => {
    expect(isFresh(line({ at: 1, arrivedAt: NOW }), NOW + PULSE_MS - 1)).toBe(
      true,
    );
    expect(isFresh(line({ at: 1, arrivedAt: NOW }), NOW + PULSE_MS)).toBe(
      false,
    );
    expect(isFresh(line({ at: 1 }), NOW)).toBe(false);
  });
});

describe("todayCounts", () => {
  const lines = [
    line({ at: Date.parse("2026-10-02T09:00:00Z"), level: "error" }),
    line({ at: Date.parse("2026-10-02T08:00:00Z"), level: "warning" }),
    line({ at: Date.parse("2026-10-02T07:00:00Z"), level: "warning" }),
    line({ at: Date.parse("2026-10-01T09:00:00Z"), level: "error" }),
    line({ at: Date.parse("2026-10-02T06:00:00Z"), level: "info" }),
  ];

  it("counts errors and warnings written on today's date", () => {
    expect(todayCounts(lines, NOW, "UTC")).toEqual({ errors: 1, warnings: 2 });
  });

  it("takes today in the timezone it is given", () => {
    // 15:00 UTC on the 1st is already the 2nd in Sydney.
    const lateOnTheFirst = Date.parse("2026-10-01T15:00:00Z");
    expect(todayCounts(lines, lateOnTheFirst, "UTC")).toEqual({
      errors: 1,
      warnings: 0,
    });
    expect(todayCounts(lines, lateOnTheFirst, "Australia/Sydney")).toEqual({
      errors: 1,
      warnings: 2,
    });
  });

  it("falls back to the browser's timezone for a name it does not know", () => {
    expect(() => todayCounts(lines, NOW, "Not/AZone")).not.toThrow();
  });
});

describe("the card's words", () => {
  it("sums today up with plural counts", () => {
    expect(logSummary({ errors: 0, warnings: 3 })).toBe(
      "0 errors · 3 warnings today",
    );
    expect(logSummary({ errors: 1, warnings: 1 })).toBe(
      "1 error · 1 warning today",
    );
  });

  it("narrows the summary by the day, then the warnings, then to the errors, and to nothing where there is no room for those", () => {
    expect(logSummaryWords({ errors: 2, warnings: 0 })).toEqual([
      "2 errors · 0 warnings today",
      "2 errors · 0 warnings",
      "2 errors",
      "",
    ]);
  });

  it("says what an empty list means for each switch", () => {
    expect(emptyLogWords("problems")).toBe("No errors or warnings in the log.");
    expect(emptyLogWords("all")).toBe("Nothing has been logged yet.");
    expect(emptyLogWords("error")).toBe("No errors in the log.");
    expect(emptyLogWords("warning")).toBe("No warnings in the log.");
  });
});
