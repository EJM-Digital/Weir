import type {
  ActivityEventItem,
  ActivityRecentResponse,
  SystemReadiness,
} from "../lib/api/types";
import type {
  ProcessingFile,
  ProcessingFilesPage,
} from "../lib/processing/files-api";
import type { ProcessingLibrary } from "../lib/processing/libraries-api";
import { recordedProcessingFile } from "./recorded";

/** A file as the server lists it, with only what a test says set; everything else is what a file Weir just found has. */
export function processingFile(
  partial: Partial<ProcessingFile> = {},
): ProcessingFile {
  return {
    ...recordedProcessingFile(),
    id: 7,
    library_id: 1,
    library_name: "Movies",
    relative_path: "Film (2024)/film.mkv",
    status: "processing_failed",
    status_reason: "ffmpeg could not read the audio track.",
    blocked_by_connection: null,
    size_bytes: 4_000_000_000,
    failure_class: "execution",
    failure_attempts: 1,
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
    created_at: "2026-09-01T00:00:00Z",
    updated_at: "2026-09-01T00:00:00Z",
    last_seen_at: null,
    last_attempt_at: null,
    ...partial,
  };
}

/** The reply to a files list. */
export function filesPage(files: ProcessingFile[]): ProcessingFilesPage {
  return { files, status_counts: {}, returned: files.length, limit: 200 };
}

/** A workflow as the libraries list returns it, with only what the Dashboard reads set. */
export function workflowRow(
  partial: Pick<ProcessingLibrary, "id" | "name"> & Partial<ProcessingLibrary>,
): ProcessingLibrary {
  return {
    enabled: true,
    media_type: "movie",
    watched_folder: "D:/watched",
    work_folder: "D:/work",
    output_folder: "D:/output",
    manager_connection_ids: [],
    display_order: partial.id,
    ...partial,
  } as ProcessingLibrary;
}

/** The reply to a readiness read from a server whose workers are all running. */
export function readyServer(): SystemReadiness {
  return {
    machine_name: "WEIR-TEST",
    machine_name_looks_generated: false,
    ready: true,
    startup_seconds: 1,
    status: "ready",
    steps: [],
    version: "3.2.16",
    worker_health: [],
  };
}

/** A page of Activity entries. */
export function activityPage(
  items: ActivityEventItem[],
  extra: Partial<ActivityRecentResponse> = {},
): ActivityRecentResponse {
  return {
    items,
    has_more: false,
    retention_days: 90,
    total: items.length,
    ...extra,
  };
}

/** The entry a finished download's pass writes: a file cleaned from `sourceBytes` down to `outputBytes`. */
export function passFinished(
  id: number,
  relativePath: string,
  options: {
    libraryId?: number;
    sourceBytes?: number;
    outputBytes?: number;
    createdAt?: string;
  } = {},
): ActivityEventItem {
  const {
    libraryId = 1,
    sourceBytes = 4_000_000_000,
    outputBytes = 3_000_000_000,
    createdAt = "2026-10-15T10:00:00+00:00",
  } = options;
  return {
    id,
    created_at: createdAt,
    module: "processing",
    event_type: "processing.file_remux_pass_completed",
    title: `Remux finished for ${relativePath}`,
    library_id: libraryId,
    relative_path: relativePath,
    detail: JSON.stringify({
      outcome: "live_output_written",
      ok: true,
      relative_media_path: relativePath,
      source_size_bytes: sourceBytes,
      output_size_bytes: outputBytes,
      remux_required: true,
      removed_audio: [],
      removed_subtitles: [],
    }),
  };
}
