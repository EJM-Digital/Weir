import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  ProcessingFile,
  ProcessingFileRemoveOptions,
} from "../../lib/processing/files-api";
import { ActivityFileActions } from "./activity-file-actions";

const forget = vi.fn().mockResolvedValue(undefined);
const removeOptions = vi.fn<() => Promise<ProcessingFileRemoveOptions>>();
const pending = { options: false, forgetting: false };

vi.mock("../../lib/processing/files-queries", () => {
  const mutation = (fn = vi.fn(), isPending = false) => ({
    mutateAsync: fn,
    isPending,
  });
  return {
    useForgetProcessingFile: () => mutation(forget, pending.forgetting),
    useProcessingFileRemoveOptions: () =>
      mutation(removeOptions, pending.options),
    useMoveProcessingFileToTop: () => mutation(),
    useRequeueProcessingFile: () => mutation(),
    useProcessingWhyHeld: () => mutation(),
    useProcessProcessingFileNow: () => mutation(),
    useProcessingCheckLibraryAgain: () => mutation(),
    useProcessingFileTracks: () => mutation(),
    useSubmitProcessingManualPlan: () => mutation(),
  };
});
const handedOffNote = vi.fn<() => string | null>(() => null);
vi.mock("../../lib/processing/use-handed-off-note", () => ({
  useHandedOffNote: () => handedOffNote(),
}));
vi.mock("../../lib/processing/libraries-queries", () => ({
  useProcessingLibrariesQuery: () => ({
    data: [{ id: 1, name: "Movies", media_type: "movie" }],
  }),
}));
vi.mock("../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => ({ data: undefined }),
}));

/** A `remove-options` response with a recorded fingerprint — the everyday case, needing nothing confirmed. */
function removeOptionsResult(
  partial: Partial<ProcessingFileRemoveOptions> = {},
): ProcessingFileRemoveOptions {
  return {
    requires_choice: true,
    manager_label: null,
    delete_handled_by_manager: false,
    keep_notifies_manager: false,
    fingerprint_recorded: true,
    unconfirmed_size_bytes: null,
    unconfirmed_modified_at: null,
    delete_refused_reason: null,
    ...partial,
  };
}

function file(partial: Partial<ProcessingFile> = {}): ProcessingFile {
  return {
    id: 7,
    library_id: 1,
    library_name: "Movies",
    relative_path: "Film (2024)/film.mkv",
    status: "processing_failed",
    status_reason: "ffmpeg could not read the audio track.",
    blocked_by_connection: null,
    size_bytes: 4_000_000_000,
    failure_class: "execution",
    failure_attempts: 1,
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
    created_at: "2026-09-01T00:00:00Z",
    updated_at: "2026-09-01T00:00:00Z",
    last_seen_at: null,
    last_attempt_at: null,
    ...partial,
  };
}

function renderActions(partial: Partial<ProcessingFile> = {}) {
  render(
    <ActivityFileActions file={file(partial)} editable onRemoved={vi.fn()} />,
  );
}

describe("ActivityFileActions remove dialog", () => {
  beforeEach(() => {
    forget.mockReset().mockResolvedValue(undefined);
    removeOptions.mockReset();
    pending.options = false;
    pending.forgetting = false;
  });

  it("says Checking while Weir works out what removing would do", () => {
    pending.options = true;
    renderActions();

    expect(screen.getByRole("button", { name: "Checking…" })).toBeDisabled();
  });

  it("says Removing while the removal runs", () => {
    pending.forgetting = true;
    renderActions();

    expect(screen.getByRole("button", { name: "Removing…" })).toBeDisabled();
  });
  handedOffNote.mockReset().mockReturnValue(null);

  it("removes a plain title immediately, with no dialog, when it does not qualify for a choice", async () => {
    removeOptions.mockResolvedValue(
      removeOptionsResult({ requires_choice: false }),
    );
    renderActions({ status: "processed" });

    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));

    await waitFor(() => expect(forget).toHaveBeenCalledWith({ id: 7 }));
    expect(
      screen.queryByTestId("activity-remove-dialog"),
    ).not.toBeInTheDocument();
  });

  it("opens the dialog naming Weir alone when no manager is linked", async () => {
    removeOptions.mockResolvedValue(removeOptionsResult());
    renderActions();

    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));

    expect(
      await screen.findByTestId("activity-remove-dialog"),
    ).toBeInTheDocument();
    expect(screen.getByText("Delete the file")).toBeInTheDocument();
    expect(
      screen.getByText("Weir alone: the file is deleted."),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Weir leaves it until it changes."),
    ).toBeInTheDocument();
  });

  it("names the manager that will delete the download and search again", async () => {
    removeOptions.mockResolvedValue(
      removeOptionsResult({
        manager_label: "Radarr",
        delete_handled_by_manager: true,
      }),
    );
    renderActions();

    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));

    expect(
      await screen.findByText(
        "Delete the download and ask Radarr for another copy",
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "Radarr removes it, blocks this release and searches again.",
      ),
    ).toBeInTheDocument();
  });

  it("does not offer to delete the file of a workflow linked to a media manager, and says why", async () => {
    removeOptions.mockResolvedValue(
      removeOptionsResult({
        delete_refused_reason:
          "This workflow is linked to Radarr, so the original stays with your download client, which may still be seeding. Weir did not delete the file; remove the download from your download client.",
      }),
    );
    renderActions();

    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));

    const choice = await screen.findByTestId(
      "activity-remove-dialog-choice-delete",
    );
    expect(within(choice).getByRole("radio")).toBeDisabled();
    expect(choice).toHaveTextContent("This workflow is linked to Radarr");
  });

  it("says the manager will be told the file will not be imported when keep notifies it", async () => {
    removeOptions.mockResolvedValue(
      removeOptionsResult({
        manager_label: "Deluno",
        delete_handled_by_manager: true,
        keep_notifies_manager: true,
      }),
    );
    renderActions();

    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));

    expect(
      await screen.findByText(
        "Weir leaves it until it changes. Deluno is told it won't be imported.",
      ),
    ).toBeInTheDocument();
  });

  it("sends the chosen resolution when a choice other than delete is confirmed", async () => {
    removeOptions.mockResolvedValue(removeOptionsResult());
    renderActions();
    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));
    fireEvent.click(
      await screen.findByTestId("activity-remove-dialog-choice-keep"),
    );

    fireEvent.click(screen.getByTestId("activity-remove-dialog-confirm"));

    await waitFor(() =>
      expect(forget).toHaveBeenCalledWith({ id: 7, resolution: "keep" }),
    );
  });

  it("opens with nothing chosen, Remove disabled and focus on the first choice", async () => {
    removeOptions.mockResolvedValue(removeOptionsResult());
    renderActions();

    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));

    await screen.findByTestId("activity-remove-dialog");
    for (const radio of screen.getAllByRole("radio")) {
      expect(radio).not.toBeChecked();
    }
    expect(screen.getByTestId("activity-remove-dialog-confirm")).toBeDisabled();
    expect(screen.getAllByRole("radio")[0]).toHaveFocus();
  });

  it("removes nothing when Enter or a click reaches Remove before a choice is made", async () => {
    removeOptions.mockResolvedValue(removeOptionsResult());
    renderActions();
    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));
    await screen.findByTestId("activity-remove-dialog");

    fireEvent.keyDown(document.activeElement ?? document.body, {
      key: "Enter",
    });
    fireEvent.click(screen.getByTestId("activity-remove-dialog-confirm"));

    expect(forget).not.toHaveBeenCalled();
    expect(screen.getByTestId("activity-remove-dialog")).toBeInTheDocument();
  });

  it("enables Remove once a choice is made and sends exactly that resolution", async () => {
    removeOptions.mockResolvedValue(removeOptionsResult());
    renderActions();
    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));
    fireEvent.click(
      await screen.findByTestId("activity-remove-dialog-choice-retry"),
    );

    const remove = screen.getByTestId("activity-remove-dialog-confirm");
    expect(remove).toBeEnabled();
    fireEvent.click(remove);

    await waitFor(() => expect(forget).toHaveBeenCalledTimes(1));
    expect(forget).toHaveBeenCalledWith({ id: 7, resolution: "retry" });
  });

  it("shows the server's refusal in the dialog and keeps it open when the action fails", async () => {
    removeOptions.mockResolvedValue(
      removeOptionsResult({
        manager_label: "Radarr",
        delete_handled_by_manager: true,
      }),
    );
    forget.mockRejectedValueOnce(
      new Error("Radarr did not accept the rejection, so nothing was removed."),
    );
    renderActions();
    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));
    fireEvent.click(
      await screen.findByTestId("activity-remove-dialog-choice-delete"),
    );

    fireEvent.click(screen.getByTestId("activity-remove-dialog-confirm"));

    expect(
      await screen.findByText(
        "Radarr did not accept the rejection, so nothing was removed.",
      ),
    ).toBeInTheDocument();
    expect(screen.getByTestId("activity-remove-dialog")).toBeInTheDocument();
  });

  it("shows the file's current details to confirm for a title with no recorded fingerprint", async () => {
    removeOptions.mockResolvedValue(
      removeOptionsResult({
        fingerprint_recorded: false,
        unconfirmed_size_bytes: 4_200_000_000,
        unconfirmed_modified_at: "2026-09-24T10:04:00Z",
      }),
    );
    renderActions();

    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));

    const unconfirmed = await screen.findByTestId(
      "activity-remove-dialog-unconfirmed",
    );
    expect(within(unconfirmed).getByText(/3\.91 GB/)).toBeInTheDocument();
    expect(
      screen.getByText(
        "Weir didn't note this file's details when it failed, so check it's the one you mean.",
      ),
    ).toBeInTheDocument();
  });

  it("never shows the file's current details to confirm for a title with a recorded fingerprint", async () => {
    removeOptions.mockResolvedValue(removeOptionsResult());
    renderActions();

    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));

    await screen.findByTestId("activity-remove-dialog");
    expect(
      screen.queryByTestId("activity-remove-dialog-unconfirmed"),
    ).not.toBeInTheDocument();
  });

  it("sends the file's confirmed details with delete or keep for a title with no recorded fingerprint", async () => {
    removeOptions.mockResolvedValue(
      removeOptionsResult({
        fingerprint_recorded: false,
        unconfirmed_size_bytes: 4_200_000_000,
        unconfirmed_modified_at: "2026-09-24T10:04:00Z",
      }),
    );
    renderActions();
    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));
    fireEvent.click(
      await screen.findByTestId("activity-remove-dialog-choice-delete"),
    );

    fireEvent.click(screen.getByTestId("activity-remove-dialog-confirm"));

    await waitFor(() =>
      expect(forget).toHaveBeenCalledWith({
        id: 7,
        resolution: "delete",
        confirm: {
          size_bytes: 4_200_000_000,
          modified_at: "2026-09-24T10:04:00Z",
        },
      }),
    );
  });

  it("always offers a way to just remove the title without touching the file", async () => {
    removeOptions.mockResolvedValue(
      removeOptionsResult({
        fingerprint_recorded: false,
        unconfirmed_size_bytes: 4_200_000_000,
        unconfirmed_modified_at: "2026-09-24T10:04:00Z",
      }),
    );
    renderActions();
    fireEvent.click(screen.getByRole("button", { name: "Remove from list" }));
    fireEvent.click(
      await screen.findByTestId("activity-remove-dialog-choice-remove"),
    );

    fireEvent.click(screen.getByTestId("activity-remove-dialog-confirm"));

    await waitFor(() =>
      expect(forget).toHaveBeenCalledWith({
        id: 7,
        resolution: "remove",
        confirm: {
          size_bytes: 4_200_000_000,
          modified_at: "2026-09-24T10:04:00Z",
        },
      }),
    );
  });
});

describe("ActivityFileActions for a file that is no longer in the watched folder", () => {
  beforeEach(() => {
    handedOffNote.mockReset().mockReturnValue(null);
  });

  it("offers only Remove from list, since nothing else can be done without the file", () => {
    renderActions({
      status: "on_hold",
      source_gone: true,
      hold_until: "2026-09-01T00:10:00Z",
    });

    expect(
      screen.getByRole("button", { name: "Remove from list" }),
    ).toBeInTheDocument();
    for (const name of [
      "Choose tracks",
      "Check again",
      "Why is this held?",
      "Pass through unchanged",
    ]) {
      expect(screen.queryByRole("button", { name })).toBeNull();
    }
  });

  it("still offers every action for a file held for another reason", () => {
    renderActions({ status: "on_hold", source_gone: false });

    for (const name of [
      "Choose tracks",
      "Check again",
      "Why is this held?",
      "Pass through unchanged",
      "Remove from list",
    ]) {
      expect(screen.getByRole("button", { name })).toBeInTheDocument();
    }
  });
});

describe("ActivityFileActions check again", () => {
  beforeEach(() => {
    handedOffNote.mockReset().mockReturnValue(null);
  });

  it("offers Check again for a held file in a workflow Weir scans", () => {
    renderActions({ status: "on_hold" });

    expect(
      screen.getByRole("button", { name: "Check again" }),
    ).toBeInTheDocument();
  });

  it("offers no Check again for a workflow a media manager hands its downloads to", () => {
    handedOffNote.mockReturnValue("Deluno hands this workflow its downloads.");

    renderActions({ status: "on_hold" });

    expect(screen.queryByRole("button", { name: "Check again" })).toBeNull();
  });
});

describe("ActivityFileActions for a file the workflow removed", () => {
  beforeEach(() => {
    handedOffNote.mockReset().mockReturnValue(null);
  });

  const skip = { status: "skipped", failure_class: null } as const;

  it("offers nothing that needs the file, only Remove from list", () => {
    renderActions({ ...skip, skip_kind: "below_minimum_size_removed" });

    for (const name of [
      "Pass through unchanged",
      "Process again",
      "Check again",
    ]) {
      expect(screen.queryByRole("button", { name })).toBeNull();
    }
    expect(
      screen.getByRole("button", { name: "Remove from list" }),
    ).toBeInTheDocument();
  });

  it("still offers to pass through a file the minimum size only skipped", () => {
    renderActions({ ...skip, skip_kind: "below_minimum_size" });

    expect(
      screen.getByRole("button", { name: "Pass through unchanged" }),
    ).toBeInTheDocument();
  });
});
