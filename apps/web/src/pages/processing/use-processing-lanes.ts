import { useMemo } from "react";

import { useLiveProgress } from "../../lib/activity/use-activity-stream-invalidation";
import { useProcessingFilesQuery } from "../../lib/processing/files-queries";
import { useProcessingJobsInspectionQuery } from "../../lib/processing/jobs-inspection/queries";
import { useProcessingLibrariesQuery } from "../../lib/processing/libraries-queries";
import { parseAppTime } from "../../lib/ui/mm-format-date";
import {
  boardFiles,
  buildLanes,
  libraryCleanWorkflowId,
  queuedBeyondPage,
} from "./processing-model";
import {
  ACTIVE_JOBS_LIMIT,
  FILES_QUERY,
  WORKING_FILES_QUERY,
} from "./working-count";

/**
 * The lanes' files, grouped by where each one is, with what each library knows about its next look.
 * `lanes` holds every workflow's files, which the cards leaving the board are tracked against; `scoped` holds
 * only the chosen workflow's (all of them when none is chosen), which is what the page shows. `queuedMore` is how many
 * queued files that scope holds beyond the cards drawn for them, since the page of files stops at its limit.
 */
export function useLanes(workflowId: number | null = null) {
  const files = useProcessingFilesQuery(FILES_QUERY);
  const scopedFiles = useProcessingFilesQuery(
    workflowId === null
      ? FILES_QUERY
      : { ...FILES_QUERY, library_id: workflowId },
  );
  const workingFiles = useProcessingFilesQuery(WORKING_FILES_QUERY);
  const libraries = useProcessingLibrariesQuery();
  const activeJobs = useProcessingJobsInspectionQuery(
    "active",
    ACTIVE_JOBS_LIMIT,
  );
  const liveProgress = useLiveProgress();
  const built = useMemo(() => {
    const all = libraries.data ?? [];
    const nextLooks = new Map<number, { at: number; interval: number }>();
    for (const library of all) {
      const at = parseAppTime(library.next_look_at);
      if (at != null) {
        nextLooks.set(library.id, {
          at,
          interval: library.scan_interval_seconds,
        });
      }
    }
    const allFiles = boardFiles(
      files.data?.files ?? [],
      workingFiles.data?.files ?? [],
      liveProgress,
    );
    const jobs = activeJobs.data?.jobs ?? [];
    const names = new Map(all.map((l) => [l.id, l.name]));
    const readyAfter = new Map(all.map((l) => [l.id, l.ready_after_seconds]));
    const build = (inFiles: typeof allFiles, inJobs: typeof jobs) =>
      buildLanes(inFiles, inJobs, names, readyAfter, nextLooks);
    const everything = build(allFiles, jobs);
    if (workflowId === null) return { lanes: everything, scoped: everything };
    const scoped = build(
      allFiles.filter((file) => file.library_id === workflowId),
      jobs.filter((job) => libraryCleanWorkflowId(job) === workflowId),
    );
    return { lanes: everything, scoped };
  }, [
    files.data,
    workingFiles.data,
    activeJobs.data,
    libraries.data,
    liveProgress,
    workflowId,
  ]);
  const queuedMore = queuedBeyondPage(
    scopedFiles.data?.status_counts ?? {},
    built.scoped,
  );
  return {
    files,
    libraries,
    lanes: built.lanes,
    scoped: built.scoped,
    queuedMore,
  };
}
