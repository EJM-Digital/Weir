import { useRef, useState } from "react";

import { EmptyState } from "../../../../components/shared/empty-state";
import { PageLoading } from "../../../../components/shared/page-loading";
import { SidePanel } from "../../../../components/shared/side-panel";
import { AdminOnlyNote } from "../../../../components/shared/admin-only-note";
import { useIsAdmin } from "../../../../lib/auth/can-edit";
import { PageToolbarAddButton } from "../../../../components/shell/page-toolbar-actions";
import type { DownloadClientConnection } from "../../../../lib/download-clients/download-clients-api";
import { useDownloadClientConnectionsQuery } from "../../../../lib/download-clients/queries";
import { connectionTitle } from "../../../../lib/ui/connection-title";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { useAppDateFormatter } from "../../../../lib/ui/mm-format-date";
import { plural } from "../../../../lib/ui/mm-plural";
import { SettingsLoadError } from "../../settings-load-error";
import { AddDownloadClientFields } from "./add-download-client-form";
import { DownloadClientCard } from "./download-client-card";
import { ConnectionList } from "./connection-row";

/**
 * Bare download clients (SABnzbd, NZBGet, qBittorrent, Deluge, Transmission) Weir can read a
 * watched-folder suggestion from. Composes the list and the add drawer.
 */
export function DownloadClientsSection() {
  const connections = useDownloadClientConnectionsQuery();
  const admin = useIsAdmin();
  const fmt = useAppDateFormatter();
  const [adding, setAdding] = useState(false);
  const [justCreated, setJustCreated] =
    useState<DownloadClientConnection | null>(null);
  const kindRef = useRef<HTMLSelectElement>(null);

  if (connections.isPending)
    return <PageLoading label="Loading download clients" />;
  if (connections.isError) return <SettingsLoadError what="download clients" />;

  const startAdding = () => {
    setJustCreated(null);
    setAdding(true);
  };

  return (
    <div
      className="mm-quiet-stack"
      data-testid="suite-settings-download-clients"
    >
      <AdminOnlyNote
        what="a download client"
        testId="download-client-needs-admin"
      />
      {justCreated ? (
        <p className="mm-quiet-note" data-testid="download-client-created-note">
          {connectionTitle(justCreated)} is connected. Weir will offer its
          folder as a suggestion in the workflow editor — nothing is applied on
          its own.
        </p>
      ) : null}

      {connections.data.length === 0 ? (
        <EmptyState
          title="No download client connected"
          testId="download-clients-empty"
          action={
            <button
              type="button"
              className={mmActionButtonClass({ variant: "secondary" })}
              disabled={!admin}
              onClick={startAdding}
            >
              Add your first download client
            </button>
          }
        >
          No download client is connected. Weir can still suggest watched
          folders from Sonarr, Radarr or Deluno; add one only if you run a bare
          download client Weir should read instead. A folder you type yourself
          always wins.
        </EmptyState>
      ) : (
        <ConnectionList
          title="Connected"
          count={`${plural(connections.data.length, "download client", "download clients")}. Weir only reads their settings, and never changes them.`}
        >
          {connections.data.map((connection) => (
            <DownloadClientCard
              key={connection.id}
              connection={connection}
              fmt={fmt}
            />
          ))}
        </ConnectionList>
      )}

      <PageToolbarAddButton
        label="Add download client"
        dataTestId="download-client-add"
        disabled={!admin}
        onClick={startAdding}
      />
      <SidePanel
        open={adding}
        title="Add a download client"
        eyebrow="Setup · Connections"
        initialFocus={kindRef}
        onClose={() => setAdding(false)}
        dataTestId="download-client-add-panel"
      >
        <AddDownloadClientFields
          kindRef={kindRef}
          onCancel={() => setAdding(false)}
          onCreated={(connection) => {
            setAdding(false);
            setJustCreated(connection);
          }}
        />
      </SidePanel>
    </div>
  );
}
