import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type {
  ProcessingFile,
  ProcessingFileLog,
} from "../../lib/processing/files-api";
import type { LiveProgressEntry } from "../../lib/activity/use-activity-stream-invalidation";
import type { KeptFile } from "../../lib/processing/kept-files-api";
import type { LibraryClean } from "../../lib/processing/library-cleans-api";
import { activityEntries, type ActivityEntry } from "./activity-entries";
import { ActivityPage } from "./activity-page";
import { activityFilePath } from "./activity-links";

const files: {
  files: ProcessingFile[];
  status_counts: Record<string, number>;
} = { files: [], status_counts: {} };
const cleans: { cleans: LibraryClean[] } = { cleans: [] };
const kept: { files: KeptFile[] } = { files: [] };
const filesQueryState: {
  isLoading: boolean;
  isError: boolean;
  error: unknown;
} = { isLoading: false, isError: false, error: null };
const keptQueryState: {
  isLoading: boolean;
  isError: boolean;
  error: unknown;
} = { isLoading: false, isError: false, error: null };
const attention: { entries: ActivityEntry[]; count: number } = {
  entries: [],
  count: 0,
};
const me = { role: "admin" };
const liveProgress: Record<string, LiveProgressEntry> = {};
const requeue = vi.fn();
const requeueFiles = vi.fn();
const processNow = vi.fn();
const processKeptAgain = vi.fn();
const fetchLog = vi.fn<(id: number) => Promise<ProcessingFileLog>>();

vi.mock(
  "../../lib/activity/use-activity-stream-invalidation",
  async (importOriginal) => ({
    ...(await importOriginal<
      typeof import("../../lib/activity/use-activity-stream-invalidation")
    >()),
    useLiveProgress: () => liveProgress,
  }),
);
vi.mock("../../lib/processing/files-queries", async (importOriginal) => {
  const mutation = (fn = vi.fn()) => ({
    mutateAsync: fn,
    mutate: (
      input: unknown,
      opts?: { onSuccess?: (r: unknown) => void; onError?: () => void },
    ) => void Promise.resolve(fn(input)).then(opts?.onSuccess, opts?.onError),
    isPending: false,
  });
  return {
    ...(await importOriginal<
      typeof import("../../lib/processing/files-queries")
    >()),
    useFileHistoryQuery: () => ({
      data: files,
      isLoading: filesQueryState.isLoading,
      isError: filesQueryState.isError,
      error: filesQueryState.error,
      refetch: vi.fn(),
    }),
    useLibraryCleansQuery: () => ({
      data: {
        cleans: cleans.cleans,
        returned: cleans.cleans.length,
        limit: 200,
      },
      isLoading: false,
      isError: false,
    }),
    useRequeueProcessingFiles: () => mutation(requeueFiles),
    useRequeueProcessingFile: () => mutation(requeue),
    useForgetProcessingFile: () => mutation(),
    useProcessingFileRemoveOptions: () => mutation(),
    useMoveProcessingFileToTop: () => mutation(),
    useProcessingWhyHeld: () => mutation(),
    useProcessProcessingFileNow: () => mutation(processNow),
    useProcessingCheckLibraryAgain: () => mutation(),
    useProcessingFileTracks: () => mutation(),
    useSubmitProcessingManualPlan: () => mutation(),
  };
});
const libraries = [
  { id: 1, name: "TV", media_type: "tv", manager_connection_ids: [1] },
];
vi.mock("../../lib/processing/use-handed-off-note", () => ({
  useHandedOffNote: () => null,
}));
vi.mock("../../lib/processing/libraries-queries", () => ({
  useProcessingLibrariesQuery: () => ({ data: libraries }),
}));
vi.mock("../../lib/processing/kept-files-queries", () => ({
  useKeptFilesQuery: () => ({
    data: kept,
    isLoading: keptQueryState.isLoading,
    isPending: keptQueryState.isLoading,
    isError: keptQueryState.isError,
    error: keptQueryState.error,
  }),
  useProcessKeptFileAgain: () => ({
    mutate: (
      input: number,
      opts?: {
        onSuccess?: (r: unknown) => void;
        onError?: (e: unknown) => void;
      },
    ) =>
      void Promise.resolve(processKeptAgain(input)).then(
        opts?.onSuccess,
        opts?.onError,
      ),
    isPending: false,
  }),
}));
vi.mock("../../lib/auth/queries", () => ({
  useMeQuery: () => ({ data: me }),
}));
vi.mock("./use-activity-attention", () => ({
  useActivityAttention: () => attention,
}));
vi.mock("./activity-retention-note", () => ({
  ActivityRetentionNote: () => <div data-testid="activity-retention" />,
}));
vi.mock("./activity-rejected-again", () => ({
  ProcessRejectedAgain: ({
    libraryId,
    libraryName,
  }: {
    libraryId: number | undefined;
    libraryName: string | undefined;
  }) => (
    <div data-testid="process-rejected-again">
      {libraryId ?? "all"}|{libraryName ?? "no name"}
    </div>
  ),
}));
vi.mock("../../lib/pause/pause-queries", () => ({
  usePauseQuery: () => ({ data: { paused: false } }),
}));
vi.mock("../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => ({ data: undefined }),
}));
vi.mock("../../lib/processing/files-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/processing/files-api")>()),
  fetchProcessingFileLog: (id: number) => fetchLog(id),
}));

function file(partial: Partial<ProcessingFile>): ProcessingFile {
  return {
    id: 1,
    library_id: 1,
    library_name: "TV",
    relative_path: "Northbound/Northbound.S08E09.mkv",
    status: "processed",
    status_reason: "Finished processing this file.",
    blocked_by_connection: null,
    size_bytes: 4_000_000_000,
    failure_class: null,
    failure_attempts: 0,
    next_retry_at: null,
    output_collision_policy: null,
    output_collision_action: null,
    output_collision_reason: null,
    video_width: null,
    video_height: null,
    video_codec: null,
    audio_track_count: null,
    subtitle_track_count: null,
    duration_seconds: null,
    direct_play: [],
    progress_percent: null,
    progress_message: null,
    progress_eta_seconds: null,
    hold_until: null,
    size_changed_at: null,
    created_at: "2026-08-19T04:00:00",
    updated_at: "2026-08-19T04:00:00",
    last_seen_at: null,
    last_attempt_at: null,
    ...partial,
  };
}

function renderPage(entry = "/activity") {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const page = () => (
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[entry]}>
        <ActivityPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
  const view = render(page());
  /** Draws the page again, as it is when the list it reads has changed. */
  return { ...view, refresh: () => view.rerender(page()) };
}

describe("ActivityPage", () => {
  beforeEach(() => {
    requeue.mockReset();
    me.role = "admin";
    requeueFiles.mockReset();
    requeueFiles.mockResolvedValue({
      requeued: 2,
      skipped: 0,
      detail: "Queued 2 files again.",
    });
    processNow.mockReset();
    processKeptAgain.mockReset();
    fetchLog.mockReset();
    cleans.cleans = [];
    kept.files = [];
    for (const path of Object.keys(liveProgress)) delete liveProgress[path];
    attention.entries = [];
    attention.count = 0;
    filesQueryState.isLoading = false;
    filesQueryState.isError = false;
    filesQueryState.error = null;
    keptQueryState.isLoading = false;
    keptQueryState.isError = false;
    keptQueryState.error = null;
    files.files = [
      file({ id: 1 }),
      file({
        id: 2,
        relative_path: "Glass Orchard/Glass.Orchard.S03E03.mkv",
        status: "processing_failed",
        status_reason: "The download ended early.",
        updated_at: "2026-08-19T03:00:00",
      }),
    ];
  });

  describe("with no file to list", () => {
    /** The card the header's pickers fold into: it must be there, with its header, so the title line folds the same. */
    function expectEmptyFilesCard(words: string) {
      const card = screen.getByRole("region", { name: "Files" });
      expect(within(card).getByText("0 shown")).toBeInTheDocument();
      expect(within(card).getByText(words)).toBeInTheDocument();
    }

    it("keeps the Files card when nothing has been handled yet", () => {
      files.files = [];
      renderPage();

      expectEmptyFilesCard(
        "Nothing yet. Every file Weir picks up will be listed here, from the moment it arrives.",
      );
      expect(screen.getByTestId("activity-retention")).toBeInTheDocument();
    });

    it("keeps the Files card when the filter matches nothing", () => {
      renderPage("/activity?show=skipped");

      expectEmptyFilesCard("No file matches. Try All, or look further back.");
    });

    it("keeps the Files card in the Needs you view with nothing waiting", () => {
      renderPage("/activity?show=attention");

      expectEmptyFilesCard("No file is waiting on you.");
    });
  });

  it("says its file activity could not load, through the shared load-error wording", () => {
    filesQueryState.isError = true;
    filesQueryState.error = new Error("boom");
    renderPage();

    expect(
      screen.getByText(
        "Weir couldn't load your file activity. Reload the page to try again.",
      ),
    ).toBeInTheDocument();
  });

  it("lands on the file a link from Needs you names, in its group, though it is not the newest there", () => {
    const held = file({
      id: 7,
      relative_path: "Held/The.Held.One.mkv",
      status: "on_hold",
      status_reason: "Waiting for the file to finish being written.",
      updated_at: "2026-07-01T03:00:00",
    });
    files.files = [
      file({
        id: 8,
        relative_path: "Held/Newer.Held.mkv",
        status: "on_hold",
        updated_at: "2026-08-19T03:00:00",
      }),
      held,
    ];

    renderPage(activityFilePath(held));

    expect(screen.getByTestId("activity-detail")).toHaveTextContent(
      "The.Held.One.mkv",
    );
  });

  it("shows a running pass's percent from the live stream, with no new read of the list", () => {
    const path = "Glass Orchard/Glass.Orchard.S03E04.mkv";
    files.files = [
      file({
        id: 3,
        relative_path: path,
        status: "processing",
        progress_percent: 5,
      }),
    ];
    liveProgress[path] = {
      relativePath: path,
      status: "processing",
      stage: null,
      percent: 42.4,
      etaSeconds: null,
      message: null,
      speed: null,
      elapsedSeconds: null,
      removedAudio: [],
      removedSubtitles: [],
    };

    renderPage();

    const [row] = screen.getAllByRole("row").slice(1);
    expect(within(row).getByText("Writing · 42%")).toBeInTheDocument();
  });

  it("ends the page with how long file activity is kept", () => {
    renderPage();
    expect(screen.getByTestId("activity-retention")).toBeInTheDocument();
  });

  it("tells, under a file, how long its activity is kept once the file is gone", async () => {
    fetchLog.mockResolvedValue({
      file_id: 1,
      relative_path: "",
      retention_days: 45,
      entries: [],
    });
    renderPage("/activity?file=1");

    expect(
      await screen.findByTestId("activity-retention-note"),
    ).toHaveTextContent("Kept while the file exists, then 45 days.");
  });

  it("says a file's activity is kept until it is removed when the days are 0", async () => {
    fetchLog.mockResolvedValue({
      file_id: 1,
      relative_path: "",
      retention_days: 0,
      entries: [],
    });
    renderPage("/activity?file=1");

    expect(
      await screen.findByTestId("activity-retention-note"),
    ).toHaveTextContent("Kept until you remove it.");
  });

  it("counts every file under the chip it belongs to", () => {
    fetchLog.mockResolvedValue({
      file_id: 1,
      relative_path: "",
      retention_days: 90,
      entries: [],
    });
    renderPage();
    const chips = screen.getByRole("group", { name: "Show" });
    expect(
      within(chips).getByRole("button", { name: /All\s*2/ }),
    ).toHaveAttribute("aria-pressed", "true");
    expect(
      within(chips).getByRole("button", { name: /Finished\s*1/ }),
    ).toBeInTheDocument();
    expect(
      within(chips).getByRole("button", { name: /Failed\s*1/ }),
    ).toBeInTheDocument();
  });

  it("counts a cleaned copy no media manager has taken yet under In progress, not Finished and not Needs you", () => {
    files.files = [
      file({
        id: 3,
        status: "processed",
        handback: {
          output_path: "/hand-back/a.mkv",
          written_at: "2026-08-19T03:00:00",
          outcome: null,
          outcome_by: null,
          outcome_at: null,
          imported_path: null,
          outcome_reason: null,
          released_at: null,
          settled_at: null,
          release_note: null,
        },
      }),
    ];
    renderPage();

    const chips = screen.getByRole("group", { name: "Show" });
    expect(
      within(chips).getByRole("button", { name: /All\s*1/ }),
    ).toBeInTheDocument();
    expect(
      within(chips).getByRole("button", { name: /In progress\s*1/ }),
    ).toBeInTheDocument();
    expect(
      within(chips).getByRole("button", { name: /Finished\s*0/ }),
    ).toBeInTheDocument();
    expect(
      within(chips).getByRole("button", { name: /Needs you\s*0/ }),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Waiting for your media manager"),
    ).toBeInTheDocument();
  });

  describe("the Needs you view, which is what the sidebar's badge counts", () => {
    const chips = () => screen.getByRole("group", { name: "Show" });

    it("has a chip between Finished and On hold, counting what the badge counts", () => {
      attention.count = 7;
      renderPage();

      const names = within(chips())
        .getAllByRole("button")
        .map((chip) => chip.textContent?.replace(/\s+/g, " ").trim());

      expect(names).toEqual([
        "All 2",
        "In progress 0",
        "Finished 1",
        "Needs you 7",
        "On hold 0",
        "Skipped 0",
        "Failed 1",
        "Kept 0",
      ]);
    });

    it("lists the files waiting on a person, whatever their age", () => {
      const waiting = file({
        id: 40,
        relative_path: "Old/Old.And.Stuck.mkv",
        status: "on_hold",
        updated_at: "2025-01-01T03:00:00",
      });
      attention.entries = activityEntries([waiting], []);
      attention.count = 1;
      renderPage("/activity?show=attention");

      expect(
        within(chips()).getByRole("button", { name: /Needs you\s*1/ }),
      ).toHaveAttribute("aria-pressed", "true");
      expect(screen.getByTestId("activity-detail")).toHaveTextContent(
        "Old.And.Stuck.mkv",
      );
    });

    it("says no file is waiting when none is", () => {
      renderPage("/activity?show=attention");

      expect(
        screen.getByText("No file is waiting on you."),
      ).toBeInTheDocument();
    });
  });

  it("shows what the open file kept and removed, and why", async () => {
    fetchLog.mockResolvedValue({
      file_id: 1,
      relative_path: "Northbound/Northbound.S08E09.mkv",
      retention_days: 90,
      entries: [
        {
          id: 9,
          recorded_at: "2026-08-19T04:00:00",
          outcome: "live_output_written",
          title: "",
          library_name: "TV",
          detail: {
            outcome: "live_output_written",
            audio_after: "English 5.1 E-AC-3",
            removed_audio: [
              "English 2.0 (commentary excluded — remove commentary enabled)",
            ],
            subs_after: "English",
            removed_subtitles: ["spa"],
            source_size_bytes: 4_000_000_000,
            output_size_bytes: 3_500_000_000,
          },
          story: [
            {
              heading: "Handed back",
              sentence: "Written for Sonarr.",
              tone: "good",
            },
          ],
        },
      ],
    });
    renderPage("/activity?file=1");
    const detail = await screen.findByTestId("activity-detail");
    const trackset = await within(detail).findByTestId("activity-tracks");
    expect(within(trackset).getByText("2 kept")).toBeInTheDocument();
    expect(within(trackset).getByText("2 removed")).toBeInTheDocument();
    expect(within(trackset).getAllByText("Removed")).toHaveLength(2);
    expect(
      within(detail).getByText(
        "Commentary excluded — remove commentary enabled",
      ),
    ).toBeInTheDocument();
    expect(within(detail).getByText("Spanish")).toBeInTheDocument();
    expect(within(detail).getByText("477 MB")).toBeInTheDocument();
    expect(within(detail).getByText("Handed back")).toBeInTheDocument();
  });

  describe("the copy Weir handed back", () => {
    const handback = {
      output_path: "/ready/tv/Northbound.S08E09.mkv",
      written_at: "2026-08-19T04:00:00",
      outcome: null,
      outcome_by: null,
      outcome_at: null,
      imported_path: null,
      outcome_reason: null,
      released_at: null,
      settled_at: null,
      release_note: null,
    };
    const originalLinks = libraries[0].manager_connection_ids;

    beforeEach(() => {
      fetchLog.mockResolvedValue({
        file_id: 1,
        relative_path: "",
        retention_days: 90,
        entries: [],
      });
      files.files = [file({ id: 1, handback })];
    });
    afterEach(() => {
      libraries[0].manager_connection_ids = originalLinks;
    });

    it("says a linked workflow's copy is waiting for its media manager to import it", async () => {
      renderPage("/activity?file=1");

      expect(await screen.findByTestId("activity-handback")).toHaveTextContent(
        "for your media manager to import",
      );
    });

    it("says a Weir-only workflow's copy is in the output folder, with nothing left to wait for", async () => {
      libraries[0].manager_connection_ids = [];
      renderPage("/activity?file=1");

      const story = await screen.findByTestId("activity-handback");
      expect(story).toHaveTextContent("Cleaned copy");
      expect(story).toHaveTextContent(
        "The cleaned copy is in the output folder, at /ready/tv/Northbound.S08E09.mkv.",
      );
      expect(story).not.toHaveTextContent(/import|media manager|yet/i);
    });
  });

  it("says a file's record could not load, through the shared load-error wording", async () => {
    fetchLog.mockRejectedValue(new Error("boom"));
    renderPage("/activity?file=1");
    const detail = await screen.findByTestId("activity-detail");

    expect(
      await within(detail).findByText(
        "Weir couldn't load this file's record. Reload the page to try again.",
      ),
    ).toBeInTheDocument();
  });

  it("offers Try again on a failed file and says what the server answered", async () => {
    fetchLog.mockResolvedValue({
      file_id: 2,
      relative_path: "",
      retention_days: 90,
      entries: [],
    });
    requeue.mockResolvedValue({ detail: "Queued again." });
    renderPage("/activity?file=2");
    const detail = screen.getByTestId("activity-detail");
    expect(
      within(detail).getByText("Weir gave up on this file."),
    ).toBeInTheDocument();
    fireEvent.click(within(detail).getByRole("button", { name: "Try again" }));
    expect(
      await within(detail).findByText("Queued again."),
    ).toBeInTheDocument();
    expect(requeue).toHaveBeenCalledWith(2);
  });

  it("says Weir will try a failed file again when a retry is owed, and that it gave up when none is", async () => {
    fetchLog.mockResolvedValue({
      file_id: 2,
      relative_path: "",
      retention_days: 90,
      entries: [],
    });
    files.files = [
      file({
        id: 2,
        status: "processing_failed",
        status_reason: "The download ended early.",
        failure_attempts: 1,
        next_retry_at: "2026-08-19T04:05:00",
      }),
    ];
    renderPage("/activity?file=2");

    const detail = screen.getByTestId("activity-detail");
    expect(
      within(detail).getByText("This attempt failed, and Weir will try again."),
    ).toBeInTheDocument();
    expect(
      within(detail).queryByText("Weir gave up on this file."),
    ).not.toBeInTheDocument();
  });

  it("asks before passing a file through unchanged, and does nothing when told not now", async () => {
    fetchLog.mockResolvedValue({
      file_id: 2,
      relative_path: "",
      retention_days: 90,
      entries: [],
    });
    processNow.mockResolvedValue({});
    renderPage("/activity?file=2");
    const detail = screen.getByTestId("activity-detail");
    const passThrough = within(detail).getByRole("button", {
      name: "Pass through unchanged",
    });

    fireEvent.click(passThrough);
    fireEvent.click(screen.getByRole("button", { name: "Not now" }));
    expect(processNow).not.toHaveBeenCalled();

    fireEvent.click(passThrough);
    const dialog = screen.getByRole("dialog", {
      name: "Pass this file through unchanged?",
    });
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Pass through unchanged" }),
    );

    expect(
      await within(detail).findByText(
        "Queued to pass through unchanged. Weir checks the copy before removing the original.",
      ),
    ).toBeInTheDocument();
    expect(processNow).toHaveBeenCalledWith(
      expect.objectContaining({ library_id: 1, pass_through_unchanged: true }),
    );
  });

  it("offers Process again on a file Weir is done with", async () => {
    files.files = [file({ id: 1, status: "cancelled" })];
    fetchLog.mockResolvedValue({
      file_id: 1,
      relative_path: "",
      retention_days: 90,
      entries: [],
    });
    requeue.mockResolvedValue({ detail: "Queued 1 file again." });
    renderPage("/activity?file=1");
    const detail = screen.getByTestId("activity-detail");

    fireEvent.click(
      within(detail).getByRole("button", { name: "Process again" }),
    );

    expect(
      await within(detail).findByText("Queued 1 file again."),
    ).toBeInTheDocument();
    expect(requeue).toHaveBeenCalledWith(1);
  });

  it("acts on exactly the failed files shown, not everything matching the filter", () => {
    files.files = [
      file({ id: 1, status: "processing_failed" }),
      file({ id: 2, status: "processing_failed" }),
      file({ id: 3, status: "skipped" }),
    ];
    renderPage("/activity?show=failed");

    const retryAll = screen.getByRole("button", {
      name: "Try the 2 failed files again",
    });
    fireEvent.click(retryAll);

    expect(requeueFiles).toHaveBeenCalledWith({ file_ids: [1, 2] });
  });

  it("keeps what the server answered after the last failed file is queued and has left the list", async () => {
    files.files = [file({ id: 1, status: "processing_failed" })];
    requeueFiles.mockResolvedValue({
      requeued: 1,
      skipped: 0,
      detail: "Queued 1 file again.",
    });
    const view = renderPage("/activity?show=failed");

    fireEvent.click(
      screen.getByRole("button", { name: "Try the 1 failed file again" }),
    );
    expect(await screen.findByText("Queued 1 file again.")).toBeInTheDocument();
    files.files = [file({ id: 1, status: "unprocessed" })];
    view.refresh();

    expect(screen.getByText("Queued 1 file again.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Try the/ })).toBeNull();
  });

  it("offers Process all again under Failed when a rejected file is listed", () => {
    files.files = [file({ id: 1, status: "rejected" })];
    renderPage("/activity?show=failed");

    expect(screen.getByTestId("process-rejected-again")).toHaveTextContent(
      "all|no name",
    );
  });

  it("hands Process all again the workflow Activity is narrowed to", () => {
    files.files = [file({ id: 1, status: "rejected" })];
    renderPage("/activity?show=failed&library=1");

    expect(screen.getByTestId("process-rejected-again")).toHaveTextContent(
      "1|TV",
    );
  });

  it("does not offer Process all again when only failed files are listed", () => {
    files.files = [file({ id: 1, status: "processing_failed" })];
    renderPage("/activity?show=failed");

    expect(
      screen.queryByTestId("process-rejected-again"),
    ).not.toBeInTheDocument();
  });

  it("does not offer Process all again outside the Failed group", () => {
    files.files = [file({ id: 1, status: "rejected" })];
    renderPage();

    expect(
      screen.queryByTestId("process-rejected-again"),
    ).not.toBeInTheDocument();
  });

  it("does not offer Process all again to someone who cannot edit", () => {
    me.role = "viewer";
    files.files = [file({ id: 1, status: "rejected" })];
    renderPage("/activity?show=failed");

    expect(
      screen.queryByTestId("process-rejected-again"),
    ).not.toBeInTheDocument();
  });

  it("keeps a skip out of Failed and gives it its own neutral group", () => {
    files.files = [file({ id: 1, status: "skipped" })];
    renderPage();

    const chips = screen.getByRole("group", { name: "Show" });
    expect(
      within(chips).getByRole("button", { name: /Skipped\s*1/ }),
    ).toBeInTheDocument();
    expect(
      within(chips).getByRole("button", { name: /Failed\s*0/ }),
    ).toBeInTheDocument();
  });

  it("lists a file the rules rejected under Failed, as rejected rather than skipped", () => {
    files.files = [
      file({
        id: 1,
        status: "rejected",
        failure_class: "rules",
        status_reason:
          "Rejected: none of its audio tracks are in English, and your rules keep only English audio, so there would be nothing to keep. The file was left where it is.",
      }),
    ];
    renderPage();

    const chips = screen.getByRole("group", { name: "Show" });
    expect(
      within(chips).getByRole("button", { name: /Failed\s*1/ }),
    ).toBeInTheDocument();
    expect(
      within(chips).getByRole("button", { name: /Skipped\s*0/ }),
    ).toBeInTheDocument();
    expect(screen.getAllByText("Rejected").length).toBeGreaterThan(0);
  });

  it("opens a rules rejection's detail with its reason once, not after a second 'Rejected'", () => {
    const reason =
      'Rejected: none of its audio tracks are in English, and the "Movies" rules keep only English audio, so there would be nothing to keep. The file was left where it is.';
    files.files = [
      file({
        id: 1,
        status: "rejected",
        failure_class: "rules",
        status_reason: reason,
      }),
    ];
    fetchLog.mockResolvedValue({
      file_id: 1,
      relative_path: "",
      retention_days: 90,
      entries: [],
    });
    renderPage("/activity?file=1");

    const lead = within(screen.getByTestId("activity-detail")).getByText(
      (_, element) => element?.className === "mm-history-detail__lead",
    );
    expect(lead.textContent).toBe(reason);
  });

  it("tells a rules rejection's timeline step as Rejected, with its reason", async () => {
    files.files = [
      file({
        id: 1,
        status: "rejected",
        failure_class: "rules",
        status_reason: "Rejected: it has no audio tracks.",
      }),
    ];
    fetchLog.mockResolvedValue({
      file_id: 1,
      relative_path: "",
      retention_days: 90,
      entries: [
        {
          id: 3,
          recorded_at: "2026-08-19T04:00:00",
          outcome: "failed_before_execution",
          title: "Rejected film.mkv",
          library_name: "Movies",
          detail: {
            outcome: "failed_before_execution",
            rejected_without_manager: true,
          },
          story: [
            {
              heading: "Rejected",
              sentence:
                "This file has no audio tracks, so there would be nothing to keep. The file was left where it is.",
              tone: "warn",
            },
          ],
        },
      ],
    });
    renderPage("/activity?file=1");

    const story = await screen.findByRole("list", { name: "What happened" });
    expect(within(story).getByText("Rejected")).toBeInTheDocument();
    expect(
      within(story).getByText(/The file was left where it is\./),
    ).toBeInTheDocument();
    expect(within(story).queryByText("Could not finish")).toBeNull();
  });

  it("opens a manager rejection's detail with its label, then its reason", () => {
    files.files = [
      file({
        id: 1,
        status: "rejected",
        failure_class: "preflight",
        status_reason: "Deluno removed the download.",
      }),
    ];
    fetchLog.mockResolvedValue({
      file_id: 1,
      relative_path: "",
      retention_days: 90,
      entries: [],
    });
    renderPage("/activity?file=1");

    const lead = within(screen.getByTestId("activity-detail")).getByText(
      (_, element) => element?.className === "mm-history-detail__lead",
    );
    expect(lead.textContent).toBe(
      "Rejected for a replacement. Deluno removed the download.",
    );
  });

  it("says a file was rejected for a replacement when a media manager was asked for another copy", () => {
    files.files = [
      file({ id: 1, status: "rejected", failure_class: "preflight" }),
    ];
    renderPage();

    expect(screen.getByText("Rejected for a replacement")).toBeInTheDocument();
  });

  it("lists a library clean alongside downloads, as its own kind of entry", () => {
    files.files = [];
    cleans.cleans = [
      {
        kind: "library_clean",
        id: 5,
        library_id: 2,
        library_name: "Films",
        relative_path: "Heat (1995)/Heat.mkv",
        outcome: "cleaned",
        detail: "Cleaned Heat.mkv: removed 1 audio track.",
        trigger: null,
        recorded_at: "2026-08-19T04:00:00",
      },
    ];
    renderPage();

    expect(screen.getAllByText("Heat.mkv").length).toBeGreaterThan(0);
  });

  it("opens a library clean from the list, and a download after it, each in place of the other", () => {
    files.files = [file({ id: 1 })];
    cleans.cleans = [
      {
        kind: "library_clean",
        id: 5,
        library_id: 2,
        library_name: "Films",
        relative_path: "Heat (1995)/Heat.mkv",
        outcome: "cleaned",
        detail: "Cleaned Heat.mkv: removed 1 audio track.",
        trigger: null,
        recorded_at: "2026-08-19T04:00:00",
      },
    ];
    renderPage();

    fireEvent.click(screen.getByTitle(/Heat\.mkv/));
    expect(screen.getByTestId("activity-detail")).toHaveTextContent(
      "Cleaned Heat.mkv: removed 1 audio track.",
    );

    const downloads = screen
      .getAllByRole("button")
      .filter((button) => button.className.includes("mm-history-file"))
      .filter((button) => !/Heat\.mkv/.test(button.title));
    fireEvent.click(downloads[0]);
    expect(screen.getByTestId("activity-detail")).not.toHaveTextContent(
      "Cleaned Heat.mkv: removed 1 audio track.",
    );
  });

  it("counts kept files on their own chip, since a kept file has no entry to count", () => {
    kept.files = [
      {
        id: 7,
        library_id: 1,
        library_name: "Movies",
        relative_path: "Film/film.mkv",
        size_bytes: 100,
        kept_at: "2026-09-26T00:00:00",
      },
    ];
    renderPage();

    const chips = screen.getByRole("group", { name: "Show" });
    expect(
      within(chips).getByRole("button", { name: /Kept\s*1/ }),
    ).toBeInTheDocument();
  });

  it("lists kept files under the Kept chip, with the way back to processing them again", async () => {
    kept.files = [
      {
        id: 7,
        library_id: 1,
        library_name: "Movies",
        relative_path: "Film (2024)/Film.2024.1080p.WEB-DL.mkv",
        size_bytes: 4_000_000_000,
        kept_at: "2026-09-26T00:00:00",
      },
    ];
    processKeptAgain.mockResolvedValue({
      detail:
        "Weir is checking this file's workflow now and will queue it once it is ready.",
    });
    renderPage("/activity?show=kept");

    expect(await screen.findByTestId("kept-file-row-7")).toBeInTheDocument();
    expect(screen.getByText("Movies")).toBeInTheDocument();
    expect(screen.getByText("3.73 GB")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Process again" }));

    expect(
      await screen.findByText(
        "Weir is checking this file's workflow now and will queue it once it is ready.",
      ),
    ).toBeInTheDocument();
    expect(processKeptAgain).toHaveBeenCalledWith(7);
  });

  it("says nothing is kept right now instead of showing an empty activity pane", () => {
    renderPage("/activity?show=kept");

    expect(
      screen.getByText("No files are kept right now."),
    ).toBeInTheDocument();
  });

  it("says its kept files could not load, through the shared load-error wording", () => {
    keptQueryState.isError = true;
    keptQueryState.error = new Error("boom");
    renderPage("/activity?show=kept");

    expect(
      screen.getByText(
        "Weir couldn't load your kept files. Reload the page to try again.",
      ),
    ).toBeInTheDocument();
  });
});
