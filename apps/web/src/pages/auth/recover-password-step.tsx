import { FormEvent, useId, useState } from "react";
import { useNavigate } from "react-router-dom";

import { AuthPasswordField } from "../../components/auth/auth-password-field";
import { errorMessage } from "../../lib/api/error-message";
import type { SpentRecoveryCode } from "../../lib/api/types";
import { useRecoveryPasswordMutation } from "../../lib/auth/queries";
import { RECOVERY_TEXT } from "../../lib/auth/recovery-text";

const MIN_PASSWORD_LENGTH = 8;

/**
 * The second step of getting back in: the username the code belongs to, and a new password entered twice. The sign-in page then
 * says the password was changed.
 */
export function RecoverPasswordStep({
  spent,
  onStartAgain,
}: {
  spent: SpentRecoveryCode;
  onStartAgain: () => void;
}) {
  const navigate = useNavigate();
  const fieldId = useId();
  const setPassword = useRecoveryPasswordMutation();
  const [password, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [problem, setProblem] = useState<string | null>(null);

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    setProblem(null);
    if (password.length < MIN_PASSWORD_LENGTH) {
      setProblem(RECOVERY_TEXT.recoverTooShort);
      return;
    }
    if (password !== confirmPassword) {
      setProblem(RECOVERY_TEXT.recoverMismatch);
      return;
    }
    setPassword.mutate(
      { recoveryToken: spent.recovery_token, newPassword: password },
      {
        onSuccess: () =>
          void navigate("/login?password=changed", { replace: true }),
      },
    );
  };

  return (
    <form
      data-testid="recover-password-form"
      className="mm-auth-form"
      onSubmit={onSubmit}
    >
      <p className="mm-auth-lead">
        {RECOVERY_TEXT.recoverUsernameLead}{" "}
        <strong data-testid="recover-username">{spent.username}</strong>.{" "}
        {RECOVERY_TEXT.recoverEndedSessions}
      </p>
      <AuthPasswordField
        id={`${fieldId}-new`}
        testId="recover-password"
        name="password"
        label={RECOVERY_TEXT.recoverPasswordLabel}
        revealLabel="password"
        autoComplete="new-password"
        value={password}
        onChange={setNewPassword}
        required
        minLength={MIN_PASSWORD_LENGTH}
        maxLength={512}
      />
      <AuthPasswordField
        id={`${fieldId}-confirm`}
        testId="recover-confirm-password"
        name="confirm_password"
        label={RECOVERY_TEXT.recoverConfirmLabel}
        revealLabel="confirmation"
        autoComplete="new-password"
        value={confirmPassword}
        onChange={setConfirmPassword}
        required
        minLength={MIN_PASSWORD_LENGTH}
        maxLength={512}
      />
      {problem ? (
        <p className="mm-auth-banner" data-status="broken" role="alert">
          {problem}
        </p>
      ) : null}
      {setPassword.isError ? (
        <p className="mm-auth-banner" data-status="broken" role="alert">
          {errorMessage(setPassword.error, RECOVERY_TEXT.recoverPasswordFailed)}
        </p>
      ) : null}
      <button
        type="submit"
        data-testid="recover-password-submit"
        className="mm-auth-submit"
        disabled={setPassword.isPending}
      >
        {setPassword.isPending
          ? RECOVERY_TEXT.recoverPasswordBusy
          : RECOVERY_TEXT.recoverPasswordSubmit}
      </button>
      <p className="mm-auth-footer-link">
        <button
          type="button"
          className="mm-quiet-link"
          data-testid="recover-start-again"
          onClick={onStartAgain}
        >
          {RECOVERY_TEXT.recoverStartAgain}
        </button>
      </p>
    </form>
  );
}
