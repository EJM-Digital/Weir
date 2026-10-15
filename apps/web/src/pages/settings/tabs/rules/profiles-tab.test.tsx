import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { ApiHttpError } from "../../../../lib/api/client";
import * as authQueries from "../../../../lib/auth/queries";
import * as librariesApi from "../../../../lib/processing/libraries-api";
import * as ruleSetsApi from "../../../../lib/processing/rule-sets-api";
import type { ProcessingRuleSet } from "../../../../lib/processing/rule-sets-api";
import { ProfilesTab } from "./profiles-tab";

const ruleSet: ProcessingRuleSet = {
  id: 4,
  name: "Feature films",
  primary_audio_lang: "eng",
  secondary_audio_lang: "jpn",
  tertiary_audio_lang: "",
  default_audio_slot: "primary",
  remove_commentary: true,
  subtitle_mode: "keep_listed",
  subtitle_langs_csv: "eng",
  preserve_forced_subs: true,
  preserve_default_subs: true,
  audio_preference_mode: "preferred_langs_quality",
  audio_sorters_json: JSON.stringify([
    { field: "language", value: "eng", reversed: false },
    { field: "channels", value: ">=5.1", reversed: false },
  ]),
  subtitle_sorters_json: JSON.stringify([
    { field: "forced", value: null, reversed: false },
  ]),
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
  used_by_library_count: 2,
  updated_at: "2026-07-28T04:00:00Z",
};

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

// The tab lists the libraries using each profile; unstubbed, that is a real request to the test host.
beforeEach(() => {
  vi.spyOn(librariesApi, "fetchProcessingLibraries").mockResolvedValue([]);
});

afterEach(() => {
  vi.restoreAllMocks();
});

function stubEditableProfile() {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role: "operator" },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(ruleSetsApi, "fetchProcessingRuleSets").mockResolvedValue([ruleSet]);
}

it("lays the profile out as audio, subtitles, original language and cleanup, with track ordering on request", async () => {
  stubEditableProfile();

  render(<ProfilesTab />, { wrapper });

  expect(
    await screen.findByRole("heading", { name: "Audio" }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("heading", { name: "Subtitles" }),
  ).toBeInTheDocument();
  expect(screen.getByText("Original language")).toBeInTheDocument();
  expect(screen.getByText("Also remove")).toBeInTheDocument();

  const orderedSections = [
    "Audio",
    "Subtitles",
    "Original language",
    "Also remove",
  ].map((name) => screen.getByRole("heading", { name }));
  orderedSections.slice(0, -1).forEach((heading, index) => {
    expect(heading.compareDocumentPosition(orderedSections[index + 1]!)).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
  });

  fireEvent.click(
    screen.getByRole("button", { name: /Advanced track ordering/ }),
  );
  expect(screen.getByText("Audio order")).toBeInTheDocument();
  expect(screen.getByText("Subtitle order")).toBeInTheDocument();
  expect(screen.getByDisplayValue(">=5.1")).toBeInTheDocument();
});

it("saves every edited rule with the profile", async () => {
  stubEditableProfile();
  const update = vi
    .spyOn(ruleSetsApi, "updateProcessingRuleSet")
    .mockResolvedValue({
      ...ruleSet,
      keep_original_language: true,
      remove_images: true,
    });

  render(<ProfilesTab />, { wrapper });

  await screen.findByRole("heading", { name: "Audio" });

  fireEvent.click(
    screen.getByRole("checkbox", { name: /Keep the original language/ }),
  );
  fireEvent.click(screen.getByRole("checkbox", { name: /Embedded images/ }));

  // Issue #495/#497: audio keep mode, SDH removal and the subtitle cap/strategy.
  expect(
    screen.getByRole("heading", { name: "Track naming and chapters" }),
  ).toBeInTheDocument();
  fireEvent.change(
    screen.getByRole("combobox", { name: /Audio tracks kept/ }),
    { target: { value: "per_language" } },
  );
  fireEvent.click(
    screen.getByRole("checkbox", { name: /Remove hearing-impaired subtitles/ }),
  );
  fireEvent.change(
    screen.getByRole("spinbutton", {
      name: /Limit subtitles kept per language/,
    }),
    { target: { value: "2" } },
  );
  fireEvent.change(
    screen.getByRole("combobox", { name: /How to pick the best subtitle/ }),
    { target: { value: "accessibility" } },
  );

  // Issue #498: standardize track names, template live preview, and chapters.
  fireEvent.click(
    screen.getByRole("checkbox", {
      name: /Standardize audio and subtitle track names/,
    }),
  );
  expect(
    screen.getByText(
      (_, element) =>
        element?.tagName.toLowerCase() === "span" &&
        element.textContent?.replace(/\s+/g, " ").trim() ===
          "Preview: English (VFQ) 5.1 TrueHD",
    ),
  ).toBeInTheDocument();
  const templateField = screen.getByRole("textbox", {
    name: /Track name template/,
  });
  fireEvent.change(templateField, {
    target: { value: "{language} {bogus}" },
  });
  expect(
    screen.getByText(/Unknown placeholder '\{bogus\}'/),
  ).toBeInTheDocument();
  fireEvent.change(templateField, {
    target: { value: "{language} {codec}" },
  });
  fireEvent.click(
    screen.getByRole("checkbox", { name: /Clear video track names/ }),
  );
  fireEvent.click(screen.getByRole("checkbox", { name: /Remove chapters/ }));

  fireEvent.click(screen.getByRole("button", { name: "Save profile" }));

  await waitFor(() => {
    expect(update).toHaveBeenCalledWith(
      4,
      expect.objectContaining({
        keep_original_language: true,
        remove_images: true,
        audio_sorters_json: expect.stringContaining("channels"),
        audio_keep_mode: "per_language",
        remove_hearing_impaired_subs: true,
        subtitle_max_per_language: 2,
        subtitle_quality_strategy: "accessibility",
        standardize_track_names: true,
        track_name_template: "{language} {codec}",
        clear_video_track_names: true,
        remove_chapters: true,
      }),
    );
  });
  const sentRuleSet = update.mock.calls[0]?.[1];
  expect(sentRuleSet).not.toHaveProperty("id");
  expect(sentRuleSet).not.toHaveProperty("used_by_library_count");
  expect(sentRuleSet).not.toHaveProperty("updated_at");
});

it("says Weir looks up each title's original language itself, with no provider or key to set up", async () => {
  stubEditableProfile();

  render(<ProfilesTab />, { wrapper });

  await screen.findByRole("heading", { name: "Audio" });

  expect(
    screen.getByText("Weir looks up each title's original language itself."),
  ).toBeInTheDocument();
  expect(screen.queryByText(/provider/i)).not.toBeInTheDocument();
  expect(screen.queryByText(/API key/i)).not.toBeInTheDocument();
});

it("shows a load error when the profiles fail to load", async () => {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role: "operator" },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(ruleSetsApi, "fetchProcessingRuleSets").mockRejectedValue(
    new Error("network down"),
  );

  render(<ProfilesTab />, { wrapper });

  expect(await screen.findByTestId("settings-load-error")).toHaveTextContent(
    "Weir couldn’t load your profiles.",
  );
});

it("asks before switching profiles with unsaved edits, and keeps them when told to stay", async () => {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role: "operator" },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(ruleSetsApi, "fetchProcessingRuleSets").mockResolvedValue([
    ruleSet,
    { ...ruleSet, id: 5, name: "TV shows", used_by_library_count: 0 },
  ]);

  render(<ProfilesTab />, { wrapper });

  const nameField = await screen.findByPlaceholderText("English feature films");
  fireEvent.change(nameField, { target: { value: "Feature films (edited)" } });

  fireEvent.change(screen.getByRole("combobox", { name: "Profile" }), {
    target: { value: "5" },
  });

  expect(screen.getByTestId("settings-unsaved-changes")).toHaveTextContent(
    "You have unsaved changes to Feature films. Leave without saving?",
  );
  fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));
  expect(screen.getByPlaceholderText("English feature films")).toHaveValue(
    "Feature films (edited)",
  );

  fireEvent.change(screen.getByRole("combobox", { name: "Profile" }), {
    target: { value: "5" },
  });
  fireEvent.click(screen.getByTestId("settings-unsaved-changes-confirm"));
  expect(screen.getByPlaceholderText("English feature films")).toHaveValue(
    "TV shows",
  );
});

function removalOfAnUnusedProfile() {
  stubEditableProfile();
  vi.spyOn(ruleSetsApi, "fetchProcessingRuleSets").mockResolvedValue([
    { ...ruleSet, used_by_library_count: 0 },
  ]);
}

async function confirmRemovingTheProfile() {
  fireEvent.click(
    await screen.findByRole("button", { name: "Remove profile" }),
  );
  fireEvent.click(screen.getByTestId("remove-rule-set-dialog-confirm"));
}

it("says why the server refused to remove a profile and keeps it selected", async () => {
  removalOfAnUnusedProfile();
  const reason =
    "Feature films is still used by 1 workflow. Point them at another rule set first.";
  vi.spyOn(ruleSetsApi, "deleteProcessingRuleSet").mockRejectedValue(
    new ApiHttpError("/api/v1/processing/rule-sets/4", 409, reason),
  );

  render(<ProfilesTab />, { wrapper });
  await confirmRemovingTheProfile();

  expect(await screen.findByRole("status")).toHaveTextContent(reason);
  expect(screen.getByPlaceholderText("English feature films")).toHaveValue(
    "Feature films",
  );
});

it("falls back to a plain sentence when the removal fails without a reason", async () => {
  removalOfAnUnusedProfile();
  vi.spyOn(ruleSetsApi, "deleteProcessingRuleSet").mockRejectedValue(
    new TypeError("Failed to fetch"),
  );

  render(<ProfilesTab />, { wrapper });
  await confirmRemovingTheProfile();

  expect(await screen.findByRole("status")).toHaveTextContent(
    "That profile could not be removed.",
  );
});
