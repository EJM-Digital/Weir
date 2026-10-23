import { fireEvent, screen, waitFor, within } from "@testing-library/react";
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
  elapse,
  useFakeClock,
  useRealClock,
} from "../../../../test/fake-clock";
import {
  JOB_ROW,
  NOW,
  chooseLevels,
  logPage,
  renderLog,
  stubEventSource,
} from "../../../../test/log-test-support";

/** How long typing must pause before the search is sent. */
const SEARCH_PAUSE_MS = 400;

const mocks = vi.hoisted(() => ({
  fetchSystemLog: vi.fn(),
}));

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
  useProcessingLibrariesQuery: () => ({
    data: [
      { id: 1, name: "Movies" },
      { id: 2, name: "TV" },
    ],
  }),
}));

vi.mock("../../../../lib/auth/queries", () => ({
  useMeQuery: () => ({ data: { id: 1, username: "alice", role: "operator" } }),
}));

vi.mock("../../../../lib/pause/pause-queries", () => ({
  usePauseQuery: () => ({ data: { paused: false } }),
}));

beforeAll(stubEventSource);

beforeEach(() => {
  mocks.fetchSystemLog.mockResolvedValue(logPage());
});

afterEach(() => {
  useRealClock();
  vi.clearAllMocks();
});

/** The filters the log last asked the server for. */
function lastRequest(): Record<string, unknown> {
  return mocks.fetchSystemLog.mock.calls.at(-1)?.[0] as Record<string, unknown>;
}

async function rendered(address?: string) {
  const view = renderLog(address);
  await screen.findAllByTestId("log-row");
  return view;
}

function chip(group: string, name: RegExp | string) {
  return within(screen.getByRole("group", { name: group })).getByRole(
    "button",
    { name },
  );
}

describe("the filters", () => {
  it("count what choosing each would show, and start with everything", async () => {
    await rendered();

    expect(lastRequest()).toEqual({ limit: 50 });
    expect(chip("Source", /All/)).toHaveAttribute("aria-pressed", "true");
    expect(chip("Source", /Events/)).toHaveTextContent("1");
    expect(chip("Source", /Jobs/)).toHaveTextContent("1");
    expect(chip("Source", /Server/)).toHaveTextContent("1");
    fireEvent.click(screen.getByTestId("logs-level-picker"));
    expect(screen.getByRole("option", { name: "Errors · 1" })).toBeVisible();
    expect(screen.getByRole("option", { name: "Warnings · 1" })).toBeVisible();
    // Information and successes are one choice: everything that is not a problem.
    expect(screen.getByRole("option", { name: "Info · 1" })).toBeVisible();
  });

  it("show a dot in each level's meaning beside its choice", async () => {
    await rendered();

    fireEvent.click(screen.getByTestId("logs-level-picker"));

    for (const [name, meaning] of [
      ["Errors · 1", "broken"],
      ["Warnings · 1", "attention"],
      ["Info · 1", "idle"],
    ]) {
      const dot = screen
        .getByRole("option", { name })
        .querySelector(".mm-status-dot");
      expect(dot).toHaveAttribute("data-status", meaning);
    }
  });

  it("choose one source at a time, and keep it when its chip is pressed again", async () => {
    await rendered();
    const pressed = () =>
      screen
        .getAllByRole("button", { pressed: true })
        .filter((button) => button.closest('[aria-label="Source"]'));

    fireEvent.click(chip("Source", /Jobs/));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ source: ["job"] }),
    );
    expect(screen.getByTestId("location")).toHaveTextContent("source=job");

    fireEvent.click(chip("Source", /Server/));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ source: ["server"] }),
    );
    expect(screen.getByTestId("location")).not.toHaveTextContent("job");
    expect(pressed()).toHaveLength(1);
    expect(chip("Source", /Server/)).toHaveAttribute("aria-pressed", "true");

    const requests = mocks.fetchSystemLog.mock.calls.length;
    fireEvent.click(chip("Source", /Server/));
    expect(chip("Source", /Server/)).toHaveAttribute("aria-pressed", "true");
    expect(mocks.fetchSystemLog.mock.calls.length).toBe(requests);

    fireEvent.click(chip("Source", /All/));
    await waitFor(() =>
      expect(chip("Source", /All/)).toHaveAttribute("aria-pressed", "true"),
    );
    expect(pressed()).toHaveLength(1);
    expect(screen.getByTestId("location")).toHaveTextContent("?tab=logs");
  });

  it("holds the list busy until the answer to a new filter arrives, so the last answer's rows do not pass for it", async () => {
    await rendered();
    const feed = () => screen.getByTestId("log-feed").closest("section");
    expect(feed()).toHaveAttribute("aria-busy", "false");

    let answer: (page: ReturnType<typeof logPage>) => void = () => undefined;
    mocks.fetchSystemLog.mockReturnValueOnce(
      new Promise((resolve) => {
        answer = resolve;
      }),
    );
    fireEvent.click(chip("Source", /Jobs/));

    await waitFor(() => expect(feed()).toHaveAttribute("aria-busy", "true"));
    expect(screen.getAllByTestId("log-row")).toHaveLength(3);

    answer(logPage([JOB_ROW]));
    await waitFor(() => expect(feed()).toHaveAttribute("aria-busy", "false"));
    expect(screen.getAllByTestId("log-row")).toHaveLength(1);
  });

  it("narrow to levels, the Info choice standing for information and successes together", async () => {
    await rendered();
    const picker = screen.getByTestId("logs-level-picker");
    expect(picker).toHaveTextContent("All levels");

    fireEvent.click(picker);
    fireEvent.click(screen.getByRole("option", { name: /^Errors/ }));
    expect(picker).toHaveTextContent("Errors");
    fireEvent.click(screen.getByRole("option", { name: /^Warnings/ }));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ level: ["error", "warning"] }),
    );
    expect(picker).toHaveTextContent("2 levels");

    fireEvent.click(screen.getByRole("option", { name: /^Errors/ }));
    fireEvent.click(screen.getByRole("option", { name: /^Info/ }));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({
        level: ["warning", "info", "success"],
      }),
    );
    fireEvent.click(screen.getByRole("option", { name: /^Info/ }));
    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("level=warning"),
    );
    expect(picker).toHaveTextContent("Warnings");
  });

  it("treat every level chosen as all levels", async () => {
    await rendered("/system?tab=logs&level=error,warning");
    const picker = screen.getByTestId("logs-level-picker");

    fireEvent.click(picker);
    fireEvent.click(screen.getByRole("option", { name: /^Info/ }));

    await waitFor(() => expect(lastRequest()).toEqual({ limit: 50 }));
    expect(picker).toHaveTextContent("All levels");
  });

  it("narrow to several categories at once, with how many rows each has", async () => {
    await rendered();

    fireEvent.click(screen.getByTestId("logs-category-picker"));
    fireEvent.click(screen.getByRole("option", { name: "Backups · 1" }));
    fireEvent.click(screen.getByRole("option", { name: "Processing · 1" }));

    await waitFor(() =>
      expect(lastRequest()).toMatchObject({
        category: ["processing", "backups"],
      }),
    );
    expect(screen.getByTestId("logs-category-picker")).toHaveTextContent(
      "2 categories",
    );
  });

  it("narrow to a workflow", async () => {
    await rendered();

    fireEvent.click(screen.getByTestId("logs-workflow-picker"));
    expect(screen.queryByRole("option", { name: "TV" })).toBeNull();
    fireEvent.click(screen.getByRole("option", { name: "Movies" }));

    await waitFor(() => expect(lastRequest()).toMatchObject({ workflow: 1 }));
    expect(screen.getByTestId("location")).toHaveTextContent("workflow=1");
  });

  it("narrow to a time counted back from when it was chosen", async () => {
    await rendered();
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(NOW);

    fireEvent.click(screen.getByTestId("logs-when-picker"));
    fireEvent.click(screen.getByRole("option", { name: "Last hour" }));

    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ from: "2026-10-02T11:00:00.000Z" }),
    );
    expect(screen.getByTestId("location")).toHaveTextContent("when=hour");
  });

  it("start today at midnight in Weir's time zone", async () => {
    await rendered();
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(NOW);

    fireEvent.click(screen.getByTestId("logs-when-picker"));
    fireEvent.click(screen.getByRole("option", { name: "Today" }));

    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ from: "2026-10-02T00:00:00.000Z" }),
    );
  });

  it("read a custom range from two dates the reader picks, in Weir's time zone", async () => {
    await rendered();

    fireEvent.click(screen.getByTestId("logs-when-picker"));
    fireEvent.click(screen.getByRole("option", { name: "Custom range" }));
    const range = await screen.findByTestId("logs-range");
    fireEvent.change(within(range).getByLabelText("From"), {
      target: { value: "2026-10-01T08:00" },
    });
    fireEvent.change(within(range).getByLabelText("To"), {
      target: { value: "2026-10-02T08:00" },
    });

    await waitFor(() =>
      expect(lastRequest()).toMatchObject({
        from: "2026-10-01T08:00:00.000Z",
        to: "2026-10-02T08:00:00.000Z",
      }),
    );
  });

  it("search once typing pauses, with no Apply", async () => {
    useFakeClock();
    await rendered();

    fireEvent.change(
      screen.getByRole("searchbox", { name: "Search the log" }),
      {
        target: { value: "disk full" },
      },
    );

    expect(lastRequest()).not.toHaveProperty("q");
    await elapse(SEARCH_PAUSE_MS - 1);
    expect(lastRequest()).not.toHaveProperty("q");
    await elapse(1);
    expect(lastRequest()).toMatchObject({ q: "disk full" });
    expect(screen.getByTestId("location")).toHaveTextContent("q=disk+full");
    expect(
      screen.queryByRole("button", { name: /apply/i }),
    ).not.toBeInTheDocument();
  });

  it("keep the filters in the address, and read them back from it", async () => {
    await rendered(
      "/system?tab=logs&source=job&level=error&category=processing&workflow=1&when=week&q=ffmpeg",
    );

    expect(lastRequest()).toMatchObject({
      source: ["job"],
      level: ["error"],
      category: ["processing"],
      workflow: 1,
      q: "ffmpeg",
    });
    expect(lastRequest()).toHaveProperty("from");
    expect(chip("Source", /Jobs/)).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByTestId("logs-level-picker")).toHaveTextContent("Errors");
    expect(
      screen.getByRole("searchbox", { name: "Search the log" }),
    ).toHaveValue("ffmpeg");
  });

  it("keep the tab in the address while they change", async () => {
    await rendered();

    chooseLevels(/^Errors/);

    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("tab=logs"),
    );
  });

  it("can be cleared in one press", async () => {
    await rendered("/system?tab=logs&source=job&level=error");

    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));

    await waitFor(() => expect(lastRequest()).toEqual({ limit: 50 }));
    expect(screen.getByTestId("location")).toHaveTextContent("?tab=logs");
  });
});

describe("the filters only some rows have", () => {
  it("are in the Refine row: an event's type, result and cause, a job's status and a server line's stack trace", async () => {
    await rendered();

    fireEvent.click(screen.getByRole("button", { name: "Refine" }));
    fireEvent.click(
      within(screen.getByTestId("logs-event-type")).getByRole("button"),
    );
    fireEvent.click(screen.getByRole("option", { name: "Sign-in finished" }));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({
        event_type: "auth.login_succeeded",
      }),
    );

    fireEvent.click(screen.getByRole("button", { name: "Result" }));
    fireEvent.click(screen.getByRole("option", { name: "Failed" }));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ result: "failed" }),
    );

    fireEvent.click(screen.getByRole("button", { name: "Why it happened" }));
    fireEvent.click(screen.getByRole("option", { name: "Schedule" }));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ trigger: "scheduled" }),
    );

    fireEvent.click(
      within(screen.getByTestId("logs-job-status")).getByRole("button"),
    );
    fireEvent.click(screen.getByRole("option", { name: "Failed" }));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ status: ["failed"] }),
    );

    fireEvent.click(screen.getByRole("button", { name: "Stack traces only" }));
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ has_exception: true }),
    );
    expect(
      screen.getByRole("button", { name: "Refine · 5" }),
    ).toBeInTheDocument();
  });

  it("show their count on Refine and stay open while any is set", async () => {
    await rendered("/system?tab=logs&trigger=manual");

    expect(
      screen.getByRole("button", { name: "Refine · 1" }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("logs-refine")).toBeInTheDocument();
  });
});
