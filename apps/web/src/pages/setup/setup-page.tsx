import { FormEvent, useState } from "react";
import { Link, Navigate, useNavigate } from "react-router-dom";
import { AuthPasswordField } from "../../components/auth/auth-password-field";
import { AuthBrandStack } from "../../components/brand/auth-brand-stack";
import { ApiEntryError } from "../../components/shared/api-entry-error";
import { PageLoading } from "../../components/shared/page-loading";
import {
  useBootstrapMutation,
  useBootstrapStatusQuery,
  useMeQuery,
} from "../../lib/auth/queries";
import { errorMessage } from "../../lib/api/error-message";
import { SetupCodesStep } from "./setup-codes-step";

export function SetupPage() {
  const navigate = useNavigate();
  const me = useMeQuery();
  const boot = useBootstrapStatusQuery();
  const bootstrap = useBootstrapMutation();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [setupCode, setSetupCode] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);

  // The account exists and is signed in; setup waits here until the recovery codes are saved, whatever the reads below now say.
  if (bootstrap.isSuccess) {
    return (
      <SetupCodesStep
        codes={bootstrap.data.recovery_codes}
        onSaved={() => void navigate("/", { replace: true })}
      />
    );
  }

  if (me.isPending || boot.isPending) {
    return <PageLoading label="Checking setup..." />;
  }

  if (me.data) {
    return <Navigate to="/" replace />;
  }

  if (boot.isError) {
    return (
      <main className="mm-auth-body" id="mm-main-content" tabIndex={-1}>
        <div className="mm-auth-frame">
          <AuthBrandStack />
          <div className="mm-auth-card">
            <ApiEntryError error={boot.error} />
            <p className="mm-auth-footer-link !mt-4">
              <Link to="/login">Back to sign in</Link>
            </p>
          </div>
        </div>
      </main>
    );
  }

  if (!boot.data?.bootstrap_allowed) {
    return <Navigate to="/login" replace />;
  }

  // A reason given for what was typed no longer applies once it is changed.
  const forgetRefusal = () => {
    setValidationError(null);
    if (bootstrap.isError) {
      bootstrap.reset();
    }
  };

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    const trimmedUsername = username.trim();
    if (!trimmedUsername) {
      setValidationError("Admin username is required.");
      return;
    }
    if (password.length < 8) {
      setValidationError("Password must be at least 8 characters.");
      return;
    }
    if (password !== confirmPassword) {
      setValidationError("Passwords do not match.");
      return;
    }
    setValidationError(null);
    try {
      await bootstrap.mutateAsync({
        username: trimmedUsername,
        password,
        setupCode: setupCode.trim() || undefined,
      });
    } catch {
      /* surfaced below */
    }
  };

  return (
    <main className="mm-auth-body" id="mm-main-content" tabIndex={-1}>
      <div className="mm-auth-frame">
        <AuthBrandStack />
        <div className="mm-auth-card">
          <p className="mm-auth-eyebrow">First run</p>
          <h1 className="mm-auth-title">Create your account</h1>
          <p className="mm-auth-lead">
            Choose the username and password you&apos;ll use to sign in. Weir
            will sign you in right away, show you your recovery codes, and then
            run the first-run setup wizard.
          </p>

          <form
            data-testid="setup-form"
            className="mm-auth-form"
            onSubmit={onSubmit}
          >
            <label className="mm-auth-label" htmlFor="setup-user">
              Admin username
            </label>
            <input
              id="setup-user"
              data-testid="setup-username"
              name="username"
              autoComplete="username"
              className="mm-auth-input"
              value={username}
              onChange={(e) => {
                setUsername(e.target.value);
                forgetRefusal();
              }}
              required
              maxLength={64}
            />
            <AuthPasswordField
              id="setup-pass"
              testId="setup-password"
              name="password"
              label="Password (min. 8 characters)"
              revealLabel="password"
              autoComplete="new-password"
              value={password}
              onChange={(value) => {
                setPassword(value);
                forgetRefusal();
              }}
              required
              minLength={8}
              maxLength={512}
            />
            <AuthPasswordField
              id="setup-confirm-pass"
              testId="setup-confirm-password"
              name="confirm_password"
              label="Confirm password"
              revealLabel="confirmation"
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(value) => {
                setConfirmPassword(value);
                forgetRefusal();
              }}
              required
              minLength={8}
              maxLength={512}
            />
            {boot.data?.requires_setup_code ? (
              <>
                <label className="mm-auth-label" htmlFor="setup-code">
                  Setup code
                </label>
                <input
                  id="setup-code"
                  data-testid="setup-code"
                  name="setup_code"
                  autoComplete="off"
                  className="mm-auth-input"
                  value={setupCode}
                  onChange={(e) => {
                    setSetupCode(e.target.value);
                    forgetRefusal();
                  }}
                  required
                  maxLength={32}
                />
                <p className="mm-auth-hint">
                  Shown in Weir&apos;s log (docker logs) and saved in the
                  setup-code file in Weir&apos;s data folder.
                </p>
              </>
            ) : null}
            {validationError ? (
              <p className="mm-auth-banner" data-status="broken" role="alert">
                {validationError}
              </p>
            ) : null}
            {bootstrap.isError ? (
              <p className="mm-auth-banner" data-status="broken" role="alert">
                {errorMessage(bootstrap.error, "Setup failed.")}
              </p>
            ) : null}
            <button
              type="submit"
              data-testid="setup-submit"
              className="mm-auth-submit"
              disabled={bootstrap.isPending}
            >
              {bootstrap.isPending ? "Creating..." : "Create your account"}
            </button>
          </form>

          <p className="mm-auth-footer-link">
            Already have an account?{" "}
            <Link to="/login" state={{ manualSignIn: true }}>
              Sign in
            </Link>
          </p>
        </div>
      </div>
    </main>
  );
}
