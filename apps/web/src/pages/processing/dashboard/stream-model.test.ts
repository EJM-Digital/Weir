import { describe, expect, it } from "vitest";

import type { ActivityEventItem } from "../../../lib/api/types";
import {
  STREAM_ROWS,
  buildStream,
  noteWords,
  streamWhen,
} from "./stream-model";

let nextId = 100;

function event(overrides: Partial<ActivityEventItem>): ActivityEventItem {
  nextId += 1;
  return {
    id: nextId,
    created_at: "2026-08-18T09:58:00Z",
    event_type: "auth.login_succeeded",
    module: "auth",
    title: "x",
    ...overrides,
  } as ActivityEventItem;
}

function pass(detail: object, overrides: Partial<ActivityEventItem> = {}) {
  return event({
    event_type: "processing.file_remux_pass_completed",
    module: "processing",
    relative_path: "The.Quiet.Harbour.S01E06.mkv",
    detail: JSON.stringify({
      relative_media_path: "The.Quiet.Harbour.S01E06.mkv",
      ...detail,
    }),
    ...overrides,
  });
}

const sentenceOf = (row: { parts: (string | { bold: string })[] }) =>
  row.parts
    .map((part) => (typeof part === "string" ? part : part.bold))
    .join("");

describe("the activity stream", () => {
  it("words a cleaned file from its own outcome and links to its story in Activity", () => {
    const { rows } = buildStream([
      pass({
        outcome: "live_output_written",
        ok: true,
        source_size_bytes: 2_437_000_000,
        output_size_bytes: 2_103_000_000,
        removed_audio: ["a", "b"],
      }),
    ]);

    expect(sentenceOf(rows[0])).toBe("The Quiet Harbour S01E06 cleaned");
    expect(rows[0].meaning).toBe("done");
    expect(rows[0].note).toBe("Saved 319 MB · removed 2 audio");
    expect(rows[0].to).toBe("/activity?q=The.Quiet.Harbour.S01E06.mkv");
  });

  it("words a file that needed nothing changed as already clean", () => {
    const { rows } = buildStream([
      pass({ outcome: "live_skipped_not_required", ok: true }),
    ]);

    expect(sentenceOf(rows[0])).toBe(
      "The Quiet Harbour S01E06 was already clean",
    );
    expect(rows[0].meaning).toBe("done");
  });

  it("says a rejection is a rejection and a real failure is a failure", () => {
    const { rows } = buildStream([
      pass({
        outcome: "failed_before_execution",
        ok: false,
        rejected_without_manager: true,
        reason: "Rejected: it has no audio tracks.",
      }),
      pass({ outcome: "failed_during_execution", ok: false }),
    ]);

    expect(sentenceOf(rows[0])).toContain("was rejected");
    expect(rows[0].meaning).toBe("attention");
    expect(sentenceOf(rows[1])).toContain("couldn't finish");
    expect(rows[1].meaning).toBe("broken");
  });

  it("lists a library clean however long its entry's detail is", () => {
    const { rows, routine } = buildStream([
      event({
        event_type: "library.file_cleaned",
        module: "library",
        relative_path: "Heat (1995)/Heat.1995.mkv",
        detail: JSON.stringify({
          relative_path: "Heat (1995)/Heat.1995.mkv",
          library_id: 2,
          trigger: "scheduled",
          note: "x".repeat(200),
        }),
      }),
    ]);

    expect(rows).toHaveLength(1);
    expect(routine).toBe(0);
  });

  it("counts a manager's resent hand-offs as routine instead of listing each one", () => {
    const resent = () =>
      event({
        event_type: "processing.file_skipped_repeat",
        module: "processing",
        title: "Skipped: already imported (Heat.1995.mkv)",
        relative_path: "Heat (1995)/Heat.1995.mkv",
        detail: JSON.stringify({
          result: "skipped",
          trigger: "webhook",
          note: "x".repeat(200),
        }),
      });

    const { rows, routine } = buildStream([resent(), resent()]);

    expect(rows).toHaveLength(0);
    expect(routine).toBe(2);
  });

  it("lists a repeat the person asked for, with its own title, however long its entry's detail is", () => {
    const { rows, routine } = buildStream([
      event({
        event_type: "processing.file_skipped_repeat",
        module: "processing",
        title: "Skipped: already imported (Heat.1995.mkv)",
        relative_path: "Heat (1995)/Heat.1995.mkv",
        detail: JSON.stringify({
          result: "skipped",
          trigger: "manual",
          relative_media_path: "Heat (1995)/Heat.1995.mkv",
          user_message: "Already imported: Deluno collected the cleaned copy",
          note: "x".repeat(200),
        }),
      }),
    ]);

    expect(rows.map(sentenceOf)).toEqual([
      "Skipped: already imported (Heat.1995.mkv)",
    ]);
    expect(rows[0].to).toBe("/activity?q=Heat%20(1995)%2FHeat.1995.mkv");
    expect(routine).toBe(0);
  });

  it("words a library clean as cleaned in place", () => {
    const { rows } = buildStream([
      event({
        event_type: "library.file_cleaned",
        module: "library",
        title: "Cleaned in place",
        relative_path: "Movies/Heat (1995)/Heat.1995.mkv",
        detail: JSON.stringify({
          relative_path: "Movies/Heat (1995)/Heat.1995.mkv",
        }),
      }),
    ]);

    expect(sentenceOf(rows[0])).toBe("Heat (1995) cleaned in place");
  });

  it("says which manager imported a handed-back copy, or that it did not", () => {
    const outcome = (kind: string) =>
      event({
        event_type: "processing.handback_outcome",
        module: "processing",
        detail: JSON.stringify({
          outcome: kind,
          outcome_by: "Radarr",
          relative_media_path: "Heat.1995.mkv",
        }),
      });

    const { rows } = buildStream([
      outcome("imported"),
      outcome("not-imported"),
    ]);

    expect(sentenceOf(rows[0])).toBe("Radarr imported Heat (1995)");
    expect(rows[0].meaning).toBe("done");
    expect(sentenceOf(rows[1])).toBe("Radarr did not import Heat (1995)");
    expect(rows[1].meaning).toBe("attention");
  });

  it.each([
    [
      "a manager that did not name itself",
      { outcome: "imported", relative_media_path: "Heat.1995.mkv" },
      {},
      "A media manager imported Heat (1995)",
      "/activity?q=Heat.1995.mkv",
    ],
    [
      "a file named only by the entry",
      { outcome: "imported", outcome_by: "Radarr" },
      { relative_path: "Heat.1995.mkv" },
      "Radarr imported Heat (1995)",
      "/activity?q=Heat.1995.mkv",
    ],
    [
      "no file named at all",
      { outcome: "imported", outcome_by: "Radarr" },
      {},
      "Radarr imported ",
      "/system?tab=logs",
    ],
  ])("words a handed-back copy with %s", (_name, detail, entry, words, to) => {
    const { rows } = buildStream([
      event({
        event_type: "processing.handback_outcome",
        module: "processing",
        detail: JSON.stringify(detail),
        ...entry,
      }),
    ]);

    expect(sentenceOf(rows[0])).toBe(words);
    expect(rows[0].to).toBe(to);
  });

  it("leaves out a pass that finished nothing", () => {
    expect(
      buildStream([pass({ outcome: "skipped_guardrail", ok: true })]).rows,
    ).toEqual([]);
  });

  it("uses the log's own title for anything else, and links to the log", () => {
    const { rows } = buildStream([
      event({ event_type: "auth.login_succeeded", module: "auth" }),
    ]);

    expect(sentenceOf(rows[0])).toBe("Sign-in finished");
    expect(rows[0].to).toBe("/system?tab=logs");
  });

  it("sets a pass's live progress aside, and counts routine housekeeping instead of listing it", () => {
    const stream = buildStream([
      event({
        event_type: "processing.file_processing_progress",
        module: "processing",
      }),
      event({
        event_type: "processing.work_temp_stale_sweep_completed",
        module: "processing",
      }),
      event({
        event_type: "processing.candidate_gate_completed",
        module: "processing",
      }),
    ]);

    expect(stream.rows).toEqual([]);
    expect(stream.routine).toBe(2);
  });

  it("folds neighbours that say the same thing into one line with a count", () => {
    const { rows } = buildStream([
      event({ event_type: "auth.login_succeeded", module: "auth" }),
      event({ event_type: "auth.login_succeeded", module: "auth" }),
      event({ event_type: "auth.login_succeeded", module: "auth" }),
    ]);

    expect(rows).toHaveLength(1);
    expect(rows[0].times).toBe(3);
  });

  it("keeps the newest lines and no more than the stream holds", () => {
    const items = Array.from({ length: STREAM_ROWS + 5 }, (_, index) =>
      pass(
        {
          outcome: "live_output_written",
          ok: true,
          relative_media_path: `File.${index}.mkv`,
        },
        { relative_path: `File.${index}.mkv` },
      ),
    );

    const { rows } = buildStream(items);

    expect(rows).toHaveLength(STREAM_ROWS);
    expect(rows[0].id).toBe(items[0].id);
  });
});

describe("when a line happened", () => {
  const now = Date.parse("2026-08-18T10:00:00Z");

  it("reads as minutes and hours ago for the first day", () => {
    expect(streamWhen("2026-08-18T09:58:00Z", now)).toBe("2 min ago");
    expect(streamWhen("2026-08-18T07:00:00Z", now)).toBe("3 h ago");
  });

  it("reads as a date after that", () => {
    expect(streamWhen("2026-08-15T10:00:00Z", now)).toBe("Aug 15");
  });

  it("is empty for a time that cannot be read", () => {
    expect(streamWhen("", now)).toBe("");
  });
});

describe("a line's note in the room it has", () => {
  const now = Date.parse("2026-08-18T10:00:00Z");
  const row = (note: string, times = 1) => ({
    ...buildStream([event({ title: "x" })]).rows[0],
    note,
    times,
    at: "2026-08-18T09:58:00Z",
  });

  it("drops the later details of what happened first, and ends with when", () => {
    expect(
      noteWords(row("Saved 342 MB · removed 3 audio, 8 subtitles"), now),
    ).toEqual([
      "Saved 342 MB · removed 3 audio, 8 subtitles · 2 min ago",
      "Saved 342 MB · 2 min ago",
      "2 min ago",
      "Saved 342 MB",
      "removed 3 audio, 8 subtitles",
    ]);
  });

  it("drops how many times before anything else", () => {
    expect(noteWords(row("Handed back", 3), now).slice(0, 2)).toEqual([
      "3 times · Handed back · 2 min ago",
      "Handed back · 2 min ago",
    ]);
  });
});
