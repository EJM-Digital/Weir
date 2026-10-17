import {
  keepPreviousData,
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";

import {
  cleanLibraryFiles,
  fetchLibraryFiles,
  fetchLibraryOverview,
  fetchLibrarySettings,
  saveLibrarySettings,
  setLibraryFileLeaveAlone,
  setLibrarySchedule,
  triggerLibraryScan,
  type LibraryFileFilters,
  type LibraryManualPlan,
  type LibrarySettingsUpdate,
} from "./library-mode-api";
import {
  fetchLibraryRedownloads,
  requestLibraryRedownload,
} from "./library-redownload-api";
import { previewProcessingRules } from "./rules-preview-api";
import { processingKeys } from "./query-keys";

export function useLibrarySettingsQuery(libraryId: number, enabled = true) {
  return useQuery({
    queryKey: processingKeys.librarySettings(libraryId),
    queryFn: () => fetchLibrarySettings(libraryId),
    enabled: enabled && libraryId > 0,
  });
}

/** Everything the Library view reads about one library, invalidated together after a scan, a clean or a change of setup. */
function invalidateLibraryViews(
  qc: ReturnType<typeof useQueryClient>,
  libraryId: number,
) {
  for (const key of [
    processingKeys.libraryFiles(libraryId),
    processingKeys.libraryOverview(libraryId),
  ]) {
    void qc.invalidateQueries({ queryKey: key });
  }
}

/**
 * Saves the library's setup (see LibrarySettingsUpdate). Its checks decide whether seeding counts as a problem at
 * all (#568), and a new rules profile changes what every file would do, so the views are read again.
 */
export function useSaveLibrarySettings(libraryId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (updates: LibrarySettingsUpdate) =>
      saveLibrarySettings(libraryId, updates),
    onSuccess: () => {
      void qc.invalidateQueries({
        queryKey: processingKeys.librarySettings(libraryId),
      });
      invalidateLibraryViews(qc, libraryId);
    },
  });
}

export function useTriggerLibraryScan(libraryId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => triggerLibraryScan(libraryId),
    onSuccess: () => invalidateLibraryViews(qc, libraryId),
  });
}

export function useLibraryFilesQuery(
  libraryId: number,
  filters: LibraryFileFilters,
  enabled = true,
) {
  return useQuery({
    queryKey: processingKeys.libraryFileList(libraryId, filters),
    queryFn: () => fetchLibraryFiles(libraryId, filters),
    enabled: enabled && libraryId > 0,
    // Changing a sort, a filter or the page makes a new query key. Without this the table would blank out
    // and fall back to its empty state for a moment on every one of those, which reads as "no files".
    placeholderData: keepPreviousData,
  });
}

/**
 * #568's Overview. While a scan is queued or running the totals and breakdowns are still changing: the server says so
 * on `library_scan` when the scan is queued, as its files are indexed and when it ends, and each of those reads this again.
 */
export function useLibraryOverviewQuery(libraryId: number, enabled = true) {
  return useQuery({
    queryKey: processingKeys.libraryOverview(libraryId),
    queryFn: () => fetchLibraryOverview(libraryId),
    enabled: enabled && libraryId > 0,
  });
}

/**
 * What the library's rules would do to one file. A preview reads the real file, so it is asked for once
 * per opened file and never on a timer.
 */
export function useLibraryFilePreviewQuery(
  libraryId: number,
  path: string | null,
  /** The profile the library cleans by when it has chosen its own; null means its workflow's. */
  ruleSetId: number | null,
  enabled: boolean,
) {
  return useQuery({
    queryKey: processingKeys.libraryFilePreview(
      libraryId,
      path ?? "",
      ruleSetId,
    ),
    queryFn: () =>
      previewProcessingRules({
        libraryId,
        absolutePath: path ?? "",
        ruleSetId: ruleSetId ?? undefined,
      }),
    enabled: enabled && path !== null,
    staleTime: 5 * 60 * 1000,
    retry: false,
  });
}

export function useCleanLibraryFiles(libraryId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      paths,
      confirm,
      manual,
    }: {
      paths: string[];
      confirm: boolean;
      /** Your own choice of tracks, for one file; without it the library's rules decide. */
      manual?: LibraryManualPlan;
    }) => cleanLibraryFiles(libraryId, paths, confirm, manual),
    // Asking what a clean would remove changes nothing, so the views have nothing to catch up on.
    onSuccess: (result) => {
      if (result.kind === "cleaned") invalidateLibraryViews(qc, libraryId);
    },
  });
}

/** "Leave this file alone", and its undo. */
export function useSetLibraryFileLeaveAlone(libraryId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ path, leaveAlone }: { path: string; leaveAlone: boolean }) =>
      setLibraryFileLeaveAlone(libraryId, path, leaveAlone),
    onSuccess: () => invalidateLibraryViews(qc, libraryId),
  });
}

/**
 * The files a past clean left missing a track the rules now keep. Read only while a file panel is open, since
 * that is the one place "Download again" is offered.
 */
export function useLibraryRedownloadsQuery(libraryId: number) {
  return useQuery({
    queryKey: processingKeys.libraryRedownloads(libraryId),
    queryFn: () => fetchLibraryRedownloads(libraryId),
    enabled: libraryId > 0,
  });
}

export function useRequestLibraryRedownload(libraryId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (path: string) => requestLibraryRedownload(libraryId, path),
    onSuccess: () => {
      void qc.invalidateQueries({
        queryKey: processingKeys.libraryRedownloads(libraryId),
      });
      invalidateLibraryViews(qc, libraryId);
    },
  });
}

export function useSetLibrarySchedule(libraryId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({
      enabled,
      confirm,
    }: {
      enabled: boolean;
      confirm: boolean;
    }) => setLibrarySchedule(libraryId, enabled, confirm),
    onSuccess: (result) => {
      if (!("kind" in result)) {
        void qc.invalidateQueries({
          queryKey: processingKeys.librarySettings(libraryId),
        });
        invalidateLibraryViews(qc, libraryId);
      }
    },
  });
}
