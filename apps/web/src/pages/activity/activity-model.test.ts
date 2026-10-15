import { describe, expect, it } from "vitest";
import type { ProcessingFile } from "../../lib/processing/files-api";
import {
  agoWords,
  awaitsImport,
  handbackStory,
  importedLabel,
  readableTrack,
  detailSizes,
  sizesFromRecord,
  tracksFromRecord,
  workflowKindLookup,
} from "./activity-model";

function file(partial: Partial<ProcessingFile>): ProcessingFile {
  return {
    id: 1,
    library_id: 1,
    library_name: "TV",
    relative_path: "Show/Show.S01E01.mkv",
    status: "processed",
    status_reason: "",
    blocked_by_connection: null,
    size_bytes: 1,
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

describe("readable tracks", () => {
  it("names the language, the layout and the codec, and drops the stream number", () => {
    expect(readableTrack("fre eac3 6 ch (stream 2)")).toBe("French 5.1 E-AC-3");
    expect(readableTrack("ger aac 2 ch")).toBe("German 2.0 AAC");
  });

  it("names a bare language code", () => {
    expect(readableTrack("spa")).toBe("Spanish");
    expect(readableTrack("English")).toBe("English");
  });
});

describe("tracks from a pass record", () => {
  it("lists kept tracks and removed ones with the planner's reason", () => {
    const tracks = tracksFromRecord({
      audio_after: "English 5.1 E-AC-3",
      removed_audio: [
        "fre eac3 6 ch (stream 2): removed (not selected — eng eac3 6 ch (stream 1) kept)",
        "English 2.0 (commentary excluded — remove commentary enabled)",
      ],
      subs_after: "English · Japanese",
      removed_subtitles: ["spa"],
      removed_images: ["mjpeg 600x900"],
    });
    expect(tracks).toEqual([
      { kind: "Audio", what: "English 5.1 E-AC-3", kept: true, why: "" },
      {
        kind: "Audio",
        what: "French 5.1 E-AC-3",
        kept: false,
        why: "Not selected — English 5.1 E-AC-3 kept",
      },
      {
        kind: "Audio",
        what: "English 2.0",
        kept: false,
        why: "Commentary excluded — remove commentary enabled",
      },
      { kind: "Subtitle", what: "English", kept: true, why: "" },
      { kind: "Subtitle", what: "Japanese", kept: true, why: "" },
      { kind: "Subtitle", what: "Spanish", kept: false, why: "" },
      { kind: "Other", what: "mjpeg 600x900", kept: false, why: "" },
    ]);
  });

  it("treats the planner's dash and None as no track", () => {
    expect(tracksFromRecord({ audio_after: "—", subs_after: "None" })).toEqual(
      [],
    );
  });

  it("reports sizes only when both are known", () => {
    expect(
      sizesFromRecord({ source_size_bytes: 100, output_size_bytes: 60 }),
    ).toEqual({
      before: 100,
      after: 60,
      saved: 40,
    });
    expect(sizesFromRecord({ source_size_bytes: 100 })).toBeNull();
  });

  it("always has Before, After and Saved to show, even when nothing changed", () => {
    expect(
      detailSizes(file({ status: "processed", size_bytes: 100 }), {
        source_size_bytes: 100,
        output_size_bytes: 60,
      }),
    ).toEqual({
      before: 100,
      after: 60,
      saved: 40,
      notRecorded: false,
      note: null,
    });
    // Handed back as it was: the same size after, and nothing saved.
    expect(
      detailSizes(file({ status: "passed_through", size_bytes: 100 }), null),
    ).toMatchObject({ before: 100, after: 100, saved: 0 });
    // Never written: its size, and "not written" rather than a gap.
    expect(
      detailSizes(file({ status: "cancelled", size_bytes: 100 }), null),
    ).toMatchObject({
      before: 100,
      after: null,
      saved: null,
      note: "Weir has not written a new copy of this file.",
    });
  });

  describe("a file that went through unchanged", () => {
    const handedBack =
      "Weir handed the file back as it was, so its size did not change.";

    it("is told as handed back when the record says it was passed through", () => {
      expect(
        detailSizes(file({ status: "processed", size_bytes: 100 }), {
          outcome: "live_output_written",
          pass_through_unchanged: true,
        }),
      ).toEqual({
        before: 100,
        after: 100,
        saved: 0,
        notRecorded: false,
        note: handedBack,
      });
    });

    it("is told as handed back when it already matched the rules", () => {
      expect(
        detailSizes(file({ status: "processed", size_bytes: 100 }), {
          outcome: "live_skipped_not_required",
          source_size_bytes: 100,
          output_size_bytes: 100,
        }),
      ).toMatchObject({ before: 100, after: 100, saved: 0, note: handedBack });
    });
  });

  describe("a file Weir cleaned", () => {
    it("shows the exact saving when it is far smaller than the file", () => {
      const sizes = detailSizes(
        file({ status: "processed", size_bytes: 3826958687 }),
        {
          outcome: "live_output_written",
          pass_through_unchanged: false,
          source_size_bytes: 3826958687,
          output_size_bytes: 3826958687 - 412 * 1024,
        },
      );
      expect(sizes).toMatchObject({ saved: 412 * 1024, note: null });
      expect(sizes.note).toBeNull();
    });

    it("says the size was not recorded rather than calling it handed back as it was", () => {
      const sizes = detailSizes(
        file({ status: "processed", size_bytes: 100 }),
        {
          outcome: "live_output_written",
          pass_through_unchanged: false,
          source_size_bytes: null,
          output_size_bytes: null,
          source_fingerprint_size: 3826958687,
        },
      );
      expect(sizes).toMatchObject({
        before: 3826958687,
        after: null,
        saved: null,
        notRecorded: true,
      });
      expect(sizes.note).toMatch(/^Size not recorded/);
      expect(sizes.note).not.toMatch(/as it was/);
    });
  });
});

describe("what became of the copy Weir handed back", () => {
  const now = Date.parse("2026-08-19T04:06:00Z");
  const copy = {
    output_path: "/hand-back/Show/Show.S01E01.mkv",
    written_at: "2026-08-19T03:00:00",
    outcome: null,
    outcome_by: null,
    outcome_at: null,
    imported_path: null,
    outcome_reason: null,
    released_at: null,
    settled_at: null,
    release_note: null,
  } as const;

  it("says who imported it, when, where it went and what Weir did with its copy", () => {
    const handback = {
      ...copy,
      outcome: "imported" as const,
      outcome_by: "Sonarr",
      outcome_at: "2026-08-19T04:00:00",
      imported_path: "/tv/Show/Season 01/Show - S01E01.mkv",
      released_at: "2026-08-19T04:00:00",
      settled_at: "2026-08-19T04:00:00",
      release_note:
        "Weir removed its copy from the hand-back folder, because Sonarr has the file now.",
    };
    expect(handbackStory(handback, now, "linked")).toEqual({
      heading: "Imported by Sonarr",
      sentence:
        "Sonarr imported it 6 min ago. It is in the library at /tv/Show/Season 01/Show - S01E01.mkv. Weir removed its copy from the hand-back folder, because Sonarr has the file now.",
      meaning: "done",
    });
    expect(importedLabel(file({ handback }))).toBe("Imported by Sonarr");
  });

  it("says when a media manager will not import it, and that Weir kept its copy", () => {
    const story = handbackStory(
      {
        ...copy,
        outcome: "not-imported",
        outcome_by: "Deluno",
        release_note:
          "Deluno will not import this file: The release is a sample. Weir kept its copy in the hand-back folder.",
      },
      now,
      "linked",
    );
    expect(story?.heading).toBe("Deluno will not import it");
    expect(story?.meaning).toBe("attention");
  });

  it("says Weir is still waiting when no media manager has said anything", () => {
    expect(handbackStory(copy, now, "linked")?.sentence).toBe(
      "Weir put the cleaned copy at /hand-back/Show/Show.S01E01.mkv 1 h ago for your media manager to import. No media manager has said it imported it yet.",
    );
    expect(handbackStory(copy, now, "linked")?.meaning).toBe("todo");
    expect(handbackStory(null, now, "linked")).toBeNull();
    expect(importedLabel(file({ handback: copy }))).toBeNull();
  });

  it("says a Weir-only workflow's copy is in the output folder, with no import or waiting wording", () => {
    const story = handbackStory(copy, now, "weir_only");

    expect(story).toEqual({
      heading: "Cleaned copy",
      sentence:
        "The cleaned copy is in the output folder, at /hand-back/Show/Show.S01E01.mkv. It was written 1 h ago.",
      meaning: "done",
    });
    expect(story?.sentence).not.toMatch(/import|media manager|yet/i);
  });

  it("still tells what a media manager said about a copy whose workflow has since been unlinked", () => {
    const story = handbackStory(
      { ...copy, outcome: "imported", outcome_by: "Sonarr" },
      now,
      "weir_only",
    );

    expect(story?.heading).toBe("Imported by Sonarr");
  });
});

describe("ago", () => {
  it("reads the server's zone-less times as UTC", () => {
    const now = Date.parse("2026-08-19T04:06:00Z");
    expect(agoWords("2026-08-19T04:00:00", now)).toBe("6 min ago");
  });
});

describe("whether a handed-back copy awaits a media manager", () => {
  const unanswered = {
    output_path: "/hand-back/Show/Show.S01E01.mkv",
    written_at: "2026-08-19T03:00:00",
    outcome: null,
    outcome_by: null,
    outcome_at: null,
    imported_path: null,
    outcome_reason: null,
    released_at: null,
    settled_at: null,
    release_note: null,
  } as const;

  it("does while no manager has answered and Weir has not settled it", () => {
    expect(awaitsImport(unanswered, "linked")).toBe(true);
  });

  it("does not once a manager imported it or refused it", () => {
    expect(awaitsImport({ ...unanswered, outcome: "imported" }, "linked")).toBe(
      false,
    );
    expect(
      awaitsImport({ ...unanswered, outcome: "not-imported" }, "linked"),
    ).toBe(false);
  });

  it("does not once Weir settled it with a note, in a Weir-only workflow, or when there is no copy", () => {
    expect(
      awaitsImport(
        {
          ...unanswered,
          settled_at: "2026-08-19T04:00:00",
          release_note: "Weir removed its copy.",
        },
        "linked",
      ),
    ).toBe(false);
    expect(awaitsImport(unanswered, "weir_only")).toBe(false);
    expect(awaitsImport(null, "linked")).toBe(false);
  });

  it("agrees with the story told of the copy: it is waiting exactly when the story says to do", () => {
    const now = Date.parse("2026-08-19T04:06:00Z");
    const copies = [
      unanswered,
      { ...unanswered, outcome: "imported" as const },
      { ...unanswered, outcome: "not-imported" as const },
    ];
    for (const copy of copies) {
      for (const kind of ["linked", "weir_only"] as const) {
        expect(awaitsImport(copy, kind)).toBe(
          handbackStory(copy, now, kind)?.meaning === "todo",
        );
      }
    }
  });
});

describe("the kind of workflow a file belongs to", () => {
  const workflows = [
    { id: 1, manager_connection_ids: [4] },
    { id: 2, manager_connection_ids: [] },
  ];

  it("is linked when a manager is, and Weir only when none is", () => {
    const kindOf = workflowKindLookup(workflows);

    expect(kindOf({ library_id: 1 })).toBe("linked");
    expect(kindOf({ library_id: 2 })).toBe("weir_only");
  });

  it("is linked for a workflow that has been removed, and unknown until the workflows are", () => {
    expect(workflowKindLookup(workflows)({ library_id: 9 })).toBe("linked");
    expect(workflowKindLookup(undefined)({ library_id: 1 })).toBeNull();
  });
});

it("says nothing for a time it cannot read", () => {
  const now = Date.parse("2026-08-19T04:06:00Z");
  expect(agoWords("not a time", now)).toBe("");
  expect(agoWords(" ", now)).toBe("");
});
