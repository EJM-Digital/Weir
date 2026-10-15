import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useMemo } from "react";

import { useActivityStreamInvalidations } from "../../../lib/activity/use-activity-stream-invalidation";
import {
  fetchProcessingFiles,
  type ProcessingFile,
} from "../../../lib/processing/files-api";
import { processingKeys } from "../../../lib/processing/query-keys";
import { NEEDS_FILES_READ, NEEDS_FILE_STATUSES } from "./needs-model";

const NO_FILES: ProcessingFile[] = [];
/** A file failed, turned away or held changes only when Weir looks at it, so the list follows the stream gently. */
const STREAM_THROTTLE_MS = 3_000;

export type NeedsFiles = {
  files: ProcessingFile[];
  /** Why the latest read failed, or null when it worked. The files are then the last that were read, or none. */
  error: unknown;
};

/**
 * The files that may wait on a person: failed, rejected, held and skipped, newest first. Which of them do
 * is the model's to say. A read that failed is reported with them, since no files is not the same as none waiting.
 */
export function useNeedsFiles(
  workflowId: number | null | undefined,
): NeedsFiles {
  const { query, queryKeys } = useMemo(() => {
    const read = {
      file_status: [...NEEDS_FILE_STATUSES],
      library_id: workflowId ?? undefined,
      limit: NEEDS_FILES_READ,
    };
    return { query: read, queryKeys: [processingKeys.fileList(read)] as const };
  }, [workflowId]);
  const files = useQuery({
    queryKey: queryKeys[0],
    queryFn: () => fetchProcessingFiles(query),
    placeholderData: keepPreviousData,
  });
  useActivityStreamInvalidations(queryKeys, {
    exact: true,
    throttleMs: STREAM_THROTTLE_MS,
  });
  return { files: files.data?.files ?? NO_FILES, error: files.error };
}
