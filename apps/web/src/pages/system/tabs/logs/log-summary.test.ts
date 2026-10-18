import { describe, expect, it } from "vitest";

import { logSummaryWords } from "./log-summary";

describe("logSummaryWords", () => {
  const live = { paused: false };

  it("counts every entry when all of them are in hand", () => {
    expect(
      logSummaryWords({ loaded: 212, total: 212, filtered: false, ...live }),
    ).toBe("212 entries · live");
    expect(
      logSummaryWords({ loaded: 1, total: 1, filtered: false, ...live }),
    ).toBe("1 entry · live");
    expect(
      logSummaryWords({ loaded: 0, total: 0, filtered: false, ...live }),
    ).toBe("0 entries · live");
  });

  it("says how many are shown when there are more than the page holds", () => {
    expect(
      logSummaryWords({ loaded: 50, total: 212, filtered: false, ...live }),
    ).toBe("Showing 50 of 212 entries · live");
    expect(
      logSummaryWords({ loaded: 50, total: 1234, filtered: true, ...live }),
    ).toBe("Showing 50 of 1,234 entries · live");
  });

  it("says the entries match once filters narrow the log and all of them are in hand", () => {
    expect(
      logSummaryWords({ loaded: 3, total: 3, filtered: true, ...live }),
    ).toBe("3 entries matching · live");
    expect(
      logSummaryWords({ loaded: 1, total: 1, filtered: true, ...live }),
    ).toBe("1 entry matching · live");
  });

  it("does not call the list live while the live link is down", () => {
    expect(
      logSummaryWords({ loaded: 3, total: 3, filtered: false, paused: true }),
    ).toBe("3 entries · live updates paused");
    expect(
      logSummaryWords({ loaded: 50, total: 212, filtered: true, paused: true }),
    ).toBe("Showing 50 of 212 entries · live updates paused");
  });
});
