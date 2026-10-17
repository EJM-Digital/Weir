import { useRef, useState } from "react";

import { EmptyState } from "../../../../components/shared/empty-state";
import { PageLoading } from "../../../../components/shared/page-loading";
import { SidePanel } from "../../../../components/shared/side-panel";
import { AdminOnlyNote } from "../../../../components/shared/admin-only-note";
import { useIsAdmin } from "../../../../lib/auth/can-edit";
import { PageToolbarAddButton } from "../../../../components/shell/page-toolbar-actions";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import { useMediaManagerConnectionsQuery } from "../../../../lib/media-managers/queries";
import { useAppDateFormatter } from "../../../../lib/ui/mm-format-date";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { plural } from "../../../../lib/ui/mm-plural";
import { SettingsLoadError } from "../../settings-load-error";
import { AddConnectionFields } from "./add-connection-form";
import { ConnectionCard } from "./connection-card";
import { ConnectionList } from "./connection-row";
import { UnsignedWebhookBanner } from "./connection-status";
import { NewConnectionSecretPrompt } from "./new-connection-secret-prompt";
import { WeirOnlyWorkflows } from "./weir-only-workflows";

/** Setup › Connections › Media managers: the media managers that send files to Weir. */
export function MediaManagersTab() {
  const connections = useMediaManagerConnectionsQuery();
  const admin = useIsAdmin();
  const fmt = useAppDateFormatter();
  const [adding, setAdding] = useState(false);
  const [justCreated, setJustCreated] = useState<MediaManagerConnection | null>(
    null,
  );
  const kindRef = useRef<HTMLSelectElement>(null);

  if (connections.isPending)
    return <PageLoading label="Loading media managers" />;
  if (connections.isError) return <SettingsLoadError what="media managers" />;

  const startAdding = () => {
    setJustCreated(null);
    setAdding(true);
  };

  return (
    <div className="mm-quiet-stack" data-testid="suite-settings-media-managers">
      <AdminOnlyNote
        what="a media manager"
        testId="media-manager-needs-admin"
      />
      {justCreated ? (
        <NewConnectionSecretPrompt
          connection={justCreated}
          onDismiss={() => setJustCreated(null)}
        />
      ) : null}

      {connections.data.length === 0 ? (
        <EmptyState
          title="No media manager connected"
          testId="media-managers-empty"
          action={
            <button
              type="button"
              className={mmActionButtonClass({ variant: "secondary" })}
              disabled={!admin}
              onClick={startAdding}
            >
              Add your first media manager
            </button>
          }
        >
          Nothing is connected yet, so no files are reaching Weir. Radarr,
          Sonarr and Deluno tell Weir when a download is finished and take the
          cleaned file back.
        </EmptyState>
      ) : (
        <ConnectionList
          title="Connected"
          count={`${plural(connections.data.length, "media manager", "media managers")}. Weir checks each one every minute.`}
          banner={<UnsignedWebhookBanner connections={connections.data} />}
        >
          {connections.data.map((connection) => (
            <ConnectionCard
              key={connection.id}
              connection={connection}
              fmt={fmt}
            />
          ))}
        </ConnectionList>
      )}

      <WeirOnlyWorkflows />

      <PageToolbarAddButton
        label="Add media manager"
        dataTestId="media-manager-add"
        disabled={!admin}
        onClick={startAdding}
      />
      <SidePanel
        open={adding}
        title="Add a media manager"
        eyebrow="Setup · Connections"
        initialFocus={kindRef}
        onClose={() => setAdding(false)}
        dataTestId="media-manager-add-panel"
      >
        <AddConnectionFields
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
