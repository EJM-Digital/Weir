import type {
  LibraryCleanResult,
  LibraryFile,
  LibraryFilesResult,
  LibraryOverview,
  LibraryScanInfo,
  LibrarySettings,
  LibraryTotals,
} from "../lib/processing/library-mode-api";
import type { ProcessingRulesPreviewResult } from "../lib/processing/rules-preview-api";

/** A file in a library as the server lists it, with only what a test says set; everything else is a film the rules would change. */
export function libraryFile(partial: Partial<LibraryFile> = {}): LibraryFile {
  return {
    path: "D:/films/Arrival (2016)/Arrival (2016).mkv",
    size_bytes: 2_437_000_000,
    modified_at: 1_758_000_000,
    classification: "would_change",
    summary: "Would remove 1 audio track",
    reason: null,
    removed_audio_tracks: 1,
    removed_subtitle_tracks: 0,
    estimated_bytes_saved: 333_000_000,
    manager_kind: null,
    manager_title: null,
    video_codec: "h264",
    video_height: 1080,
    resolution_class: "1080p",
    audio_track_count: 2,
    subtitle_track_count: 0,
    audio_summary: "English 5.1 + French",
    subtitle_summary: "None",
    link_count: 1,
    problem_kind: null,
    cleaned_at: null,
    leave_alone: false,
    status: "needs_cleaning",
    status_reason: "new",
    ...partial,
  };
}

/** A file the rules already match, which nothing needs to clean. */
export function matchingFile(partial: Partial<LibraryFile> = {}): LibraryFile {
  return libraryFile({
    classification: "matches",
    summary: "Already matches your rules",
    removed_audio_tracks: 0,
    estimated_bytes_saved: 0,
    status: "matches",
    status_reason: null,
    ...partial,
  });
}

/** The library's counts for the files given. */
export function totalsOf(files: LibraryFile[]): LibraryTotals {
  const count = (status: LibraryFile["status"]) =>
    files.filter((file) => file.status === status).length;
  return {
    files: files.length,
    size_bytes: files.reduce((sum, file) => sum + file.size_bytes, 0),
    matches: count("matches"),
    would_change: files.filter((f) => f.classification === "would_change")
      .length,
    cannot_process: count("cant_clean_yet"),
    total_removed_audio_tracks: files.reduce(
      (sum, file) => sum + file.removed_audio_tracks,
      0,
    ),
    total_removed_subtitle_tracks: files.reduce(
      (sum, file) => sum + file.removed_subtitle_tracks,
      0,
    ),
    estimated_bytes_saved: files.reduce(
      (sum, file) => sum + file.estimated_bytes_saved,
      0,
    ),
    cleaned: files.filter((file) => file.cleaned_at !== null).length,
    left_alone: count("left_alone"),
    by_status: {
      needs_cleaning: count("needs_cleaning"),
      cleaning: count("cleaning"),
      matches: count("matches"),
      cant_clean_yet: count("cant_clean_yet"),
      left_alone: count("left_alone"),
    },
  };
}

/** A finished scan, taken a couple of minutes ago. */
export function finishedScan(
  partial: Partial<LibraryScanInfo> = {},
): LibraryScanInfo {
  return {
    job_id: 1,
    status: "completed",
    running: false,
    files_seen: null,
    generated_at: 1_758_000_000,
    errors: [],
    ...partial,
  };
}

/** The overview of a library holding `files`. */
export function libraryOverview(
  files: LibraryFile[],
  partial: Partial<LibraryOverview> = {},
): LibraryOverview {
  return {
    library_id: 1,
    folders_configured: 1,
    schedule: { enabled: false, next_run_at: null },
    scan: finishedScan(),
    totals: totalsOf(files),
    breakdowns: {
      video_codec: [],
      resolution: [],
      audio: [],
      audio_language: [],
      subtitle_language: [],
    },
    problems: [],
    ...partial,
  };
}

/** One page of a library's files, as the Files table asks for it. */
export function libraryFilesPage(
  files: LibraryFile[],
  partial: Partial<LibraryFilesResult> = {},
): LibraryFilesResult {
  const totals = totalsOf(files);
  return {
    library_id: 1,
    scan: finishedScan(),
    summary: totals,
    filtered: totals,
    files,
    total: files.length,
    page: 1,
    page_size: 200,
    sort: "path",
    direction: "asc",
    ...partial,
  };
}

/** A library's saved setup, with one folder and the library's own rules. */
export function librarySettings(
  partial: Partial<LibrarySettings> = {},
): LibrarySettings {
  return {
    library_folders: ["D:/films"],
    library_schedule_enabled: false,
    clean_hardlinked_files: false,
    skip_if_manager_would_redownload: true,
    keep_original_after_clean: false,
    originals_folder: "",
    library_rule_set_id: null,
    ...partial,
  };
}

/**
 * What the server answers a clean that has not been confirmed: a 400 carrying what the clean would remove. The
 * server's own wording, from `LibraryModeMapping.ConfirmationRequired`.
 */
export function confirmationRequired(
  files: number,
  tracks: number,
  bytesSaved: number,
  warnings: string[] = [],
): Record<string, unknown> {
  return {
    error: "confirm_final_removal_required",
    detail: `${files} files, ${tracks} tracks will be removed. Removed tracks are gone for good; getting one back means downloading the title again.`,
    files_count: files,
    tracks_count: tracks,
    estimated_bytes_saved: bytesSaved,
    warnings,
  };
}

/** What the server answers a confirmed clean that queued `queued` files. */
export function cleanQueued(
  queued: number,
  partial: Partial<Omit<LibraryCleanResult, "kind">> = {},
): Omit<LibraryCleanResult, "kind"> {
  return {
    estimated_bytes_saved: 333_000_000,
    files_count: queued,
    job_ids: Array.from({ length: queued }, (_, index) => index + 1),
    queued,
    skipped_paths: [],
    tracks_count: queued,
    warnings: [],
    ...partial,
  };
}

/** What "try the rules on this file" answers for a film with English and French audio, of which the rules keep the English. */
export function previewOfTwoAudioTracks(): ProcessingRulesPreviewResult {
  return {
    tracks: [
      {
        index: 1,
        type: "audio",
        codec: "eac3",
        language: "English",
        channels: 6,
        action: "keep",
        reasons: ["Your first choice language"],
        default: true,
        forced: false,
        title: "",
      },
      {
        index: 2,
        type: "audio",
        codec: "aac",
        language: "French",
        channels: 2,
        action: "drop",
        reasons: ["Not a language your rules keep"],
        default: false,
        forced: false,
        title: "",
      },
    ],
    notes: [],
    metadata_notes: [],
    remux_required: true,
    estimated_size_reduction_bytes: 333_000_000,
    estimated_size_reduction_is_estimate: true,
    original_language: null,
    library_id: 1,
    media_scope: "movie",
    inspected_path: "D:/films/Arrival (2016)/Arrival (2016).mkv",
  };
}
