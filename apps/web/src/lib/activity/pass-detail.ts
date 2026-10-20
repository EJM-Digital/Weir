/**
 * Plain words for the facts a processing pass records in its Activity detail: the outcome, how long it
 * took, what it saved and what happened to the folders around the file.
 */
import { formatBytes } from "../format/bytes";

/** The detail of a finished pass (REMUX_PASS_COMPLETED_EVENT). Every field is optional: older entries lack some. */
export type RemuxPassDetail = {
  outcome?: string;
  ok?: boolean;
  media_scope?: string;
  relative_media_path?: string;
  inspected_source_path?: string;
  stream_counts?: { video?: number; audio?: number; subtitle?: number };
  plan_summary?: string;
  audio_before?: string;
  audio_after?: string;
  subs_before?: string;
  subs_after?: string;
  removed_audio?: string[];
  removed_subtitles?: string[];
  after_track_lines_meaning?: string;
  remux_required?: boolean;
  pass_through_unchanged?: boolean;
  live_mutations_skipped?: boolean;
  output_file?: string;
  reason?: string;
  /** Set when the rules rejected the file and no media manager was involved. */
  rejected_without_manager?: boolean;
  /** What happened to a rejected file, such as "The file was left where it is." */
  rejected_cleanup_detail?: string;
  job_id?: number;
  ffmpeg_argv?: string[];
  ffmpeg_argv_truncated?: boolean;
  source_size_bytes?: number | null;
  output_size_bytes?: number | null;
  output_completeness_note?: string | null;
  source_folder_deleted?: boolean;
  source_folder_skip_reason?: string;
  tv_season_folder_deleted?: boolean;
  tv_season_folder_skip_reason?: string;
  movie_output_folder_deleted?: boolean;
  movie_output_folder_skip_reason?: string;
  movie_output_truth_check?: string;
  tv_output_season_folder_deleted?: boolean;
  tv_output_season_folder_skip_reason?: string;
  tv_output_truth_check?: string;
};

/** The detail of a pass still running (FILE_PROGRESS_EVENT). */
export type FileProgressDetail = {
  status?: string;
  message?: string;
  relative_media_path?: string;
  inspected_source_path?: string;
  output_file?: string;
  percent?: number | null;
  eta_seconds?: number | null;
  elapsed_seconds?: number | null;
  processed_seconds?: number | null;
  duration_seconds?: number | null;
  speed?: string | null;
  reason?: string | null;
};

/**
 * Whether a pass finished nothing: it was skipped, or waited for a file that was not ready, or the file was gone. It wrote no
 * output and checked that none was needed, so it is never a cleaned file, whatever its reason.
 */
export function finishedNothing(outcome: string | null | undefined): boolean {
  return (
    outcome === "skipped_guardrail" ||
    outcome === "source_not_ready" ||
    outcome === "source_gone"
  );
}

/** A rejection by the rules with no media manager involved: a decision, not a failure. */
export function isRejectedByRules(
  detail: { rejected_without_manager?: unknown } | null | undefined,
): boolean {
  return detail?.rejected_without_manager === true;
}

export function outcomeLabel(
  detail: Pick<
    RemuxPassDetail,
    "outcome" | "pass_through_unchanged" | "rejected_without_manager"
  >,
): string {
  const { outcome } = detail;
  if (isRejectedByRules(detail)) return "Rejected";
  if (
    detail.pass_through_unchanged &&
    outcome === "live_skipped_not_required"
  ) {
    return "Passed through unchanged";
  }
  switch (outcome) {
    case "live_output_written":
      return "File processed";
    case "live_skipped_not_required":
      return "No changes needed";
    case "failed_before_execution":
      return "Could not check file";
    case "failed_during_execution":
      return "Could not process file";
    default:
      return outcome || "Unknown outcome";
  }
}

/** "45s", "3m 07s", "1h 05m". */
export function formatDuration(
  seconds: number | null | undefined,
): string | null {
  if (typeof seconds !== "number" || !Number.isFinite(seconds) || seconds < 0)
    return null;
  const rounded = Math.round(seconds);
  const mins = Math.floor(rounded / 60);
  const secs = rounded % 60;
  if (mins <= 0) return `${secs}s`;
  const hours = Math.floor(mins / 60);
  const remMins = mins % 60;
  if (hours <= 0) return `${mins}m ${secs.toString().padStart(2, "0")}s`;
  return `${hours}h ${remMins.toString().padStart(2, "0")}m`;
}

/** ffmpeg's "16x" reads as "16x realtime"; anything else is shown as it came. */
export function formatProcessingSpeed(
  speed: string | null | undefined,
): string | null {
  const value = (speed || "").trim();
  if (!value) return null;
  if (/^[0-9]+(?:\.[0-9]+)?x$/i.test(value)) {
    return `${value} realtime`;
  }
  return value;
}

/** What a pass did to the file's size, or null when either size is missing. */
export function formatSavings(
  source: number | null | undefined,
  output: number | null | undefined,
): string | null {
  if (
    typeof source !== "number" ||
    typeof output !== "number" ||
    !Number.isFinite(source) ||
    !Number.isFinite(output)
  ) {
    return null;
  }
  const delta = source - output;
  if (delta === 0) return "No size change";
  const percent = source > 0 ? Math.abs(delta / source) * 100 : null;
  const sizeText = formatBytes(Math.abs(delta));
  if (percent != null && (percent < 0.1 || Math.abs(delta) < 1024 * 1024)) {
    const direction = delta > 0 ? "saved" : "container overhead";
    return `Size basically unchanged (${sizeText} ${direction})`;
  }
  const share = percent != null ? ` (${percent.toFixed(1)}%)` : "";
  return delta > 0
    ? `Saved ${sizeText}${share}`
    : `Grew by ${sizeText}${share}`;
}

/** What happened to the watched and output folders after the pass, for the scope it ran in. */
export function cleanupStatus(
  parsed: RemuxPassDetail,
): { label: string; value: string }[] {
  if (parsed.media_scope === "movie") {
    return [
      {
        label: "Watched-folder cleanup",
        value: parsed.source_folder_deleted
          ? "Removed source release folder"
          : parsed.source_folder_skip_reason || "Not removed",
      },
      {
        label: "Output-folder cleanup",
        value: parsed.movie_output_folder_deleted
          ? "Removed output title folder"
          : parsed.movie_output_folder_skip_reason ||
            (parsed.movie_output_truth_check
              ? `Not removed (${parsed.movie_output_truth_check})`
              : "Not removed"),
      },
    ];
  }
  if (parsed.media_scope === "tv") {
    return [
      {
        label: "Watched-folder cleanup",
        value: parsed.tv_season_folder_deleted
          ? "Removed watched season folder"
          : parsed.tv_season_folder_skip_reason || "Not removed",
      },
      {
        label: "Output-folder cleanup",
        value: parsed.tv_output_season_folder_deleted
          ? "Removed output season folder"
          : parsed.tv_output_season_folder_skip_reason ||
            (parsed.tv_output_truth_check
              ? `Not removed (${parsed.tv_output_truth_check})`
              : "Not removed"),
      },
    ];
  }
  return [];
}

/** "eng AC3 5.1; jpn AAC 2.0" as a list, one track each. */
export function splitTrackList(value: string | undefined): string[] {
  if (!value) return [];
  return value
    .split(/[;,]/)
    .map((item) => item.trim())
    .filter(Boolean);
}
