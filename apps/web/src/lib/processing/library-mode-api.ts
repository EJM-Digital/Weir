/** Library mode (#505): clean files already in a library, in place. */
import { sendJson, sendJsonUnchecked } from "../api/send-json";
import { apiFetch, readJson, requireOk } from "../api/client";
import type { Schema } from "../api/types";

export type LibrarySettings = Schema<"LibrarySettingsOut">;
export type LibraryFile = Schema<"LibraryFileOut">;
export type LibraryFileStatus = LibraryFile["status"];
/** Why a file is not something Weir will clean. */
export type LibraryProblemKind = NonNullable<LibraryFile["problem_kind"]>;
export type LibraryTotals = Schema<"LibraryTotalsOut">;
export type LibraryScanInfo = Schema<"LibraryScanStateOut">;
export type LibraryBreakdowns = Schema<"LibraryBreakdownsOut">;
/** Once a day, inside the library's own schedule window. next_run_at is null when it is off or cannot run. */
export type LibraryModeSchedule = Schema<"LibraryModeScheduleOut">;
export type LibraryOverview = Schema<"LibraryOverviewOut">;
export type LibraryFilesResult = Schema<"LibraryFilesOut">;
export type LibraryScanTrigger = Schema<"LibraryScanTriggerOut">;
export type LibraryCleanResult = {
  kind: "cleaned";
} & Schema<"LibraryCleanOut">;

/** The facets a library is broken down by and its files filtered on, as the server names them. */
export const LIBRARY_FACETS = [
  "video_codec",
  "resolution",
  "audio",
  "audio_language",
  "subtitle_language",
] as const satisfies readonly (keyof LibraryBreakdowns)[];

export type LibraryFacet = (typeof LIBRARY_FACETS)[number];

/** The Files table's sortable columns, as the server names them. */
export type LibraryFileSort = LibraryFilesResult["sort"];

/** Every way the Files table can be narrowed, sorted and paged. */
export interface LibraryFileFilters {
  /** Where each file stands now against the current rules; no value is every file. */
  status?: LibraryFileStatus;
  manager?: string;
  q?: string;
  problem?: LibraryProblemKind;
  facets?: Partial<Record<LibraryFacet, string>>;
  sort?: LibraryFileSort;
  direction?: "asc" | "desc";
  page?: number;
  page_size?: number;
}

/** The exact #505 point 5 confirmation: "N files, M tracks will be removed..." plus the size behind it. */
export interface LibraryConfirmationRequired {
  kind: "confirmation_required";
  detail: string;
  files_count: number;
  tracks_count: number;
  estimated_bytes_saved: number;
  /** #508's per-file preflight notes (seeding, re-download risk), one line per file that would be skipped. */
  warnings?: string[];
}

function librarySettingsPath(libraryId: number): string {
  return `/api/v1/processing/libraries/${libraryId}/library-settings`;
}

export async function fetchLibrarySettings(
  libraryId: number,
): Promise<LibrarySettings> {
  const path = librarySettingsPath(libraryId);
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load this library's setup");
  return readJson<LibrarySettings>(r);
}

/** #735: the folder a kept original goes in when a library has not set one of its own. */
export const DEFAULT_ORIGINALS_FOLDER_NAME = ".weir-originals";

/** What the "Keep the original after cleaning" hint and the file drawer's recoverability note both show. */
export function originalsFolderLabel(originalsFolder: string): string {
  return originalsFolder.trim().length > 0
    ? originalsFolder
    : `a ${DEFAULT_ORIGINALS_FOLDER_NAME} folder inside each library folder`;
}

/**
 * What a save of the library's setup sends. `library_folders` is required on every call (the API replaces the
 * whole list, not just the fields sent, when it is present — an absent list is read as "no folders", not "leave
 * unchanged"), so a checkbox-only save must still pass the library's current folders. Every other field is
 * optional and keeps its saved value when left out; a null `library_rule_set_id` goes back to the workflow's
 * profile.
 */
export interface LibrarySettingsUpdate {
  library_folders: string[];
  clean_hardlinked_files?: boolean;
  skip_if_manager_would_redownload?: boolean;
  keep_original_after_clean?: boolean;
  originals_folder?: string;
  library_rule_set_id?: number | null;
}

export async function saveLibrarySettings(
  libraryId: number,
  updates: LibrarySettingsUpdate,
): Promise<LibrarySettings> {
  const path = librarySettingsPath(libraryId);
  const r = await sendJson(
    path,
    "PUT",
    updates,
    "Could not save this library's setup",
  );
  return readJson<LibrarySettings>(r);
}

/** What a person is told when a check of the library could not be started and the server gave no reason of its own. */
export const SCAN_START_FAILED =
  "Weir couldn't start a check. Try again in a moment.";

/** What a person is told when a clean could not be started and the server gave no reason of its own. */
export const CLEAN_START_FAILED =
  "Weir couldn't start the clean. Nothing was changed. Try again.";

/** What a person is told when a file could not be set aside, or taken back, and the server gave no reason of its own. */
export const LEAVE_ALONE_FAILED =
  "Weir couldn't save that. The file is as it was; try again.";

export async function triggerLibraryScan(
  libraryId: number,
): Promise<LibraryScanTrigger> {
  const path = `/api/v1/processing/libraries/${libraryId}/library-scan`;
  const r = await sendJson(path, "POST", {}, SCAN_START_FAILED);
  return readJson<LibraryScanTrigger>(r);
}

/** Turns the Files table's filters into the query string the server reads them from. */
export function libraryFileFiltersToParams(
  filters: LibraryFileFilters,
): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.status) params.set("status", filters.status);
  if (filters.manager) params.set("manager", filters.manager);
  if (filters.q) params.set("q", filters.q);
  if (filters.problem) params.set("problem", filters.problem);
  for (const facet of LIBRARY_FACETS) {
    const value = filters.facets?.[facet];
    if (value) params.set(facet, value);
  }
  if (filters.sort) params.set("sort", filters.sort);
  if (filters.direction) params.set("direction", filters.direction);
  if (filters.page && filters.page > 1)
    params.set("page", String(filters.page));
  if (filters.page_size) params.set("page_size", String(filters.page_size));
  return params;
}

export async function fetchLibraryFiles(
  libraryId: number,
  filters: LibraryFileFilters = {},
): Promise<LibraryFilesResult> {
  const suffix = libraryFileFiltersToParams(filters).toString();
  const path = `/api/v1/processing/libraries/${libraryId}/library-files${suffix ? `?${suffix}` : ""}`;
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load this workflow's files");
  return readJson<LibraryFilesResult>(r);
}

const NO_FILES_IN_ANY_STATUS: LibraryTotals["by_status"] = {
  needs_cleaning: 0,
  cleaning: 0,
  matches: 0,
  cant_clean_yet: 0,
  left_alone: 0,
};

/**
 * An overview with a count for every status. A server that does not report the per-status counts, such as an older one,
 * counts no file in any status rather than leaving the page to read a count that is not there.
 */
export function withStatusCounts(overview: LibraryOverview): LibraryOverview {
  const { totals } = overview;
  if (!totals || totals.by_status) return overview;
  return {
    ...overview,
    totals: { ...totals, by_status: NO_FILES_IN_ANY_STATUS },
  };
}

/** #568's Overview: totals and every breakdown, aggregated on the server. */
export async function fetchLibraryOverview(
  libraryId: number,
): Promise<LibraryOverview> {
  const path = `/api/v1/processing/libraries/${libraryId}/library-overview`;
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load this workflow's overview");
  return withStatusCounts(await readJson<LibraryOverview>(r));
}

/** Parses the structured 400 the API returns when removal needs confirming. Rethrows anything else. */
async function readConfirmationOr<T>(
  path: string,
  r: Response,
  fallback: string,
): Promise<LibraryConfirmationRequired | T> {
  if (r.status === 400) {
    const body = await readJson<Record<string, unknown>>(r);
    if (body && body.error === "confirm_final_removal_required") {
      return {
        kind: "confirmation_required",
        detail: String(body.detail ?? ""),
        files_count: Number(body.files_count ?? 0),
        tracks_count: Number(body.tracks_count ?? 0),
        estimated_bytes_saved: Number(body.estimated_bytes_saved ?? 0),
        warnings: Array.isArray(body.warnings)
          ? body.warnings.map(String)
          : undefined,
      };
    }
  }
  await requireOk(path, r, fallback);
  return readJson<T>(r);
}

/**
 * Your own choice of tracks for one file (#501's shape), sent with a clean instead of leaving it to the library's
 * rules. `expected_size_bytes` is the size the file had when you chose: Weir refuses the clean rather than apply
 * the choice to a file that has changed since.
 */
export interface LibraryManualPlan {
  keep: { index: number; default: boolean; forced: boolean }[];
  order: number[];
  expected_size_bytes?: number;
}

export async function cleanLibraryFiles(
  libraryId: number,
  paths: string[],
  confirm_final_removal: boolean,
  manual?: LibraryManualPlan,
): Promise<LibraryConfirmationRequired | LibraryCleanResult> {
  const path = `/api/v1/processing/libraries/${libraryId}/library-files/clean`;
  const r = await sendJsonUnchecked(path, "POST", {
    paths,
    confirm_final_removal,
    ...(manual
      ? {
          manual_plan: { keep: manual.keep, order: manual.order },
          ...(manual.expected_size_bytes === undefined
            ? {}
            : { expected_size_bytes: manual.expected_size_bytes }),
        }
      : {}),
  });
  const result = await readConfirmationOr<Omit<LibraryCleanResult, "kind">>(
    path,
    r,
    CLEAN_START_FAILED,
  );
  return "kind" in result ? result : { kind: "cleaned", ...result };
}

/** "Leave this file alone" and its undo; a rescan does not forget it. */
export async function setLibraryFileLeaveAlone(
  libraryId: number,
  filePath: string,
  leave_alone: boolean,
): Promise<{ path: string; leave_alone: boolean }> {
  const path = `/api/v1/processing/libraries/${libraryId}/library-files/leave-alone`;
  const r = await sendJson(
    path,
    "POST",
    { path: filePath, leave_alone },
    LEAVE_ALONE_FAILED,
  );
  return readJson<{ path: string; leave_alone: boolean }>(r);
}

export async function setLibrarySchedule(
  libraryId: number,
  enabled: boolean,
  confirm_final_removal: boolean,
): Promise<LibraryConfirmationRequired | LibrarySettings> {
  const path = `/api/v1/processing/libraries/${libraryId}/library-schedule`;
  const r = await sendJsonUnchecked(path, "POST", {
    enabled,
    confirm_final_removal,
  });
  return readConfirmationOr<LibrarySettings>(
    path,
    r,
    "Could not change the workflow schedule",
  );
}
