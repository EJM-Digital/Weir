import { describe, expect, it } from "vitest";
import type { ProcessingFile } from "../../lib/processing/files-api";
import type { LibraryClean } from "../../lib/processing/library-cleans-api";
import {
  cleanEntry,
  downloadEntry,
  entryGroup,
  entryMeaning,
  hasRejectedFiles,
  activityEntries,
  activityGroupOf,
  inGroup,
  retryableFailures,
  waitsOnAPerson,
  ACTIVITY_GROUPS,
} from "./activity-entries";
import { recordedProcessingFile } from "../../test/recorded";

function file(partial: Partial<ProcessingFile>): ProcessingFile {
  return {
    ...recordedProcessingFile(),
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

const UNANSWERED_HANDBACK = {
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

function clean(partial: Partial<LibraryClean>): LibraryClean {
  return {
    kind: "library_clean",
    id: 1,
    library_id: 2,
    library_name: "Films",
    relative_path: "Heat (1995)/Heat.mkv",
    outcome: "cleaned",
    detail: "Cleaned Heat.mkv: removed 1 audio track.",
    trigger: null,
    recorded_at: "2026-08-19T04:00:00",
    ...partial,
  };
}

describe("activity groups", () => {
  it("puts a file a manager still has under In progress, since it only waits its turn", () => {
    expect(activityGroupOf(file({ status: "blocked_upstream" }))).toBe(
      "working",
    );
  });

  it("puts a file Weir gave up on under Failed, whatever it failed on and however often", () => {
    expect(
      activityGroupOf(
        file({ status: "processing_failed", failure_attempts: 5 }),
      ),
    ).toBe("failed");
  });

  it("moves on_hold into On hold rather than In progress", () => {
    expect(activityGroupOf(file({ status: "on_hold" }))).toBe("needs");
  });

  it("gives a skip its own neutral group rather than counting it as failed", () => {
    expect(activityGroupOf(file({ status: "skipped" }))).toBe("skipped");
  });

  it("sorts the rest by what is happening to them", () => {
    expect(activityGroupOf(file({ status: "unprocessed" }))).toBe("working");
    expect(activityGroupOf(file({ status: "passed_through" }))).toBe(
      "finished",
    );
    expect(activityGroupOf(file({ status: "rejected" }))).toBe("failed");
    expect(activityGroupOf(file({ status: "disabled" }))).toBeNull();
  });
});

describe("a library clean's group", () => {
  it("follows its outcome: cleaned finishes, skipped is neutral, failed is failed", () => {
    expect(entryGroup(cleanEntry(clean({ outcome: "cleaned" })))).toBe(
      "finished",
    );
    expect(entryGroup(cleanEntry(clean({ outcome: "skipped" })))).toBe(
      "skipped",
    );
    expect(entryGroup(cleanEntry(clean({ outcome: "failed" })))).toBe("failed");
  });
});

describe("a cleaned copy waiting for its media manager", () => {
  const waiting = file({
    status: "processed",
    handback: { ...UNANSWERED_HANDBACK },
  });
  const importedCopy = file({
    status: "processed",
    handback: {
      ...UNANSWERED_HANDBACK,
      outcome: "imported",
      outcome_by: "Sonarr",
    },
  });

  it("reads as waiting, not done, and is listed under All and In progress but not Finished", () => {
    const entry = downloadEntry(waiting, "linked");

    expect(entryMeaning(entry)).toBe("todo");
    expect(entryGroup(entry)).toBe("working");
    expect(inGroup(entry, "all")).toBe(true);
    expect(inGroup(entry, "working")).toBe(true);
    expect(inGroup(entry, "finished")).toBe(false);
  });

  it("does not wait on a person, so it is not under Needs you", () => {
    expect(waitsOnAPerson(waiting)).toBe(false);
    expect(inGroup(downloadEntry(waiting, "linked"), "attention")).toBe(false);
  });

  it("is done once the manager has imported it", () => {
    const entry = downloadEntry(importedCopy, "linked");

    expect(entryMeaning(entry)).toBe("done");
    expect(entryGroup(entry)).toBe("finished");
  });

  it("is done in a Weir-only workflow, which has no manager to wait for, and while the workflows are unknown", () => {
    expect(entryMeaning(downloadEntry(waiting, "weir_only"))).toBe("done");
    expect(entryMeaning(downloadEntry(waiting))).toBe("done");
  });

  it("counts under In progress when entries are built with the workflows' kinds", () => {
    const entries = activityEntries([waiting], [], () => "linked");

    expect(entries.filter((entry) => inGroup(entry, "working"))).toHaveLength(
      1,
    );
    expect(entries.filter((entry) => inGroup(entry, "finished"))).toHaveLength(
      0,
    );
  });
});

describe("a file its media manager still has", () => {
  it("reads as waiting and does not wait on a person", () => {
    const held = file({ status: "blocked_upstream" });

    expect(entryMeaning(downloadEntry(held))).toBe("todo");
    expect(waitsOnAPerson(held)).toBe(false);
    expect(inGroup(downloadEntry(held), "attention")).toBe(false);
    expect(inGroup(downloadEntry(held), "needs")).toBe(false);
  });
});

describe("inGroup", () => {
  it("puts every entry in All, whatever its own group is", () => {
    expect(inGroup(downloadEntry(file({ status: "disabled" })), "all")).toBe(
      true,
    );
  });
});

describe("activityEntries", () => {
  it("lists downloads and cleans together, newest change first", () => {
    const entries = activityEntries(
      [file({ id: 1, updated_at: "2026-08-19T03:00:00" })],
      [clean({ id: 1, recorded_at: "2026-08-19T05:00:00" })],
    );
    expect(entries.map((entry) => entry.kind)).toEqual([
      "library_clean",
      "download",
    ]);
  });
});

describe("retryableFailures", () => {
  it("keeps only the failed downloads, leaving out a failed clean or any other status", () => {
    const entries = activityEntries(
      [
        file({ id: 1, status: "processing_failed" }),
        file({ id: 2, status: "skipped" }),
      ],
      [clean({ id: 1, outcome: "failed" })],
    );
    expect(retryableFailures(entries).map((f) => f.id)).toEqual([1]);
  });
});

describe("hasRejectedFiles", () => {
  it("is true only when a listed download was rejected", () => {
    const rejected = activityEntries([file({ id: 1, status: "rejected" })], []);
    const failed = activityEntries(
      [file({ id: 2, status: "processing_failed" })],
      [clean({ id: 1, outcome: "failed" })],
    );

    expect([hasRejectedFiles(rejected), hasRejectedFiles(failed)]).toEqual([
      true,
      false,
    ]);
  });
});

describe("the files that wait on a person", () => {
  const skippedBy = (reason: string) =>
    file({ status: "skipped", status_reason: reason });

  it("are the failed and rejected ones, a hold with no clock on it, and a skip by one of the workflow's rules", () => {
    expect(waitsOnAPerson(file({ status: "processing_failed" }))).toBe(true);
    expect(waitsOnAPerson(file({ status: "rejected" }))).toBe(true);
    expect(waitsOnAPerson(file({ status: "on_hold", hold_until: null }))).toBe(
      true,
    );
    expect(
      waitsOnAPerson(
        skippedBy("Skipped because its path matches an exclude pattern."),
      ),
    ).toBe(true);
  });

  it("leave out a hold that is counting down, a routine skip and a file Weir is still working on", () => {
    expect(
      waitsOnAPerson(
        file({ status: "on_hold", hold_until: "2026-08-19T05:00:00" }),
      ),
    ).toBe(false);
    expect(waitsOnAPerson(skippedBy("Not a video file."))).toBe(false);
    expect(waitsOnAPerson(file({ status: "unprocessed" }))).toBe(false);
    expect(waitsOnAPerson(file({ status: "processed" }))).toBe(false);
  });

  it("leave out a file under the workflow's minimum size, which is the minimum doing its job", () => {
    const extra = file({
      status: "skipped",
      status_reason: "Skipped because Weir leaves small files alone.",
      skip_kind: "below_minimum_size",
    });

    expect(waitsOnAPerson(extra)).toBe(false);
    expect(inGroup(downloadEntry(extra), "attention")).toBe(false);
    expect(entryMeaning(downloadEntry(extra))).toBe("idle");
  });

  it("go by the skip's code, never its sentence", () => {
    expect(
      waitsOnAPerson(
        skippedBy(
          "Skipped because this file is 12.1 MB, under the 50 MB minimum.",
        ),
      ),
    ).toBe(true);
  });

  it("keep every other rule's skip, whatever its size or date", () => {
    for (const reason of [
      "Skipped because this file is 90.0 MB and exceeds the 50 MB workflow maximum.",
      "Skipped because its path does not match this workflow's include patterns.",
      "Skipped because its path matches this workflow's exclude patterns.",
      "Skipped because its last-modified time (2026-01-01T00:00:00) is before this workflow's allowed window.",
    ]) {
      expect(waitsOnAPerson(skippedBy(reason))).toBe(true);
    }
  });

  it("make up the Needs you view, whichever group each is in, and never a library clean", () => {
    const failed = downloadEntry(file({ status: "processing_failed" }));
    const held = downloadEntry(file({ status: "on_hold" }));
    const finished = downloadEntry(file({ status: "processed" }));
    const cleanFailed = cleanEntry(clean({ outcome: "failed" }));

    expect(
      [failed, held, finished, cleanFailed].filter((entry) =>
        inGroup(entry, "attention"),
      ),
    ).toEqual([failed, held]);
  });

  it("have a chip of their own, named Needs you, apart from the files that are on hold", () => {
    expect(ACTIVITY_GROUPS.map((group) => group.label)).toEqual([
      "All",
      "In progress",
      "Finished",
      "Needs you",
      "On hold",
      "Skipped",
      "Failed",
      "Kept",
    ]);
  });
});

describe("what an entry means", () => {
  const meaningOf = (status: ProcessingFile["status"]) =>
    entryMeaning(downloadEntry(file({ status })));

  it("is waiting for a file that has not started, under way for one being written, and done for one handed back", () => {
    expect(meaningOf("unprocessed")).toBe("todo");
    expect(meaningOf("out_of_schedule")).toBe("todo");
    expect(meaningOf("processing")).toBe("doing");
    expect(meaningOf("processed")).toBe("done");
  });

  it("asks for a look at a file on hold, passed through or rejected, and calls only a failure broken, though a rejection is listed under Failed", () => {
    expect(meaningOf("on_hold")).toBe("attention");
    expect(meaningOf("passed_through")).toBe("attention");
    expect(meaningOf("rejected")).toBe("attention");
    expect(meaningOf("processing_failed")).toBe("broken");
  });

  it("leaves a skipped, switched-off or cancelled file idle", () => {
    expect(meaningOf("skipped")).toBe("idle");
    expect(meaningOf("disabled")).toBe("idle");
    expect(meaningOf("cancelled")).toBe("idle");
  });

  it("counts a library file that already matched as done, as one that was cleaned", () => {
    expect(entryMeaning(cleanEntry(clean({ outcome: "cleaned" })))).toBe(
      "done",
    );
    expect(entryMeaning(cleanEntry(clean({ outcome: "skipped" })))).toBe(
      "done",
    );
    expect(entryMeaning(cleanEntry(clean({ outcome: "failed" })))).toBe(
      "broken",
    );
  });
});
