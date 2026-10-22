import { describe, expect, it } from "vitest";

import type { ActivityEventItem } from "../api/types";
import { eventDisplay, eventOptions, inlineDetailOf } from "./activity-display";
import {
  FILE_PROGRESS_EVENT,
  REMUX_PASS_COMPLETED_EVENT,
  SKIPPED_REPEAT_EVENT,
} from "./event-types";

function entry(
  event_type: string,
  fields: Partial<ActivityEventItem> = {},
): ActivityEventItem {
  return {
    id: 10,
    created_at: "2026-10-07T03:24:00",
    event_type,
    module: "processing",
    title: "",
    detail: null,
    ...fields,
  };
}

const withDetail = (detail: Record<string, unknown> | null) =>
  detail ? JSON.stringify(detail) : null;

describe("eventDisplay for a file being processed", () => {
  const progress = (detail: Record<string, unknown> | null) =>
    eventDisplay(entry(FILE_PROGRESS_EVENT, { detail: withDetail(detail) }));

  it.each([
    [
      { status: "finished", percent: 100, relative_media_path: "A/Film.mkv" },
      "Film.mkv finished processing",
      "Writing the cleaned-up file (100%)",
      "Processing finished",
      "done",
    ],
    [
      { status: "failed", percent: "41.6", relative_media_path: "A/Film.mkv" },
      "Film.mkv could not be processed",
      "Writing the cleaned-up file (42%)",
      "Processing stopped",
      "broken",
    ],
    [
      { status: "running", relative_media_path: "A/Film.mkv" },
      "Processing Film.mkv",
      "Preparing the cleaned-up file",
      "Processing now",
      "doing",
    ],
    [
      { status: "running" },
      "Processing file",
      "Preparing the cleaned-up file",
      "Processing now",
      "doing",
    ],
  ])("reads %j in plain words", (detail, title, summary, chip, meaning) => {
    expect(progress(detail)).toMatchObject({
      title,
      summary,
      chip,
      meaning,
      compact: false,
    });
  });

  it("still reads a progress entry that carries no detail", () => {
    const display = progress(null);

    expect(display.title).toBe("Processing file");
    expect(display.detail).toBeNull();
  });
});

describe("eventDisplay for a pass that finished nothing", () => {
  it.each([
    [{ outcome: "source_not_ready" }, "Waiting"],
    [{ outcome: "source_gone" }, "Skipped"],
    [{ outcome: "skipped_guardrail", skip_kind: "other" }, "Skipped"],
  ])(
    "names %j as %s and gives a reason when the server gave none",
    (detail, chip) => {
      const display = eventDisplay(
        entry(REMUX_PASS_COMPLETED_EVENT, {
          title: "Left alone",
          detail: withDetail(detail),
        }),
      );

      expect(display.chip).toBe(chip);
      expect(display.summary).toBe("Weir did not process this file");
      expect(display.meaning).toBe("idle");
    },
  );
});

describe("eventDisplay for a pass that did the work", () => {
  const pass = (detail: Record<string, unknown> | null) =>
    eventDisplay(
      entry(REMUX_PASS_COMPLETED_EVENT, { detail: withDetail(detail) }),
    );

  it.each([
    [
      "handed back unchanged",
      { relative_media_path: "A/Film.mkv", pass_through_unchanged: true },
      "Film.mkv was passed through unchanged",
      "Handed back without applying your rules",
      "Passed through",
      "attention",
    ],
    [
      "needing no changes",
      {
        relative_media_path: "A/Film.mkv",
        outcome: "live_skipped_not_required",
      },
      "No changes needed for Film.mkv",
      "No changes were needed",
      "No changes needed",
      "done",
    ],
    [
      "failing during the work",
      { relative_media_path: "A/Film.mkv", outcome: "failed_during_execution" },
      "Film.mkv could not be processed",
      "Weir could not finish this file",
      "Processing failed",
      "broken",
    ],
    [
      "writing a file the manager then refused",
      {
        relative_media_path: "A/Film.mkv",
        outcome: "live_output_written",
        ok: false,
      },
      "Film.mkv was processed successfully",
      "Cleaned-up file written",
      "File processed",
      "broken",
    ],
    [
      "writing a file that already fits",
      { relative_media_path: "A/Film.mkv", remux_required: false },
      "Film.mkv was processed successfully",
      "The file already fits your rules",
      "File processed",
      "done",
    ],
    [
      "writing a cleaned file",
      { relative_media_path: "A/Film.mkv", remux_required: true },
      "Film.mkv was processed successfully",
      "Cleaned-up file written",
      "File processed",
      "done",
    ],
    [
      "being rejected with no reason",
      { relative_media_path: "A/Film.mkv", rejected_without_manager: true },
      "Rejected Film.mkv",
      "Weir rejected this file",
      "Rejected",
      "attention",
    ],
    [
      "naming the file by where it was inspected",
      { inspected_source_path: "D:\\In\\Show.mkv" },
      "Show.mkv was processed successfully",
      "Cleaned-up file written",
      "File processed",
      "done",
    ],
    [
      "naming no file",
      {},
      "File was processed successfully",
      "Cleaned-up file written",
      "File processed",
      "done",
    ],
  ])("reads a pass %s", (_what, detail, title, summary, chip, meaning) => {
    expect(pass(detail)).toMatchObject({ title, summary, chip, meaning });
  });

  it("reads an entry with no detail as a plain success", () => {
    expect(pass(null)).toMatchObject({
      title: "File was processed successfully",
      meaning: "done",
    });
  });
});

describe("eventDisplay for sign-in and connection activity", () => {
  it.each([
    ["auth", "auth.login_failed", "Account and sign-in activity", "attention"],
    ["auth", "auth.login_denied", "Account and sign-in activity", "attention"],
    ["auth", "auth.login_succeeded", "Account and sign-in activity", "done"],
    ["auth", "auth.password_changed", "Account and sign-in activity", "done"],
    ["auth", "auth.session_seen", "Account and sign-in activity", "idle"],
    ["arr_library", "arr_library.checked", "Service connection check", "idle"],
  ])("reads %s event %s", (module, eventType, summary, meaning) => {
    expect(
      eventDisplay(entry(eventType, { module, detail: "checked" })),
    ).toMatchObject({ summary, meaning, chip: null, detail: "checked" });
  });
});

describe("eventDisplay for routine processing and everything else", () => {
  it("lists a routine cleanup as one short finished row", () => {
    expect(
      eventDisplay(
        entry("processing.candidate_gate_completed", { detail: "3 checked" }),
      ),
    ).toMatchObject({
      title: "Queue check finished",
      meaning: "done",
      compact: true,
      detail: "3 checked",
    });
  });

  it.each([
    ["Import failed", null, "broken"],
    ["Rules", "A rule was missing", "attention"],
    ["Setup", "Backup saved", "done"],
    ["Note", "Nothing to say", "idle"],
  ])("reads %s with detail %s as %s", (title, detail, meaning) => {
    const ev = entry("system.something_new", {
      module: "system",
      title,
      detail,
    });

    expect(eventDisplay(ev).meaning).toBe(meaning);
  });

  it.each([
    ["processing", "Processing"],
    ["system", "System"],
  ])("files a %s entry under %s", (module, summary) => {
    expect(eventDisplay(entry("x.something_new", { module }))).toMatchObject({
      title: "something new",
      summary,
      detail: null,
      compact: false,
    });
  });

  it("folds a long detail into a compact row", () => {
    const long = "x".repeat(121);

    expect(
      eventDisplay(entry("x.something_new", { detail: long })).compact,
    ).toBe(true);
  });

  it("gives a skipped repeat with no title of its own the event's label", () => {
    expect(eventDisplay(entry(SKIPPED_REPEAT_EVENT)).title).toBe(
      "Skipped: already done or imported",
    );
  });
});

describe("inlineDetailOf", () => {
  const inline = (ev: ActivityEventItem) =>
    inlineDetailOf(ev, eventDisplay(ev));

  it("puts a short plain-text detail on the summary line", () => {
    expect(
      inline(
        entry("auth.login_succeeded", { module: "auth", detail: "alice" }),
      ),
    ).toBe("alice");
  });

  it.each([
    ["no detail", entry("auth.login_succeeded", { module: "auth" })],
    [
      "structured facts",
      entry("auth.login_succeeded", { module: "auth", detail: '{"a":1}' }),
    ],
    ["a compact row", entry("x.something_new", { detail: "y".repeat(130) })],
    [
      "a pass in progress",
      entry(FILE_PROGRESS_EVENT, { detail: "Writing the file now" }),
    ],
    [
      "a finished pass",
      entry(REMUX_PASS_COMPLETED_EVENT, { detail: "Done and dusted" }),
    ],
  ])("leaves %s off the summary line", (_what, ev) => {
    expect(inline(ev)).toBeNull();
  });
});

describe("eventOptions", () => {
  it("lists each event type once, labelled and in label order", () => {
    expect(
      eventOptions([
        entry("system.processing_resumed"),
        entry("system.processing_paused"),
        entry("system.processing_resumed"),
      ]),
    ).toEqual([
      { value: "system.processing_paused", label: "Processing paused" },
      { value: "system.processing_resumed", label: "Processing resumed" },
    ]);
  });
});
