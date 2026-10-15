import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  useCurrentSessionQuery,
  useMeQuery,
} from "../../../../lib/auth/queries";
import {
  clearSignedIn,
  finishChangingPassword,
  markSignedIn,
} from "../../../../lib/auth/signed-in-before";
import {
  serveRecordedReplies,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { ChangePasswordForm, ChangeUsernameForm } from "./account-forms";
import {
  csrfToken,
  currentSession,
  me,
  meAfterPasswordChange,
  meRenamed,
  passwordChanged,
  passwordContainsUsername,
  passwordNarrowMix,
  passwordTooCommon,
  passwordTooShort,
  passwordUnchanged,
  passwordWrongCurrent,
  usernameChanged,
  usernameUnchanged,
  usernameWrongPassword,
} from "./security-replies";
import { heldReply, renderSecurity } from "./security-test-support";

const CSRF = "/api/v1/auth/csrf";
const ME = "/api/v1/auth/me";
const SESSION = "/api/v1/auth/session";
const CHANGE_PASSWORD = "/api/v1/auth/change-password";
const CHANGE_USERNAME = "/api/v1/auth/change-username";
const CSRF_TOKEN = (JSON.parse(csrfToken.body) as { csrf_token: string })
  .csrf_token;

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  clearSignedIn();
  finishChangingPassword();
  vi.unstubAllGlobals();
});

/** What the rest of the page shows of the signed-in account, read from the same queries the page reads. */
function WhoIsSignedIn() {
  const account = useMeQuery();
  const session = useCurrentSessionQuery();
  return (
    <>
      <p data-testid="who">{account.data?.username ?? "nobody"}</p>
      <p data-testid="session">{session.data ? "signed in" : "none"}</p>
    </>
  );
}

const field = (placeholder: string) =>
  screen.getByPlaceholderText(placeholder, { exact: true });
const press = (name: string) =>
  fireEvent.click(screen.getByRole("button", { name }));
const type = (placeholder: string, value: string) =>
  fireEvent.change(field(placeholder), { target: { value } });

function fillPasswords(current: string, next: string, confirm: string) {
  type("Enter current password", current);
  type("Enter new password", next);
  type("Re-enter new password", confirm);
}

describe("Change password", () => {
  it("refuses new passwords that differ and sends nothing", () => {
    weir = serveRecordedReplies({});
    renderSecurity(<ChangePasswordForm />);

    fillPasswords("current-pass-1", "new-pass-one-1", "new-pass-one-2");
    press("Change password");

    expect(screen.getByRole("alert")).toHaveTextContent(
      "New passwords do not match.",
    );
    expect(weir.requests).toEqual([]);
    expect(screen.queryByText(/Password changed/)).toBeNull();
  });

  it("asks for all three fields and sends nothing when one is empty", () => {
    weir = serveRecordedReplies({});
    renderSecurity(<ChangePasswordForm />);

    fillPasswords("current-pass-1", "new-pass-one-1", "");
    press("Change password");

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Fill in all three password fields.",
    );
    expect(weir.requests).toEqual([]);
  });

  it("sends the current and the new password, clears the fields and leaves nobody signed in to ask about", async () => {
    weir = serveRecordedReplies({
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${ME}`]: [me, meAfterPasswordChange],
      [`GET ${SESSION}`]: currentSession,
      [`POST ${CHANGE_PASSWORD}`]: passwordChanged,
    });
    markSignedIn();
    renderSecurity(
      <>
        <WhoIsSignedIn />
        <ChangePasswordForm />
      </>,
    );
    await waitFor(() =>
      expect(screen.getByTestId("who")).toHaveTextContent("recorded-admin"),
    );
    await waitFor(() =>
      expect(screen.getByTestId("session")).toHaveTextContent("signed in"),
    );

    fillPasswords("current-pass-1", "new-pass-one-1", "new-pass-one-1");
    press("Change password");

    await waitFor(() =>
      expect(screen.getByTestId("who")).toHaveTextContent("nobody"),
    );
    expect(screen.getByTestId("session")).toHaveTextContent("none");
    expect(weir.sent("POST", CHANGE_PASSWORD).map((r) => r.body)).toEqual([
      {
        csrf_token: CSRF_TOKEN,
        current_password: "current-pass-1",
        new_password: "new-pass-one-1",
      },
    ]);
    expect(field("Enter current password")).toHaveValue("");
    expect(field("Enter new password")).toHaveValue("");
    expect(field("Re-enter new password")).toHaveValue("");
    expect(weir.sent("GET", ME)).toHaveLength(1);
    expect(weir.sent("GET", SESSION)).toHaveLength(1);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it.each([
    [
      "the current password is wrong",
      passwordWrongCurrent,
      "Current password is incorrect.",
    ],
    [
      "the new password is the current one",
      passwordUnchanged,
      "New password must be different from the current password.",
    ],
    [
      "the new password is too short",
      passwordTooShort,
      "New password must be at least 8 characters.",
    ],
    [
      "the new password is too common",
      passwordTooCommon,
      "Password is too common. Choose a stronger password.",
    ],
    [
      "the new password has the username in it",
      passwordContainsUsername,
      "Password must not contain the username.",
    ],
    [
      "the new password has too few different characters",
      passwordNarrowMix,
      "Password must use a wider mix of characters.",
    ],
  ])(
    "says so when %s, and keeps what was typed ready to correct",
    async (_when, refusal, said) => {
      weir = serveRecordedReplies({
        [`GET ${CSRF}`]: csrfToken,
        [`POST ${CHANGE_PASSWORD}`]: refusal,
      });
      renderSecurity(<ChangePasswordForm />);

      fillPasswords("current-pass-1", "new-pass-one-1", "new-pass-one-1");
      press("Change password");

      expect(await screen.findByRole("alert")).toHaveTextContent(said);
      expect(
        screen.getByRole("button", { name: "Change password" }),
      ).toBeEnabled();
      for (const placeholder of [
        "Enter current password",
        "Enter new password",
        "Re-enter new password",
      ]) {
        expect(field(placeholder)).toBeEnabled();
      }
      expect(field("Enter new password")).toHaveValue("new-pass-one-1");
      expect(screen.queryByText(/Password changed/)).toBeNull();
    },
  );

  it("takes a corrected try after a refusal and no longer shows the refusal", async () => {
    weir = serveRecordedReplies({
      [`GET ${CSRF}`]: csrfToken,
      [`POST ${CHANGE_PASSWORD}`]: [passwordWrongCurrent, passwordChanged],
    });
    renderSecurity(<ChangePasswordForm />);
    fillPasswords("wrong-pass-1", "new-pass-one-1", "new-pass-one-1");
    press("Change password");
    await screen.findByRole("alert");

    type("Enter current password", "current-pass-1");
    press("Change password");

    await waitFor(() =>
      expect(field("Enter current password")).toHaveValue(""),
    );
    expect(screen.queryByRole("alert")).toBeNull();
    expect(
      weir
        .sent("POST", CHANGE_PASSWORD)
        .map((r) => (r.body as Record<string, string>).current_password),
    ).toEqual(["wrong-pass-1", "current-pass-1"]);
  });

  it("shows Saving… and holds the fields and their Show buttons while the request is out", async () => {
    const held = heldReply(passwordChanged);
    weir = serveRecordedReplies({
      [`GET ${CSRF}`]: csrfToken,
      [`POST ${CHANGE_PASSWORD}`]: held.answer,
    });
    renderSecurity(<ChangePasswordForm />);
    fillPasswords("current-pass-1", "new-pass-one-1", "new-pass-one-1");

    press("Change password");

    expect(
      await screen.findByRole("button", { name: "Saving…" }),
    ).toBeDisabled();
    for (const placeholder of [
      "Enter current password",
      "Enter new password",
      "Re-enter new password",
    ]) {
      expect(field(placeholder)).toBeDisabled();
    }
    for (const name of [
      "Show password",
      "Show new password",
      "Show password confirmation",
    ]) {
      expect(screen.getByRole("button", { name })).toBeDisabled();
    }
    held.release();
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Change password" }),
      ).toBeEnabled(),
    );
  });
});

describe("Change username", () => {
  it("asks for both fields and sends nothing when one is empty", () => {
    weir = serveRecordedReplies({});
    renderSecurity(<ChangeUsernameForm />);

    type("Enter a new username", "   ");
    type("Confirm it is you", "current-pass-1");
    press("Change username");

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Enter the new username and your current password.",
    );
    expect(weir.requests).toEqual([]);
  });

  it("sends the trimmed name with the password, clears both fields, confirms and shows the new name", async () => {
    weir = serveRecordedReplies({
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${ME}`]: [me, meRenamed],
      [`GET ${SESSION}`]: currentSession,
      [`POST ${CHANGE_USERNAME}`]: usernameChanged,
    });
    renderSecurity(
      <>
        <WhoIsSignedIn />
        <ChangeUsernameForm />
      </>,
    );
    await waitFor(() =>
      expect(screen.getByTestId("who")).toHaveTextContent("recorded-admin"),
    );

    type("Enter a new username", "  renamed-admin  ");
    type("Confirm it is you", "current-pass-1");
    press("Change username");

    expect(
      await screen.findByText(
        "Username changed. Use it the next time you sign in.",
      ),
    ).toHaveAttribute("role", "status");
    expect(weir.sent("POST", CHANGE_USERNAME).map((r) => r.body)).toEqual([
      {
        csrf_token: CSRF_TOKEN,
        current_password: "current-pass-1",
        new_username: "renamed-admin",
      },
    ]);
    expect(field("Enter a new username")).toHaveValue("");
    expect(field("Confirm it is you")).toHaveValue("");
    await waitFor(() =>
      expect(screen.getByTestId("who")).toHaveTextContent("renamed-admin"),
    );
    expect(weir.sent("GET", ME)).toHaveLength(2);
  });

  it.each([
    [
      "the password is wrong",
      usernameWrongPassword,
      "Current password is incorrect.",
    ],
    [
      "the name is the one the account already has",
      usernameUnchanged,
      "New username must be different from the current username.",
    ],
  ])(
    "says so when %s, and keeps what was typed ready to correct",
    async (_when, refusal, said) => {
      weir = serveRecordedReplies({
        [`GET ${CSRF}`]: csrfToken,
        [`POST ${CHANGE_USERNAME}`]: refusal,
      });
      renderSecurity(<ChangeUsernameForm />);

      type("Enter a new username", "renamed-admin");
      type("Confirm it is you", "current-pass-1");
      press("Change username");

      expect(await screen.findByRole("alert")).toHaveTextContent(said);
      expect(
        screen.getByRole("button", { name: "Change username" }),
      ).toBeEnabled();
      expect(field("Enter a new username")).toBeEnabled();
      expect(field("Enter a new username")).toHaveValue("renamed-admin");
      expect(screen.queryByText(/Username changed/)).toBeNull();
    },
  );

  it("shows Saving… and holds the fields and the Show button while the request is out", async () => {
    const held = heldReply(usernameChanged);
    weir = serveRecordedReplies({
      [`GET ${CSRF}`]: csrfToken,
      [`POST ${CHANGE_USERNAME}`]: held.answer,
    });
    renderSecurity(<ChangeUsernameForm />);
    type("Enter a new username", "renamed-admin");
    type("Confirm it is you", "current-pass-1");

    press("Change username");

    expect(
      await screen.findByRole("button", { name: "Saving…" }),
    ).toBeDisabled();
    expect(field("Enter a new username")).toBeDisabled();
    expect(field("Confirm it is you")).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Show current password" }),
    ).toBeDisabled();
    held.release();
    await screen.findByText(
      "Username changed. Use it the next time you sign in.",
    );
  });
});
