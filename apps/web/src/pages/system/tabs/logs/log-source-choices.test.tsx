import { fireEvent, screen, waitFor } from "@testing-library/react";
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
  EVENT_ROW,
  JOB_ROW,
  SERVER_ROW,
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

/** What the server answers for each source, counted as it counts: only the rows of the sources asked for. */
const BY_SOURCE = {
  event: [EVENT_ROW],
  job: [JOB_ROW],
  server: [SERVER_ROW],
};

beforeEach(() => {
  mocks.fetchSystemLog.mockImplementation(
    async (request: { source?: (keyof typeof BY_SOURCE)[] }) => {
      const rows = (request.source ?? ["event", "job", "server"]).flatMap(
        (source) => BY_SOURCE[source],
      );
      const everything = logPage([EVENT_ROW, JOB_ROW, SERVER_ROW]);
      return {
        ...logPage(rows),
        counts: { ...logPage(rows).counts, source: everything.counts.source },
      };
    },
  );
});

afterEach(() => {
  vi.clearAllMocks();
});

function lastRequest(): Record<string, unknown> {
  return mocks.fetchSystemLog.mock.calls.at(-1)?.[0] as Record<string, unknown>;
}

async function rendered(address?: string) {
  renderLog(address);
  await screen.findAllByTestId("log-row");
}

function pickSource(name: RegExp) {
  fireEvent.click(screen.getByRole("button", { name, description: undefined }));
}

/** Waits until the list is no longer waiting on the answer to the last choice. */
async function answered() {
  await waitFor(() =>
    expect(screen.getByTestId("log-feed").closest("section")).toHaveAttribute(
      "aria-busy",
      "false",
    ),
  );
}

function optionsOf(testId: string): string[] {
  const picker = screen.getByTestId(testId);
  fireEvent.click(picker);
  const names = [...document.querySelectorAll('[role="option"]')].map(
    (option) => option.textContent ?? "",
  );
  fireEvent.click(picker);
  return names;
}

describe("the pickers follow the source", () => {
  it("hide the workflow picker for the server log, and bring it back for the others", async () => {
    await rendered();
    expect(screen.getByTestId("logs-workflow-picker")).toBeInTheDocument();

    pickSource(/^Server/);
    await waitFor(() =>
      expect(lastRequest()).toMatchObject({ source: ["server"] }),
    );
    expect(screen.queryByTestId("logs-workflow-picker")).toBeNull();

    pickSource(/^Jobs/);
    await waitFor(() =>
      expect(screen.getByTestId("logs-workflow-picker")).toBeInTheDocument(),
    );
  });

  it("let a chosen workflow go when the server log is picked", async () => {
    await rendered("/system?tab=logs&workflow=1");
    expect(lastRequest()).toMatchObject({ workflow: 1 });

    pickSource(/^Server/);

    await waitFor(() => expect(lastRequest()).not.toHaveProperty("workflow"));
    expect(lastRequest()).toMatchObject({ source: ["server"] });
    expect(screen.getByTestId("location")).not.toHaveTextContent("workflow");
  });

  it("offer only the categories and levels the source has rows for, and offer them again for another", async () => {
    await rendered();
    expect(optionsOf("logs-category-picker")).toEqual([
      "Processing · 1",
      "Backups · 1",
      "Sign-in · 1",
    ]);

    pickSource(/^Server/);
    await answered();
    expect(optionsOf("logs-category-picker")).toEqual(["Backups · 1"]);
    expect(optionsOf("logs-level-picker")).toEqual(["Warnings · 1"]);

    pickSource(/^All/);
    await answered();
    expect(optionsOf("logs-category-picker")).toHaveLength(3);
  });

  it("offer only the workflows that have rows", async () => {
    await rendered();

    expect(optionsOf("logs-workflow-picker")).toEqual([
      "All workflows",
      "Movies",
    ]);
  });

  it("drop a chosen category and level the new source has none of, and keep the rest", async () => {
    await rendered("/system?tab=logs&category=processing,sign_in&level=error");
    expect(lastRequest()).toMatchObject({
      category: ["processing", "sign_in"],
      level: ["error"],
    });

    pickSource(/^Events/);

    await waitFor(() =>
      expect(lastRequest()).toMatchObject({
        source: ["event"],
        category: ["sign_in"],
      }),
    );
    expect(lastRequest()).not.toHaveProperty("level");
    expect(screen.getByTestId("location")).toHaveTextContent(
      "source=event&category=sign_in",
    );
    expect(screen.getByTestId("location")).not.toHaveTextContent("level");
  });

  it("keep a choice an address asks for, even when nothing has it", async () => {
    mocks.fetchSystemLog.mockResolvedValue(logPage([]));
    renderLog("/system?tab=logs&source=server&category=processing");

    await waitFor(() =>
      expect(screen.getByTestId("log-empty")).toBeInTheDocument(),
    );
    expect(lastRequest()).toMatchObject({ category: ["processing"] });
  });
});
