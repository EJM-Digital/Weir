import {
  useActivityStreamInvalidations,
  useLiveProgress,
} from "../../lib/activity/use-activity-stream-invalidation";
import { useProcessingFilesQuery } from "../../lib/processing/files-queries";
import { useProcessingJobsInspectionQuery } from "../../lib/processing/jobs-inspection/queries";
import { processingKeys } from "../../lib/processing/query-keys";
import {
  WORKING_FILE_STATUS,
  boardFiles,
  countWorking,
} from "./processing-model";

/** The general page of files the Pipeline is built from. */
export const FILES_QUERY = { limit: 200 } as const;

// The Working lane's own fetch, separate from the general page: every currently-processing file, with no
// paging risk of losing one that is still running (#781). 1000 is the endpoint's own ceiling, not a real
// expectation — files-at-once tops out far below it.
export const WORKING_FILES_QUERY = {
  file_status: WORKING_FILE_STATUS,
  limit: 1000,
} as const;
export const ACTIVE_JOBS_LIMIT = 50;

const WORKING_KEYS = [
  processingKeys.fileList(FILES_QUERY),
  processingKeys.fileList(WORKING_FILES_QUERY),
  processingKeys.jobsInspectionList("active", ACTIVE_JOBS_LIMIT),
] as const;
/** A running pass rewrites its progress several times a second and each write reaches the stream; a count needs no more than this. */
const STREAM_THROTTLE_MS = 750;

/**
 * How many files the Working lane is showing, for the sidebar. It reads the board's own lists, lays the live
 * progress over them and counts them the way the lane does, so the two can never disagree; the server's count of jobs holding a
 * slot covers every kind of job and every file state, which the lane does not show.
 * The lists share the page's cache, so on Processing this costs no extra request. They are read again whenever the
 * stream says a file or a job changed, on whichever page is open.
 */
export function useWorkingCount(): number {
  const files = useProcessingFilesQuery(FILES_QUERY);
  const workingFiles = useProcessingFilesQuery(WORKING_FILES_QUERY);
  const jobs = useProcessingJobsInspectionQuery("active", ACTIVE_JOBS_LIMIT);
  const liveProgress = useLiveProgress();
  useActivityStreamInvalidations(WORKING_KEYS, {
    throttleMs: STREAM_THROTTLE_MS,
  });

  return countWorking(
    boardFiles(
      files.data?.files ?? [],
      workingFiles.data?.files ?? [],
      liveProgress,
    ),
    jobs.data?.jobs ?? [],
  );
}
