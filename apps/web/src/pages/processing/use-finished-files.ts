import { useEffect, useMemo, useRef, useState } from "react";

import {
  LIBRARY_FILE_CLEANED_EVENT,
  REMUX_PASS_COMPLETED_EVENT,
} from "../../lib/activity/event-types";
import {
  finishedFileFromEvent,
  type FinishedFile,
} from "../../lib/activity/processing-outcome";
import { useActivityRecentQuery } from "../../lib/activity/queries";
import { shownBy, type Filter } from "./processing-filter";
import { prettyName } from "./processing-model";
import { finishedLine } from "./processing-words";

const RECENT_PASSES = 24;
const RECENT_CLEANS = 12;

/** The newest finished files, and why they could not be read when a read failed, so an empty list is not taken for a quiet day. */
type FinishedReading = { files: FinishedFile[]; error: unknown };

/**
 * The newest finished files, from the Activity entry each one wrote: both kinds, or the one kind and the one
 * workflow the page is narrowed to, which the server picks out so the shelf has that many of its own to show.
 */
export function useFinishedFiles(
  filter: Filter = "all",
  workflowId: number | null = null,
): FinishedReading {
  const workflow = workflowId === null ? {} : { library_id: workflowId };
  const withPasses = shownBy(filter, { source: "download" });
  const withCleans = shownBy(filter, { source: "library" });
  // A download whose title the owner has since removed from Activity drops out here; a library clean has no
  // such removal, and library files are not in known_files_only's reckoning, so its query never asks for it. A file
  // is listed once, as it stands now: a failure it has since got past, or an earlier pass of the same file, is left out.
  const passes = useActivityRecentQuery(
    {
      limit: RECENT_PASSES,
      event_type: REMUX_PASS_COMPLETED_EVENT,
      known_files_only: true,
      current_only: true,
      with_total: false,
      ...workflow,
    },
    { enabled: withPasses },
  );
  const cleans = useActivityRecentQuery(
    {
      limit: withPasses ? RECENT_CLEANS : RECENT_PASSES,
      event_type: LIBRARY_FILE_CLEANED_EVENT,
      ...workflow,
    },
    { enabled: withCleans },
  );
  const files = useMemo(() => {
    const all = [...(passes.data?.items ?? []), ...(cleans.data?.items ?? [])]
      .map(finishedFileFromEvent)
      .filter((item): item is FinishedFile => item !== null);
    return all.sort((a, b) => b.finishedAt.localeCompare(a.finishedAt));
  }, [passes.data, cleans.data]);
  return { files, error: passes.error ?? cleans.error ?? null };
}

/**
 * "{name} finished: {summary}" for a screen reader, the moment a new file lands here. Nothing is
 * announced for the files already on screen when the page itself first loads.
 */
export function useFinishedAnnouncement(
  finished: readonly FinishedFile[],
): string {
  const [announcement, setAnnouncement] = useState("");
  const lastId = useRef<number | null>(null);
  const initialized = useRef(false);
  useEffect(() => {
    const newest = finished[0] ?? null;
    if (!initialized.current) {
      initialized.current = true;
      lastId.current = newest?.id ?? null;
      return;
    }
    if (!newest || newest.id === lastId.current) return;
    lastId.current = newest.id;
    setAnnouncement(
      `${prettyName(newest.relativePath)} finished: ${finishedLine(newest)}`,
    );
  }, [finished]);
  return announcement;
}
