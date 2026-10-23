/**
 * For one media manager connection: whether each workflow it feeds is fully chained end to end (Weir's own folders
 * plus this connection's own setup), compact enough to sit under `FedWorkflows` without pushing the card around.
 * The lines behind each answer are shown once, in the workflow editor's Folder chain, which each row opens.
 */

import { Link } from "react-router-dom";

import {
  READINESS_LABELS,
  READINESS_MEANING,
  readinessOf,
  type LibraryFolderChain,
} from "../../../../lib/processing/library-folder-chain-api";
import {
  useConnectionFolderChainQuery,
  useProcessingLibrariesQuery,
} from "../../../../lib/processing/libraries-queries";
import { workflowEditorPath } from "../../../../lib/settings/setup-areas";

function libraryName(
  libraries: { id: number; name: string }[] | undefined,
  libraryId: number,
): string {
  return (
    libraries?.find((library) => library.id === libraryId)?.name ??
    `Workflow ${libraryId}`
  );
}

function chainLines(chain: LibraryFolderChain) {
  return [
    ...chain.local.lines,
    ...chain.managers.flatMap((manager) => manager.lines),
    ...chain.download_clients.flatMap((client) => client.lines),
  ];
}

export function ConnectionFolderChain({
  connectionId,
}: {
  connectionId: number;
}) {
  const chain = useConnectionFolderChainQuery(connectionId);
  const libraries = useProcessingLibrariesQuery();

  if (chain.isLoading) {
    return (
      <p className="mm-conn-row__problem">
        Checking its workflows&apos; folder chains…
      </p>
    );
  }

  if (chain.isError) {
    // Quiet failure, like FedWorkflows above it: this is an auxiliary, always-on background check inside a card
    // full of other alerts, not a user-initiated action, so it does not compete for the page's one role="alert".
    return (
      <p className="mm-conn-row__problem mm-status-text" data-status="broken">
        Weir could not check its workflows&apos; folder chains just now.
      </p>
    );
  }

  if (!chain.data?.length) {
    return null;
  }
  const entries = chain.data;

  return (
    <div className="mm-conn-chain" data-testid="media-manager-folder-chain">
      {entries.map((entry) => {
        const readiness = readinessOf(entry.ready, chainLines(entry));
        return (
          <p key={entry.library_id} className="mm-conn-chain__row">
            <span className="mm-conn-row__when">
              {libraryName(libraries.data, entry.library_id)}
            </span>
            <span
              className="mm-status-text"
              data-status={READINESS_MEANING[readiness]}
            >
              {READINESS_LABELS[readiness]}
            </span>
            <Link
              className="mm-quiet-link"
              to={workflowEditorPath(entry.library_id)}
            >
              See its folder chain
            </Link>
          </p>
        );
      })}
    </div>
  );
}
