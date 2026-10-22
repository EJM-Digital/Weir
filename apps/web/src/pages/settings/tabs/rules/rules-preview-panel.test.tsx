import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import type { ProcessingRuleSetWrite } from "../../../../lib/processing/rule-sets-api";
import * as librariesQueries from "../../../../lib/processing/libraries-queries";
import {
  elapse,
  useFakeClock,
  useRealClock,
} from "../../../../test/fake-clock";
import * as rulesPreviewApi from "../../../../lib/processing/rules-preview-api";
import type { ProcessingRulesPreviewResult } from "../../../../lib/processing/rules-preview-api";
import { RulesPreviewPanel } from "./rules-preview-panel";

const rules: ProcessingRuleSetWrite = {
  name: "Feature films",
  primary_audio_lang: "eng",
  secondary_audio_lang: "",
  tertiary_audio_lang: "",
  default_audio_slot: "primary",
  remove_commentary: true,
  subtitle_mode: "keep_all",
  subtitle_langs_csv: "",
  preserve_forced_subs: true,
  preserve_default_subs: true,
  audio_preference_mode: "preferred_langs_quality",
  audio_sorters_json: "",
  subtitle_sorters_json: "",
  keep_original_language: false,
  original_language_additional_csv: "",
  original_language_keep_only_first: true,
  original_language_first_if_none: true,
  original_language_treat_empty_as_original: false,
  remove_images: false,
  remove_attachments: false,
  remove_title: false,
  remove_language_tags: false,
  remove_other_metadata: false,
  remove_hearing_impaired_subs: false,
  audio_keep_mode: "single",
  subtitle_max_per_language: 0,
  subtitle_quality_strategy: "text_first",
  standardize_track_names: false,
  track_name_template: "{language}{variant} {channels} {codec}",
  track_name_overrides: {
    forced: "{language} {flags}",
    hearing_impaired: "{language} {flags}",
    commentary: "{language} {flags}",
    audio_description: "{language} {flags}",
  },
  clear_video_track_names: false,
  remove_chapters: false,
};

const result: ProcessingRulesPreviewResult = {
  library_id: 7,
  media_scope: "movie",
  inspected_path: "C:\\Media\\Movies\\Movie (2020)\\Movie.mkv",
  tracks: [
    {
      index: 0,
      type: "video",
      codec: "h264",
      language: "",
      title: "",
      channels: 0,
      action: "keep",
      default: false,
      forced: false,
      reasons: ["Video track kept unchanged."],
    },
    {
      index: 1,
      type: "audio",
      codec: "aac",
      language: "eng",
      title: "",
      channels: 2,
      action: "keep",
      default: true,
      forced: false,
      reasons: ["Selected as the preferred audio track."],
    },
    {
      index: 2,
      type: "audio",
      codec: "aac",
      language: "jpn",
      title: "",
      channels: 2,
      action: "drop",
      default: false,
      forced: false,
      reasons: ["Removed non-selected jpn aac 2 ch (stream 2)."],
    },
  ],
  notes: [
    "Track ranking: quality.",
    "Removed non-selected jpn aac 2 ch (stream 2).",
  ],
  metadata_notes: [],
  remux_required: true,
  estimated_size_reduction_bytes: 2_400_000,
  estimated_size_reduction_is_estimate: true,
  original_language: null,
};

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function stubLibraries() {
  vi.spyOn(librariesQueries, "useProcessingLibrariesQuery").mockReturnValue({
    data: [{ id: 7, name: "Movies" } as ProcessingLibrary],
    isLoading: false,
  } as ReturnType<typeof librariesQueries.useProcessingLibrariesQuery>);
}

/** How long the rules draft must stay unchanged before the preview runs again. */
const RERUN_PAUSE_MS = 700;

afterEach(() => {
  vi.restoreAllMocks();
  useRealClock();
});

it("stays collapsed until the operator opens it", () => {
  stubLibraries();
  render(<RulesPreviewPanel rules={rules} />, { wrapper });

  expect(
    screen.getByRole("button", { name: "Try on a file" }),
  ).toBeInTheDocument();
  expect(screen.queryByLabelText("Workflow")).not.toBeInTheDocument();
});

it("previews a file and renders the per-track plan, notes, and estimate", async () => {
  stubLibraries();
  const preview = vi
    .spyOn(rulesPreviewApi, "previewProcessingRules")
    .mockResolvedValue(result);

  render(<RulesPreviewPanel rules={rules} />, { wrapper });
  fireEvent.click(screen.getByRole("button", { name: "Try on a file" }));

  fireEvent.change(
    screen.getByLabelText("Path within the watched or output folder"),
    { target: { value: "Movie (2020)/Movie.mkv" } },
  );
  fireEvent.click(screen.getByRole("button", { name: "Preview" }));

  await waitFor(() => expect(preview).toHaveBeenCalled());
  expect(preview.mock.calls[0][0]).toEqual(
    expect.objectContaining({
      libraryId: 7,
      relativePath: "Movie (2020)/Movie.mkv",
      rules,
    }),
  );

  expect(
    await screen.findByText("Weir would rewrite this file"),
  ).toBeInTheDocument();
  expect(screen.getByText("h264")).toBeInTheDocument();
  expect(screen.getAllByText("Keep")).toHaveLength(2);
  expect(screen.getByText("Drop")).toBeInTheDocument();
  expect(
    screen.getAllByText("Removed non-selected jpn aac 2 ch (stream 2)."),
  ).not.toHaveLength(0);
  expect(screen.getByText(/smaller \(estimate only\)/)).toBeInTheDocument();
});

it("shows the server's error message when the preview is refused", async () => {
  stubLibraries();
  vi.spyOn(rulesPreviewApi, "previewProcessingRules").mockRejectedValue(
    new Error(
      "That path is not an existing file inside this workflow's watched or output folder.",
    ),
  );

  render(<RulesPreviewPanel rules={rules} />, { wrapper });
  fireEvent.click(screen.getByRole("button", { name: "Try on a file" }));
  fireEvent.change(
    screen.getByLabelText("Path within the watched or output folder"),
    { target: { value: "../elsewhere.mkv" } },
  );
  fireEvent.click(screen.getByRole("button", { name: "Preview" }));

  expect(
    await screen.findByText(
      "That path is not an existing file inside this workflow's watched or output folder.",
    ),
  ).toBeInTheDocument();
});

it("offers a folder browser when the file is anywhere on the machine", () => {
  stubLibraries();
  render(<RulesPreviewPanel rules={rules} />, { wrapper });
  fireEvent.click(screen.getByRole("button", { name: "Try on a file" }));

  fireEvent.change(screen.getByLabelText("Where is the file?"), {
    target: { value: "anywhere" },
  });

  expect(screen.getByLabelText("Folder")).toBeInTheDocument();
  expect(screen.getByLabelText("File name")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Browse" })).toBeInTheDocument();
});

it("goes back to a path inside the workflow when the person picks that again", () => {
  stubLibraries();
  render(<RulesPreviewPanel rules={rules} />, { wrapper });
  fireEvent.click(screen.getByRole("button", { name: "Try on a file" }));
  const where = screen.getByLabelText("Where is the file?");
  fireEvent.change(where, { target: { value: "anywhere" } });

  fireEvent.change(where, { target: { value: "library" } });

  expect(
    screen.getByLabelText("Path within the watched or output folder"),
  ).toBeInTheDocument();
  expect(screen.queryByLabelText("Folder")).toBeNull();
});

it("re-runs automatically a moment after the rules draft changes", async () => {
  useFakeClock();
  stubLibraries();
  const preview = vi
    .spyOn(rulesPreviewApi, "previewProcessingRules")
    .mockResolvedValue(result);

  const { rerender } = render(<RulesPreviewPanel rules={rules} />, {
    wrapper,
  });
  fireEvent.click(screen.getByRole("button", { name: "Try on a file" }));
  fireEvent.change(
    screen.getByLabelText("Path within the watched or output folder"),
    { target: { value: "Movie (2020)/Movie.mkv" } },
  );

  await waitFor(() => expect(preview).toHaveBeenCalledTimes(1));

  rerender(
    <RulesPreviewPanel rules={{ ...rules, primary_audio_lang: "jpn" }} />,
  );

  await elapse(RERUN_PAUSE_MS - 1);
  expect(preview).toHaveBeenCalledTimes(1);
  await elapse(1);
  expect(preview).toHaveBeenCalledTimes(2);
  expect(preview.mock.calls[1][0]).toEqual(
    expect.objectContaining({ rules: { ...rules, primary_audio_lang: "jpn" } }),
  );
});
