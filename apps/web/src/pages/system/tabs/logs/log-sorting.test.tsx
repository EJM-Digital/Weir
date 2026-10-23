import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import {
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from "vitest";

import {
  ALL_ROWS,
  logPage,
  renderLog,
  stubEventSource,
} from "../../../../test/log-test-support";

const mocks = vi.hoisted(() => ({ fetchSystemLog: vi.fn() }));

vi.mock("../../../../lib/system/system-log-api", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/system/system-log-api")
  >()),
  fetchSystemLog: mocks.fetchSystemLog,
}));
vi.mock("../../../../lib/settings/queries", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/settings/queries")
  >()),
  useAppSettingsQuery: () => ({ data: { app_timezone: "UTC" } }),
}));
vi.mock("../../../../lib/processing/libraries-queries", () => ({
  useProcessingLibrariesQuery: () => ({ data: [{ id: 1, name: "Movies" }] }),
}));
vi.mock("../../../../lib/auth/queries", () => ({
  useMeQuery: () => ({ data: { id: 1, username: "alice", role: "operator" } }),
}));
vi.mock("../../../../lib/pause/pause-queries", () => ({
  usePauseQuery: () => ({ data: { paused: false } }),
}));

beforeAll(stubEventSource);

beforeEach(() => {
  localStorage.clear();
  mocks.fetchSystemLog.mockResolvedValue(logPage());
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function lastRequest(): Record<string, unknown> {
  return mocks.fetchSystemLog.mock.calls.at(-1)?.[0] as Record<string, unknown>;
}

async function rendered() {
  renderLog();
  await screen.findAllByTestId("log-row");
}

function logTable() {
  return screen.getByRole("table", { name: "Log" });
}

function headings() {
  return screen
    .getAllByRole("columnheader")
    .map((heading) => heading.textContent);
}

describe("the log's columns", () => {
  it("starts newest first with the time column marked, asking the server for no particular sort", async () => {
    await rendered();

    expect(screen.getByRole("columnheader", { name: "Time" })).toHaveAttribute(
      "aria-sort",
      "descending",
    );
    expect(lastRequest()).not.toHaveProperty("sort");
  });

  it("asks the server to sort when a heading is clicked, errors first for the level", async () => {
    await rendered();

    fireEvent.click(within(logTable()).getByRole("button", { name: "Level" }));

    await vi.waitFor(() =>
      expect(lastRequest()).toMatchObject({ sort: "level", direction: "asc" }),
    );
  });

  it("drops the day headings when the rows are not in the order of time", async () => {
    await rendered();
    expect(document.querySelector(".mm-log-day")).not.toBeNull();

    mocks.fetchSystemLog.mockResolvedValue(logPage([...ALL_ROWS].reverse()));
    fireEvent.click(
      within(logTable()).getByRole("button", { name: "Category" }),
    );

    await vi.waitFor(() =>
      expect(document.querySelector(".mm-log-day")).toBeNull(),
    );
    expect(screen.getAllByTestId("log-row")).toHaveLength(3);
  });

  it("puts each row's day before its time when the rows are not in the order of time, and only its time when they are", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-02T12:00:00Z"));
    await rendered();
    const times = () =>
      screen
        .getAllByTestId("log-row")
        .map((row) => row.querySelector('[data-col="time"]')?.textContent);
    expect(times()).toEqual(["11:50:00 am", "11:45:00 am", "11:30:00 am"]);

    mocks.fetchSystemLog.mockResolvedValue(logPage([...ALL_ROWS].reverse()));
    fireEvent.click(within(logTable()).getByRole("button", { name: "Level" }));

    await vi.waitFor(() =>
      expect(times()).toEqual([
        "Today 11:30:00 am",
        "Today 11:45:00 am",
        "Today 11:50:00 am",
      ]),
    );
    expect(logTable().style.getPropertyValue("--log-cols")).toMatch(
      /^10.25rem /,
    );
  });

  it("offers no sort on what a row says, but lets its column move", async () => {
    await rendered();

    expect(
      screen.queryByRole("button", { name: "What happened" }),
    ).not.toBeInTheDocument();
    const title = screen.getByRole("columnheader", { name: "What happened" });
    fireEvent.keyDown(title, { key: "ArrowLeft", altKey: true });

    expect(headings().indexOf("What happened")).toBe(4);
  });

  it("moves a row's cells with the column and still opens the row across its full width", async () => {
    await rendered();
    const time = screen.getByRole("columnheader", { name: "Time" });

    fireEvent.keyDown(time, { key: "ArrowRight", altKey: true });

    const [first] = screen.getAllByTestId("log-row");
    const cells = Array.from(
      first.querySelectorAll<HTMLElement>("[data-col]"),
    ).map((cell) => cell.dataset.col);
    expect(cells.slice(0, 2)).toEqual(["level", "time"]);

    fireEvent.click(within(first).getByRole("button"));
    expect(first.querySelector("[id^='log-row-']")).not.toBeNull();
  });

  it("puts the columns and the sort back with Reset columns", async () => {
    await rendered();
    fireEvent.click(within(logTable()).getByRole("button", { name: "Level" }));
    fireEvent.keyDown(screen.getByRole("columnheader", { name: "Time" }), {
      key: "ArrowRight",
      altKey: true,
    });

    fireEvent.click(screen.getByRole("button", { name: "Columns" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));

    expect(headings()[0]).toBe("Time");
    expect(screen.getByRole("columnheader", { name: "Time" })).toHaveAttribute(
      "aria-sort",
      "descending",
    );
    expect(
      screen.getByRole("columnheader", { name: "Level" }),
    ).not.toHaveAttribute("aria-sort");
  });
});
