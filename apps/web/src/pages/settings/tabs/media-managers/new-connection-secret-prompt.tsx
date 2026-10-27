import { errorMessage } from "../../../../lib/api/error-message";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import { useGenerateMediaManagerWebhookSecret } from "../../../../lib/media-managers/queries";
import { connectionTitle } from "../../../../lib/ui/connection-title";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { RevealedSecret } from "./revealed-secret";

const SECRET_FAILURE = "The secret could not be created.";

/**
 * Right after a media manager is added, offer to create its secret at once instead of leaving that
 * to be found later inside the new card's own setup details. Declining changes nothing: "Create a
 * secret" is still there under the card, whenever it is wanted.
 */
export function NewConnectionSecretPrompt({
  connection,
  onDismiss,
}: {
  connection: MediaManagerConnection;
  onDismiss: () => void;
}) {
  const secret = useGenerateMediaManagerWebhookSecret();
  // Read from the request itself, not from a callback of mutate(): a callback is dropped if the request
  // finishes before the prompt has finished subscribing to it, and the secret would never be shown.
  const revealed = secret.data?.webhook_secret ?? null;

  if (revealed) {
    return (
      <div
        className="mm-quiet-note"
        data-testid="media-manager-new-secret-prompt"
      >
        <RevealedSecret
          managerName={connectionTitle(connection)}
          secret={revealed}
        />
        <button
          type="button"
          className={`mt-2 ${mmActionButtonClass({ variant: "secondary" })}`}
          onClick={onDismiss}
        >
          Done
        </button>
      </div>
    );
  }

  return (
    <div
      className="mm-quiet-note"
      data-testid="media-manager-new-secret-prompt"
    >
      <p>Create a secret for {connectionTitle(connection)} now?</p>
      {secret.isError ? (
        <p
          className="mm-status-text mt-2 text-sm"
          data-status="broken"
          role="alert"
        >
          {errorMessage(secret.error, SECRET_FAILURE)}
        </p>
      ) : null}
      <div className="mt-2 flex flex-wrap gap-2">
        <button
          type="button"
          data-testid="media-manager-new-secret-create"
          className={mmActionButtonClass({ variant: "primary" })}
          disabled={secret.isPending}
          onClick={() => secret.mutate(connection.id)}
        >
          {secret.isPending ? "Creating…" : "Create a secret"}
        </button>
        <button
          type="button"
          data-testid="media-manager-new-secret-dismiss"
          className={mmActionButtonClass({ variant: "tertiary" })}
          disabled={secret.isPending}
          onClick={onDismiss}
        >
          Not now
        </button>
      </div>
    </div>
  );
}
