import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  ProcessingFile,
  ProcessingFileRemoveOptions,
} from "../../../lib/processing/files-api";
import type { ProcessingLibrary } from "../../../lib/processing/libraries-api";
import { NeedsPanel } from "./needs-panel";

type RequeueHandlers = {
  onSuccess: (result: {
    detail: string;
    requeued: number;
    skipped: number;
  }) => void;
  onError: (error: unknown) => void;
};

const requeue = {
  mutate: vi.fn(),
  isPending: false,
};
const forget = vi.fn();
const removeOptions = vi.fn<() => Promise<ProcessingFileRemoveOptions>>();
const readiness = { worker_health: [] as unknown[] };
const failedJobs = { jobs: [] as unknown[] };
const needFiles = { files: [] as ProcessingFile[] };
const rejectedAgain = vi.fn();
const checkAgain = { mutate: vi.fn(), isPending: false };
const removalPending = { options: false, forgetting: false };
let fits = Number.MAX_SAFE_INTEGER;

vi.mock("./fit-rows", () => ({
  useFittingRows: () => [{ current: null }, fits],
}));

vi.mock("../../../lib/processing/files-queries", () => ({
  useRequeueProcessingFile: () => requeue,
  useProcessingCheckLibraryAgain: () => checkAgain,
  useForgetProcessingFile: () => ({
    mutateAsync: forget,
    isPending: removalPending.forgetting,
  }),
  useProcessingFileRemoveOptions: () => ({
    mutateAsync: removeOptions,
    isPending: removalPending.options,
  }),
}));
vi.mock("../../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => ({ data: undefined }),
}));
vi.mock("../../../lib/system/readiness-queries", () => ({
  useSystemReadinessQuery: () => ({ data: readiness }),
}));
vi.mock("../../../lib/processing/jobs-inspection/queries", () => ({
  useProcessingJobsInspectionQuery: () => ({ data: failedJobs }),
}));
const handedOffNote = vi.fn<() => string | null>(() => null);
vi.mock("../../../lib/processing/use-handed-off-note", () => ({
  useHandedOffNote: () => handedOffNote(),
}));
vi.mock("../../../lib/processing/libraries-queries", () => ({
  useProcessingLibrariesQuery: () => ({ data: workflows }),
}));
vi.mock("../../../lib/activity/use-activity-stream-invalidation", () => ({
  useActivityStreamInvalidations: vi.fn(),
}));
vi.mock("./needs-files", () => ({
  useNeedsFiles: (workflowId: number | null | undefined) => ({
    files: needFiles.files.filter(
      (file) => workflowId == null || file.library_id === workflowId,
    ),
    error: null,
  }),
}));
vi.mock("../../activity/activity-rejected-again", () => ({
  ProcessRejectedAgain: (props: {
    libraryId?: number;
    libraryName?: string;
    onNotice?: (text: string, failed: boolean) => void;
  }) => {
    rejectedAgain(props);
    return (
      <button
        type="button"
        onClick={() => props.onNotice?.("Queued 1 file.", false)}
      >
        Process all again
      </button>
    );
  },
}));

const workflows = [
  { id: 1, name: "TV", enabled: true, watched_folder: "D:/tv" },
  { id: 2, name: "Movies", enabled: true, watched_folder: "D:/movies" },
] as ProcessingLibrary[];

function file(overrides: Partial<ProcessingFile>): ProcessingFile {
  return {
    id: 9,
    library_id: 1,
    library_name: "TV",
    relative_path: "Ember.and.Ash.S01E02.mkv",
    status: "processing_failed",
    status_reason: "The new file would not play. The original is safe.",
    failure_class: "execution",
    ...overrides,
  } as ProcessingFile;
}

const stuckFile = file({});
const rejected = file({
  id: 12,
  relative_path: "Old.Film.2019.mkv",
  status: "rejected",
  failure_class: "rules",
  status_reason: "Rejected: none of its audio tracks are in English.",
});

function renderPanel(
  props: Partial<React.ComponentProps<typeof NeedsPanel>> = {},
) {
  render(
    <MemoryRouter>
      <NeedsPanel {...props} />
    </MemoryRouter>,
  );
  return screen.getByRole("region", { name: "Needs you" });
}

beforeEach(() => {
  requeue.mutate = vi.fn();
  requeue.isPending = false;
  checkAgain.mutate = vi.fn();
  checkAgain.isPending = false;
  removalPending.options = false;
  removalPending.forgetting = false;
  forget.mockReset().mockResolvedValue({ detail: "Removed." });
  removeOptions.mockReset();
  rejectedAgain.mockReset();
  handedOffNote.mockReset().mockReturnValue(null);
  fits = Number.MAX_SAFE_INTEGER;
  readiness.worker_health = [];
  failedJobs.jobs = [];
  needFiles.files = [];
});

describe("the Needs you panel when nothing needs a person", () => {
  it("says All clear in one line, with no count and no list", () => {
    const panel = renderPanel();

    expect(within(panel).getByText("All clear")).toBeInTheDocument();
    expect(panel).not.toHaveTextContent("to look at");
    expect(screen.queryByTestId("live-needs")).toBeNull();
  });

  it("stays clear for an extra the workflow's minimum size left alone", () => {
    needFiles.files = [
      file({
        id: 21,
        relative_path: "Nosferatu.1922/Gallery.mkv",
        status: "skipped",
        failure_class: null,
        skip_kind: "below_minimum_size",
        status_reason:
          "Skipped because this file is 12.1 MB, under the 50 MB minimum.",
      }),
    ];

    const panel = renderPanel();

    expect(within(panel).getByText("All clear")).toBeInTheDocument();
    expect(panel).not.toHaveTextContent("Excluded by a rule");
    expect(screen.queryByTestId("live-needs")).toBeNull();
  });
});

describe("the Needs you panel narrowed to one kind of work", () => {
  it("says what it is clear of when nothing from that work needs a person", () => {
    needFiles.files = [stuckFile];

    const panel = renderPanel({ filter: "library" });

    expect(panel).toHaveTextContent("Nothing from library cleaning.");
  });

  it("lists the files of new downloads and counts them", () => {
    needFiles.files = [stuckFile];

    const panel = renderPanel({ filter: "download" });

    expect(panel).toHaveTextContent("1 to look at");
    expect(screen.queryByText("All clear")).toBeNull();
  });
});

describe("the Needs you panel when something does", () => {
  it("shows an amber count in its header and links to the files in Activity", () => {
    failedJobs.jobs = [{ id: 1 }];
    needFiles.files = [stuckFile];
    const panel = renderPanel();

    expect(within(panel).getByText("1 to look at")).toHaveClass(
      "mm-needs__count",
    );
    expect(
      within(panel).getByRole("link", { name: "Activity: Needs you" }),
    ).toHaveAttribute("href", "/activity?show=failed");
  });

  it("sends each group's 'and N more' to the Activity view of that kind of file", () => {
    const failedMany = [1, 2, 3, 4, 5].map((id) =>
      file({ id: 100 + id, relative_path: `Failed.${id}.mkv` }),
    );
    const heldMany = [1, 2, 3, 4, 5].map((id) =>
      file({
        id: 200 + id,
        relative_path: `Held.${id}.mkv`,
        status: "on_hold",
        failure_class: null,
        hold_until: null,
      }),
    );
    const skippedMany = [1, 2, 3, 4, 5].map((id) =>
      file({
        id: 300 + id,
        relative_path: `Skipped.${id}.mkv`,
        status: "skipped",
        failure_class: null,
        status_reason: "Skipped because its path matches an exclude pattern.",
      }),
    );
    needFiles.files = [...failedMany, ...heldMany, ...skippedMany];
    renderPanel();

    const more = screen
      .getAllByRole("link", { name: "and 1 more in Activity →" })
      .map((link) => link.getAttribute("href"));

    expect(more).toEqual([
      "/activity?show=failed",
      "/activity?show=needs",
      "/activity?show=skipped",
    ]);
  });

  it("lists only the rows that fit, and its header link says how many more there are", () => {
    failedJobs.jobs = [{ id: 1 }];
    needFiles.files = [stuckFile, rejected];
    // The Weir group's heading and its row fit; the two files after them do not.
    fits = 2;
    const panel = renderPanel();

    const more = within(panel).getByRole("link", {
      name: "Activity: Needs you",
    });
    expect(more).toHaveTextContent("2 more");
    expect(more).toHaveAttribute("href", "/activity?show=failed");
  });

  it("keeps the header link's own name when every row fits", () => {
    needFiles.files = [stuckFile];
    const panel = renderPanel();

    expect(
      within(panel).getByRole("link", { name: "Activity: Needs you" }),
    ).toHaveTextContent("Activity");
  });

  it("keeps a workflow's own view when the panel is narrowed to it", () => {
    needFiles.files = [1, 2, 3, 4, 5].map((id) =>
      file({ id: 100 + id, library_id: 2, library_name: "Movies" }),
    );
    renderPanel({ workflowId: 2 });

    expect(
      screen.getByRole("link", { name: "and 1 more in Activity →" }),
    ).toHaveAttribute("href", "/activity?show=failed&library=2");
  });

  it("groups files by what went wrong, each group titled in plain words", () => {
    needFiles.files = [stuckFile, rejected];
    renderPanel();

    expect(
      screen.getByRole("region", { name: "1 couldn't finish writing" }),
    ).toHaveTextContent("Ember and Ash S01E02");
    expect(
      screen.getByRole("region", { name: "1 not in a language you keep" }),
    ).toHaveTextContent("Old Film (2019)");
  });

  it("gives a file a few words on why, its workflow and the server's sentence as a tooltip", () => {
    needFiles.files = [stuckFile];
    renderPanel();

    const row = screen.getByTestId("live-needs");
    expect(row).toHaveTextContent("Writing stopped · original kept");
    expect(row).toHaveTextContent("TV");
    expect(screen.getByTitle("The new file would not play.")).toBeVisible();
  });

  it("offers Process all again beside the rejected files only, for the workflow the panel is narrowed to", () => {
    needFiles.files = [stuckFile, rejected];
    renderPanel({ workflowId: 1 });

    const failedGroup = screen.getByRole("region", {
      name: "1 couldn't finish writing",
    });
    const rejectedGroup = screen.getByRole("region", {
      name: "1 not in a language you keep",
    });
    expect(
      within(failedGroup).queryByRole("button", { name: "Process all again" }),
    ).toBeNull();
    expect(
      within(rejectedGroup).getByRole("button", { name: "Process all again" }),
    ).toBeInTheDocument();
    expect(rejectedAgain).toHaveBeenCalledWith(
      expect.objectContaining({ libraryId: 1, libraryName: "TV" }),
    );
  });

  it("says what Process all again did in the panel, which outlasts the button", () => {
    needFiles.files = [rejected];
    renderPanel({ workflowId: 1 });

    fireEvent.click(screen.getByRole("button", { name: "Process all again" }));

    expect(screen.getByRole("status")).toHaveTextContent("Queued 1 file.");
  });

  it("narrows to one workflow's files, but still says what is wrong with Weir itself", () => {
    failedJobs.jobs = [{ id: 1 }];
    const movie = file({
      id: 20,
      library_id: 2,
      library_name: "Movies",
      relative_path: "Old.Film.2019.mkv",
    });
    needFiles.files = [stuckFile, movie];
    const panel = renderPanel({ workflowId: 2 });

    expect(panel).toHaveTextContent("1 to look at");
    expect(panel).toHaveTextContent("1 job failed");
    expect(panel).toHaveTextContent("Old Film (2019)");
    expect(panel).not.toHaveTextContent("Ember and Ash");
  });

  it("opens the file's story from the row when the page can show it", () => {
    const onOpen = vi.fn();
    needFiles.files = [stuckFile];
    renderPanel({ onOpen });

    fireEvent.click(screen.getByRole("button", { name: "Open →" }));

    expect(onOpen).toHaveBeenCalledWith(stuckFile);
  });

  it("opens the file in Activity when the page has no story to show", () => {
    needFiles.files = [stuckFile];
    renderPanel();

    expect(
      screen.getByRole("link", { name: "Open in Activity →" }),
    ).toHaveAttribute("href", "/activity?show=failed&file=9&within=all");
  });

  describe("a file that is decided in Activity", () => {
    const held = file({
      id: 31,
      status: "on_hold",
      failure_class: null,
      hold_until: null,
      status_reason: "Waiting for the file to finish being written.",
    });
    const skipped = file({
      id: 32,
      status: "skipped",
      failure_class: null,
      status_reason: "Skipped because its path matches an exclude pattern.",
    });

    it.each([
      ["held", held, "/activity?show=needs&file=31&within=all"],
      ["skipped", skipped, "/activity?show=skipped&file=32&within=all"],
      ["rejected", rejected, "/activity?show=failed&file=12&within=all"],
    ])(
      "has an Open in Activity action for a %s file, beside its story",
      (_kind, decided, href) => {
        needFiles.files = [decided];
        renderPanel({ onOpen: vi.fn() });

        expect(
          screen.getByRole("link", { name: "Open in Activity →" }),
        ).toHaveAttribute("href", href);
        expect(
          screen.getByRole("button", { name: "Open →" }),
        ).toBeInTheDocument();
      },
    );

    it("leaves a failed file with its own actions and its story", () => {
      needFiles.files = [stuckFile];
      renderPanel({ onOpen: vi.fn() });

      expect(
        screen.queryByRole("link", { name: "Open in Activity →" }),
      ).toBeNull();
    });
  });

  it("counts only files in its header, and gives what is wrong with Weir its own heading", () => {
    failedJobs.jobs = [{ id: 1 }, { id: 2 }];
    needFiles.files = [stuckFile];
    const panel = renderPanel();

    expect(within(panel).getByText("1 to look at")).toBeInTheDocument();
    expect(panel).toHaveTextContent("1 thing to fix in Weir");
  });

  it("links a problem with Weir itself to where it is fixed", () => {
    failedJobs.jobs = [{ id: 1 }];
    renderPanel();

    expect(
      screen.getByRole("link", { name: "Review failed jobs →" }),
    ).toHaveAttribute("href", "/system?tab=logs&source=job&status=failed");
  });
});

describe("trying a file again", () => {
  it("queues that file", () => {
    needFiles.files = [stuckFile];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(requeue.mutate).toHaveBeenCalledWith(9, expect.any(Object));
  });

  it("calls it Process again for a rejected file", () => {
    needFiles.files = [rejected];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Process again" }));

    expect(requeue.mutate).toHaveBeenCalledWith(12, expect.any(Object));
  });

  it("shows that it is working while it queues", () => {
    requeue.isPending = true;
    needFiles.files = [stuckFile];
    renderPanel();

    expect(screen.getByRole("button", { name: "Queueing…" })).toBeDisabled();
  });

  it("says in two words that it is queued", () => {
    requeue.mutate = vi.fn((_id: number, handlers: RequeueHandlers) =>
      handlers.onSuccess({
        detail: "Queued again by hand. It starts as soon as there is room.",
        requeued: 1,
        skipped: 0,
      }),
    );
    needFiles.files = [stuckFile];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(screen.getByRole("status")).toHaveTextContent("Queued again.");
  });

  it("gives the server's reason when it would not queue the file", () => {
    requeue.mutate = vi.fn((_id: number, handlers: RequeueHandlers) =>
      handlers.onSuccess({
        detail: "The original of this file is no longer in the watched folder.",
        requeued: 0,
        skipped: 1,
      }),
    );
    needFiles.files = [stuckFile];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(screen.getByRole("status")).toHaveTextContent(
      "The original of this file is no longer in the watched folder.",
    );
  });

  it("says why it could not be queued, as an alert", () => {
    requeue.mutate = vi.fn((_id: number, handlers: RequeueHandlers) =>
      handlers.onError(new TypeError("boom")),
    );
    needFiles.files = [stuckFile];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Couldn't queue that file.",
    );
  });
});

describe("asking a workflow to look again at a held or skipped file", () => {
  const held = file({
    id: 30,
    library_id: 2,
    status: "on_hold",
    failure_class: null,
    hold_until: null,
    status_reason: "Weir could not open this file for reading.",
  });

  it("offers Check again for a stuck file, and checks its workflow", () => {
    needFiles.files = [held];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    expect(checkAgain.mutate).toHaveBeenCalledWith(
      { media_scope: "movie", library_id: 2 },
      expect.any(Object),
    );
    expect(requeue.mutate).not.toHaveBeenCalled();
  });

  it("says that the workflow is being checked again", () => {
    needFiles.files = [held];
    checkAgain.mutate = vi.fn((_vars: unknown, handlers: RequeueHandlers) =>
      handlers.onSuccess({ detail: "", requeued: 0, skipped: 0 }),
    );
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    expect(screen.getByRole("status")).toHaveTextContent(
      "Checking this workflow again",
    );
  });

  it("says, as an alert, when the workflow could not be checked", () => {
    needFiles.files = [held];
    checkAgain.mutate = vi.fn((_vars: unknown, handlers: RequeueHandlers) =>
      handlers.onError(new TypeError("boom")),
    );
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Check again" }));

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Couldn't check that workflow.",
    );
  });

  it("shows that it is checking while the workflow looks again", () => {
    needFiles.files = [held];
    checkAgain.isPending = true;
    renderPanel();

    expect(screen.getByRole("button", { name: "Checking…" })).toBeDisabled();
  });

  it("does not offer to check a workflow a media manager hands its downloads to, and says why", () => {
    needFiles.files = [held];
    handedOffNote.mockReturnValue("Deluno hands this workflow its downloads.");
    renderPanel();

    const button = screen.getByRole("button", { name: "Check again" });

    expect(button).toBeDisabled();
    expect(button).toHaveAttribute(
      "title",
      "Deluno hands this workflow its downloads.",
    );
    fireEvent.click(button);
    expect(checkAgain.mutate).not.toHaveBeenCalled();
  });
});

describe("removing a file", () => {
  const choice = (partial: Partial<ProcessingFileRemoveOptions> = {}) =>
    ({
      requires_choice: true,
      manager_label: null,
      delete_handled_by_manager: false,
      keep_notifies_manager: false,
      fingerprint_recorded: true,
      unconfirmed_size_bytes: null,
      unconfirmed_modified_at: null,
      delete_refused_reason: null,
      ...partial,
    }) as ProcessingFileRemoveOptions;

  it("opens Activity's remove dialog when the file is still in the watched folder", async () => {
    removeOptions.mockResolvedValue(choice());
    needFiles.files = [stuckFile];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));

    expect(await screen.findByTestId("activity-remove-dialog")).toBeVisible();
    expect(forget).not.toHaveBeenCalled();
  });

  it("removes what the person chose in that dialog", async () => {
    removeOptions.mockResolvedValue(choice());
    needFiles.files = [stuckFile];
    renderPanel();
    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));

    fireEvent.click(
      await screen.findByTestId("activity-remove-dialog-choice-keep"),
    );
    fireEvent.click(screen.getByTestId("activity-remove-dialog-confirm"));

    await waitFor(() =>
      expect(forget).toHaveBeenCalledWith({
        id: 9,
        resolution: "keep",
        confirm: undefined,
      }),
    );
    expect(await screen.findByRole("status")).toHaveTextContent("Removed.");
  });

  it("drops a file that needs no choice from the list straight away", async () => {
    removeOptions.mockResolvedValue(choice({ requires_choice: false }));
    needFiles.files = [stuckFile];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));

    await waitFor(() => expect(forget).toHaveBeenCalledWith({ id: 9 }));
    expect(screen.queryByTestId("activity-remove-dialog")).toBeNull();
  });

  it("shows that it is checking while Weir works out what removing would do", () => {
    needFiles.files = [stuckFile];
    removalPending.options = true;
    renderPanel();

    expect(screen.getByRole("button", { name: "Checking…" })).toBeDisabled();
  });

  it("shows that it is removing while the removal runs", () => {
    needFiles.files = [stuckFile];
    removalPending.forgetting = true;
    renderPanel();

    expect(screen.getByRole("button", { name: "Removing…" })).toBeDisabled();
  });

  it("says when Weir could not tell what removing would do", async () => {
    removeOptions.mockRejectedValue(new TypeError("boom"));
    needFiles.files = [stuckFile];
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Remove…" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Weir could not check what removing this file would do.",
    );
  });
});
