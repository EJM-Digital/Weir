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
  EVENT_ROW,
  JOB_ROW,
  PENDING_JOB_ROW,
  SERVER_ROW,
  logPage,
  renderLog,
  stubEventSource,
} from "./log-test-support";

const mocks = vi.hoisted(() => ({
  fetchSystemLog: vi.fn(),
  fetchSystemLogExport: vi.fn(),
  fetchServerLogDownload: vi.fn(),
  fetchOperationalHistoryPreview: vi.fn(),
  resetOperationalHistory: vi.fn(),
  postCancel: vi.fn(),
  postRecover: vi.fn(),
  saveBlobAs: vi.fn(),
  role: { current: "operator" as string },
}));

vi.mock("../../../../lib/system/system-log-api", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/system/system-log-api")
  >()),
  fetchSystemLog: mocks.fetchSystemLog,
  fetchSystemLogExport: mocks.fetchSystemLogExport,
}));

vi.mock("../../../../lib/settings/queries", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/settings/queries")
  >()),
  useAppSettingsQuery: () => ({ data: { app_timezone: "UTC" } }),
}));

vi.mock("../../../../lib/settings/settings-api", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/settings/settings-api")
  >()),
  fetchServerLogDownload: mocks.fetchServerLogDownload,
  fetchOperationalHistoryPreview: mocks.fetchOperationalHistoryPreview,
  resetOperationalHistory: mocks.resetOperationalHistory,
}));

vi.mock(
  "../../../../lib/processing/jobs-inspection/api",
  async (importOriginal) => ({
    ...(await importOriginal<
      typeof import("../../../../lib/processing/jobs-inspection/api")
    >()),
    postProcessingJobCancelPending: mocks.postCancel,
    postProcessingJobRecoverFinalizeFailed: mocks.postRecover,
  }),
);

vi.mock("../../../../lib/ui/save-file", () => ({
  saveBlobAs: mocks.saveBlobAs,
}));

vi.mock("../../../../lib/processing/libraries-queries", () => ({
  useProcessingLibrariesQuery: () => ({ data: [{ id: 1, name: "Movies" }] }),
}));

vi.mock("../../../../lib/auth/queries", () => ({
  useMeQuery: () => ({
    data: { id: 1, username: "alice", role: mocks.role.current },
  }),
}));

vi.mock("../../../../lib/pause/pause-queries", () => ({
  usePauseQuery: () => ({ data: { paused: false } }),
}));

beforeAll(stubEventSource);

beforeEach(() => {
  mocks.role.current = "operator";
  mocks.fetchSystemLog.mockReset();
  mocks.fetchSystemLog.mockResolvedValue(
    logPage([EVENT_ROW, SERVER_ROW, JOB_ROW]),
  );
});

afterEach(() => {
  vi.clearAllMocks();
});

async function rendered() {
  const view = renderLog();
  await screen.findAllByTestId("log-row");
  return view;
}

/** Opens the row for the thing with this title. */
function open(title: string) {
  const row = screen.getByText(title).closest("[role=row]") as HTMLElement;
  fireEvent.click(within(row).getByRole("button", { expanded: false }));
  return row;
}

describe("an open row", () => {
  it("says what an event was, why it happened and how it went", async () => {
    await rendered();

    const row = open("Sign-in finished");

    expect(within(row).getByText("You started this")).toBeInTheDocument();
    expect(within(row).getByText("Finished")).toBeInTheDocument();
    expect(within(row).getByText("auth.login_succeeded")).toBeInTheDocument();
    expect(
      within(row).getByRole("button", { expanded: true }),
    ).toBeInTheDocument();
  });

  it("says what a job was, how often it was tried, what it was given and why it stopped", async () => {
    await rendered();

    const row = open("Couldn't finish this job for heat.mkv");

    expect(
      within(row).getByText("Couldn't finish this job for heat.mkv."),
    ).toBeInTheDocument();
    expect(
      within(row).getByText(/Read the error below, fix the cause/),
    ).toBeInTheDocument();
    expect(within(row).getByText("#7")).toBeInTheDocument();
    expect(within(row).getByText("3 of 3")).toBeInTheDocument();
    expect(within(row).getByText("remux:1:heat")).toBeInTheDocument();
    expect(
      within(row).getByText("ffmpeg stopped unexpectedly"),
    ).toBeInTheDocument();
    expect(
      within(row).getByText(/"relative_media_path": "Heat \(1995\)\/heat.mkv"/),
    ).toBeInTheDocument();
  });

  it("links a job's file to Activity", async () => {
    await rendered();

    const row = open("Couldn't finish this job for heat.mkv");

    expect(
      within(row).getByRole("link", { name: "Open the file in Activity →" }),
    ).toHaveAttribute("href", "/activity?q=heat.mkv&within=all&library=1");
  });

  it("says where a server line came from, and shows its exception", async () => {
    await rendered();

    const row = open("The backup folder is nearly full");

    expect(
      within(row).getByText("weir.platform.suite_settings.backups"),
    ).toBeInTheDocument();
    expect(within(row).getByText("req-1")).toBeInTheDocument();
    expect(
      within(row).getByText(/Not enough space\s+at Backup.Write\(\)/),
    ).toBeInTheDocument();
  });

  it("reads a server line with an exception as its sentence, and keeps the tool's text for the open row", async () => {
    const toolText =
      "Weir.Core.Media.MediaUnreadableException: [matroska,webm @ 000001534eaabd00] EBML header parsing failed";
    const sentence =
      "Reading Film.mkv failed. Weir couldn't read this file: it isn't a video Weir recognises, or it is damaged or incomplete.";
    mocks.fetchSystemLog.mockResolvedValue(
      logPage([
        {
          ...SERVER_ROW,
          title: sentence,
          server: {
            ...SERVER_ROW.server!,
            message: sentence,
            traceback: toolText,
          },
        },
      ]),
    );
    await rendered();

    const row = screen.getByText(sentence).closest("[role=row]") as HTMLElement;

    expect(within(row).queryByText(/matroska/)).not.toBeInTheDocument();
    fireEvent.click(within(row).getByRole("button", { expanded: false }));
    expect(
      within(row).getByText(/EBML header parsing failed/),
    ).toBeInTheDocument();
  });

  it("opens one row at a time", async () => {
    await rendered();

    const first = open("Sign-in finished");
    open("The backup folder is nearly full");

    expect(
      within(first).getByRole("button", { expanded: false }),
    ).toBeInTheDocument();
  });

  it("narrows the log to one job from the job, the server line that names it, and back", async () => {
    await rendered();
    open("Couldn't finish this job for heat.mkv");

    fireEvent.click(
      screen.getByRole("button", { name: "Everything about this job →" }),
    );

    await waitFor(() =>
      expect(mocks.fetchSystemLog).toHaveBeenLastCalledWith(
        expect.objectContaining({ job: 7 }),
      ),
    );
    expect(screen.getByTestId("logs-job-filter")).toHaveTextContent(
      "Everything about job #7",
    );
    fireEvent.click(screen.getByRole("button", { name: "Show all" }));
    await waitFor(() =>
      expect(screen.queryByTestId("logs-job-filter")).not.toBeInTheDocument(),
    );
  });

  it("offers the job an event names, and no job for one that names none", async () => {
    mocks.fetchSystemLog.mockResolvedValue(
      logPage([
        {
          ...EVENT_ROW,
          event: {
            ...EVENT_ROW.event!,
            detail: '{"job_id":7,"message":"Done."}',
          },
        },
        {
          ...EVENT_ROW,
          id: "event:42",
          title: "Sign-out finished",
          event: {
            ...EVENT_ROW.event!,
            id: 42,
            event_type: "auth.logout",
            title: "Sign-out finished",
            detail: "alice",
          },
        },
      ]),
    );
    await rendered();

    const plain = open("Sign-out finished");
    expect(
      within(plain).queryByRole("button", { name: /Everything about job/ }),
    ).not.toBeInTheDocument();
    const named = open("Sign-in finished");
    fireEvent.click(
      within(named).getByRole("button", {
        name: "Everything about job #7 →",
      }),
    );

    await waitFor(() =>
      expect(mocks.fetchSystemLog).toHaveBeenLastCalledWith(
        expect.objectContaining({ job: 7 }),
      ),
    );
    expect(screen.getByTestId("logs-job-filter")).toHaveTextContent(
      "Everything about job #7",
    );
  });

  it("offers the job a server line names", async () => {
    await rendered();

    const row = open("The backup folder is nearly full");

    expect(
      within(row).getByRole("button", { name: "Everything about this job →" }),
    ).toBeInTheDocument();
  });
});

describe("a job's actions", () => {
  beforeEach(() => {
    mocks.fetchSystemLog.mockResolvedValue(
      logPage([
        PENDING_JOB_ROW,
        {
          ...JOB_ROW,
          id: "job:9",
          level: "warning",
          title:
            "The work finished for heat.mkv, but its result could not be saved",
          job: { ...JOB_ROW.job!, id: 9, status: "handler_ok_finalize_failed" },
        },
        JOB_ROW,
      ]),
    );
  });

  it("can take a queued job back, and reads the log again afterwards", async () => {
    mocks.postCancel.mockResolvedValue({
      ok: true,
      job_id: 8,
      status: "cancelled",
    });
    await rendered();
    const row = open("Queued for alien.mkv");

    fireEvent.click(
      within(row).getByRole("button", { name: "Cancel pending" }),
    );

    await waitFor(() => expect(mocks.postCancel).toHaveBeenCalledWith(8));
    await waitFor(() =>
      expect(mocks.fetchSystemLog.mock.calls.length).toBeGreaterThan(1),
    );
  });

  it("can save the result of a job that finished its work but could not record it", async () => {
    mocks.postRecover.mockResolvedValue({
      ok: true,
      job_id: 9,
      status: "completed",
    });
    await rendered();
    const row = open(
      "The work finished for heat.mkv, but its result could not be saved",
    );

    fireEvent.click(
      within(row).getByRole("button", { name: "Recover result" }),
    );

    await waitFor(() => expect(mocks.postRecover).toHaveBeenCalledWith(9));
  });

  it("offers neither on a job that failed", async () => {
    await rendered();

    const failed = open("Couldn't finish this job for heat.mkv");

    expect(
      within(failed).queryByRole("button", { name: /Cancel|Recover/ }),
    ).not.toBeInTheDocument();
  });

  it("is not offered to a viewer", async () => {
    mocks.role.current = "viewer";
    await rendered();

    const row = open("Queued for alien.mkv");

    expect(
      within(row).queryByRole("button", { name: "Cancel pending" }),
    ).not.toBeInTheDocument();
  });

  it("says why a cancel did not work", async () => {
    mocks.postCancel.mockRejectedValue(
      new Error("Only pending jobs can be cancelled."),
    );
    await rendered();
    const row = open("Queued for alien.mkv");

    fireEvent.click(
      within(row).getByRole("button", { name: "Cancel pending" }),
    );

    expect(await within(row).findByRole("alert")).toHaveTextContent(/cancel/i);
  });
});
