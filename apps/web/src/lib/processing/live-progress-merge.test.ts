import { describe, expect, it } from "vitest";
import type { LiveProgressEntry } from "../activity/use-activity-stream-invalidation";
import type { ProcessingFile } from "./files-api";
import { mergeLiveProgress } from "./live-progress-merge";
import { recordedProcessingFile } from "../../test/recorded";

function file(overrides: Partial<ProcessingFile>): ProcessingFile {
  return {
    ...recordedProcessingFile(),
    id: 1,
    library_id: 2,
    library_name: "TV",
    relative_path: "Film/Film.mkv",
    status: "processing",
    status_reason: "",
    blocked_by_connection: null,
    size_bytes: 2_437_000_000,
    failure_class: null,
    failure_attempts: 0,
    next_retry_at: null,
    output_collision_policy: null,
    output_collision_action: null,
    output_collision_reason: null,
    video_width: 1920,
    video_height: 1080,
    video_codec: "h264",
    audio_track_count: 5,
    subtitle_track_count: 7,
    duration_seconds: 2700,
    direct_play: [],
    progress_percent: 10,
    progress_message: "Weir has started writing the cleaned-up file.",
    progress_eta_seconds: 120,
    progress_status: "processing",
    progress_speed: "1.0x",
    progress_elapsed_seconds: 5,
    progress_removed_audio: [],
    progress_removed_subtitles: [],
    hold_until: null,
    size_changed_at: null,
    created_at: "2026-08-18T09:50:00",
    updated_at: "2026-08-18T09:59:00",
    last_seen_at: null,
    last_attempt_at: null,
    ...overrides,
  };
}

function progress(overrides: Partial<LiveProgressEntry>): LiveProgressEntry {
  return {
    relativePath: "Film/Film.mkv",
    status: "processing",
    stage: "writing",
    percent: 42.5,
    etaSeconds: 30,
    message: "Weir is writing the cleaned-up file.",
    speed: "148x",
    elapsedSeconds: 9,
    removedAudio: ["fre aac 2ch: removed"],
    removedSubtitles: [],
    ...overrides,
  };
}

describe("mergeLiveProgress", () => {
  it("returns the same list when nothing is live", () => {
    const files = [file({})];
    expect(mergeLiveProgress(files, {})).toBe(files);
  });

  it("overlays a matching file's progress fields with the fresher live values", () => {
    const files = [file({})];

    const merged = mergeLiveProgress(files, {
      "Film/Film.mkv": progress({}),
    });

    expect(merged[0]).toMatchObject({
      progress_status: "processing",
      progress_percent: 42.5,
      progress_eta_seconds: 30,
      progress_message: "Weir is writing the cleaned-up file.",
      progress_speed: "148x",
      progress_elapsed_seconds: 9,
      progress_removed_audio: ["fre aac 2ch: removed"],
      progress_removed_subtitles: [],
    });
  });

  it("shows a Waiting file as processing once the stream has progress for it", () => {
    const files = [
      file({ id: 1, status: "unprocessed", progress_percent: null }),
      file({
        id: 2,
        status: "out_of_schedule",
        relative_path: "Other/Other.mkv",
        progress_percent: null,
      }),
    ];

    const merged = mergeLiveProgress(files, {
      "Film/Film.mkv": progress({}),
      "Other/Other.mkv": progress({ relativePath: "Other/Other.mkv" }),
    });

    expect(merged.map((f) => f.status)).toEqual(["processing", "processing"]);
    expect(merged[0].progress_percent).toBe(42.5);
  });

  it("does not move a file that has already ended back to processing because a frame lingers", () => {
    const files = [
      file({ id: 1, status: "processed" }),
      file({ id: 2, status: "on_hold", relative_path: "Other/Other.mkv" }),
    ];

    const merged = mergeLiveProgress(files, {
      "Film/Film.mkv": progress({}),
      "Other/Other.mkv": progress({ relativePath: "Other/Other.mkv" }),
    });

    expect(merged.map((f) => f.status)).toEqual(["processed", "on_hold"]);
  });

  it("leaves a file the stream says nothing about untouched", () => {
    const files = [file({ relative_path: "Other/Other.mkv" })];

    const merged = mergeLiveProgress(files, {
      "Film/Film.mkv": progress({}),
    });

    expect(merged[0]).toBe(files[0]);
  });
});
