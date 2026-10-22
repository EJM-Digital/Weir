import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { SecurityOverview } from "../../../../lib/settings/types";
import { SignInProtectionSection } from "./sign-in-protection-section";

const HEALTHY: SecurityOverview = {
  session_signing_configured: true,
  sign_in_cookie_https_mode: "auto",
  sign_in_cookie_https_plain: "Matched to each connection.",
  sign_in_cookie_same_site: "Lax",
  standard_session_idle_timeout_plain: "14 days",
  standard_session_absolute_timeout_plain: "90 days",
  trusted_session_idle_timeout_plain: "60 days",
  trusted_session_absolute_timeout_plain: "365 days",
  extra_https_hardening_enabled: true,
  sign_in_attempt_limit: 30,
  sign_in_attempt_window_plain: "1 minute",
  first_time_setup_attempt_limit: 10,
  first_time_setup_attempt_window_plain: "1 hour",
  allowed_browser_origins_count: 1,
  restart_required_note: "Ask whoever runs the server to change these.",
};

function shown(overview: SecurityOverview) {
  render(
    <SignInProtectionSection
      overviewQ={{ data: overview, isError: false } as never}
    />,
  );
  return screen.getByTestId("suite-security-posture");
}

describe("how sign-in is protected", () => {
  it("says every protection is in force, with the note under them, when none needs a look", () => {
    const panel = shown(HEALTHY);

    expect(panel).toHaveTextContent("9 protections in force");
    expect(panel).toHaveTextContent(HEALTHY.restart_required_note);
  });

  it("puts the protections that need a look first, with the note beside them", () => {
    const panel = shown({ ...HEALTHY, session_signing_configured: false });

    expect(panel).toHaveTextContent("1 row needs attention");
    expect(within(panel).getByText(HEALTHY.restart_required_note)).toHaveClass(
      "mm-protection__advice",
    );
  });

  it("shows no note when the server sent none", () => {
    const panel = shown({
      ...HEALTHY,
      session_signing_configured: false,
      restart_required_note: "",
    });

    expect(panel.querySelector(".mm-protection__advice")).toBeNull();
    expect(panel.querySelector(".mm-sys-note")).toBeNull();
  });

  it("shows no note under healthy protections when the server sent none", () => {
    const panel = shown({ ...HEALTHY, restart_required_note: "" });

    expect(panel.querySelector(".mm-sys-note")).toBeNull();
  });
});
