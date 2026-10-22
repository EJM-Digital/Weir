import { describe, expect, it } from "vitest";

import type { ActivityEventItem } from "../api/types";
import {
  LIBRARY_FILE_CLEANED_EVENT,
  REMUX_PASS_COMPLETED_EVENT,
} from "./event-types";
import { finishedFileFromEvent } from "./processing-outcome";

function passEvent(detail: Record<string, unknown>): ActivityEventItem {
  return {
    id: 1,
    created_at: "2026-09-29T09:00:00",
    event_type: REMUX_PASS_COMPLETED_EVENT,
    module: "processing",
    title: "",
    library_id: 1,
    relative_path: "Film (2024)/film.mkv",
    detail: JSON.stringify({
      relative_media_path: "Film (2024)/film.mkv",
      ...detail,
    }),
  };
}

const REJECTION_REASON =
  "Rejected: none of its audio tracks are in English, and your rules keep only English audio, so there would be nothing to keep.";

describe("finishedFileFromEvent for a pass that ended in a rejection", () => {
  it("reads a rules rejection as rejected, with the reason and what became of the file", () => {
    const finished = finishedFileFromEvent(
      passEvent({
        outcome: "failed_before_execution",
        ok: false,
        rejected_without_manager: true,
        reason: REJECTION_REASON,
        rejected_cleanup_detail: "The file was left where it is.",
      }),
    );

    expect(finished?.kind).toBe("rejected");
    expect(finished?.sentence).toBe(
      `${REJECTION_REASON} The file was left where it is.`,
    );
  });

  it("still says it was rejected when the pass recorded no reason", () => {
    const finished = finishedFileFromEvent(
      passEvent({
        outcome: "failed_before_execution",
        ok: false,
        rejected_without_manager: true,
      }),
    );

    expect(finished?.kind).toBe("rejected");
    expect(finished?.sentence).toBe("Rejected");
  });

  it("keeps a real failure as failed, with no sentence of its own", () => {
    const finished = finishedFileFromEvent(
      passEvent({ outcome: "failed_during_execution", ok: false }),
    );

    expect(finished?.kind).toBe("failed");
    expect(finished?.sentence).toBeNull();
  });

  it("keeps a failure while a media manager is asked for a replacement as failed", () => {
    const finished = finishedFileFromEvent(
      passEvent({
        outcome: "failed_before_execution",
        ok: false,
        reject_queued: true,
      }),
    );

    expect(finished?.kind).toBe("failed");
  });
});

describe("finishedFileFromEvent for a pass that finished nothing", () => {
  it.each([
    [
      "a file under the workflow's minimum size",
      {
        outcome: "skipped_guardrail",
        guardrail: "minimum_input_file_size",
        skip_kind: "below_minimum_size",
      },
    ],
    [
      "a file a safety check skipped for its age",
      { outcome: "skipped_guardrail", guardrail: "minimum_file_age" },
    ],
    ["a file that was not ready yet", { outcome: "source_not_ready" }],
    ["a file that was gone", { outcome: "source_gone" }],
  ])(
    "is no finished file for %s: nothing was written or checked",
    (_, detail) => {
      expect(
        finishedFileFromEvent(passEvent({ ok: true, ...detail })),
      ).toBeNull();
    },
  );

  it("is cleaned only when the pass wrote an output, and already clean when it checked that none was needed", () => {
    expect(
      finishedFileFromEvent(
        passEvent({ ok: true, outcome: "live_output_written" }),
      )?.kind,
    ).toBe("cleaned");
    expect(
      finishedFileFromEvent(
        passEvent({ ok: true, outcome: "live_skipped_not_required" }),
      )?.kind,
    ).toBe("already");
  });
});

describe("finishedFileFromEvent and the poster", () => {
  it("carries the poster address the entry names", () => {
    const entry = {
      ...passEvent({ outcome: "live_output_written", ok: true }),
      poster_url: "/api/v1/artwork/posters/movie-detour-1945",
    };

    expect(finishedFileFromEvent(entry)?.posterUrl).toBe(
      "/api/v1/artwork/posters/movie-detour-1945",
    );
  });

  it("has none when the entry names none", () => {
    const finished = finishedFileFromEvent(
      passEvent({ outcome: "live_output_written", ok: true }),
    );

    expect(finished?.posterUrl).toBeNull();
  });
});

describe("finishedFileFromEvent for a pass that wrote a file", () => {
  const written = { outcome: "live_output_written", ok: true };

  it("reads a file passed through untouched as passed", () => {
    const finished = finishedFileFromEvent(
      passEvent({ ...written, pass_through_unchanged: true }),
    );

    expect(finished?.kind).toBe("passed");
  });

  it.each([
    [
      "the path the pass recorded",
      { relative_media_path: "A/a.mkv" },
      "B/b.mkv",
      "A/a.mkv",
    ],
    [
      "the path of the entry when the pass recorded none",
      { relative_media_path: undefined },
      "B/b.mkv",
      "B/b.mkv",
    ],
    [
      "a plain name when neither has one",
      { relative_media_path: undefined },
      null,
      "this file",
    ],
  ])("names the file by %s", (_name, detail, entryPath, name) => {
    const entry = {
      ...passEvent({ ...written, ...detail }),
      relative_path: entryPath,
    };

    expect(finishedFileFromEvent(entry)?.relativePath).toBe(name);
  });

  it.each([
    [
      "the output is smaller",
      { source_size_bytes: 900, output_size_bytes: 400 },
      500,
    ],
    [
      "the output is as big",
      { source_size_bytes: 400, output_size_bytes: 400 },
      null,
    ],
    [
      "the output is bigger",
      { source_size_bytes: 400, output_size_bytes: 900 },
      null,
    ],
    ["a size is missing", { source_size_bytes: 900 }, null],
  ])("counts the space saved when %s", (_name, sizes, saved) => {
    expect(
      finishedFileFromEvent(passEvent({ ...written, ...sizes }))?.savedBytes,
    ).toBe(saved);
  });

  it("counts the tracks taken out", () => {
    const finished = finishedFileFromEvent(
      passEvent({
        ...written,
        removed_audio: ["fra", "deu"],
        removed_subtitles: ["fra"],
      }),
    );

    expect([finished?.removedAudio, finished?.removedSubtitles]).toEqual([
      2, 1,
    ]);
  });

  it("belongs to no workflow when the entry names none", () => {
    const entry = { ...passEvent(written), library_id: undefined };

    expect(finishedFileFromEvent(entry)?.libraryId).toBeNull();
  });
});

describe("finishedFileFromEvent for a library file that was cleaned", () => {
  function cleaned(
    detail: Record<string, unknown>,
    entry: Partial<ActivityEventItem> = {},
  ): ActivityEventItem {
    return {
      id: 7,
      created_at: "2026-09-29T09:00:00",
      event_type: LIBRARY_FILE_CLEANED_EVENT,
      module: "library",
      title: "Cleaned Film.mkv",
      library_id: 2,
      relative_path: "entry/film.mkv",
      detail: JSON.stringify(detail),
      ...entry,
    };
  }

  it("is a cleaned file of the library, told by the entry's title", () => {
    expect(
      finishedFileFromEvent(cleaned({ relative_path: "Film/film.mkv" })),
    ).toMatchObject({
      source: "library",
      kind: "cleaned",
      relativePath: "Film/film.mkv",
      libraryId: 2,
      sentence: "Cleaned Film.mkv",
      posterUrl: null,
    });
  });

  it.each([
    [
      "the entry's path when the detail has none",
      { relative_path: "entry/film.mkv" },
      "entry/film.mkv",
    ],
    ["a plain name when neither has one", { relative_path: null }, "this file"],
  ])("names the file by %s", (_name, entry, name) => {
    expect(
      finishedFileFromEvent(cleaned({}, entry as Partial<ActivityEventItem>))
        ?.relativePath,
    ).toBe(name);
  });

  it("carries the poster, and belongs to no workflow when the entry names none", () => {
    const finished = finishedFileFromEvent(
      cleaned({}, { library_id: undefined, poster_url: "/api/poster" }),
    );

    expect([finished?.libraryId, finished?.posterUrl]).toEqual([
      null,
      "/api/poster",
    ]);
  });
});

describe("finishedFileFromEvent for a pass with no readable detail", () => {
  it.each([["nothing"], ["words, not a record"]])(
    "is no finished file when the entry carries %s",
    (detail) => {
      const entry = {
        ...passEvent({}),
        detail: detail === "nothing" ? null : "Pass done",
      };

      expect(finishedFileFromEvent(entry)).toBeNull();
    },
  );
});

describe("finishedFileFromEvent for any other entry", () => {
  it("is no finished file", () => {
    const entry = { ...passEvent({}), event_type: "something.else" };

    expect(finishedFileFromEvent(entry)).toBeNull();
  });
});
