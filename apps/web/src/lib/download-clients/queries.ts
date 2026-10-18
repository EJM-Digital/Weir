import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
  createDownloadClientConnection,
  deleteDownloadClientConnection,
  fetchDownloadClientConnections,
  fetchDownloadClientSuggestions,
  testDownloadClientConnection,
  updateDownloadClientConnection,
  type DownloadClientConnection,
  type DownloadClientConnectionCreate,
  type DownloadClientConnectionUpdate,
  type DownloadClientSuggestion,
} from "./download-clients-api";

import { downloadClientKeys } from "./query-keys";

export function useDownloadClientConnectionsQuery(enabled = true) {
  return useQuery<DownloadClientConnection[]>({
    queryKey: downloadClientKeys.connections,
    queryFn: fetchDownloadClientConnections,
    enabled,
  });
}

/** One-click watched-folder suggestions from every enabled bare download client, for the library editor. */
export function useDownloadClientSuggestionsQuery(mediaType: "movie" | "tv") {
  return useQuery<DownloadClientSuggestion[]>({
    queryKey: downloadClientKeys.suggestions(mediaType),
    queryFn: () => fetchDownloadClientSuggestions(mediaType),
  });
}

export function useCreateDownloadClientConnection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: DownloadClientConnectionCreate) =>
      createDownloadClientConnection(data),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: downloadClientKeys.connections }),
  });
}

export function useUpdateDownloadClientConnection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { id: number; data: DownloadClientConnectionUpdate }) =>
      updateDownloadClientConnection(vars.id, vars.data),
    // Settles once the list is read again, so the card never goes back to its old word while the new one is on its way.
    onSuccess: () =>
      qc.invalidateQueries({ queryKey: downloadClientKeys.connections }),
  });
}

export function useDeleteDownloadClientConnection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => deleteDownloadClientConnection(id),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: downloadClientKeys.connections }),
  });
}

export function useTestDownloadClientConnection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => testDownloadClientConnection(id),
    // The result is stored on the row, so the list is stale until it is read again; this settles once it has been.
    onSuccess: () =>
      qc.invalidateQueries({ queryKey: downloadClientKeys.connections }),
  });
}
