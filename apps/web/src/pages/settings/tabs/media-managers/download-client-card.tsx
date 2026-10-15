import { useState } from "react";

import { ConfirmDialog } from "../../../../components/ui/confirm-dialog";
import { errorMessage } from "../../../../lib/api/error-message";
import type { DownloadClientConnection } from "../../../../lib/download-clients/download-clients-api";
import {
  useDeleteDownloadClientConnection,
  useTestDownloadClientConnection,
  useUpdateDownloadClientConnection,
} from "../../../../lib/download-clients/queries";
import { useIsAdmin } from "../../../../lib/auth/can-edit";
import { connectionTitle } from "../../../../lib/ui/connection-title";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { ConnectionStatusLine } from "./connection-health";
import { ConnectionRow } from "./connection-row";
import { DownloadClientEditForm } from "./download-client-edit-form";

const TEST_FAILURE = "The download client could not be tested.";
const TOGGLE_FAILURE = "This download client could not be turned on or off.";

export function RemoveDownloadClientDialog({
  connection,
  remove,
  onClose,
}: {
  connection: DownloadClientConnection;
  remove: ReturnType<typeof useDeleteDownloadClientConnection>;
  onClose: () => void;
}) {
  return (
    <ConfirmDialog
      testId="download-client-remove-confirm"
      title={`Remove ${connectionTitle(connection)}?`}
      description={
        <>
          <p>
            Weir will stop reading folder suggestions from{" "}
            {connectionTitle(connection)}. Its address and saved credentials go
            with it, so connecting it again means setting it up from scratch.
          </p>
          <p>
            Weir never controlled {connectionTitle(connection)} — nothing about
            it changes. No watched folder set from its suggestion is undone.
          </p>
        </>
      }
      confirmLabel="Remove download client"
      tone="danger"
      busy={remove.isPending}
      error={
        remove.isError
          ? errorMessage(remove.error, "Could not remove this download client.")
          : null
      }
      onCancel={() => {
        remove.reset();
        onClose();
      }}
      onConfirm={() => remove.mutate(connection.id, { onSuccess: onClose })}
    />
  );
}

function toggleLabel(connection: DownloadClientConnection, pending: boolean) {
  if (pending) return connection.enabled ? "Disabling…" : "Enabling…";
  return connection.enabled ? "Disable" : "Enable";
}

/** One connected download client: whether it answers, and how to wire it up or remove it. */
export function DownloadClientCard({
  connection,
  fmt,
}: {
  connection: DownloadClientConnection;
  fmt: (iso: string | null) => string;
}) {
  const update = useUpdateDownloadClientConnection();
  const remove = useDeleteDownloadClientConnection();
  const admin = useIsAdmin();
  const test = useTestDownloadClientConnection();
  const [confirmingRemoval, setConfirmingRemoval] = useState(false);
  const [editing, setEditing] = useState(false);
  const busy = update.isPending || remove.isPending || test.isPending;
  const locked = busy || editing;

  return (
    <ConnectionRow
      title={connectionTitle(connection)}
      status={
        <ConnectionStatusLine
          connection={connection}
          fmt={fmt}
          unchecked="Not tested yet"
          testId="download-client-status"
        />
      }
      testId="download-client-card"
      actions={
        <>
          <button
            type="button"
            data-testid="download-client-test"
            className={mmActionButtonClass({ variant: "secondary" })}
            disabled={locked}
            onClick={() => test.mutate(connection.id)}
          >
            {test.isPending ? "Testing…" : "Test"}
          </button>
          <button
            type="button"
            data-testid="download-client-edit"
            className={mmActionButtonClass({ variant: "secondary" })}
            disabled={locked || !admin}
            onClick={() => setEditing(true)}
          >
            Edit
          </button>
          <button
            type="button"
            className={mmActionButtonClass({ variant: "secondary" })}
            disabled={locked || !admin}
            onClick={() =>
              update.mutate({
                id: connection.id,
                data: { enabled: !connection.enabled },
              })
            }
          >
            {toggleLabel(connection, update.isPending)}
          </button>
          <button
            type="button"
            data-testid="download-client-remove"
            className={mmActionButtonClass({ variant: "danger-outline" })}
            disabled={locked || !admin}
            aria-haspopup="dialog"
            onClick={() => {
              remove.reset();
              setConfirmingRemoval(true);
            }}
          >
            Remove
          </button>
        </>
      }
    >
      {test.isError ? (
        <p
          className="mm-status-text mt-2 text-sm"
          data-status="broken"
          role="alert"
        >
          {errorMessage(test.error, TEST_FAILURE)}
        </p>
      ) : null}

      {update.isError ? (
        <p
          className="mm-status-text mt-2 text-sm"
          data-status="broken"
          role="alert"
        >
          {errorMessage(update.error, TOGGLE_FAILURE)}
        </p>
      ) : null}

      {editing ? (
        <DownloadClientEditForm
          connection={connection}
          onClose={() => setEditing(false)}
        />
      ) : null}

      {confirmingRemoval ? (
        <RemoveDownloadClientDialog
          connection={connection}
          remove={remove}
          onClose={() => setConfirmingRemoval(false)}
        />
      ) : null}
    </ConnectionRow>
  );
}
