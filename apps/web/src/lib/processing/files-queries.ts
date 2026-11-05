import {
  keepPreviousData,
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";

import { activityKeys } from "../activity/query-keys";
import { useActivityStreamInvalidations } from "../activity/use-activity-stream-invalidation";
import { postProcessingFileRemuxPassEnqueue } from "./file-remux-pass-api";
import {
  fetchProcessingFiles,
  fetchProcessingFileRemoveOptions,
  forgetProcessingFile,
  fetchProcessingFileLog,
  fetchProcessingFileTracks,
  fetchProcessingWhyHeld,
  moveProcessingFileToTop,
  postProcessingManualPlan,
  requeueProcessingFile,
  requeueProcessingFiles,
  type ProcessingBulkRequeueQuery,
  type ProcessingFileRemovalConfirm,
  type ProcessingFileRemovalResolution,
  type ProcessingFilesPage,
  type ProcessingFilesQuery,
  type ProcessingManualPlanChoice,
} from "./files-api";
import {
  fetchLibraryCleans,
  type LibraryCleansQuery,
} from "./library-cleans-api";
import { postProcessingWatchedFolderRemuxScanDispatchEnqueue } from "./watched-folder-scan-api";
import { processingKeys } from "./query-keys";

export function useProcessingFilesQuery(query: ProcessingFilesQuery = {}) {
  return useQuery<ProcessingFilesPage>({
    queryKey: processingKeys.fileList(query),
    queryFn: () => fetchProcessingFiles(query),
    // Narrowing to another workflow keeps the page that is on screen until the new one arrives.
    placeholderData: keepPreviousData,
  });
}

/**
 * A file's Activity row changes when its status does, and a thousand-row read is dear, so the list follows the stream
 * gently. A running pass's percent does not wait for it: the page lays the stream's live progress over the rows.
 */
const ACTIVITY_THROTTLE_MS = 2_000;
const HISTORY_KEYS = [processingKeys.files] as const;
const CLEAN_KEYS = [processingKeys.libraryCleanLists] as const;

/**
 * Activity's downloads. The list on screen stays while a new filter loads, and it is read again each time the stream says
 * a file changed (#914).
 */
export function useFileHistoryQuery(query: ProcessingFilesQuery) {
  useActivityStreamInvalidations(HISTORY_KEYS, {
    throttleMs: ACTIVITY_THROTTLE_MS,
  });
  return useQuery<ProcessingFilesPage>({
    queryKey: processingKeys.fileList(query),
    queryFn: () => fetchProcessingFiles(query),
    placeholderData: keepPreviousData,
  });
}

/** Activity's library cleans (#695), kept on screen while a new filter loads, and read again as the stream says one changed. */
export function useLibraryCleansQuery(query: LibraryCleansQuery) {
  useActivityStreamInvalidations(CLEAN_KEYS, {
    throttleMs: ACTIVITY_THROTTLE_MS,
  });
  return useQuery({
    queryKey: processingKeys.libraryCleans(query),
    queryFn: () => fetchLibraryCleans(query),
    placeholderData: keepPreviousData,
  });
}

/**
 * Forgetting a file leaves its Activity events and job rows alone on the server — System keeps its own record of
 * them, and a job's dedupe key keeps refusing a second pass for the same file — but the file no longer counts
 * toward the live Processing alert or "Just finished" list, which read those same rows filtered to files Weir
 * still knows about. Invalidating them here is what makes that drop show up without a reload. The overview's
 * lifetime totals (processed, failed, space saved) read the same rows unfiltered and do not change, so they are
 * not invalidated here.
 */
export function useForgetProcessingFile() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      id,
      resolution,
      confirm,
    }: {
      id: number;
      resolution?: ProcessingFileRemovalResolution;
      confirm?: ProcessingFileRemovalConfirm;
    }) => forgetProcessingFile(id, resolution, confirm),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: processingKeys.files });
      void qc.invalidateQueries({ queryKey: activityKeys.recent });
      void qc.invalidateQueries({ queryKey: processingKeys.jobsInspection });
    },
  });
}

/**
 * What Activity's remove dialog should offer for one title (#785), asked only when someone opens the dialog for
 * it — not for every row on every render.
 */
export function useProcessingFileRemoveOptions() {
  return useMutation({
    mutationFn: (id: number) => fetchProcessingFileRemoveOptions(id),
  });
}

export function useMoveProcessingFileToTop() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => moveProcessingFileToTop(id),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: processingKeys.files }),
  });
}

export function useRequeueProcessingFile() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => requeueProcessingFile(id),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: processingKeys.files }),
  });
}

export function useRequeueProcessingFiles() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (query: ProcessingBulkRequeueQuery) =>
      requeueProcessingFiles(query),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: processingKeys.files }),
  });
}

export function useProcessingWhyHeld() {
  // A mutation rather than a query: this asks the managers *right now*, and only when
  // someone asks. Running it on render would poll every connection for every file.
  return useMutation({
    mutationFn: (id: number) => fetchProcessingWhyHeld(id),
  });
}

/** One file's processing record, read again whenever the file itself changes. */
export function useProcessingFileLogQuery(fileId: number, updatedAt: string) {
  return useQuery({
    queryKey: processingKeys.fileLog(fileId, updatedAt),
    queryFn: () => fetchProcessingFileLog(fileId),
  });
}

export function useProcessingFileLog() {
  // A mutation rather than a query: a processing record is read when someone opens it,
  // not for every row on every render.
  return useMutation({
    mutationFn: (id: number) => fetchProcessingFileLog(id),
  });
}

export function useProcessingFileTracks() {
  // A mutation rather than a query: the tracks come from a fresh ffprobe of the source, done
  // only when an operator opens "Choose tracks" for one file, not for every row on every render.
  return useMutation({
    mutationFn: (id: number) => fetchProcessingFileTracks(id),
  });
}

export function useSubmitProcessingManualPlan() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      id,
      choice,
    }: {
      id: number;
      choice: ProcessingManualPlanChoice;
    }) => postProcessingManualPlan(id, choice),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: processingKeys.files }),
  });
}

export function useProcessProcessingFileNow() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      relative_media_path,
      media_scope,
      library_id,
      pass_through_unchanged,
    }: {
      relative_media_path: string;
      media_scope: "movie" | "tv";
      library_id?: number;
      pass_through_unchanged?: boolean;
    }) =>
      postProcessingFileRemuxPassEnqueue({
        relative_media_path,
        media_scope,
        library_id,
        pass_through_unchanged,
      }),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: processingKeys.files }),
  });
}

export function useProcessingCheckLibraryAgain() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      media_scope,
      library_id,
    }: {
      media_scope: "movie" | "tv";
      library_id: number;
    }) =>
      postProcessingWatchedFolderRemuxScanDispatchEnqueue({
        enqueue_remux_jobs: true,
        media_scope,
        library_id,
      }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: processingKeys.files });
      void qc.invalidateQueries({ queryKey: processingKeys.jobs });
    },
  });
}
