import {
  cleanup,
  fireEvent,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
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
  NOW,
  chooseLevels,
  logPage,
  renderLog,
  stubEventSource,
} from "../../../../test/log-test-support";

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
  vi.useRealTimers();
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

describe("the list", () => {
  it("is one list of events, jobs and server lines under the day they fell on, newest first", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(NOW);
    await rendered();

    const rows = screen.getAllByTestId("log-row");
    expect(rows.map((row) => row.getAttribute("data-source"))).toEqual([
      "event",
      "server",
      "job",
    ]);
    expect(screen.getByText("Today")).toBeInTheDocument();
  });

  it("says in each row when, how it went, where it came from, what it is about, its workflow and what it was", async () => {
    await rendered();

    const [eventRow, serverRow, jobRow] = screen.getAllByTestId("log-row");
    expect(within(eventRow).getByText("11:50:00 am")).toBeInTheDocument();
    expect(
      within(eventRow).getByRole("img", { name: "Success" }),
    ).toBeInTheDocument();
    expect(within(eventRow).getByText("Event")).toBeInTheDocument();
    expect(within(eventRow).getByText("Sign-in")).toBeInTheDocument();
    expect(within(eventRow).getByText("Sign-in finished")).toBeInTheDocument();
    expect(
      within(serverRow).getByRole("img", { name: "Warning" }),
    ).toBeInTheDocument();
    expect(within(serverRow).getByText("Server")).toBeInTheDocument();
    expect(within(serverRow).getByText("Backups")).toBeInTheDocument();
    expect(
      within(jobRow).getByRole("img", { name: "Error" }),
    ).toBeInTheDocument();
    expect(within(jobRow).getByText("Movies")).toBeInTheDocument();
    expect(
      within(jobRow).getByText("Couldn't finish this job for heat.mkv"),
    ).toBeInTheDocument();
    expect(
      within(jobRow).getByText("Process a media file · attempt 3 of 3"),
    ).toBeInTheDocument();
  });

  it("opens a row to its detail and closes it again on a second press", async () => {
    await rendered();
    const head = () =>
      screen.getAllByRole("button", { name: /Sign-in finished/ })[0];

    fireEvent.click(head());
    expect(head()).toHaveAttribute("aria-expanded", "true");

    fireEvent.click(head());
    expect(head()).toHaveAttribute("aria-expanded", "false");
  });

  it("says how many entries there are and that it is live", async () => {
    await rendered();

    expect(screen.getByTestId("log-summary")).toHaveTextContent(
      "3 entries · live",
    );
  });

  it("says when nothing has been logged, and when nothing matches the filters", async () => {
    mocks.fetchSystemLog.mockResolvedValue(logPage([]));
    renderLog();
    expect(
      await screen.findByText("Nothing has been logged yet."),
    ).toBeInTheDocument();

    cleanup();
    renderLog("/system?tab=logs&level=error");
    expect(
      await screen.findByText("Nothing in the log matches these filters."),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Clear filters →" }));
    expect(
      await screen.findByText("Nothing has been logged yet."),
    ).toBeInTheDocument();
    expect(screen.getByTestId("location")).toHaveTextContent("?tab=logs");
  });
});

describe("an address from before the three lists became one log", () => {
  it.each([
    ["events", ["event"]],
    ["jobs", ["job"]],
    ["server", ["server"]],
    ["log", ["server"]],
  ])("lands show=%s on the %j source", async (show, source) => {
    await rendered(`/system?tab=logs&show=${show}`);

    expect(lastRequest()).toMatchObject({ source });
    expect(chip("Source", /All/)).toHaveAttribute("aria-pressed", "false");
  });

  it("keeps the job status an older link asked for", async () => {
    await rendered("/system?tab=logs&show=jobs&status=failed");

    expect(lastRequest()).toMatchObject({
      source: ["job"],
      status: ["failed"],
    });
    expect(chip("Source", /Jobs/)).toHaveAttribute("aria-pressed", "true");
  });

  it("is replaced by the source in the address once a filter is changed", async () => {
    await rendered("/system?tab=logs&show=jobs");

    chooseLevels(/Errors/);

    await waitFor(() =>
      expect(screen.getByTestId("location")).toHaveTextContent("source=job"),
    );
    expect(screen.getByTestId("location")).not.toHaveTextContent("show=");
  });
});
