import { describe, expect, it } from "vitest";

import type {
  ProcessingFile,
  ProcessingFileHandback,
} from "../../lib/processing/files-api";
import {
  detailSizes,
  handbackStory,
  importedLabel,
  sizesFromRecord,
  tracksFromRecord,
} from "./activity-model";

const now = Date.parse("2026-08-19T04:06:00Z");

const waiting: ProcessingFileHandback = {
  output_path: "/hand-back/Film.mkv",
  written_at: "2026-08-19T03:00:00",
  outcome: null,
  outcome_by: null,
  outcome_at: null,
  imported_path: null,
  outcome_reason: null,
  released_at: null,
  settled_at: null,
  release_note: null,
};

describe("the story of a handed-back copy that has little to tell", () => {
  it("says an import by an unnamed manager, with no time, place or note", () => {
    expect(
      handbackStory({ ...waiting, outcome: "imported" }, now, "linked"),
    ).toEqual({
      heading: "Imported by your media manager",
      sentence: "Your media manager imported it.",
      meaning: "done",
    });
  });

  it("says what a refusal means for the copy when the manager gave no reason", () => {
    expect(
      handbackStory(
        { ...waiting, outcome: "not-imported", outcome_by: "Deluno" },
        now,
        "linked",
      ),
    ).toEqual({
      heading: "Deluno will not import it",
      sentence:
        "Deluno will not import this file. Weir kept its copy at /hand-back/Film.mkv.",
      meaning: "attention",
    });
  });

  it("tells a copy Weir settled itself by the note it left", () => {
    expect(
      handbackStory(
        {
          ...waiting,
          settled_at: "2026-08-19T04:00:00",
          release_note: "Weir removed its copy after a day.",
        },
        now,
        "linked",
      ),
    ).toEqual({
      heading: "Handed back",
      sentence: "Weir removed its copy after a day.",
      meaning: "idle",
    });
  });

  it.each([
    [
      "with the time it was written",
      "2026-08-19T03:00:00",
      " It was written 1 h ago.",
    ],
    ["with no time written", null, ""],
  ])(
    "places a copy in a Weir-only output folder %s",
    (_what, writtenAt, tail) => {
      expect(
        handbackStory({ ...waiting, written_at: writtenAt }, now, "weir_only"),
      ).toEqual({
        heading: "Cleaned copy",
        sentence: `The cleaned copy is in the output folder, at /hand-back/Film.mkv.${tail}`,
        meaning: "done",
      });
    },
  );

  it("waits for a manager without a time when none was written", () => {
    expect(
      handbackStory({ ...waiting, written_at: null }, now, "linked")?.sentence,
    ).toBe(
      "Weir put the cleaned copy at /hand-back/Film.mkv for your media manager to import. No media manager has said it imported it yet.",
    );
  });

  it("names an unnamed manager in the file list", () => {
    expect(
      importedLabel({
        handback: { ...waiting, outcome: "imported" },
      } as ProcessingFile),
    ).toBe("Imported by your media manager");
  });
});

describe("the sizes in a pass record", () => {
  it.each([
    [
      "both sizes",
      { source_size_bytes: 100, output_size_bytes: 60 },
      { before: 100, after: 60, saved: 40 },
    ],
    [
      "an output that grew",
      { source_size_bytes: 60, output_size_bytes: 100 },
      { before: 60, after: 100, saved: 0 },
    ],
    ["an empty source", { source_size_bytes: 0, output_size_bytes: 60 }, null],
    ["an empty output", { source_size_bytes: 60, output_size_bytes: 0 }, null],
    [
      "a size as text",
      { source_size_bytes: "60", output_size_bytes: 30 },
      null,
    ],
    ["no sizes", {}, null],
  ])("reads %s", (_what, detail, sizes) => {
    expect(sizesFromRecord(detail)).toEqual(sizes);
  });
});

describe("the sizes Activity shows for a file", () => {
  const as = (status: string, size_bytes: number) =>
    ({ status, size_bytes }) as Pick<ProcessingFile, "status" | "size_bytes">;

  it("says a measured pass that handed the file back unchanged kept its size", () => {
    expect(
      detailSizes(as("processed", 1), {
        source_size_bytes: 100,
        output_size_bytes: 100,
        pass_through_unchanged: true,
      }),
    ).toMatchObject({
      before: 100,
      after: 100,
      saved: 0,
      note: "Weir handed the file back as it was, so its size did not change.",
    });
  });

  it.each([
    [
      "the size the pass recorded",
      as("passed_through", 7),
      { source_size_bytes: 50 },
      50,
    ],
    [
      "the size of the file when its record has none",
      as("passed_through", 7),
      { outcome: "live_skipped_not_required" },
      7,
    ],
    [
      "the fingerprint size when that is all the record has",
      as("processed", 7),
      { source_fingerprint_size: 80, output_copied_without_remux: true },
      80,
    ],
  ])("gives a file handed back as it was %s", (_what, file, detail, size) => {
    expect(detailSizes(file, detail)).toMatchObject({
      before: size,
      after: size,
      saved: 0,
      notRecorded: false,
    });
  });

  it("knows no saving for a handed-back file of unknown size", () => {
    expect(detailSizes(as("passed_through", 0), null)).toMatchObject({
      before: null,
      after: null,
      saved: null,
    });
  });

  it("shows a file Weir has not written a copy of at its own size", () => {
    expect(detailSizes(as("pending", 9), null)).toEqual({
      before: 9,
      after: null,
      saved: null,
      notRecorded: false,
      note: "Weir has not written a new copy of this file.",
    });
    expect(detailSizes(as("pending", 0), {}).before).toBeNull();
  });
});

describe("a removal line with no reason to give", () => {
  it("lists the track with an empty reason", () => {
    expect(
      tracksFromRecord({ removed_audio: ["English 2.0 ()"] }),
    ).toMatchObject([
      { kind: "Audio", what: "English 2.0", why: "", kept: false },
    ]);
  });
});
