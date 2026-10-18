import {
  clearSignOutFailed,
  useSignOutFailed,
} from "../../lib/auth/sign-out-failed";

/**
 * Says so when Sign out did not go through: the person is still signed in, which matters on a computer others use, and the sign-in
 * page they were just shown made it look otherwise. It stays until they dismiss it or sign out again.
 */
export function SignOutFailedBanner() {
  if (!useSignOutFailed()) return null;
  return (
    <div
      className="mm-live-banner mm-status-text"
      data-status="broken"
      data-testid="sign-out-failed"
      role="alert"
    >
      <span>
        Weir couldn&apos;t sign you out, so you&apos;re still signed in. Try
        Sign out again.
      </span>
      <button
        type="button"
        className="mm-live-banner__dismiss"
        onClick={clearSignOutFailed}
      >
        Dismiss
      </button>
    </div>
  );
}
