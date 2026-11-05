import { keepPreviousData, useQuery } from "@tanstack/react-query";
import {
  fetchActivityRecent,
  type ActivityRecentFilters,
} from "../api/activity-api";

import { activityKeys } from "./query-keys";

/**
 * Recent entries. The rows on screen stay while a changed filter loads, so a panel never flashes empty between two
 * filters; `keepWhileLoading: false` is for a list about one subject (a file's own history), where another
 * subject's rows would be wrong, not merely old.
 */
export function useActivityRecentQuery(
  filters?: ActivityRecentFilters,
  {
    enabled = true,
    keepWhileLoading = true,
  }: { enabled?: boolean; keepWhileLoading?: boolean } = {},
) {
  return useQuery({
    enabled,
    placeholderData: keepWhileLoading ? keepPreviousData : undefined,
    queryKey: activityKeys.recentList(filters),
    queryFn: () => fetchActivityRecent(filters),
    staleTime: 15_000,
  });
}

/** Whether two windows ask for the same thing and differ at most in where they start. */
function sameWindow(held: object, next: object): boolean {
  const without = (filters: object) =>
    JSON.stringify(
      Object.entries(filters)
        .filter(([name, value]) => name !== "date_from" && value !== undefined)
        .sort(([a], [b]) => a.localeCompare(b)),
    );
  return without(held) === without(next);
}

/** Every entry in a window, not only the first page: what a chart counts from must not stop at 100. */
export type ActivityWindow = {
  items: NonNullable<Awaited<ReturnType<typeof fetchActivityRecent>>["items"]>;
  /** The server's count of every entry that matches, whether or not all were fetched. */
  total: number;
  /** False only when the window held more than `maxPages` pages. */
  complete: boolean;
};

export function useActivityWindowQuery(
  filters: Omit<ActivityRecentFilters, "limit" | "before_id">,
  maxPages = 5,
  { enabled = true }: { enabled?: boolean } = {},
) {
  return useQuery({
    enabled,
    queryKey: activityKeys.window(filters, maxPages),
    // A window that rolls forward is the same question asked again, so what it held stays until the next arrives; a
    // different workflow or event type is a different question, and another's figures would be wrong.
    placeholderData: (previous, previousQuery) =>
      previousQuery &&
      previousQuery.queryKey[4] === maxPages &&
      sameWindow(previousQuery.queryKey[3] as object, filters)
        ? previous
        : undefined,
    queryFn: async (): Promise<ActivityWindow> => {
      const items: ActivityWindow["items"] = [];
      let total = 0;
      let beforeId: number | undefined;
      for (let page = 0; page < maxPages; page++) {
        const response = await fetchActivityRecent({
          ...filters,
          limit: 100,
          before_id: beforeId,
        });
        const got = response.items ?? [];
        if (page === 0) total = response.total;
        items.push(...got);
        if (!response.has_more || got.length === 0)
          return { items, total, complete: true };
        beforeId = got[got.length - 1].id;
      }
      return { items, total, complete: false };
    },
    staleTime: 15_000,
  });
}
