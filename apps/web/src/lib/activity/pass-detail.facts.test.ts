import { describe, expect, it } from "vitest";

import {
  cleanupStatus,
  finishedNothing,
  formatDuration,
  formatSavings,
  outcomeLabel,
  splitTrackList,
} from "./pass-detail";

describe("outcomeLabel", () => {
  it.each([
    [{ outcome: "live_output_written" }, "File processed"],
    [{ outcome: "live_skipped_not_required" }, "No changes needed"],
    [
      { outcome: "live_skipped_not_required", pass_through_unchanged: true },
      "Passed through unchanged",
    ],
    [{ outcome: "failed_before_execution" }, "Could not check file"],
    [{ outcome: "failed_during_execution" }, "Could not process file"],
    [{ outcome: "something_new" }, "something_new"],
    [{}, "Unknown outcome"],
  ])("says %j as %s", (detail, label) => {
    expect(outcomeLabel(detail)).toBe(label);
  });
});

describe("finishedNothing", () => {
  it.each([
    ["skipped_guardrail", true],
    ["source_not_ready", true],
    ["source_gone", true],
    ["live_output_written", false],
    [null, false],
    [undefined, false],
  ])("is %s -> %s", (outcome, expected) => {
    expect(finishedNothing(outcome)).toBe(expected);
  });
});

describe("formatDuration", () => {
  it.each([
    [0, "0s"],
    [59.6, "1m 00s"],
    [3600, "1h 00m"],
    [Number.NaN, null],
    [null, null],
    [undefined, null],
  ])("reads %s as %s", (seconds, text) => {
    expect(formatDuration(seconds)).toBe(text);
  });
});

describe("formatSavings", () => {
  const mb = 1024 ** 2;

  it.each([
    [100 * mb, 50 * mb, "Saved 50.0 MB (50.0%)"],
    [50 * mb, 100 * mb, "Grew by 50.0 MB (100.0%)"],
    [100 * mb, 100 * mb - 1000, "Size basically unchanged (1000 B saved)"],
    [0, 5, "Grew by 5 B"],
    [0, -5, "Saved 5 B"],
    [Number.NaN, 5, null],
    [5, Number.POSITIVE_INFINITY, null],
    [5, undefined, null],
  ])("reads %s to %s as %s", (source, output, text) => {
    expect(formatSavings(source, output)).toBe(text);
  });
});

describe("cleanupStatus", () => {
  it("reports a movie's removed folders", () => {
    expect(
      cleanupStatus({
        media_scope: "movie",
        source_folder_deleted: true,
        movie_output_folder_deleted: true,
      }),
    ).toEqual([
      {
        label: "Watched-folder cleanup",
        value: "Removed source release folder",
      },
      { label: "Output-folder cleanup", value: "Removed output title folder" },
    ]);
  });

  it.each([
    [
      "the reasons given",
      {
        source_folder_skip_reason: "Other files remain",
        movie_output_folder_skip_reason: "Not empty",
        movie_output_truth_check: "ignored",
      },
      "Other files remain",
      "Not empty",
    ],
    [
      "the output check when it gave no reason",
      { movie_output_truth_check: "files remain" },
      "Not removed",
      "Not removed (files remain)",
    ],
    ["nothing", {}, "Not removed", "Not removed"],
  ])(
    "reports a movie's kept folders with %s",
    (_what, fields, watched, output) => {
      expect(
        cleanupStatus({ media_scope: "movie", ...fields }).map(
          (row) => row.value,
        ),
      ).toEqual([watched, output]);
    },
  );

  it("reports a season's removed folders", () => {
    expect(
      cleanupStatus({
        media_scope: "tv",
        tv_season_folder_deleted: true,
        tv_output_season_folder_deleted: true,
      }).map((row) => row.value),
    ).toEqual([
      "Removed watched season folder",
      "Removed output season folder",
    ]);
  });

  it.each([
    [
      "the reasons given",
      {
        tv_season_folder_skip_reason: "Other episodes remain",
        tv_output_season_folder_skip_reason: "Not empty",
        tv_output_truth_check: "ignored",
      },
      "Other episodes remain",
      "Not empty",
    ],
    [
      "the output check when it gave no reason",
      { tv_output_truth_check: "episodes remain" },
      "Not removed",
      "Not removed (episodes remain)",
    ],
    ["nothing", {}, "Not removed", "Not removed"],
  ])(
    "reports a season's kept folders with %s",
    (_what, fields, watched, output) => {
      expect(
        cleanupStatus({ media_scope: "tv", ...fields }).map((row) => row.value),
      ).toEqual([watched, output]);
    },
  );

  it("reports nothing for a scope it does not know", () => {
    expect(cleanupStatus({ media_scope: "music" })).toEqual([]);
    expect(cleanupStatus({})).toEqual([]);
  });
});

describe("splitTrackList", () => {
  it.each([
    ["eng AC3 5.1; jpn AAC 2.0", ["eng AC3 5.1", "jpn AAC 2.0"]],
    ["eng, jpn,, ", ["eng", "jpn"]],
    ["", []],
    [undefined, []],
  ])("splits %j", (value, tracks) => {
    expect(splitTrackList(value)).toEqual(tracks);
  });
});
