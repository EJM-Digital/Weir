import { describe, expect, it } from "vitest";

import type {
  LibraryFile,
  LibraryTotals,
} from "../../lib/processing/library-mode-api";
import {
  emptyStatusLine,
  groupOf,
  savingLine,
  statusWords,
  type LibraryStatus,
} from "./library-model";

const file = (fields: Partial<LibraryFile>) =>
  ({
    path: "Show/Season 1/Show.S01E01.mkv",
    classification: "would_change",
    status: "needs_cleaning",
    problem_kind: null,
    manager_title: null,
    reason: null,
    summary: null,
    removed_audio_tracks: 0,
    removed_subtitle_tracks: 0,
    ...fields,
  }) as LibraryFile;

describe("what a file is grouped under", () => {
  it.each([
    ["its manager's title", { manager_title: "Show (2024)" }, "Show (2024)"],
    ["its season and show", {}, "Show · Season 1"],
    ["its folder", { path: "Films/Film (2024)/Film.mkv" }, "Film (2024)"],
    [
      "a season folder with nothing above it",
      { path: "Season 1/ep.mkv" },
      "Season 1",
    ],
    ["its own name when it sits alone", { path: "film.mkv" }, "film.mkv"],
    ["a placeholder when it has no path", { path: "" }, "Files"],
  ])("is %s", (_what, fields, group) => {
    expect(groupOf(file(fields))).toBe(group);
  });
});

describe("what the table says about a file held back", () => {
  const held = (fields: Partial<LibraryFile>) =>
    statusWords(file({ status: "cant_clean_yet", ...fields }));

  it.each([
    [
      "the first sentence of its reason",
      {
        classification: "cannot_process",
        reason: "It has no video. Try again.",
      },
      "It has no video",
    ],
    [
      "its whole reason when that is one sentence with no stop",
      { classification: "cannot_process", reason: "  It has no video  " },
      "It has no video",
    ],
    [
      "its summary when it has no reason",
      { classification: "cannot_process", summary: "Cannot read the file." },
      "Cannot read the file",
    ],
    [
      "a plain answer when it has neither",
      { classification: "cannot_process" },
      "Weir cannot clean this one",
    ],
    ["still seeding when nothing else is known", {}, "Still seeding"],
    [
      "the problem when one is named",
      { problem_kind: "unreadable" },
      "Weir cannot read it",
    ],
  ])("gives %s", (_what, fields, words) => {
    expect(held(fields as Partial<LibraryFile>)).toBe(words);
  });
});

describe("what the table says when a status holds no file", () => {
  it.each([
    ["needs_cleaning", "No files need cleaning right now."],
    ["cleaning", "No files are being cleaned right now."],
    ["matches", "No files match your rules right now."],
    ["cant_clean_yet", "No files are held back right now."],
    ["left_alone", "You have not set any file aside."],
  ] as [LibraryStatus, string][])("says %s", (status, line) => {
    expect(emptyStatusLine(status)).toBe(line);
  });
});

describe("what cleaning would win back", () => {
  const totals = (fields: Partial<LibraryTotals>) =>
    ({
      estimated_bytes_saved: 0,
      would_change: 0,
      total_removed_audio_tracks: 0,
      total_removed_subtitle_tracks: 0,
      ...fields,
    }) as LibraryTotals;

  it.each([
    [
      "files that do not say how big their tracks are",
      {
        would_change: 3,
        total_removed_audio_tracks: 2,
        total_removed_subtitle_tracks: 3,
      },
      "5 tracks would come out; these files do not say how big each one is",
    ],
    [
      "a single such track",
      { would_change: 1, total_removed_audio_tracks: 1 },
      "1 track would come out; these files do not say how big each one is",
    ],
    ["nothing to change", {}, "Nothing to reclaim here at the moment"],
  ])("says it for %s", (_what, fields, line) => {
    expect(savingLine(totals(fields))).toBe(line);
  });

  it("says it when no scan has run", () => {
    expect(savingLine(undefined)).toBe("Nothing to reclaim here at the moment");
  });
});
