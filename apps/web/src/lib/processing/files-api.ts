import { sendJson } from "../api/send-json";
import { apiFetch, readJson, requireOk } from "../api/client";
import type { RequestBody, Schema } from "../api/types";

/** "cancelled": its queued pass was cancelled before Weir started on it (#643). */
export type ProcessingFileStatus = Schema<"ProcessingFileOut">["status"];

/** Plain words for each state. The reason string carries the detail. */
const PROCESSING_FILE_STATUS_LABELS: Record<ProcessingFileStatus, string> = {
  unprocessed: "Waiting",
  processing: "Processing",
  processed: "Done",
  processing_failed: "Failed",
  skipped: "Skipped",
  disabled: "Workflow off",
  on_hold: "On hold",
  out_of_schedule: "Waiting for workflow hours",
  blocked_upstream: "Waiting for your media manager",
  passed_through: "Passed through unchanged",
  rejected: "Rejected for a replacement",
  cancelled: "Cancelled",
};

/** How the server marks a rejection by the rules themselves, where no media manager was asked for another copy. */
export const REJECTED_BY_RULES = "rules";

/** The codes the server records for a skip under the workflow's minimum size (`SkipKinds`), and for one whose file it then removed. */
const BELOW_MINIMUM_SIZE = "below_minimum_size";
const BELOW_MINIMUM_SIZE_REMOVED = "below_minimum_size_removed";

/** Whether the workflow's minimum size skipped the file: that is the minimum doing its job, and needs nobody. */
export function isBelowMinimumSize(
  file: Pick<ProcessingFile, "status" | "skip_kind">,
): boolean {
  return (
    file.status === "skipped" &&
    (file.skip_kind === BELOW_MINIMUM_SIZE ||
      file.skip_kind === BELOW_MINIMUM_SIZE_REMOVED)
  );
}

/** Whether the workflow, set to delete rejected files, removed the file for being under its minimum size: there is nothing left to act on. */
export function wasRemovedBelowMinimumSize(
  file: Pick<ProcessingFile, "status" | "skip_kind">,
): boolean {
  return (
    file.status === "skipped" && file.skip_kind === BELOW_MINIMUM_SIZE_REMOVED
  );
}

/**
 * The plain word for a file's state. A rejection no media manager was asked about is just "Rejected", and a file
 * removed for being under the minimum size is "Removed".
 */
export function processingFileStatusLabel(
  file: Pick<ProcessingFile, "status" | "failure_class" | "skip_kind">,
): string {
  if (file.status === "rejected" && file.failure_class === REJECTED_BY_RULES) {
    return "Rejected";
  }
  if (wasRemovedBelowMinimumSize(file)) return "Removed";
  return PROCESSING_FILE_STATUS_LABELS[file.status] ?? file.status;
}

/** The state and its reason as one lead. A reason that already opens with the state's word ("Rejected: …") stands alone. */
export function processingFileLead(
  file: Pick<
    ProcessingFile,
    "status" | "failure_class" | "status_reason" | "skip_kind"
  >,
): string {
  const label = processingFileStatusLabel(file);
  if (wasRemovedBelowMinimumSize(file)) {
    return `${label}: under the workflow's minimum size.`;
  }
  if (!file.status_reason) return label;
  // A reason that opens with the state's own word ("Rejected: …", "Skipped because …") stands alone.
  return file.status_reason.toLowerCase().startsWith(label.toLowerCase())
    ? file.status_reason
    : `${label}. ${file.status_reason}`;
}

/** Whether one device the operator owns will play the file without the media server converting it. */
export type ProcessingDirectPlayVerdict = "yes" | "no" | "maybe" | "unknown";

/** Information only: this never changes what Weir does to a file. */
export interface ProcessingDirectPlay {
  device_id: string;
  device_name: string;
  /** `maybe` means partial or model-dependent support; `unknown` means not measured yet. */
  verdict: ProcessingDirectPlayVerdict;
  /** Plain words such as "cannot play DTS audio". Empty for `yes` and `unknown`. */
  reasons: string[];
}

/**
 * The copy Weir handed back for a media manager to import, and what became of it (#652): what the manager said, and
 * whether Weir removed its copy.
 */
export interface ProcessingFileHandback {
  output_path: string;
  written_at: string | null;
  /** Null while no media manager has said anything about it. */
  outcome: "imported" | "not-imported" | null;
  /** Sonarr, Radarr, Deluno. */
  outcome_by: string | null;
  outcome_at: string | null;
  /** Where the manager put it in its library. */
  imported_path: string | null;
  /** Why the manager will not import it. */
  outcome_reason: string | null;
  /** When Weir removed its copy. */
  released_at: string | null;
  settled_at: string | null;
  /** What happened to the copy, in plain words. */
  release_note: string | null;
}

export interface ProcessingFile {
  /** Which kind of Activity entry this is: always a download here. */
  kind?: "download";
  id: number;
  library_id: number;
  library_name: string;
  relative_path: string;
  status: ProcessingFileStatus;
  status_reason: string;
  /** The file is no longer in the watched folder and Weir is about to stop listing it, so nothing that needs the file can be done. */
  source_gone?: boolean;
  blocked_by_connection: string | null;
  size_bytes: number;
  failure_class: string | null;
  /** Why a skipped file was skipped, as a code (`below_minimum_size`); null for any other skip, and for a file not skipped. */
  skip_kind?: string | null;
  failure_attempts: number;
  next_retry_at: string | null;
  /** The collision policy in force and what it decided, kept on the file rather than only in an activity note. */
  output_collision_policy: string | null;
  output_collision_action: string | null;
  output_collision_reason: string | null;
  video_width: number | null;
  video_height: number | null;
  /** What the file is, measured at the last probe. Null means not probed yet, never zero. */
  video_codec: string | null;
  audio_track_count: number | null;
  subtitle_track_count: number | null;
  duration_seconds: number | null;
  /** One entry per device the operator chose. Empty when none are chosen. */
  direct_play: ProcessingDirectPlay[];
  /** How far the pass currently working on this file has got. Null when nothing is in flight. */
  progress_percent: number | null;
  progress_message: string | null;
  progress_eta_seconds: number | null;
  /** `processing` while the file is written, `finishing` during the final checks. What the Processing lanes key on. */
  progress_status?: string | null;
  /** The step the running pass is on: checking, planning, writing, verifying or handing_back. Null from an older server. */
  progress_stage?: string | null;
  /** ffmpeg's speed as it reports it, for example "148x". */
  progress_speed?: string | null;
  progress_elapsed_seconds?: number | null;
  /** What the running pass is taking out, one line per track as its plan describes it. */
  progress_removed_audio?: string[] | null;
  progress_removed_subtitles?: string[] | null;
  /** When an on-hold file becomes eligible. Null when the wait is on a writer, not the clock. */
  hold_until: string | null;
  size_changed_at: string | null;
  /** When Weir first added this file to the workbench. */
  created_at: string;
  /** When this workbench row last changed state or detail. */
  updated_at: string;
  last_seen_at: string | null;
  last_attempt_at: string | null;
  /** The copy Weir handed back, when it wrote one. */
  handback?: ProcessingFileHandback | null;
  /** Where Weir serves the title's poster. Null until one is found, when none exists, or when artwork is off. */
  poster_url?: string | null;
}

export interface ProcessingFilesPage {
  files: ProcessingFile[];
  status_counts: Record<string, number>;
  returned: number;
  limit: number;
}

/** The ways the file list sorts: by the file's path, by what happened to it, or by when it last changed. */
export type ProcessingFilesSort = "file" | "status" | "when";

export interface ProcessingFilesQuery {
  library_id?: number;
  /** Several statuses join as one comma-separated value; the server splits them (#781). */
  file_status?: ProcessingFileStatus | ProcessingFileStatus[];
  path_contains?: string;
  within_days?: number;
  limit?: number;
  /** Newest change first when not given. */
  sort?: ProcessingFilesSort;
  direction?: "asc" | "desc";
}

export const processingFilesPath = () => "/api/v1/processing/files";

/** A list path with its filters as a query string, leaving out the ones not set. */
export function withQuery(path: string, query: object): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== "") {
      params.set(key, String(value));
    }
  }
  const suffix = params.toString();
  return suffix ? `${path}?${suffix}` : path;
}

export async function fetchProcessingFiles(
  query: ProcessingFilesQuery = {},
): Promise<ProcessingFilesPage> {
  const path = withQuery(processingFilesPath(), query);
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load files");
  return readJson<ProcessingFilesPage>(r);
}

/**
 * Activity's remove dialog (#785): what to offer for one title before it is shown, and the choice a person made.
 * "remove" (the default) is today's plain forget; the other three only apply to a title `remove-options` says
 * `requires_choice` for.
 */
export type ProcessingFileRemovalResolution =
  Schema<"ProcessingFileForgetIn">["resolution"];

export type ProcessingFileRemoveOptions =
  Schema<"ProcessingFileRemoveOptionsOut">;

export async function fetchProcessingFileRemoveOptions(
  id: number,
): Promise<ProcessingFileRemoveOptions> {
  const path = `${processingFilesPath()}/${id}/remove-options`;
  const response = await apiFetch(path);
  await requireOk(
    path,
    response,
    "Could not read what removing this file would do",
  );
  return readJson<ProcessingFileRemoveOptions>(response);
}

/**
 * `delete`, `keep` and `retry` answer what actually happened (#786 review of #785) — `retry`'s `done` is false only
 * when a concluded file's original is gone, in which case `detail` says so instead of claiming it was queued.
 * A plain remove (no `resolution`) answers no body at all.
 */
export type ProcessingFileRemovalResult = Schema<"ProcessingFileRemovalOut">;

/**
 * The file's current details, exactly as `remove-options` reported them, for a title with no recorded fingerprint
 * (#786 follow-up: migration 0025 only records one going forward). `delete` and `keep` need this echoed back so
 * Weir can check the file on disk still matches what the dialog showed before touching anything.
 */
export interface ProcessingFileRemovalConfirm {
  size_bytes: number;
  modified_at: string;
}

export async function forgetProcessingFile(
  id: number,
  resolution?: ProcessingFileRemovalResolution,
  confirm?: ProcessingFileRemovalConfirm,
): Promise<ProcessingFileRemovalResult | undefined> {
  const path = `${processingFilesPath()}/${id}`;
  const response = await sendJson(
    path,
    "DELETE",
    {
      ...(resolution ? { resolution } : {}),
      ...(confirm
        ? {
            confirm_size_bytes: confirm.size_bytes,
            confirm_modified_at: confirm.modified_at,
          }
        : {}),
    },
    "Could not remove that file from the list",
  );
  return readJson<ProcessingFileRemovalResult | undefined>(response);
}

export type ProcessingFileMoveToTopResult =
  Schema<"ProcessingFileMoveToTopOut">;

export async function moveProcessingFileToTop(
  id: number,
): Promise<ProcessingFileMoveToTopResult> {
  const path = `${processingFilesPath()}/${id}/move-to-top`;
  const response = await sendJson(
    path,
    "POST",
    {},
    "Could not move that file to the front of the queue",
  );
  return readJson<ProcessingFileMoveToTopResult>(response);
}

export type ProcessingRequeueResult = Schema<"ProcessingRequeueOut">;

export async function requeueProcessingFile(
  id: number,
): Promise<ProcessingRequeueResult> {
  const path = `${processingFilesPath()}/${id}/requeue`;
  const response = await sendJson(
    path,
    "POST",
    {},
    "Could not queue that file again",
  );
  return readJson<ProcessingRequeueResult>(response);
}

export interface ProcessingBulkRequeueQuery {
  /** Only these files: the exact set on screen, whatever else matches the filters. */
  file_ids?: number[];
  library_id?: number;
  file_status?: ProcessingFileStatus;
  path_contains?: string;
  limit?: number;
}

export async function requeueProcessingFiles(
  query: ProcessingBulkRequeueQuery,
): Promise<ProcessingRequeueResult> {
  const path = `${processingFilesPath()}/requeue`;
  const response = await sendJson(
    path,
    "POST",
    query,
    "Could not queue those files again",
  );
  return readJson<ProcessingRequeueResult>(response);
}

export interface ProcessingWhyHeld {
  file_id: number;
  relative_path: string;
  library_name: string;
  recorded_status: string;
  recorded_reason: string;
  verdict: "proceed" | "wait_upstream" | "not_held" | "no_upstream_signal";
  owned: boolean;
  blocked_upstream: boolean;
  blocked_by_connection: string | null;
  queue_row_count: number;
  managers_consulted: number;
  managers_reporting: number;
  managers_without_queue_signal: string[];
  reasons: string[];
}

export async function fetchProcessingWhyHeld(
  id: number,
): Promise<ProcessingWhyHeld> {
  const path = `${processingFilesPath()}/${id}/why-held`;
  const response = await apiFetch(path);
  await requireOk(path, response, "Could not ask why that file is held");
  return readJson<ProcessingWhyHeld>(response);
}

/** One plain-language step, written by the backend from the stored pass record. */
export interface ProcessingFileStoryStep {
  heading: string;
  sentence: string;
  /** How the step should read — not a severity. */
  tone: "neutral" | "good" | "warn" | "bad";
}

export interface ProcessingFileLogEntry {
  id: number;
  recorded_at: string;
  outcome: string;
  title: string;
  library_name: string;
  detail: Record<string, unknown>;
  /** The same pass told in plain language. Empty for a record with nothing to narrate. */
  story: ProcessingFileStoryStep[];
}

export interface ProcessingFileLog {
  file_id: number;
  relative_path: string;
  /** 0 means these records are kept forever. */
  retention_days: number;
  entries: ProcessingFileLogEntry[];
}

export async function fetchProcessingFileLog(
  id: number,
): Promise<ProcessingFileLog> {
  const path = `${processingFilesPath()}/${id}/log`;
  const response = await apiFetch(path);
  await requireOk(
    path,
    response,
    "Weir couldn't load what happened to this file. Try again.",
  );
  return readJson<ProcessingFileLog>(response);
}

/** The plain-text record, for attaching to a bug report. */
export function processingFileLogDownloadPath(id: number): string {
  return `${processingFilesPath()}/${id}/log/download`;
}

/** One ffprobe stream on a held file (issue #501), with what the saved rules would do to it and why. */
export interface ProcessingFileTrack {
  index: number;
  /** video, audio, subtitle, image, attachment or other. Only video, audio and subtitle can be chosen. */
  type: "video" | "audio" | "subtitle" | "image" | "attachment" | "other";
  codec: string | null;
  language: string | null;
  title: string | null;
  /** Audio only. */
  channels: number | null;
  /** The stream's own disposition on the held source, not what the rules would choose. */
  default: boolean;
  forced: boolean;
  rule_would_keep: boolean;
  rule_reason: string;
}

export interface ProcessingFileTracks {
  file_id: number;
  relative_path: string;
  media_scope: string;
  source_fingerprint: {
    device: number;
    inode: number;
    size_bytes: number;
    modified_time_ns: number;
  };
  streams: ProcessingFileTrack[];
}

export async function fetchProcessingFileTracks(
  id: number,
): Promise<ProcessingFileTracks> {
  const path = `${processingFilesPath()}/${id}/tracks`;
  const response = await apiFetch(path);
  await requireOk(path, response, "Could not read that file's tracks");
  return readJson<ProcessingFileTracks>(response);
}

export type ProcessingManualPlanChoice = RequestBody<"ProcessingManualPlanIn">;
export type ProcessingManualPlanResult = Schema<"ProcessingManualPlanOut">;

export async function postProcessingManualPlan(
  id: number,
  choice: ProcessingManualPlanChoice,
): Promise<ProcessingManualPlanResult> {
  const path = `${processingFilesPath()}/${id}/manual-plan`;
  const response = await sendJson(
    path,
    "POST",
    choice,
    "Could not queue that track choice",
  );
  return readJson<ProcessingManualPlanResult>(response);
}
