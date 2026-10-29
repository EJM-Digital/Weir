import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  csrfToken,
  currentPasswordWrong,
  newRecoveryCodes,
  newRecoveryCodesList,
  recoveryCodesConfirmed,
  recoveryCodesNone,
  recoveryCodesNotSaved,
  recoveryCodesOneLeft,
  recoveryCodesSaved,
} from "../../../../test/recovery-replies";
import { renderSecurity } from "../../../../test/security-test-support";
import { RecoveryCodesSection } from "./recovery-codes-section";

const CSRF = "GET /api/v1/auth/csrf";
const STATUS = "GET /api/v1/auth/recovery-codes";
const MAKE = "POST /api/v1/auth/recovery-codes";
const CONFIRM = "POST /api/v1/auth/recovery-codes/confirm";

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

function serve(
  status: RecordedReply | RecordedReply[],
  make: RecordedReply | (() => Promise<RecordedReply>) = newRecoveryCodes,
) {
  weir = serveRecordedReplies({
    [CSRF]: csrfToken,
    [STATUS]: status,
    [MAKE]: make,
    [CONFIRM]: recoveryCodesConfirmed,
  });
  renderSecurity(<RecoveryCodesSection />);
  return weir;
}

const panel = () => screen.getByTestId("suite-security-recovery");

function openDialog() {
  fireEvent.click(screen.getByTestId("recovery-make"));
  return screen.getByRole("dialog", { name: "Enter your password" });
}

function makeNewCodes(password = "the-current-password") {
  const dialog = openDialog();
  fireEvent.change(within(dialog).getByLabelText("Current password"), {
    target: { value: password },
  });
  fireEvent.click(within(dialog).getByRole("button", { name: "Confirm" }));
}

describe("System › Security: recovery codes", () => {
  it("says how many codes are left, and that without the password and every code there is no way back in", async () => {
    serve(recoveryCodesSaved);

    expect(await screen.findByTestId("recovery-left")).toHaveTextContent(
      "10 of 10 recovery codes left.",
    );
    expect(screen.queryByTestId("recovery-nudge")).toBeNull();
    expect(panel()).toHaveTextContent("fresh install with a new data folder");
  });

  it("counts one code in the singular", async () => {
    serve(recoveryCodesOneLeft);

    expect(await screen.findByTestId("recovery-left")).toHaveTextContent(
      "1 of 10 recovery codes left.",
    );
  });

  it("nudges an account with no codes to make a set", async () => {
    serve(recoveryCodesNone);

    const nudge = await screen.findByTestId("recovery-nudge");
    expect(nudge).toHaveTextContent("No recovery codes yet.");
    expect(nudge).toHaveTextContent("Weir keeps asking until there is one");
    expect(screen.queryByTestId("recovery-left")).toBeNull();
  });

  it("nudges again when the codes made were never marked as saved", async () => {
    serve(recoveryCodesNotSaved);

    expect(await screen.findByTestId("recovery-nudge")).toHaveTextContent(
      "Codes were made but never marked as saved.",
    );
  });

  it("says it could not load the codes when Weir does not answer", async () => {
    serve({
      status: 500,
      body: '{"detail":"Something went wrong inside Weir."}',
    });

    expect(await within(panel()).findByRole("alert")).toHaveTextContent(
      "recovery codes",
    );
  });

  it("makes new codes with the current password asked for in a dialog, shows them once, and counts them as unsaved until the person says so", async () => {
    const server = serve([recoveryCodesSaved, recoveryCodesNotSaved]);
    await screen.findByTestId("recovery-left");

    makeNewCodes();

    expect(await screen.findByTestId("recovery-codes")).toBeVisible();
    expect(
      screen.getAllByRole("listitem").map((item) => item.textContent),
    ).toEqual(newRecoveryCodesList);
    expect(server.sent("POST", "/api/v1/auth/recovery-codes")).toEqual([
      expect.objectContaining({
        body: {
          csrf_token: "csrf.anonymous.auMOaA.2XNpaZ88IDzKnV9V3SO4ykv4tlo",
          current_password: "the-current-password",
        },
      }),
    ]);
    expect(screen.queryByTestId("recovery-make")).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("goes back to the count once the new set is marked as saved", async () => {
    const server = serve(recoveryCodesSaved);
    await screen.findByTestId("recovery-left");
    makeNewCodes();
    await screen.findByTestId("recovery-codes");

    fireEvent.click(screen.getByTestId("recovery-codes-saved"));
    expect(screen.getByRole("button", { name: "Done" })).toBeVisible();
    fireEvent.click(screen.getByTestId("recovery-codes-continue"));

    expect(await screen.findByTestId("recovery-left")).toHaveTextContent(
      "10 of 10 recovery codes left.",
    );
    expect(
      server.sent("POST", "/api/v1/auth/recovery-codes/confirm"),
    ).toHaveLength(1);
    expect(screen.queryByTestId("recovery-codes")).toBeNull();
  });

  it("asks for the password before sending anything, in the words every password gate uses", async () => {
    const server = serve(recoveryCodesSaved);
    await screen.findByTestId("recovery-left");
    const dialog = openDialog();

    fireEvent.click(within(dialog).getByRole("button", { name: "Confirm" }));

    expect(within(dialog).getByRole("alert")).toHaveTextContent(
      "Enter your current password to make this change.",
    );
    expect(server.sent("POST", "/api/v1/auth/recovery-codes")).toEqual([]);
  });

  it("closes the dialog on Cancel and sends nothing", async () => {
    const server = serve(recoveryCodesSaved);
    await screen.findByTestId("recovery-left");
    const dialog = openDialog();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(server.sent("POST", "/api/v1/auth/recovery-codes")).toEqual([]);
  });

  it("shows Weir's own reason for a wrong password and keeps the old codes", async () => {
    serve(recoveryCodesSaved, currentPasswordWrong);
    await screen.findByTestId("recovery-left");

    makeNewCodes("not-it");

    expect(await within(panel()).findByRole("alert")).toHaveTextContent(
      "Current password is not correct.",
    );
    expect(screen.getByRole("dialog")).toBeVisible();
    expect(screen.queryByTestId("recovery-codes")).toBeNull();
    expect(screen.getByTestId("recovery-left")).toBeVisible();
  });

  it("keeps the dialog busy while Weir works and cannot be confirmed twice", async () => {
    let answer: (reply: RecordedReply) => void = () => {};
    const server = serve(
      recoveryCodesSaved,
      () => new Promise<RecordedReply>((resolve) => (answer = resolve)),
    );
    await screen.findByTestId("recovery-left");

    makeNewCodes();

    const confirm = await screen.findByRole("button", { name: "Confirm" });
    expect(confirm).toBeDisabled();
    answer(newRecoveryCodes);
    await screen.findByTestId("recovery-codes");
    expect(server.sent("POST", "/api/v1/auth/recovery-codes")).toHaveLength(1);
  });

  it("offers Make recovery codes when there are none, and Make new codes when there are", async () => {
    serve(recoveryCodesNone);
    expect(await screen.findByTestId("recovery-make")).toHaveTextContent(
      "Make recovery codes",
    );
    cleanup();
    serve(recoveryCodesSaved);
    await screen.findByTestId("recovery-left");
    expect(screen.getByTestId("recovery-make")).toHaveTextContent(
      "Make new codes",
    );
  });
});
