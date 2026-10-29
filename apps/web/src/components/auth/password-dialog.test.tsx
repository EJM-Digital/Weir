import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { PasswordDialog } from "./password-dialog";

afterEach(cleanup);

function renderDialog(error: string | null = null, busy = false) {
  const onConfirm = vi.fn();
  const onCancel = vi.fn();
  render(
    <PasswordDialog
      busy={busy}
      error={error}
      onConfirm={onConfirm}
      onCancel={onCancel}
    />,
  );
  return { onConfirm, onCancel };
}

describe("the password dialog", () => {
  it("asks for the current password and hands it over on Confirm", () => {
    const { onConfirm } = renderDialog();
    expect(
      screen.getByRole("dialog", { name: "Enter your password" }),
    ).toBeVisible();
    expect(screen.getByLabelText("Current password")).toHaveFocus();

    fireEvent.change(screen.getByLabelText("Current password"), {
      target: { value: "the-current-password" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(onConfirm).toHaveBeenCalledWith("the-current-password");
  });

  it("sends nothing for an empty box and says what is missing, before any reason the server gave", () => {
    const { onConfirm } = renderDialog("Current password is not correct.");
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Current password is not correct.",
    );

    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(onConfirm).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Enter your current password to make this change.",
    );
  });

  it("cancels, and is held still while a request is out", () => {
    const { onCancel } = renderDialog();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onCancel).toHaveBeenCalledTimes(1);
    cleanup();

    renderDialog(null, true);
    expect(screen.getByRole("button", { name: "Confirm" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
    expect(screen.getByLabelText("Current password")).toBeDisabled();
  });
});
