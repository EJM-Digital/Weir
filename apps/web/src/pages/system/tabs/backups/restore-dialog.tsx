import { useRef, useState } from "react";

import { Field } from "../../../../components/shared/field";
import { ConfirmDialog } from "../../../../components/ui/confirm-dialog";

/**
 * The confirmation before a backup replaces this server's settings. A backup that holds locked secrets also asks for the backup
 * password it was made with, and stays open on a wrong one so the password can be typed again.
 */
export function RestoreDialog({
  needsPassword,
  busy,
  problem,
  onCancel,
  onConfirm,
}: {
  needsPassword: boolean;
  busy: boolean;
  /** Why the last attempt was refused; shown in the dialog so the person can correct the password. */
  problem: string | null;
  onCancel: () => void;
  onConfirm: (password: string) => void;
}) {
  const [password, setPassword] = useState("");
  const passwordInput = useRef<HTMLInputElement>(null);

  return (
    <ConfirmDialog
      title="Replace the settings on this server with this backup?"
      description={
        <>
          <p>This cannot be undone.</p>
          {needsPassword ? (
            <>
              <p>
                This backup holds your connections&apos; API keys, passwords and
                alert addresses, locked with a backup password. Enter the
                password it was made with.
              </p>
              <Field label="Backup password">
                <input
                  ref={passwordInput}
                  type="password"
                  autoComplete="off"
                  className="mm-input"
                  value={password}
                  disabled={busy}
                  onChange={(e) => setPassword(e.target.value)}
                />
              </Field>
            </>
          ) : null}
        </>
      }
      confirmLabel="Replace settings"
      cancelLabel="Keep current settings"
      busy={busy}
      busyLabel="Restoring…"
      confirmDisabled={needsPassword && password === ""}
      initialFocus={needsPassword ? passwordInput : undefined}
      tone="danger"
      error={problem}
      testId="restore-configuration-dialog"
      onCancel={onCancel}
      onConfirm={() => onConfirm(password)}
    />
  );
}
