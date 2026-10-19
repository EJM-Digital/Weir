import { useMemo } from "react";

import {
  LIBRARY_FILE_CLEANED_EVENT,
  REMUX_PASS_COMPLETED_EVENT,
} from "../../../lib/activity/event-types";
import {
  finishedFileFromEvent,
  type FinishedFile,
} from "../../../lib/activity/processing-outcome";
import { useActivityWindowQuery } from "../../../lib/activity/queries";
import { useProcessingOverviewStatsQuery } from "../../../lib/processing/queries";
import { HANDED_BACK_BUCKET_MS } from "../handed-back-model";
import { TODAY_DAYS, shownBy, type Filter } from "../processing-filter";

/** Five pages of 100, the most a day of one workflow's new downloads has come to. */
const TODAY_PAGES = 5;
/** The server counts every entry that matches, so one page is enough for how many files a library cleaned. */
const COUNT_ONLY_PAGES = 1;
const DAY_MS = 24 * 60 * 60_000;
const NO_DOWNLOADS = { cleaned: 0, savedBytes: 0 } as const;

export type TodayFigures = {
  /** Files Weir cleaned. */
  cleaned: number;
  /** The space that saved; null when the work shown records none, as a library's in-place clean does not. */
  savedBytes: number | null;
};

/** Today's figures once read, and why they could not be when a read they depend on failed. */
type TodayReading = {
  figures: TodayFigures | undefined;
  error: unknown;
};

/** What a day's finished downloads come to for one workflow: the ones Weir cleaned, and the space that saved. */
export function figuresOf(finished: readonly FinishedFile[]): {
  cleaned: number;
  savedBytes: number;
} {
  return {
    cleaned: finished.filter((file) => file.kind === "cleaned").length,
    savedBytes: finished.reduce((sum, file) => sum + (file.savedBytes ?? 0), 0),
  };
}

/** What the day's new downloads and library cleans come to together, for the work the page shows. */
export function combinedFigures(
  filter: Filter,
  downloads: { cleaned: number; savedBytes: number },
  libraryCleaned: number,
): TodayFigures {
  const withDownloads = shownBy(filter, { source: "download" });
  const withLibrary = shownBy(filter, { source: "library" });
  return {
    cleaned:
      (withDownloads ? downloads.cleaned : 0) +
      (withLibrary ? libraryCleaned : 0),
    savedBytes: withDownloads ? downloads.savedBytes : null,
  };
}

/**
 * Today's two figures for the chosen kind of work and workflow. New downloads' are the server's own totals, which
 * cannot be split by workflow, so for one workflow they are counted from that workflow's finished passes over
 * the same day. A library's are the server's count of its clean entries over that day; they record no sizes, so
 * there is no space saved to show for a library alone. The window starts on a five-minute boundary so the query
 * is not refetched every second.
 */
export function useTodayFigures(
  workflowId: number | null,
  filter: Filter,
  now: number,
): TodayReading {
  const totalsQuery = useProcessingOverviewStatsQuery(TODAY_DAYS);
  const totals = totalsQuery.data;
  const oneWorkflow = workflowId !== null;
  const withDownloads = shownBy(filter, { source: "download" });
  const withLibrary = shownBy(filter, { source: "library" });
  const since = new Date(
    Math.floor(now / HANDED_BACK_BUCKET_MS) * HANDED_BACK_BUCKET_MS - DAY_MS,
  )
    .toISOString()
    .replace("Z", "+00:00");
  const dayWindow = {
    date_from: since,
    library_id: workflowId ?? undefined,
  };
  const passesQuery = useActivityWindowQuery(
    { event_type: REMUX_PASS_COMPLETED_EVENT, ...dayWindow },
    TODAY_PAGES,
    { enabled: oneWorkflow && withDownloads },
  );
  const cleansQuery = useActivityWindowQuery(
    { event_type: LIBRARY_FILE_CLEANED_EVENT, ...dayWindow },
    COUNT_ONLY_PAGES,
    { enabled: withLibrary },
  );
  const passes = passesQuery.data;
  const cleans = cleansQuery.data;
  // Only a read the figures are built from can fail them: the totals when every workflow is shown, a workflow's passes when
  // one is, and the library's cleans when library cleaning is shown.
  const error =
    (withDownloads && !oneWorkflow ? totalsQuery.error : null) ??
    (withDownloads && oneWorkflow ? passesQuery.error : null) ??
    (withLibrary ? cleansQuery.error : null);

  const figures = useMemo(() => {
    const downloadFigures = oneWorkflow
      ? passes &&
        figuresOf(
          passes.items
            .map(finishedFileFromEvent)
            .filter((file): file is FinishedFile => file !== null),
        )
      : totals && {
          cleaned: totals.files_processed,
          savedBytes: totals.net_space_saved_bytes,
        };
    if ((withDownloads && !downloadFigures) || (withLibrary && !cleans)) {
      return undefined;
    }
    return combinedFigures(
      filter,
      downloadFigures ?? NO_DOWNLOADS,
      cleans?.total ?? 0,
    );
  }, [oneWorkflow, withDownloads, withLibrary, filter, totals, passes, cleans]);
  return { figures, error };
}
