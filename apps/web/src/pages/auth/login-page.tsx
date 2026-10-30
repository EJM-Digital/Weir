import { FormEvent, useId, useState } from "react";
import { Link, Navigate, useLocation, useNavigate } from "react-router-dom";
import { AuthPasswordField } from "../../components/auth/auth-password-field";
import { AuthBrandStack } from "../../components/brand/auth-brand-stack";
import { ApiEntryError } from "../../components/shared/api-entry-error";
import { PageLoading } from "../../components/shared/page-loading";
import {
  useBootstrapStatusQuery,
  useLoginMutation,
  useMeQuery,
} from "../../lib/auth/queries";
import { errorMessage } from "../../lib/api/error-message";
import { RECOVERY_TEXT } from "../../lib/auth/recovery-text";

export function LoginPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const searchParams = new URLSearchParams(location.search);
  const fromSetup =
    Boolean((location.state as { fromSetup?: boolean } | null)?.fromSetup) ||
    searchParams.get("bootstrap") === "created";
  // Set by the setup screen's "Already have an account?" link: this browser wants the real
  // sign-in form once, even though no admin exists yet on this install (#704). Without it, a
  // stale bootstrap-allowed read would bounce the click straight back to Setup.
  const manualSignIn = Boolean(
    (location.state as { manualSignIn?: boolean } | null)?.manualSignIn,
  );
  const passwordChanged = searchParams.get("password") === "changed";
  const sessionExpired = searchParams.get("session") === "expired";
  const sessionNotKept = searchParams.get("problem") === "session-not-kept";
  const me = useMeQuery();
  const boot = useBootstrapStatusQuery();
  const login = useLoginMutation();
  const fieldId = useId();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [trustedDevice, setTrustedDevice] = useState(false);

  if (me.isPending || boot.isPending) {
    return <PageLoading />;
  }
  if (me.data) {
    return <Navigate to="/" replace />;
  }
  if (me.isError || boot.isError) {
    const err = boot.error ?? me.error;
    return (
      <main className="mm-auth-body" id="mm-main-content" tabIndex={-1}>
        <div className="mm-auth-frame">
          <AuthBrandStack />
          <div className="mm-auth-card">
            <ApiEntryError error={err} />
          </div>
        </div>
      </main>
    );
  }
  if (boot.data.bootstrap_allowed && !fromSetup && !manualSignIn) {
    return <Navigate to="/setup" replace />;
  }

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await login.mutateAsync({
        username: username.trim(),
        password,
        trustedDevice,
      });
      void navigate("/", { replace: true });
    } catch {
      /* mutation error surfaces below */
    }
  };

  const loginUserId = `${fieldId}-user`;
  // The shared mapping (lib/api/api-error-text.ts) already turns a 429 into a wait-and-retry
  // sentence, a 403 or 400 into the server's own plain reason, and an unreachable server into
  // "Can't reach Weir" — one place for every screen to agree with, not a second copy here.
  const loginErrorMessage = login.isError
    ? errorMessage(login.error, "Sign-in failed.")
    : null;

  return (
    <main className="mm-auth-body" id="mm-main-content" tabIndex={-1}>
      <div className="mm-auth-frame">
        <AuthBrandStack />
        <div className="mm-auth-card">
          <p className="mm-auth-eyebrow">Weir</p>
          <h1 className="mm-auth-title">Sign in</h1>
          <p className="mm-auth-lead">Sign in to manage Weir on this server.</p>

          {fromSetup ? (
            <p className="mm-auth-banner" data-status="done" role="status">
              Initial account created. Sign in with the credentials you chose.
            </p>
          ) : null}
          {passwordChanged ? (
            <p className="mm-auth-banner" data-status="done" role="status">
              Password changed. Sign in with your new password.
            </p>
          ) : null}
          {sessionExpired ? (
            <p className="mm-auth-banner" data-status="attention" role="status">
              Your session expired. Sign in again to keep using Weir.
            </p>
          ) : null}
          {sessionNotKept ? (
            <div className="mm-auth-banner" data-status="broken" role="alert">
              <strong>
                Your password was correct, but the session did not stick.
              </strong>{" "}
              Your browser rejected the sign-in cookie. This usually happens
              when Weir is reached over plain HTTP while HTTPS-only cookies are
              switched on. Set <code>WEIR_SESSION_COOKIE_SECURE=auto</code> and
              restart, or reach Weir over HTTPS.
            </div>
          ) : null}

          {boot.data.bootstrap_allowed ? (
            // No `mt-2`: `.mm-auth-lead` sets its own margin, so the utility never applied.
            // The gap above comes from the banner or lead paragraph before it, as it always did.
            <p className="mm-auth-lead">
              First-time setup?{" "}
              <Link
                to="/setup"
                className="font-medium text-mm-accent-bright hover:underline"
              >
                Create the admin account
              </Link>
              .
            </p>
          ) : null}

          <form
            data-testid="login-form"
            className="mm-auth-form mt-4"
            onSubmit={onSubmit}
          >
            <label className="mm-auth-label" htmlFor={loginUserId}>
              Username
            </label>
            <input
              id={loginUserId}
              data-testid="login-username"
              name="username"
              autoComplete="username"
              className="mm-auth-input"
              autoFocus
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              required
            />
            <AuthPasswordField
              id={`${fieldId}-pass`}
              testId="login-password"
              name="password"
              label="Password"
              revealLabel="password"
              autoComplete="current-password"
              value={password}
              onChange={setPassword}
              required
            />
            {loginErrorMessage ? (
              <p className="mm-auth-banner" data-status="broken" role="alert">
                {loginErrorMessage}
              </p>
            ) : null}
            {/* A checkbox row, not a filled box inside the sign-in card. */}
            <label className="flex items-start gap-3 py-1 text-sm text-mm-text2">
              <input
                type="checkbox"
                className="mt-0.5 h-4 w-4 shrink-0 accent-mm-accent"
                aria-label="Trust this device"
                checked={trustedDevice}
                onChange={(e) => setTrustedDevice(e.target.checked)}
              />
              <span>
                <span className="block font-medium text-mm-text1">
                  Trust this device
                </span>
                <span className="block">
                  Keep this browser signed in longer on this machine.
                </span>
              </span>
            </label>
            <button
              type="submit"
              data-testid="login-submit"
              className="mm-auth-submit"
              disabled={login.isPending}
            >
              {login.isPending ? "Signing in…" : "Sign in"}
            </button>
          </form>

          <p className="mm-auth-footer-link">
            <Link to="/recover" data-testid="login-forgot">
              {RECOVERY_TEXT.forgotLink}
            </Link>
          </p>
        </div>
      </div>
    </main>
  );
}
