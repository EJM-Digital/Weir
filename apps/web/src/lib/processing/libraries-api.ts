import { sendJson } from "../api/send-json";
import { apiFetch, readJson, requireOk } from "../api/client";

export type ProcessingMediaType = "movie" | "tv";

export type ProcessingFailurePolicy = "pass_through" | "hold" | "reject";

export const PROCESSING_MEDIA_TYPE_LABELS: Record<ProcessingMediaType, string> =
  {
    movie: "Movies",
    tv: "TV episodes",
  };

/** One configured Processing library. Adding one is a POST, not a schema change. */
export interface ProcessingLibrary {
  id: number;
  name: string;
  enabled: boolean;
  media_type: ProcessingMediaType;
  display_order: number;

  watched_folder: string;
  work_folder: string;
  output_folder: string;

  media_extensions_csv: string;
  exclude_markers_csv: string;
  include_patterns_csv: string;
  exclude_patterns_csv: string;
  /** The smallest file this workflow takes, in MB. 0 takes any size. */
  min_file_size_mb: number;
  max_file_size_mb: number;
  rejected_file_action: "leave" | "delete_file";
  /** A new file is ready once neither its size nor its last-changed time has moved for this many seconds. */
  ready_after_seconds: number;
  created_after: string | null;
  created_before: string | null;
  modified_after: string | null;
  modified_before: string | null;
  exclude_hidden: boolean;
  top_level_only: boolean;

  scan_interval_seconds: number;
  /** Files beside the video that travel with it, renamed to the output's stem. Empty migrates nothing. */
  sidecar_patterns_csv: string;
  preserve_original_timestamps: boolean;
  /** Off keeps the original download in the watched folder after cleaning, so a torrent keeps seeding. */
  remove_original_after_success: boolean;
  /** Megabytes kept free on the drive this workflow writes to; a file that would leave less is held. 0 turns it off. */
  minimum_free_disk_space_mb: number;
  /** Which tool writes the output, for new downloads and library cleaning: mkvmerge for MKV when installed and FFmpeg otherwise or when mkvmerge fails (best), or FFmpeg only. */
  remux_writer: RemuxWriter;
  /** What to do when an output already exists at the same path. "replace" is the long-standing behaviour. */
  output_collision_policy: string;
  ffmpeg_strictness: string;
  ignore_size_changes: boolean;
  skip_access_tests: boolean;
  file_system_events_enabled: boolean;
  max_attempts: number;
  retry_backoff_seconds: number;
  retry_execution_failures: boolean;
  retry_preflight_failures: boolean;
  /** What happens once retries run out: hand the original back, keep it, or reject the release (#465, #471). */
  failure_policy: ProcessingFailurePolicy;
  schedule_grid: string;
  schedule_enabled: boolean;
  schedule_hours_limited: boolean;
  schedule_days: string;
  schedule_start: string;
  schedule_end: string;

  /** The most files this workflow runs at once, as a share of Files at once in Performance; 0 is no limit of its own. */
  max_concurrent_files: number;
  /** What the workflow is held to now: its own limit, and never more than Files at once in Performance. */
  effective_max_concurrent_files: number;
  priority: number;

  rule_set_id: number | null;
  manager_connection_ids: number[];
  manager_coverage: "connected" | "no_upstream_signal" | "unreachable" | string;
  manager_coverage_detail: string;
  discovered_from_connection_id: number | null;
  discovered_library_key: string | null;
  /** The media manager (Deluno) that owns the watched and output folders and keeps them up to date; null when they are the workflow's own. */
  folders_synced_from_connection_id: number | null;
  /** Queued or running jobs. Deletion is refused while this is non-zero. */
  active_job_count: number;
  /** When Weir next looks at the watched folder (the next periodic scan, or sooner when a held file's wait ends). */
  next_look_at?: string | null;
  /** Whether the periodic scan timer currently runs for this workflow: off, held back by its own hours, or scheduled. */
  periodic_scan?: "off" | "outside_hours" | "scheduled";
  /** When the periodic scan next creates a scan; null while it is off, or outside hours with no reopening. */
  next_scan_at?: string | null;
  updated_at: string | null;
}

export interface ProcessingLibraryWrite {
  name: string;
  media_type: ProcessingMediaType;
  enabled: boolean;
  watched_folder: string;
  work_folder: string;
  output_folder: string;
  media_extensions_csv: string;
  exclude_markers_csv: string;
  include_patterns_csv: string;
  exclude_patterns_csv: string;
  min_file_size_mb: number;
  max_file_size_mb: number;
  rejected_file_action: "leave" | "delete_file";
  ready_after_seconds: number;
  created_after: string | null;
  created_before: string | null;
  modified_after: string | null;
  modified_before: string | null;
  exclude_hidden: boolean;
  top_level_only: boolean;
  scan_interval_seconds: number;
  sidecar_patterns_csv: string;
  preserve_original_timestamps: boolean;
  /** Off keeps the original download in the watched folder after cleaning, so a torrent keeps seeding. */
  remove_original_after_success: boolean;
  /** Megabytes to keep free on the drive this workflow writes to; 0 turns the check off. */
  minimum_free_disk_space_mb: number;
  /** Which tool writes the output: mkvmerge for MKV when installed and FFmpeg otherwise (best), or FFmpeg only. */
  remux_writer: RemuxWriter;
  output_collision_policy: string;
  ffmpeg_strictness: string;
  ignore_size_changes: boolean;
  skip_access_tests: boolean;
  file_system_events_enabled: boolean;
  max_attempts: number;
  retry_backoff_seconds: number;
  retry_execution_failures: boolean;
  retry_preflight_failures: boolean;
  /** What happens once retries run out: hand the original back, keep it, or reject the release (#465, #471). */
  failure_policy: ProcessingFailurePolicy;
  schedule_grid: string;
  schedule_enabled: boolean;
  schedule_hours_limited: boolean;
  schedule_days: string;
  schedule_start: string;
  schedule_end: string;
  max_concurrent_files: number;
  priority: number;
  rule_set_id: number | null;
  manager_connection_ids: number[];
}

export type RemuxWriter = "best" | "ffmpeg";

/** A new library needs only a name and media type; the server fills in every other field's default. */
export type ProcessingLibraryCreate = Pick<
  ProcessingLibraryWrite,
  "name" | "media_type"
> &
  Partial<ProcessingLibraryWrite>;

export const processingLibrariesPath = () => "/api/v1/processing/libraries";
const libraryPath = (id: number) => `${processingLibrariesPath()}/${id}`;

export async function fetchProcessingLibraries(): Promise<ProcessingLibrary[]> {
  const path = processingLibrariesPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load workflows");
  return readJson<ProcessingLibrary[]>(r);
}

export async function createProcessingLibrary(
  data: ProcessingLibraryCreate,
): Promise<ProcessingLibrary> {
  const path = processingLibrariesPath();
  const r = await sendJson(path, "POST", data, "Could not add that workflow");
  return readJson<ProcessingLibrary>(r);
}

export async function updateProcessingLibrary(
  id: number,
  data: ProcessingLibraryWrite,
): Promise<ProcessingLibrary> {
  const path = libraryPath(id);
  const r = await sendJson(path, "PUT", data, "Could not save that workflow");
  return readJson<ProcessingLibrary>(r);
}

export async function deleteProcessingLibrary(id: number): Promise<void> {
  // A 409 carries the refusal reason, queued work, which the operator can act on.
  await sendJson(
    libraryPath(id),
    "DELETE",
    {},
    "Could not remove that workflow",
  );
}

export async function reorderProcessingLibraries(
  library_ids_in_order: number[],
): Promise<ProcessingLibrary[]> {
  const path = `${processingLibrariesPath()}/reorder`;
  const r = await sendJson(
    path,
    "POST",
    { library_ids_in_order },
    "Could not reorder workflows",
  );
  return readJson<ProcessingLibrary[]>(r);
}

export function writeFromProcessingLibrary(
  library: ProcessingLibrary,
): ProcessingLibraryWrite {
  return {
    name: library.name,
    media_type: library.media_type,
    enabled: library.enabled,
    watched_folder: library.watched_folder,
    work_folder: library.work_folder,
    output_folder: library.output_folder,
    media_extensions_csv: library.media_extensions_csv,
    exclude_markers_csv: library.exclude_markers_csv,
    include_patterns_csv: library.include_patterns_csv,
    exclude_patterns_csv: library.exclude_patterns_csv,
    min_file_size_mb: library.min_file_size_mb,
    max_file_size_mb: library.max_file_size_mb,
    rejected_file_action: library.rejected_file_action,
    ready_after_seconds: library.ready_after_seconds,
    created_after: library.created_after,
    created_before: library.created_before,
    modified_after: library.modified_after,
    modified_before: library.modified_before,
    exclude_hidden: library.exclude_hidden,
    top_level_only: library.top_level_only,
    scan_interval_seconds: library.scan_interval_seconds,
    sidecar_patterns_csv: library.sidecar_patterns_csv,
    preserve_original_timestamps: library.preserve_original_timestamps,
    remove_original_after_success: library.remove_original_after_success,
    minimum_free_disk_space_mb: library.minimum_free_disk_space_mb,
    remux_writer: library.remux_writer,
    output_collision_policy: library.output_collision_policy,
    ffmpeg_strictness: library.ffmpeg_strictness,
    ignore_size_changes: library.ignore_size_changes,
    skip_access_tests: library.skip_access_tests,
    file_system_events_enabled: library.file_system_events_enabled,
    max_attempts: library.max_attempts,
    retry_backoff_seconds: library.retry_backoff_seconds,
    retry_execution_failures: library.retry_execution_failures,
    retry_preflight_failures: library.retry_preflight_failures,
    failure_policy: library.failure_policy,
    schedule_grid: library.schedule_grid,
    schedule_enabled: library.schedule_enabled,
    schedule_hours_limited: library.schedule_hours_limited,
    schedule_days: library.schedule_days,
    schedule_start: library.schedule_start,
    schedule_end: library.schedule_end,
    max_concurrent_files: library.max_concurrent_files,
    priority: library.priority,
    rule_set_id: library.rule_set_id,
    manager_connection_ids: library.manager_connection_ids,
  };
}

/**
 * The type badge beside a library's name, or null when the name already says it
 * ("Movies · Movies"). Shared by every page that lists libraries so they agree on
 * when the badge is worth the room.
 */
export function processingMediaTypeBadge(
  library: Pick<ProcessingLibrary, "name" | "media_type">,
): string | null {
  const label = PROCESSING_MEDIA_TYPE_LABELS[library.media_type];
  const name = library.name.trim().toLowerCase();
  const lower = label.toLowerCase();
  if (!name || lower.includes(name) || name.includes(lower)) return null;
  if (library.media_type === "tv" && /\b(tv|shows?|series)\b/.test(name)) {
    return null;
  }
  return label;
}
