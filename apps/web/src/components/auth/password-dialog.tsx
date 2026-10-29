import { FormEvent, useId, useRef, useState } from "react";

import { PASSWORD_GATE_TEXT } from "../../lib/auth/password-gate-text";
import { mmActionButtonClass } from "../../lib/ui/mm-control-roles";
import { ModalDialog } from "../ui/modal-dialog";

/**
 * Asks for the signed-in person's password before a change that must not be made by whoever is at the keyboard. It sends nothing
 * for an empty box and says so; why a password was refused (wrong, or too many tries) is the server's, shown as `error`.
 */
export function PasswordDialog({
  busy,
  error,
  onConfirm,
  onCancel,
}: {
  busy: boolean;
  error: string | null;
  onConfirm: (password: string) => void;
  onCancel: () => void;
}) {
  const fieldId = useId();
  const field = useRef<HTMLInputElement>(null);
  const [password, setPassword] = useState("");
  const [missing, setMissing] = useState(false);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setMissing(password === "");
    if (password !== "") onConfirm(password);
  };

  return (
    <ModalDialog
      title={PASSWORD_GATE_TEXT.title}
      testId="password-dialog"
      onClose={onCancel}
      busy={busy}
      initialFocus={field}
    >
      <form onSubmit={submit}>
        <div className="mm-modal__body mm-field">
          <label className="mm-field__label" htmlFor={fieldId}>
            {PASSWORD_GATE_TEXT.label}
          </label>
          <input
            id={fieldId}
            ref={field}
            type="password"
            autoComplete="current-password"
            className="mm-input"
            data-testid="password-dialog-field"
            value={password}
            disabled={busy}
            onChange={(e) => setPassword(e.target.value)}
          />
        </div>
        {missing ? (
          <p
            className="mm-modal__error mm-status-text"
            data-status="broken"
            role="alert"
          >
            {PASSWORD_GATE_TEXT.missing}
          </p>
        ) : null}
        {error && !missing ? (
          <p
            className="mm-modal__error mm-status-text"
            data-status="broken"
            role="alert"
          >
            {error}
          </p>
        ) : null}
        <div className="mm-modal__actions">
          <button
            type="button"
            data-testid="password-dialog-cancel"
            className={mmActionButtonClass({ variant: "secondary" })}
            disabled={busy}
            onClick={onCancel}
          >
            {PASSWORD_GATE_TEXT.cancel}
          </button>
          <button
            type="submit"
            data-testid="password-dialog-confirm"
            className={mmActionButtonClass({ variant: "primary" })}
            disabled={busy}
          >
            {PASSWORD_GATE_TEXT.confirm}
          </button>
        </div>
      </form>
    </ModalDialog>
  );
}
