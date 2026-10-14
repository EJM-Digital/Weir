import { useState } from "react";

import { errorMessage } from "../../lib/api/error-message";
import type { DownloadClientConnection } from "../../lib/download-clients/download-clients-api";
import {
  useDeleteDownloadClientConnection,
  useTestDownloadClientConnection,
} from "../../lib/download-clients/queries";
import type { MediaManagerConnection } from "../../lib/media-managers/media-managers-api";
import {
  useDeleteMediaManagerConnection,
  useTestMediaManagerConnection,
} from "../../lib/media-managers/queries";
import { connectionTitle } from "../../lib/ui/connection-title";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { AddConnectionForm } from "../settings/tabs/media-managers/add-connection-form";
import { AddDownloadClientForm } from "../settings/tabs/media-managers/add-download-client-form";
import { RemoveConnectionDialog } from "../settings/tabs/media-managers/remove-connection-dialog";
import { RemoveDownloadClientDialog } from "../settings/tabs/media-managers/download-client-card";
import { NewConnectionSecretPrompt } from "../settings/tabs/media-managers/new-connection-secret-prompt";
import type { WizardConnections } from "./use-wizard-connections";
import { WizardConnectionRow } from "./wizard-connection-row";
import { MANAGER_KINDS_BY_SOURCE, type ConnectedSource } from "./wizard-source";

const TEST_FAILURE = "The connection could not be tested.";

const SOURCE_INTRO: Record<ConnectedSource, string> = {
  deluno:
    "Weir asks Deluno where its downloads arrive and where it picks cleaned files up from, and fills the workflows in from that.",
  arr: "Weir asks Sonarr and Radarr where their download client saves files, and fills the workflows in from that. Connect one or both.",
  client:
    "Weir reads where your download client saves finished downloads, and fills the workflows in from that. It only reads: nothing in the client is changed.",
};

function ManagerConnectionRow({
  connection,
}: {
  connection: MediaManagerConnection;
}) {
  const test = useTestMediaManagerConnection();
  const remove = useDeleteMediaManagerConnection();
  const [confirmingRemoval, setConfirmingRemoval] = useState(false);
  return (
    <>
      <WizardConnectionRow
        connection={{
          name: connectionTitle(connection),
          address: connection.base_url,
          answering: connection.last_test_at ? connection.last_test_ok : null,
          detail: connection.last_test_detail,
          testing: test.isPending,
          testError: test.isError
            ? errorMessage(test.error, TEST_FAILURE)
            : null,
        }}
        onTest={() => test.mutate(connection.id)}
        onRemove={() => {
          remove.reset();
          setConfirmingRemoval(true);
        }}
        removeDisabled={remove.isPending}
      />
      {confirmingRemoval ? (
        <RemoveConnectionDialog
          connection={connection}
          remove={remove}
          onClose={() => setConfirmingRemoval(false)}
        />
      ) : null}
    </>
  );
}

function DownloadClientConnectionRow({
  connection,
}: {
  connection: DownloadClientConnection;
}) {
  const test = useTestDownloadClientConnection();
  const remove = useDeleteDownloadClientConnection();
  const [confirmingRemoval, setConfirmingRemoval] = useState(false);
  return (
    <>
      <WizardConnectionRow
        connection={{
          name: connectionTitle(connection),
          address: connection.base_url,
          answering: connection.last_test_at
            ? (connection.last_test_ok ?? null)
            : null,
          detail: connection.last_test_detail ?? null,
          testing: test.isPending,
          testError: test.isError
            ? errorMessage(test.error, TEST_FAILURE)
            : null,
        }}
        onTest={() => test.mutate(connection.id)}
        onRemove={() => {
          remove.reset();
          setConfirmingRemoval(true);
        }}
        removeDisabled={remove.isPending}
      />
      {confirmingRemoval ? (
        <RemoveDownloadClientDialog
          connection={connection}
          remove={remove}
          onClose={() => setConfirmingRemoval(false)}
        />
      ) : null}
    </>
  );
}

/**
 * Connecting to the chosen source: what is already connected, and the form to connect more. Uses the same
 * forms, tests and removal dialogs as Setup › Connections › Media managers, so a connection made here is the same one.
 */
export function WizardConnect({
  source,
  connections,
  onBack,
}: {
  source: ConnectedSource;
  connections: WizardConnections;
  /** Leaves this source when there is nothing connected to keep. */
  onBack: () => void;
}) {
  const [adding, setAdding] = useState(false);
  const [justCreated, setJustCreated] = useState<MediaManagerConnection | null>(
    null,
  );
  const connected = connections.managers.length + connections.clients.length;

  if (connections.loading) {
    return <p className="mm-quiet-note">Loading what is connected…</p>;
  }
  if (connections.failed) {
    return (
      <p className="mm-status-text text-sm" data-status="broken" role="alert">
        Weir could not load what is connected. Reload the page, or choose
        &ldquo;Neither&rdquo; and pick the folders yourself.
      </p>
    );
  }

  const showForm = adding || connected === 0;
  const cancelForm = connected === 0 ? onBack : () => setAdding(false);
  const created = () => setAdding(false);

  return (
    <div className="flex flex-col gap-3" data-testid="setup-wizard-connect">
      <p className="mm-quiet-note">{SOURCE_INTRO[source]}</p>
      {connections.managers.map((connection) => (
        <ManagerConnectionRow key={connection.id} connection={connection} />
      ))}
      {connections.clients.map((connection) => (
        <DownloadClientConnectionRow
          key={connection.id}
          connection={connection}
        />
      ))}
      {justCreated &&
      connections.answering.includes(`manager:${justCreated.id}`) ? (
        <NewConnectionSecretPrompt
          connection={justCreated}
          onDismiss={() => setJustCreated(null)}
        />
      ) : null}
      {showForm && source === "client" ? (
        <AddDownloadClientForm onCancel={cancelForm} onCreated={created} />
      ) : null}
      {showForm && source !== "client" ? (
        <AddConnectionForm
          kinds={MANAGER_KINDS_BY_SOURCE[source]}
          onCancel={cancelForm}
          onCreated={(connection) => {
            created();
            setJustCreated(connection);
          }}
        />
      ) : null}
      {!showForm ? (
        <div>
          <button
            type="button"
            className={mmActionButtonClass({ variant: "secondary" })}
            onClick={() => {
              setJustCreated(null);
              setAdding(true);
            }}
          >
            Connect another
          </button>
        </div>
      ) : null}
    </div>
  );
}
