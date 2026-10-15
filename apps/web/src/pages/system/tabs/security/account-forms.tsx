import { useState, type ReactNode } from "react";

import { errorMessage } from "../../../../lib/api/error-message";
import {
  useChangePasswordMutation,
  useChangeUsernameMutation,
} from "../../../../lib/auth/queries";
import { mmActionButtonClass } from "../../../../lib/ui/mm-control-roles";
import { RevealablePasswordField, SecurityField } from "./security-field";

/** One of the Account card's two forms: what it is for, its fields, then its button at the foot of the column. */
function AccountForm({
  headingId,
  title,
  help,
  children,
}: {
  headingId: string;
  title: string;
  help: ReactNode;
  children: ReactNode;
}) {
  return (
    <section className="mm-account-form" aria-labelledby={headingId}>
      <h4 id={headingId} className="mm-account-form__title">
        {title}
      </h4>
      <p className="mm-account-form__help">{help}</p>
      {children}
    </section>
  );
}

/** Weir has one account; renaming it asks for the password first. */
export function ChangeUsernameForm() {
  const changeUsername = useChangeUsernameMutation();
  const [newUsername, setNewUsername] = useState("");
  const [password, setPassword] = useState("");
  // Bumped on each submit, so the password field remounts with its text hidden again.
  const [attempt, setAttempt] = useState(0);
  const [missing, setMissing] = useState<string | null>(null);
  const busy = changeUsername.isPending;

  const submit = () => {
    setMissing(null);
    if (newUsername.trim() === "" || password === "") {
      setMissing("Enter the new username and your current password.");
      return;
    }
    changeUsername.mutate(
      { currentPassword: password, newUsername: newUsername.trim() },
      {
        onSuccess: () => {
          setNewUsername("");
          setPassword("");
        },
        onSettled: () => setAttempt((n) => n + 1),
      },
    );
  };

  return (
    <AccountForm
      headingId="suite-security-change-username-heading"
      title="Change username"
      help={
        <>
          Weir has one account. <code>admin</code> and <code>Admin</code> are
          the same name.
        </>
      }
    >
      <SecurityField
        label="New username"
        placeholder="Enter a new username"
        autoComplete="username"
        value={newUsername}
        onChange={setNewUsername}
        disabled={busy}
      />
      <RevealablePasswordField
        key={attempt}
        label="Current password"
        revealLabel="current password"
        placeholder="Confirm it is you"
        autoComplete="current-password"
        value={password}
        onChange={setPassword}
        disabled={busy}
      />
      {changeUsername.isError ? (
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {errorMessage(changeUsername.error, "Could not change the username.")}
        </p>
      ) : null}
      {missing ? (
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {missing}
        </p>
      ) : null}
      {changeUsername.isSuccess ? (
        <p className="text-sm text-mm-text2" role="status">
          {changeUsername.data.message}
        </p>
      ) : null}
      <div className="mm-account-form__action">
        <button
          type="button"
          className={`${mmActionButtonClass({ variant: "secondary" })} mm-sys-btn`}
          disabled={busy}
          onClick={submit}
        >
          {busy ? "Saving…" : "Change username"}
        </button>
      </div>
    </AccountForm>
  );
}

const EMPTY_PASSWORDS = { current: "", next: "", confirm: "" };

/** Changing the password ends this sign-in: Weir asks for a fresh one with the new password. */
export function ChangePasswordForm() {
  const changePassword = useChangePasswordMutation();
  const [fields, setFields] = useState(EMPTY_PASSWORDS);
  const [validationError, setValidationError] = useState<string | null>(null);
  // Bumped on each submit, so the fields remount with their text hidden again.
  const [attempt, setAttempt] = useState(0);
  const busy = changePassword.isPending;
  const set = (field: keyof typeof EMPTY_PASSWORDS) => (value: string) =>
    setFields((prev) => ({ ...prev, [field]: value }));

  const submit = () => {
    setValidationError(null);
    if (
      fields.current.trim() === "" ||
      fields.next.trim() === "" ||
      fields.confirm.trim() === ""
    ) {
      setValidationError("Fill in all three password fields.");
      return;
    }
    if (fields.next !== fields.confirm) {
      setValidationError("New passwords do not match.");
      return;
    }
    changePassword.mutate(
      { currentPassword: fields.current, newPassword: fields.next },
      {
        onSuccess: () => setFields(EMPTY_PASSWORDS),
        onSettled: () => setAttempt((n) => n + 1),
      },
    );
  };

  return (
    <AccountForm
      headingId="suite-security-change-password-heading"
      title="Change password"
      help="After saving, Weir asks you to sign in again."
    >
      <div className="mm-account-form__fields" key={attempt}>
        <RevealablePasswordField
          label="Current password"
          revealLabel="password"
          placeholder="Enter current password"
          autoComplete="current-password"
          value={fields.current}
          onChange={set("current")}
          disabled={busy}
        />
        <RevealablePasswordField
          label="New password (min. 8 characters)"
          revealLabel="new password"
          placeholder="Enter new password"
          autoComplete="new-password"
          value={fields.next}
          onChange={set("next")}
          disabled={busy}
        />
        <RevealablePasswordField
          label="Confirm new password"
          revealLabel="password confirmation"
          placeholder="Re-enter new password"
          autoComplete="new-password"
          value={fields.confirm}
          onChange={set("confirm")}
          disabled={busy}
        />
      </div>
      {changePassword.isError ? (
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {errorMessage(changePassword.error, "Could not change password.")}
        </p>
      ) : null}
      {validationError ? (
        <p className="mm-status-text text-sm" data-status="broken" role="alert">
          {validationError}
        </p>
      ) : null}
      <div className="mm-account-form__action">
        <button
          type="button"
          className={`${mmActionButtonClass({ variant: "primary" })} mm-sys-btn`}
          disabled={busy}
          onClick={submit}
        >
          {busy ? "Saving…" : "Change password"}
        </button>
      </div>
    </AccountForm>
  );
}
