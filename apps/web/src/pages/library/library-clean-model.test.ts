import { describe, expect, it } from "vitest";

import type {
  LibraryCleanResult,
  LibraryFile,
} from "../../lib/processing/library-mode-api";
import { confirmationSummary, outcomeLines } from "./library-clean-model";

function known(path: string, overrides: Partial<LibraryFile>): LibraryFile {
  return {
    path,
    leave_alone: false,
    problem_kind: null,
    ...overrides,
  } as LibraryFile;
}

function result(overrides: Partial<LibraryCleanResult>): LibraryCleanResult {
  return {
    kind: "cleaned",
    queued: 0,
    job_ids: [],
    files_count: 0,
    tracks_count: 0,
    estimated_bytes_saved: 0,
    skipped_paths: [],
    warnings: [],
    ...overrides,
  };
}

describe("confirmationSummary", () => {
  it("counts the tracks and files, and what cleaning gives back", () => {
    expect(
      confirmationSummary({
        kind: "confirmation_required",
        detail: "",
        files_count: 2,
        tracks_count: 1,
        estimated_bytes_saved: 318 * 1024 * 1024,
      }),
    ).toBe("1 track will come out of 2 files, giving back about 318 MB.");
  });
});

describe("outcomeLines", () => {
  it("says what was queued", () => {
    expect(outcomeLines(result({ queued: 3 }), [])).toEqual([
      "3 files are queued to clean.",
    ]);
  });

  it("names what you left alone apart from what Weir skipped because it is still seeding", () => {
    const lines = outcomeLines(
      result({
        queued: 0,
        skipped_paths: ["D:/a/One.mkv", "D:/a/Two.mkv", "D:/a/Three.mkv"],
      }),
      [
        known("D:/a/One.mkv", { leave_alone: true }),
        known("D:/a/Two.mkv", { problem_kind: "seeding" }),
      ],
    );

    expect(lines).toEqual([
      "Nothing was queued.",
      "Left alone, so not cleaned: One.mkv.",
      "Weir skipped 1 that is still seeding: Two.mkv.",
      "Weir skipped 1: Three.mkv.",
    ]);
  });

  it("says several seeding files are still seeding, in the plural", () => {
    const lines = outcomeLines(
      result({ queued: 1, skipped_paths: ["D:/a/One.mkv", "D:/a/Two.mkv"] }),
      [
        known("D:/a/One.mkv", { problem_kind: "seeding" }),
        known("D:/a/Two.mkv", { problem_kind: "seeding" }),
      ],
    );

    expect(lines).toEqual([
      "1 file is queued to clean.",
      "Weir skipped 2 that are still seeding: One.mkv, Two.mkv.",
    ]);
  });
});
