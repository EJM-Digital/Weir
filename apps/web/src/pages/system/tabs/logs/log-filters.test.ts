import { describe, expect, it } from "vitest";

import {
  EMPTY_LOG_FILTERS,
  anyLogFilterSet,
  filtersFromParams,
  levelChoicesFor,
  levelsForChoices,
  logQuery,
  paramsFromFilters,
  rangeFor,
  refineCount,
  type LogFilters,
} from "./log-filters";

const NOON = Date.UTC(2026, 9, 2, 12, 0);

const params = (text: string) => new URLSearchParams(text);

describe("filtersFromParams", () => {
  it("reads every filter the address holds", () => {
    const filters = filtersFromParams(
      params(
        "tab=logs&source=job&level=error,warning&category=processing,sign_in&workflow=3&when=today&q=disk+full&event_type=auth.login_failed&result=failed&trigger=manual&status=failed,pending&stack=1&job=41",
      ),
    );

    expect(filters).toEqual({
      source: "job",
      levels: ["error", "warning"],
      categories: ["processing", "sign_in"],
      workflow: 3,
      when: "today",
      from: "",
      to: "",
      text: "disk full",
      eventType: "auth.login_failed",
      result: "failed",
      trigger: "manual",
      statuses: ["pending", "failed"],
      stackOnly: true,
      job: 41,
    });
  });

  it("holds nothing for an address with no filters", () => {
    expect(filtersFromParams(params("tab=logs"))).toEqual(EMPTY_LOG_FILTERS);
  });

  it("drops what it does not know rather than asking the server about it", () => {
    const filters = filtersFromParams(
      params(
        "source=disk,job&level=fatal&category=misc&workflow=-2&when=fortnight&status=stuck&job=abc",
      ),
    );

    expect(filters).toEqual({ ...EMPTY_LOG_FILTERS, source: "job" });
  });

  it.each([
    ["events", "event"],
    ["activity", "event"],
    ["jobs", "job"],
    ["server", "server"],
    ["log", "server"],
  ])("lands an older address's show=%s on the %s source", (show, source) => {
    expect(filtersFromParams(params(`tab=logs&show=${show}`)).source).toBe(
      source,
    );
  });

  it("keeps a job status an older address asked for, and the Jobs source with it", () => {
    const filters = filtersFromParams(
      params("tab=logs&show=jobs&status=failed"),
    );

    expect(filters.source).toBe("job");
    expect(filters.statuses).toEqual(["failed"]);
  });

  it("lets the source in the address win over an older show", () => {
    expect(filtersFromParams(params("show=jobs&source=server")).source).toBe(
      "server",
    );
  });

  it("shows all sources for an address that names several", () => {
    for (const address of [
      "source=event,job",
      "source=job,server,event",
      "source=event,job&show=server",
    ]) {
      expect(filtersFromParams(params(address)).source).toBeNull();
    }
  });

  it("takes the one source it knows from an address that names others too", () => {
    expect(filtersFromParams(params("source=disk,job")).source).toBe("job");
  });
});

describe("paramsFromFilters", () => {
  it("round-trips whatever the filters hold", () => {
    const filters: LogFilters = {
      source: "event",
      levels: ["error", "info", "success"],
      categories: ["scans"],
      workflow: 2,
      when: "custom",
      from: "2026-10-01T08:00",
      to: "2026-10-02T08:00",
      text: "slow",
      eventType: "library.scan_completed",
      result: "success",
      trigger: "scheduled",
      statuses: ["completed"],
      stackOnly: true,
      job: 7,
    };

    expect(filtersFromParams(paramsFromFilters(filters, params("")))).toEqual(
      filters,
    );
  });

  it("keeps the parts of the address that are not filters and drops the older show", () => {
    const next = paramsFromFilters(
      { ...EMPTY_LOG_FILTERS, levels: ["error"] },
      params("tab=logs&show=jobs&source=job&q=old"),
    );

    expect(next.get("tab")).toBe("logs");
    expect(next.get("level")).toBe("error");
    expect(next.has("show")).toBe(false);
    expect(next.has("source")).toBe(false);
    expect(next.has("q")).toBe(false);
  });

  it("leaves out a custom range's ends once another time is chosen", () => {
    const next = paramsFromFilters(
      { ...EMPTY_LOG_FILTERS, when: "week", from: "2026-10-01T08:00" },
      params(""),
    );

    expect(next.get("when")).toBe("week");
    expect(next.has("from")).toBe(false);
  });
});

describe("rangeFor", () => {
  it("counts back from the moment the time was chosen", () => {
    expect(
      rangeFor({ ...EMPTY_LOG_FILTERS, when: "hour" }, NOON, "UTC"),
    ).toEqual({ from: "2026-10-02T11:00:00.000Z" });
    expect(
      rangeFor({ ...EMPTY_LOG_FILTERS, when: "day" }, NOON, "UTC"),
    ).toEqual({
      from: "2026-10-01T12:00:00.000Z",
    });
    expect(
      rangeFor({ ...EMPTY_LOG_FILTERS, when: "week" }, NOON, "UTC"),
    ).toEqual({ from: "2026-09-25T12:00:00.000Z" });
  });

  it("starts today at midnight in Weir's time zone", () => {
    expect(
      rangeFor(
        { ...EMPTY_LOG_FILTERS, when: "today" },
        NOON,
        "America/New_York",
      ),
    ).toEqual({ from: "2026-10-02T04:00:00.000Z" });
  });

  it("reads a custom range in Weir's time zone, and leaves an end open that is empty", () => {
    expect(
      rangeFor(
        { when: "custom", from: "2026-10-01T08:00", to: "" },
        NOON,
        "Australia/Sydney",
      ),
    ).toEqual({ from: "2026-09-30T22:00:00.000Z", to: undefined });
  });

  it("has no range for any time", () => {
    expect(rangeFor(EMPTY_LOG_FILTERS, NOON, "UTC")).toEqual({});
  });
});

describe("logQuery", () => {
  it("asks only for what is set", () => {
    expect(logQuery(EMPTY_LOG_FILTERS, NOON, "UTC")).toEqual({});
  });

  it("puts every filter in the server's words", () => {
    const query = logQuery(
      {
        ...EMPTY_LOG_FILTERS,
        source: "event",
        levels: ["error"],
        categories: ["sign_in"],
        workflow: 4,
        when: "hour",
        text: "alice",
        trigger: "manual",
        stackOnly: true,
      },
      NOON,
      "UTC",
    );

    expect(query).toEqual({
      source: ["event"],
      level: ["error"],
      category: ["sign_in"],
      workflow: 4,
      q: "alice",
      trigger: "manual",
      has_exception: true,
      from: "2026-10-02T11:00:00.000Z",
    });
  });
});

describe("the level choices", () => {
  it("stand for the levels they name, Info for information and successes", () => {
    expect(levelsForChoices(["warning", "info"])).toEqual([
      "warning",
      "info",
      "success",
    ]);
    expect(levelChoicesFor(["success"])).toEqual(["info"]);
    expect(levelChoicesFor(["error", "warning", "info", "success"])).toEqual([
      "error",
      "warning",
      "info",
    ]);
  });

  it("are every level when none or all are chosen", () => {
    expect(levelsForChoices([])).toEqual([]);
    expect(levelsForChoices(["error", "warning", "info"])).toEqual([]);
  });
});

describe("what is set", () => {
  it("knows when nothing narrows the log", () => {
    expect(anyLogFilterSet(EMPTY_LOG_FILTERS)).toBe(false);
    expect(anyLogFilterSet({ ...EMPTY_LOG_FILTERS, workflow: 1 })).toBe(true);
    expect(anyLogFilterSet({ ...EMPTY_LOG_FILTERS, text: "x" })).toBe(true);
  });

  it("counts the filters that are only in the refine row", () => {
    expect(refineCount(EMPTY_LOG_FILTERS)).toBe(0);
    expect(
      refineCount({
        ...EMPTY_LOG_FILTERS,
        trigger: "manual",
        statuses: ["failed"],
        levels: ["error"],
      }),
    ).toBe(2);
  });
});

it("asks the server for the text without the white space around it, as the address holds it", () => {
  const query = logQuery(
    { ...EMPTY_LOG_FILTERS, text: "  disk full " },
    NOON,
    "UTC",
  );
  expect(query.q).toBe("disk full");
  expect(logQuery({ ...EMPTY_LOG_FILTERS, text: "   " }, NOON, "UTC")).toEqual(
    {},
  );
});
