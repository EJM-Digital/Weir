import { useState } from "react";

import { ConfirmDialog } from "../../../../components/ui/confirm-dialog";
import { errorMessage } from "../../../../lib/api/error-message";
import { useDeleteNotificationChannelMutation } from "../../../../lib/settings/queries";
import type { NotificationChannelOut } from "../../../../lib/settings/types";

/**
 * Remove asks first. The pending channel is held whole, not just its id, so the dialog can say which
 * one by name.
 */
export function useChannelRemoval() {
  const deleteMutation = useDeleteNotificationChannelMutation();
  const [pending, setPending] = useState<NotificationChannelOut | null>(null);
  const [error, setError] = useState<string | null>(null);

  const ask = (channel: NotificationChannelOut) => {
    setError(null);
    setPending(channel);
  };
  const cancel = () => {
    setError(null);
    setPending(null);
  };
  const confirm = (id: number) => {
    setError(null);
    deleteMutation.mutate(id, {
      onSuccess: () => setPending(null),
      onError: (err) =>
        setError(errorMessage(err, "Could not remove this alert.")),
    });
  };
  const deletingId = deleteMutation.isPending ? deleteMutation.variables : null;
  return { pending, error, deletingId, ask, cancel, confirm };
}

export function RemoveChannelDialog({
  removal,
}: {
  removal: ReturnType<typeof useChannelRemoval>;
}) {
  const channel = removal.pending;
  if (!channel) return null;
  return (
    <ConfirmDialog
      testId="notification-channel-remove-confirm"
      title={`Remove ${channel.label}?`}
      description={
        <>
          <p>
            Weir will stop sending alerts to {channel.label}. Its address and
            the events it listens for go with it.
          </p>
          <p>Other alerts keep working. This cannot be undone.</p>
        </>
      }
      confirmLabel="Remove alert"
      tone="danger"
      busy={removal.deletingId === channel.id}
      error={removal.error}
      onCancel={removal.cancel}
      onConfirm={() => removal.confirm(channel.id)}
    />
  );
}
