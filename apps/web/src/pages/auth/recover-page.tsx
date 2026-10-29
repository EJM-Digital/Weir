import { FormEvent, useId, useState } from "react";
import { Link } from "react-router-dom";

import { AuthBrandStack } from "../../components/brand/auth-brand-stack";
import { errorMessage } from "../../lib/api/error-message";
import { useRecoveryCodeMutation } from "../../lib/auth/queries";
import { RECOVERY_TEXT } from "../../lib/auth/recovery-text";
import { RecoverPasswordStep } from "./recover-password-step";

/**
 * Getting back in with a recovery code, from the sign-in page: the code first, which shows the username, then a new password
 * entered twice. The code is used up as soon as Weir accepts it, so stopping after the first step costs the code.
 */
export function RecoverPage() {
  const fieldId = useId();
  const spendCode = useRecoveryCodeMutation();
  const [code, setCode] = useState("");
  const [missing, setMissing] = useState(false);

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    setMissing(code.trim() === "");
    if (code.trim() !== "") {
      spendCode.mutate(code.trim());
    }
  };

  return (
    <main className="mm-auth-body" id="mm-main-content" tabIndex={-1}>
      <div className="mm-auth-frame">
        <AuthBrandStack />
        <div className="mm-auth-card" data-testid="recover">
          <p className="mm-auth-eyebrow">Weir</p>
          <h1 className="mm-auth-title">{RECOVERY_TEXT.recoverTitle}</h1>
          {spendCode.data ? (
            <RecoverPasswordStep
              spent={spendCode.data}
              onStartAgain={() => {
                spendCode.reset();
                setCode("");
              }}
            />
          ) : (
            <form
              data-testid="recover-code-form"
              className="mm-auth-form"
              onSubmit={onSubmit}
            >
              <p className="mm-auth-lead">{RECOVERY_TEXT.recoverLead}</p>
              <label className="mm-auth-label" htmlFor={`${fieldId}-code`}>
                {RECOVERY_TEXT.recoverCodeLabel}
              </label>
              <input
                id={`${fieldId}-code`}
                data-testid="recover-code"
                name="recovery_code"
                autoComplete="off"
                autoCapitalize="characters"
                spellCheck={false}
                className="mm-auth-input"
                autoFocus
                value={code}
                onChange={(e) => setCode(e.target.value)}
                maxLength={64}
              />
              <p className="mm-auth-hint">{RECOVERY_TEXT.recoverCodeHint}</p>
              {missing ? (
                <p className="mm-auth-banner" data-status="broken" role="alert">
                  {RECOVERY_TEXT.recoverCodeMissing}
                </p>
              ) : null}
              {spendCode.isError ? (
                <p className="mm-auth-banner" data-status="broken" role="alert">
                  {errorMessage(
                    spendCode.error,
                    RECOVERY_TEXT.recoverCodeFailed,
                  )}
                </p>
              ) : null}
              <button
                type="submit"
                data-testid="recover-code-submit"
                className="mm-auth-submit"
                disabled={spendCode.isPending}
              >
                {spendCode.isPending
                  ? RECOVERY_TEXT.recoverCodeBusy
                  : RECOVERY_TEXT.recoverCodeSubmit}
              </button>
            </form>
          )}
          <p className="mm-auth-footer-link">
            <Link to="/login">{RECOVERY_TEXT.recoverBack}</Link>
          </p>
        </div>
      </div>
    </main>
  );
}
