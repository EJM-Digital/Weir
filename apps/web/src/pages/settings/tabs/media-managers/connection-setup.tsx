import { useState } from "react";

import { useIsAdmin } from "../../../../lib/auth/can-edit";
import { ConfirmDialog } from "../../../../components/ui/confirm-dialog";
import { errorMessage } from "../../../../lib/api/error-message";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import type { useGenerateMediaManagerWebhookSecret } from "../../../../lib/media-managers/queries";
import { connectionTitle } from "../../../../lib/ui/connection-title";
import {
  mmActionButtonClass,
  mmTechnicalMonoSmallClass,
} from "../../../../lib/ui/mm-control-roles";
import { RevealedSecret } from "./revealed-secret";

const SECRET_FAILURE = "The secret could not be created.";

/**
 * The API returns a path, but this gets pasted into another app on another machine, so it needs the
 * host Weir is actually reachable on.
 */
function webhookUrl(connection: MediaManagerConnection): string {
  return `${window.location.origin}${connection.webhook_url_path}`;
}

function isArrApp(connection: MediaManagerConnection): boolean {
  return connection.kind === "sonarr" || connection.kind === "radarr";
}

/** Sonarr and Radarr: where the mapping lives, and the import webhook that tells Weir a file was taken (#652). */
function ArrInstructions({ name }: { name: string }) {
  return (
    <>
      <p
        className="mb-3 text-mm-text2"
        data-testid="media-manager-mapping-pointer"
      >
        {name} picks up what Weir cleans through a remote path mapping from a
        workflow&apos;s watched folder to its output folder. Open that workflow
        under Setup › Workflows: its editor shows the exact values to enter and
        checks that {name} has them.
      </p>
      <p
        className="mb-3 text-mm-text2"
        data-testid="media-manager-import-webhook"
      >
        So Weir hears when {name} imports a file: in {name}, open Settings →
        Connect, add a <strong>Webhook</strong>, tick <strong>On Import</strong>{" "}
        and <strong>On Upgrade</strong>, and use the address below with method
        POST. Add a header named <code>X-Webhook-Secret</code> holding the
        secret. Activity then says &ldquo;Imported by {name}&rdquo;, and Weir
        removes its own copy once {name} has taken it, only when that copy is
        still exactly as Weir wrote it.
      </p>
    </>
  );
}

/**
 * Replacing a secret is the one setup action that breaks something already working: the manager's
 * webhook stops being accepted the moment the old secret is gone, until the new one is pasted in.
 * Creating a first secret breaks nothing, so only replace confirms.
 */
function ReplaceSecretDialog({
  connection,
  secret,
  onClose,
  onReplaced,
}: {
  connection: MediaManagerConnection;
  secret: ReturnType<typeof useGenerateMediaManagerWebhookSecret>;
  onClose: () => void;
  onReplaced: (value: string) => void;
}) {
  return (
    <ConfirmDialog
      testId="media-manager-replace-secret-confirm"
      title={`Replace ${connectionTitle(connection)}'s secret?`}
      description={`Replacing the secret stops ${connectionTitle(connection)} reaching Weir until you paste the new one into it.`}
      confirmLabel="Replace secret"
      busy={secret.isPending}
      busyLabel="Replacing…"
      error={secret.isError ? errorMessage(secret.error, SECRET_FAILURE) : null}
      onCancel={() => {
        secret.reset();
        onClose();
      }}
      onConfirm={() =>
        secret.mutate(connection.id, {
          onSuccess: (data) => {
            onReplaced(data.webhook_secret);
            onClose();
          },
        })
      }
    />
  );
}

/**
 * The address and secret are needed once, when wiring the other app up, so they are folded away and
 * the card answers "is it connected" at a glance. The browser's own disclosure triangle is hidden,
 * so the summary carries a Show / Hide link instead.
 */
export function ConnectionSetup({
  connection,
  secret,
  busy,
}: {
  connection: MediaManagerConnection;
  /** Owned by the card, so the card's other buttons wait while a secret is made. */
  secret: ReturnType<typeof useGenerateMediaManagerWebhookSecret>;
  busy: boolean;
}) {
  const admin = useIsAdmin();
  const [revealed, setRevealed] = useState<string | null>(null);
  const [confirmingReplace, setConfirmingReplace] = useState(false);

  const startSecret = () => {
    if (connection.webhook_secret_is_set) {
      secret.reset();
      setConfirmingReplace(true);
      return;
    }
    secret.mutate(connection.id, {
      onSuccess: (data) => setRevealed(data.webhook_secret),
    });
  };

  return (
    <details
      className="mm-conn-setup group"
      data-testid="media-manager-setup-details"
    >
      <summary className="mm-conn-setup__summary [&::-webkit-details-marker]:hidden">
        <span>How to point {connectionTitle(connection)} at Weir</span>
        {connection.unsigned_webhook_warning !== null ? (
          <span className="mm-status-text" data-status="attention">
            Needs a secret
          </span>
        ) : null}
        <span className="mm-quiet-link group-open:hidden">Show →</span>
        <span className="mm-quiet-link hidden group-open:inline">Hide →</span>
      </summary>

      <div className="mm-conn-setup__body">
        {isArrApp(connection) ? (
          <ArrInstructions name={connectionTitle(connection)} />
        ) : null}
        <p className="text-mm-text2">
          In {connectionTitle(connection)}, send files to this address:
        </p>
        <code
          className={`mt-1 block ${mmTechnicalMonoSmallClass}`}
          data-testid="media-manager-webhook-url"
        >
          {webhookUrl(connection)}
        </code>

        <p className="mt-3 text-mm-text2">
          {connection.webhook_secret_is_set
            ? `${connectionTitle(connection)} must send its secret with every file. Anything without it is ignored.`
            : `There is no secret yet, so anything on your network could send files here pretending to be ${connectionTitle(connection)}.`}
        </p>

        {revealed ? (
          <RevealedSecret
            managerName={connectionTitle(connection)}
            secret={revealed}
          />
        ) : null}

        {!confirmingReplace && secret.isError ? (
          <p
            className="mm-status-text mt-2 text-xs"
            data-status="broken"
            role="alert"
          >
            {errorMessage(secret.error, SECRET_FAILURE)}
          </p>
        ) : null}

        <button
          type="button"
          data-testid="media-manager-generate-secret"
          className={`mt-3 ${mmActionButtonClass({ variant: "secondary" })}`}
          disabled={busy || !admin}
          onClick={startSecret}
        >
          {connection.webhook_secret_is_set
            ? "Replace the secret"
            : "Create a secret"}
        </button>
        {!admin ? (
          <p className="mm-quiet-note mt-2">
            Only an admin can create or replace the secret.
          </p>
        ) : null}

        {confirmingReplace ? (
          <ReplaceSecretDialog
            connection={connection}
            secret={secret}
            onClose={() => setConfirmingReplace(false)}
            onReplaced={setRevealed}
          />
        ) : null}
      </div>
    </details>
  );
}
