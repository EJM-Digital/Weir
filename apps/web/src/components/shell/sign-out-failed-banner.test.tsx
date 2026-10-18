import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import {
  clearSignOutFailed,
  markSignOutFailed,
} from "../../lib/auth/sign-out-failed";
import { SignOutFailedBanner } from "./sign-out-failed-banner";

afterEach(() => act(() => clearSignOutFailed()));

describe("SignOutFailedBanner", () => {
  it("says nothing while no sign-out has failed", () => {
    render(<SignOutFailedBanner />);

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("says the person is still signed in once a sign-out has failed, and goes when they dismiss it", () => {
    render(<SignOutFailedBanner />);

    act(() => markSignOutFailed());

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Weir couldn't sign you out, so you're still signed in. Try Sign out again.",
    );
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
