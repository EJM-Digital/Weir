import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";

import type {
  ProcessingRulesPreviewResult,
  ProcessingRulesPreviewTrack,
} from "../../../../lib/processing/rules-preview-api";
import { RulesPreviewResults } from "./rules-preview-results";

function track(
  over: Partial<ProcessingRulesPreviewTrack>,
): ProcessingRulesPreviewTrack {
  return {
    index: 0,
    type: "audio",
    codec: "aac",
    language: "eng",
    title: "",
    channels: 2,
    action: "keep",
    default: false,
    forced: false,
    reasons: [],
    ...over,
  };
}

const RESULT: ProcessingRulesPreviewResult = {
  library_id: 7,
  media_scope: "movie",
  inspected_path: "Movie.mkv",
  tracks: [
    track({ index: 0, type: "video", codec: "h264", channels: 0 }),
    track({ index: 1, type: "audio", language: "jpn", channels: 6 }),
    track({ index: 2, type: "subtitle", codec: "subrip", action: "drop" }),
    track({ index: 3, type: "audio", language: "eng", channels: 2 }),
  ],
  notes: [],
  metadata_notes: [],
  remux_required: true,
  estimated_size_reduction_bytes: null,
  estimated_size_reduction_is_estimate: true,
  original_language: null,
};

function trackNumbers() {
  return screen
    .getAllByRole("row")
    .slice(1)
    .map((row) => row.querySelector("td")?.textContent);
}

beforeEach(() => localStorage.clear());

describe("the preview's track table", () => {
  it("lists the tracks in the order the file holds them", () => {
    render(<RulesPreviewResults result={RESULT} />);

    expect(trackNumbers()).toEqual(["0", "1", "2", "3"]);
  });

  it("sorts the kinds as video, audio, subtitle rather than alphabetically", () => {
    render(<RulesPreviewResults result={RESULT} />);

    fireEvent.click(screen.getByRole("button", { name: "Type" }));

    expect(trackNumbers()).toEqual(["0", "1", "3", "2"]);
  });

  it("sorts channels as numbers, with the tracks that have none last whichever way it runs", () => {
    render(<RulesPreviewResults result={RESULT} />);

    fireEvent.click(screen.getByRole("button", { name: "Channels" }));
    expect(trackNumbers()).toEqual(["1", "3", "0", "2"]);

    fireEvent.click(screen.getByRole("button", { name: "Channels" }));
    expect(trackNumbers()).toEqual(["3", "1", "0", "2"]);
  });

  it("puts the kept tracks before the dropped ones when the actions are sorted", () => {
    render(<RulesPreviewResults result={RESULT} />);

    fireEvent.click(screen.getByRole("button", { name: "Action" }));

    expect(trackNumbers()[3]).toBe("2");
  });

  it("offers no sort on the reasons, and puts the file's order back with Reset columns", () => {
    render(<RulesPreviewResults result={RESULT} />);
    expect(
      screen.queryByRole("button", { name: "Reasons" }),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Channels" }));

    fireEvent.click(screen.getByRole("button", { name: "Columns" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));

    expect(trackNumbers()).toEqual(["0", "1", "2", "3"]);
  });
});

describe("the notes under the track table", () => {
  it("states the original language and the container changes when the plan has them", () => {
    render(
      <RulesPreviewResults
        result={{
          ...RESULT,
          original_language: {
            lookup_detail: "Matched by title.",
            lookup_status: "matched",
            note: "The original language is Japanese.",
            original_language: "jpn",
          },
          metadata_notes: ["The title tag would be cleared."],
        }}
      />,
    );

    expect(
      screen.getByText("Original language: The original language is Japanese."),
    ).toBeInTheDocument();
    expect(screen.getByText("Container changes")).toBeInTheDocument();
    expect(
      screen.getByText("The title tag would be cleared."),
    ).toBeInTheDocument();
  });

  it("shows neither heading when the plan has no such notes", () => {
    render(<RulesPreviewResults result={RESULT} />);

    expect(screen.queryByText(/Original language:/)).toBeNull();
    expect(screen.queryByText("Container changes")).toBeNull();
  });
});

it("shows a track with no codec as a dash", () => {
  render(
    <RulesPreviewResults
      result={{
        ...RESULT,
        tracks: [track({ index: 0, type: "audio", codec: "" })],
      }}
    />,
  );

  expect(screen.getAllByRole("row")[1]).toHaveTextContent("—");
});
