import {
  useMutation,
  useQuery,
  useQueryClient,
  type QueryClient,
} from "@tanstack/react-query";

import {
  createProcessingLibrary,
  deleteProcessingLibrary,
  fetchProcessingLibraries,
  reorderProcessingLibraries,
  updateProcessingLibrary,
  type ProcessingLibrary,
  type ProcessingLibraryCreate,
  type ProcessingMediaType,
  type ProcessingLibraryWrite,
} from "./libraries-api";
import {
  discoverProcessingLibraries,
  fetchProcessingLibraryDrift,
  fetchProcessingManagerSetup,
  fetchProcessingRejectSupport,
  importDiscoveredProcessingLibraries,
  unlinkDiscoveredProcessingLibrary,
} from "./library-managers-api";
import {
  fetchConnectionFolderChain,
  fetchLibraryFolderChain,
} from "./library-folder-chain-api";
import {
  createProcessingRuleSet,
  deleteProcessingRuleSet,
  fetchProcessingRuleSets,
  updateProcessingRuleSet,
  type ProcessingRuleSet,
  type ProcessingRuleSetWrite,
} from "./rule-sets-api";
import { previewProcessingRules } from "./rules-preview-api";
import { processingKeys } from "./query-keys";

/** Asks the linked managers what they can do, so only while the option is on screen. */
export function useProcessingRejectSupportQuery(
  connectionIds: number[],
  enabled: boolean,
) {
  return useQuery({
    queryKey: processingKeys.rejectSupport(connectionIds),
    queryFn: () => fetchProcessingRejectSupport(connectionIds),
    enabled,
    staleTime: 60_000,
  });
}

/**
 * Asks each connected media manager whether it will pick up what a library with these folders writes.
 * Only while the library editor is open, and keyed on the folders so a change is checked again.
 */
export function useProcessingManagerSetupQuery(
  mediaType: ProcessingMediaType,
  watchedFolder: string,
  outputFolder: string,
  removeOriginal: boolean,
  linkedConnectionIds: number[],
  enabled: boolean,
) {
  return useQuery({
    queryKey: processingKeys.managerSetup(
      mediaType,
      watchedFolder,
      outputFolder,
      removeOriginal,
      linkedConnectionIds,
    ),
    queryFn: () =>
      fetchProcessingManagerSetup(
        mediaType,
        watchedFolder,
        outputFolder,
        removeOriginal,
        linkedConnectionIds,
      ),
    enabled,
    staleTime: 30_000,
    retry: false,
  });
}

/**
 * The query for one saved library's folder chain: Weir's own watched/work/output folders plus every connected
 * manager's setup, in one read-only view. Keyed on the folders and media type too (not only the id) so an edit
 * made in the library editor, once it settles, is checked again — including right after Save, which changes the
 * saved folders under the same id. Anything that reads a chain with these options shares the editor's cache.
 */
export function libraryFolderChainOptions(
  libraryId: number,
  watchedFolder: string,
  workFolder: string,
  outputFolder: string,
  mediaType: ProcessingMediaType,
) {
  return {
    queryKey: [
      ...processingKeys.libraryFolderChains,
      libraryId,
      mediaType,
      watchedFolder,
      workFolder,
      outputFolder,
    ] as const,
    queryFn: () => fetchLibraryFolderChain(libraryId),
    staleTime: 30_000,
    retry: false,
  };
}

/** The folder chain for one saved library, once it has an id: a new library has nothing to check yet. */
export function useLibraryFolderChainQuery(
  libraryId: number | undefined,
  watchedFolder: string,
  workFolder: string,
  outputFolder: string,
  mediaType: ProcessingMediaType,
) {
  return useQuery({
    ...libraryFolderChainOptions(
      libraryId as number,
      watchedFolder,
      workFolder,
      outputFolder,
      mediaType,
    ),
    enabled: libraryId !== undefined,
  });
}

/** The same folder chain for every library linked to one media manager connection (Setup › Connections › Media managers). */
export function useConnectionFolderChainQuery(connectionId: number) {
  return useQuery({
    queryKey: ["processing", "connection-folder-chain", connectionId] as const,
    queryFn: () => fetchConnectionFolderChain(connectionId),
    staleTime: 30_000,
    retry: false,
  });
}

/**
 * Reads the workflows again after a change to them. A folder chain is a check of the folders and links saved, so
 * a change that can alter those makes every chain stale, and each is checked again the next time it is shown.
 */
function invalidateWorkflowsAndChains(qc: QueryClient) {
  void qc.invalidateQueries({ queryKey: processingKeys.libraries });
  void qc.invalidateQueries({ queryKey: processingKeys.libraryFolderChains });
}

/** The workflows, each with its next look; the server says when either changes (`libraries`). */
export function useProcessingLibrariesQuery(enabled = true) {
  return useQuery<ProcessingLibrary[]>({
    queryKey: processingKeys.libraries,
    queryFn: fetchProcessingLibraries,
    enabled,
  });
}

export function useCreateProcessingLibrary() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: ProcessingLibraryCreate) =>
      createProcessingLibrary(data),
    onSuccess: () => invalidateWorkflowsAndChains(qc),
  });
}

export function useUpdateProcessingLibrary() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { id: number; data: ProcessingLibraryWrite }) =>
      updateProcessingLibrary(vars.id, vars.data),
    onSuccess: () => invalidateWorkflowsAndChains(qc),
  });
}

export function useDeleteProcessingLibrary() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => deleteProcessingLibrary(id),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: processingKeys.libraries }),
  });
}

export function useReorderProcessingLibraries() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (ids: number[]) => reorderProcessingLibraries(ids),
    // Settles once the list is read again, so a list held in its new order is not shown in the old one first.
    onSuccess: () =>
      qc.invalidateQueries({ queryKey: processingKeys.libraries }),
  });
}

export function useDiscoverProcessingLibraries() {
  return useMutation({
    mutationFn: (connectionId: number) =>
      discoverProcessingLibraries(connectionId),
  });
}

export function useImportDiscoveredProcessingLibraries() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { connectionId: number; keys: string[] }) =>
      importDiscoveredProcessingLibraries(vars.connectionId, vars.keys),
    onSuccess: () => invalidateWorkflowsAndChains(qc),
  });
}

export function useProcessingLibraryDrift() {
  return useMutation({
    mutationFn: (connectionId: number) =>
      fetchProcessingLibraryDrift(connectionId),
  });
}

export function useUnlinkDiscoveredProcessingLibrary() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => unlinkDiscoveredProcessingLibrary(id),
    onSuccess: () => invalidateWorkflowsAndChains(qc),
  });
}

export function useProcessingRuleSetsQuery() {
  return useQuery<ProcessingRuleSet[]>({
    queryKey: processingKeys.ruleSets,
    queryFn: fetchProcessingRuleSets,
  });
}

export function useCreateProcessingRuleSet() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: ProcessingRuleSetWrite) => createProcessingRuleSet(data),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: processingKeys.ruleSets }),
  });
}

export function useUpdateProcessingRuleSet() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { id: number; data: ProcessingRuleSetWrite }) =>
      updateProcessingRuleSet(vars.id, vars.data),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: processingKeys.ruleSets }),
  });
}

export function useDeleteProcessingRuleSet() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => deleteProcessingRuleSet(id),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: processingKeys.ruleSets });
      void qc.invalidateQueries({ queryKey: processingKeys.libraries });
    },
  });
}

/** "Try on a file" (#502). No cache: every call is a fresh, read-only probe. */
export function useProcessingRulesPreview() {
  return useMutation({ mutationFn: previewProcessingRules });
}
