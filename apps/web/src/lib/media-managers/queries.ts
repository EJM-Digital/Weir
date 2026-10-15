import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
  createMediaManagerConnection,
  deleteMediaManagerConnection,
  fetchMediaManagerConnections,
  generateMediaManagerWebhookSecret,
  giveUpMediaManagerReports,
  testMediaManagerConnection,
  unlinkMediaManagerConnectionWorkflows,
  updateMediaManagerConnection,
  type ConnectionWorkflow,
  type MediaManagerConnection,
  type MediaManagerConnectionCreate,
  type MediaManagerConnectionUpdate,
} from "./media-managers-api";

import { errorMessage } from "../api/error-message";
import { deleteProcessingLibrary } from "../processing/libraries-api";
import { processingKeys } from "../processing/query-keys";
import { mediaManagerKeys } from "./query-keys";

export function useMediaManagerConnectionsQuery(enabled = true) {
  return useQuery<MediaManagerConnection[]>({
    queryKey: mediaManagerKeys.connections,
    queryFn: fetchMediaManagerConnections,
    enabled,
  });
}

export function useCreateMediaManagerConnection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (data: MediaManagerConnectionCreate) =>
      createMediaManagerConnection(data),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: mediaManagerKeys.connections }),
  });
}

export function useUpdateMediaManagerConnection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { id: number; data: MediaManagerConnectionUpdate }) =>
      updateMediaManagerConnection(vars.id, vars.data),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: mediaManagerKeys.connections }),
  });
}

export function useDeleteMediaManagerConnection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => deleteMediaManagerConnection(id),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: mediaManagerKeys.connections }),
  });
}

/** The first way to let go of the workflows that keep a connection from being removed: unlink them, leaving them as they are. */
export function useUnlinkMediaManagerWorkflows() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => unlinkMediaManagerConnectionWorkflows(id),
    onSettled: () =>
      Promise.all([
        qc.invalidateQueries({ queryKey: mediaManagerKeys.connections }),
        qc.invalidateQueries({ queryKey: processingKeys.libraries }),
      ]),
  });
}

/** The third way: stop owing the connection the reports that never reached it, so a manager that does not come back cannot hold it. */
export function useGiveUpMediaManagerReports() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => giveUpMediaManagerReports(id),
    onSettled: () =>
      qc.invalidateQueries({ queryKey: mediaManagerKeys.connections }),
  });
}

/**
 * The second way: remove the workflows themselves, one after another. It stops at the first one Weir will not remove (one with
 * work queued, say) and says why in that workflow's own words.
 */
export function useRemoveWorkflows() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (workflows: readonly ConnectionWorkflow[]) => {
      for (const workflow of workflows) {
        try {
          await deleteProcessingLibrary(workflow.id);
        } catch (error) {
          throw new Error(
            errorMessage(error, `Could not remove ${workflow.name}.`),
          );
        }
      }
    },
    onSettled: () =>
      Promise.all([
        qc.invalidateQueries({ queryKey: processingKeys.libraries }),
        qc.invalidateQueries({ queryKey: mediaManagerKeys.connections }),
      ]),
  });
}

export function useTestMediaManagerConnection() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => testMediaManagerConnection(id),
    // The test result is stored on the row, so the list is now stale.
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: mediaManagerKeys.connections }),
  });
}

export function useGenerateMediaManagerWebhookSecret() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => generateMediaManagerWebhookSecret(id),
    onSuccess: () =>
      void qc.invalidateQueries({ queryKey: mediaManagerKeys.connections }),
  });
}
