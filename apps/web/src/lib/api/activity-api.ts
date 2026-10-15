import { apiFetch, readJson, requireOk } from "./client";
import type { ActivityRecentResponse } from "./types";

export type ActivityRecentFilters = {
  limit?: number;
  module?: string;
  event_type?: string;
  search?: string;
  date_from?: string;
  date_to?: string;
  before_id?: number;
  trigger?: string;
  result?: string;
  library_id?: number;
  file?: string;
  /** "weir": Weir's own events, not about one file (System › Logs). "files": the events about a file. */
  about?: "weir" | "files";
  /**
   * Leaves out an event about a file Weir has forgotten. For a live list such as Processing's "Just finished"
   * lane, not for System › Logs or the export, which want the complete record and never set this.
   */
  known_files_only?: boolean;
  /**
   * Lists each file once, as it stands now: of the events about a file, only the newest that still describes it, so a
   * failure the file has since got past does not outlive it. For a live list of what finished, not for the complete
   * record.
   */
  current_only?: boolean;
  /** False leaves out the count of every matching entry, which a list that shows none does not need. */
  with_total?: boolean;
};

/** Whether a number can be asked for: one that is not a number (NaN, infinity) is left out of the address, as an unset one is. */
function isWhole(value: number | undefined): value is number {
  return value !== undefined && Number.isFinite(value);
}

/** The filters shared by the feed and the export, as query parameters. */
function activityFilterParams(
  options?: Omit<ActivityRecentFilters, "limit" | "before_id">,
): URLSearchParams {
  const q = new URLSearchParams();
  if (options?.module) q.set("module", options.module);
  if (options?.event_type) q.set("event_type", options.event_type);
  if (options?.search) q.set("search", options.search);
  if (options?.date_from) q.set("date_from", options.date_from);
  if (options?.date_to) q.set("date_to", options.date_to);
  if (options?.trigger) q.set("trigger", options.trigger);
  if (options?.result) q.set("result", options.result);
  if (isWhole(options?.library_id))
    q.set("library_id", String(Math.trunc(options.library_id)));
  if (options?.file) q.set("file", options.file);
  if (options?.about) q.set("about", options.about);
  return q;
}

export function activityRecentPath(options?: ActivityRecentFilters): string {
  const q = activityFilterParams(options);
  if (isWhole(options?.limit))
    q.set("limit", String(Math.trunc(options.limit)));
  if (isWhole(options?.before_id))
    q.set("before_id", String(Math.trunc(options.before_id)));
  // Not in activityFilterParams: the export shares that helper, and always wants the complete record.
  if (options?.known_files_only) {
    q.set("known_files_only", "true");
  }
  if (options?.current_only) {
    q.set("current_only", "true");
  }
  if (options?.with_total === false) {
    q.set("with_total", "false");
  }
  const qs = q.toString();
  return qs ? `/api/v1/activity/recent?${qs}` : "/api/v1/activity/recent";
}

export async function fetchActivityRecent(
  options?: ActivityRecentFilters,
): Promise<ActivityRecentResponse> {
  const path = activityRecentPath(options);
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load activity");
  return readJson<ActivityRecentResponse>(r);
}

export function activityExportPath(
  format: "csv" | "json",
  options?: ActivityRecentFilters,
): string {
  const q = activityFilterParams(options);
  q.set("format", format);
  return `/api/v1/activity/export?${q.toString()}`;
}

export function filenameFromDisposition(
  header: string | null,
  fallback: string,
): string {
  const match = header?.match(/filename="?([^";]+)"?/i);
  return match?.[1]?.trim() || fallback;
}
