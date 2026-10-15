import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { Route, Routes, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { RequireAuth } from "../../../../app/require-auth";
import {
  markLoginSucceeded,
  sessionWasNotKept,
} from "../../../../lib/auth/session-kept";
import {
  clearSignedIn,
  finishChangingPassword,
  markSignedIn,
} from "../../../../lib/auth/signed-in-before";
import { LoginPage } from "../../../../pages/auth/login-page";
import {
  serveRecordedReplies,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { ChangePasswordForm } from "./account-forms";
import {
  bootstrapStatusAfterSetup,
  csrfToken,
  me,
  meAfterPasswordChange,
  passwordChanged,
  passwordWrongCurrent,
} from "./security-replies";
import { renderSecurity } from "./security-test-support";

const ME = "/api/v1/auth/me";
const CSRF = "/api/v1/auth/csrf";
const CHANGE_PASSWORD = "/api/v1/auth/change-password";
const BOOTSTRAP = "/api/v1/auth/bootstrap/status";

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  clearSignedIn();
  finishChangingPassword();
  sessionWasNotKept();
  vi.unstubAllGlobals();
});

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

/** The pages around the form as the app lays them out: the signed-in area behind the guard, and the sign-in page. */
function renderApp() {
  return renderSecurity(
    <>
      <Where />
      <Routes>
        <Route element={<RequireAuth />}>
          <Route path="/" element={<ChangePasswordForm />} />
        </Route>
        <Route path="/login" element={<LoginPage />} />
      </Routes>
    </>,
  );
}

function changePassword() {
  fireEvent.change(screen.getByPlaceholderText("Enter current password"), {
    target: { value: "current-pass-1" },
  });
  fireEvent.change(
    screen.getByPlaceholderText("Enter new password", { exact: true }),
    { target: { value: "new-pass-one-1" } },
  );
  fireEvent.change(screen.getByPlaceholderText("Re-enter new password"), {
    target: { value: "new-pass-one-1" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Change password" }));
}

describe("Changing the password ends this sign-in", () => {
  it("sends the person to the sign-in page, which says the password was changed", async () => {
    weir = serveRecordedReplies({
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${ME}`]: [me, meAfterPasswordChange],
      [`GET ${BOOTSTRAP}`]: bootstrapStatusAfterSetup,
      [`POST ${CHANGE_PASSWORD}`]: passwordChanged,
    });
    markSignedIn();
    renderApp();
    await screen.findByRole("button", { name: "Change password" });

    changePassword();

    expect(
      await screen.findByText(
        "Password changed. Sign in with your new password.",
      ),
    ).toHaveAttribute("role", "status");
    expect(screen.getByTestId("where")).toHaveTextContent(
      "/login?password=changed",
    );
    expect(screen.queryByText(/session expired/i)).toBeNull();
    expect(screen.queryByText(/did not stick/i)).toBeNull();
  });

  it("does not call it an expired session, nor a cookie the browser refused, when the person signed in moments ago", async () => {
    weir = serveRecordedReplies({
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${ME}`]: [me, meAfterPasswordChange],
      [`GET ${BOOTSTRAP}`]: bootstrapStatusAfterSetup,
      [`POST ${CHANGE_PASSWORD}`]: passwordChanged,
    });
    markSignedIn();
    markLoginSucceeded();
    renderApp();
    await screen.findByRole("button", { name: "Change password" });

    changePassword();

    await screen.findByText(
      "Password changed. Sign in with your new password.",
    );
    expect(screen.queryByText(/did not stick/i)).toBeNull();
  });

  it("keeps the person on the form, with the refusal, when the server does not change the password", async () => {
    weir = serveRecordedReplies({
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${ME}`]: me,
      [`POST ${CHANGE_PASSWORD}`]: passwordWrongCurrent,
    });
    markSignedIn();
    renderApp();
    await screen.findByRole("button", { name: "Change password" });

    changePassword();

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Current password is incorrect.",
    );
    expect(screen.getByTestId("where")).toHaveTextContent("/");
    expect(screen.getByTestId("where")).not.toHaveTextContent("/login");
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Change password" }),
      ).toBeEnabled(),
    );
  });

  it("says a session expired, as before, when it ends for any other reason", async () => {
    weir = serveRecordedReplies({
      [`GET ${ME}`]: meAfterPasswordChange,
      [`GET ${BOOTSTRAP}`]: bootstrapStatusAfterSetup,
    });
    markSignedIn();

    renderApp();

    await screen.findByText(
      "Your session expired. Sign in again to keep using Weir.",
    );
    expect(screen.queryByText(/Password changed/)).toBeNull();
  });
});
