import { useQueries, useQueryClient } from "@tanstack/react-query";

import type { DownloadClientConnection } from "../../../lib/download-clients/download-clients-api";
import { useDownloadClientConnectionsQuery } from "../../../lib/download-clients/queries";
import type { MediaManagerConnection } from "../../../lib/media-managers/media-managers-api";
import { useMediaManagerConnectionsQuery } from "../../../lib/media-managers/queries";
import type { ProcessingLibrary } from "../../../lib/processing/libraries-api";
import {
  heldFolderChain,
  libraryFolderChainOptions,
} from "../../../lib/processing/libraries-queries";
import type { LibraryFolderChain } from "../../../lib/processing/library-folder-chain-api";
import { useMediaToolsQuery } from "../../../lib/system/media-tools";
import { useServerLooks } from "../../../lib/system/use-server-looks";
import {
  checkVerdict,
  newestTime,
  toolRows,
  whyNotInSync,
  type ToolRow,
  type WorkflowVerdict,
} from "./health-model";

const NO_MANAGERS: MediaManagerConnection[] = [];
const NO_CLIENTS: DownloadClientConnection[] = [];

export type WorkflowHealth = {
  workflow: ProcessingLibrary;
  verdict: WorkflowVerdict;
  /** One plain line on why the workflow is not in sync. Null when it is, or while the check has not answered. */
  why: string | null;
  /** The whole chain, for the views that list every line. */
  chain: LibraryFolderChain | undefined;
  /**
   * When the server last checked this chain, in ms since the epoch: the later of its own look (it checks on its own while a
   * browser watches) and the last time this page asked. Null before the first answer.
   */
  checkedAt: number | null;
  /** Reads this workflow's chain again. */
  recheck: () => Promise<unknown>;
};

export type Health = {
  workflows: WorkflowHealth[];
  /** When this page last read any workflow's chain, in ms since the epoch. Null before the first answer. */
  foldersReadAt: number | null;
  /** The media managers in scope, switched on or not. */
  managers: MediaManagerConnection[];
  /** The download clients in scope, switched on or not. */
  downloadClients: DownloadClientConnection[];
  tools: ToolRow[] | null;
  /** Asks every workflow in scope for its folder chain again. */
  recheckFolders: () => Promise<void>;
};

/** A workflow's own media managers, and the download clients its chain names. */
function inScope(
  workflowId: number | null | undefined,
  rows: WorkflowHealth[],
  managers: MediaManagerConnection[],
  downloadClients: DownloadClientConnection[],
) {
  if (workflowId == null) return { managers, downloadClients };
  const only = rows.find((row) => row.workflow.id === workflowId);
  const clientIds = new Set(
    only?.chain?.download_clients.map((client) => client.connection_id),
  );
  const managerIds = new Set(only?.workflow.manager_connection_ids);
  return {
    managers: managers.filter((manager) => managerIds.has(manager.id)),
    downloadClients: downloadClients.filter((client) =>
      clientIds.has(client.id),
    ),
  };
}

/**
 * Each enabled workflow's folder-chain verdict, the connections and the tools: every part loads on its own. The server checks
 * the folders on its own while a browser watches and says on `folder_checks` when an answer changes.
 * Given a workflow, everything narrows to it: its chain, the managers it is linked to and the download
 * clients its chain names. The tools belong to Weir, so they stay.
 */
export function useHealth(
  workflows: readonly ProcessingLibrary[],
  workflowId?: number | null,
): Health {
  const enabled = workflows.filter(
    (workflow) =>
      workflow.enabled && (workflowId == null || workflow.id === workflowId),
  );
  const client = useQueryClient();
  const chains = useQueries({
    queries: enabled.map((workflow) =>
      libraryFolderChainOptions(
        workflow.id,
        workflow.watched_folder.trim(),
        workflow.work_folder.trim(),
        workflow.output_folder.trim(),
        workflow.media_type,
      ),
    ),
  });
  const managers = useMediaManagerConnectionsQuery();
  const downloadClients = useDownloadClientConnectionsQuery();
  const tools = useMediaToolsQuery();
  const looks = useServerLooks();
  const readAts = chains.map((chain) =>
    chain.dataUpdatedAt > 0 ? chain.dataUpdatedAt : null,
  );
  const rows = enabled.map((workflow, index) => {
    const chain = chains[index];
    // Saving a workflow's folders changes the key its check is read under; the answer it had stays until the check
    // of the new folders answers, so its row does not go back to Checking.
    const answer =
      chain.data ??
      (chain.isError ? undefined : heldFolderChain(client, workflow.id));
    return {
      workflow,
      verdict: checkVerdict({ data: answer, isError: chain.isError }),
      why: answer ? whyNotInSync(answer) : null,
      chain: answer,
      checkedAt: answer
        ? newestTime(readAts[index], looks.foldersCheckedAt)
        : null,
      recheck: () => chain.refetch(),
    };
  });
  return {
    workflows: rows,
    foldersReadAt: newestTime(...readAts),
    ...inScope(
      workflowId,
      rows,
      managers.data ?? NO_MANAGERS,
      downloadClients.data ?? NO_CLIENTS,
    ),
    tools: tools.data ? toolRows(tools.data) : null,
    recheckFolders: async () => {
      await Promise.all(chains.map((chain) => chain.refetch()));
    },
  };
}
