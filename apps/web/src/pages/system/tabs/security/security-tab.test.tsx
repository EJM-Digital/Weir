import { cleanup, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  serveRecordedReplies,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  currentSession,
  oneSession,
  securityOverview,
  notAuthenticated,
} from "./security-replies";
import {
  APP_SETTINGS,
  heldReply,
  renderSecurity,
} from "./security-test-support";
import { SecurityTab } from "./security-tab";

const SESSION = "/api/v1/auth/session";
const SESSIONS = "/api/v1/auth/sessions";
const OVERVIEW = "/api/v1/suite/security-overview";

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

const protections = () => screen.getByTestId("suite-security-posture");

describe("Security: how sign-in is protected", () => {
  it("says it is loading until the server describes the protections", async () => {
    const held = heldReply(securityOverview);
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSION}`]: currentSession,
      [`GET ${SESSIONS}`]: oneSession,
      [`GET ${OVERVIEW}`]: held.answer,
    });
    renderSecurity(<SecurityTab />);

    expect(
      within(protections()).getByText("Loading server security overview…"),
    ).toBeInTheDocument();
    expect(within(protections()).queryAllByRole("listitem")).toHaveLength(0);

    held.release();
    expect(
      (await within(protections()).findAllByRole("listitem")).length,
    ).toBeGreaterThan(0);
    expect(
      within(protections()).queryByText("Loading server security overview…"),
    ).toBeNull();
  });

  it("says the sign-in has ended when the server refuses the overview, not that nothing is protected", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSION}`]: currentSession,
      [`GET ${SESSIONS}`]: oneSession,
      [`GET ${OVERVIEW}`]: notAuthenticated,
    });
    renderSecurity(<SecurityTab />);

    expect(await within(protections()).findByRole("alert")).toHaveTextContent(
      "Your sign-in has ended. Sign in again.",
    );
    expect(within(protections()).queryAllByRole("listitem")).toHaveLength(0);
    expect(
      within(protections()).queryByText("Loading server security overview…"),
    ).toBeNull();
  });

  it("says Weir cannot be reached when the request never arrives", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockRejectedValue(new TypeError("Failed to fetch")),
    );
    renderSecurity(<SecurityTab />);

    expect(await within(protections()).findByRole("alert")).toHaveTextContent(
      "Can't reach Weir. Check it's still running.",
    );
  });
});
