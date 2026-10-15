import { focusManager, QueryClient } from "@tanstack/react-query";
import {
  act,
  cleanup,
  fireEvent,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { queryClient as appQueryClient } from "../../../../app/query-client";
import {
  serveRecordedReplies,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import { ActiveSessionsSection } from "./active-sessions-section";
import {
  confirmationExpired,
  csrfToken,
  noOtherSessionsToSignOut,
  oneSession,
  otherSessionsSignedOut,
  sessionAlreadyInactive,
  sessionSignedOut,
  notAuthenticated,
  threeSessions,
  twoSessions,
} from "./security-replies";
import {
  APP_SETTINGS,
  heldReply,
  renderSecurity,
} from "./security-test-support";

const SESSIONS = "/api/v1/auth/sessions";
const REVOKE_OTHERS = "/api/v1/auth/sessions/revoke-others";
const CSRF = "/api/v1/auth/csrf";
const CSRF_TOKEN = (JSON.parse(csrfToken.body) as { csrf_token: string })
  .csrf_token;

/** The browser listed first among the others, which the server lists by most recent use. */
const NEWEST_OTHER = (
  JSON.parse(threeSessions.body) as { items: { session_id: string }[] }
).items[0].session_id;
const REVOKE_NEWEST_OTHER = `${SESSIONS}/${NEWEST_OTHER}/revoke`;

const bodyRows = () => screen.getAllByRole("row").slice(1);
const signOutOthers = () =>
  screen.queryByRole("button", { name: "Sign out other sessions" });

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
  vi.useRealTimers();
  focusManager.setFocused(undefined);
});

describe("Active sessions: the list", () => {
  it("lists every signed-in browser and marks the one in use, which has no Sign out button", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSIONS}`]: threeSessions,
    });
    renderSecurity(<ActiveSessionsSection enabled />);

    await screen.findByTestId("active-sessions");

    expect(bodyRows()).toHaveLength(3);
    const thisBrowser = bodyRows().filter((row) =>
      within(row).queryByText("This browser"),
    );
    expect(thisBrowser).toHaveLength(1);
    expect(within(thisBrowser[0]).queryByRole("button")).toBeNull();
    for (const row of bodyRows().filter((r) => !thisBrowser.includes(r))) {
      expect(
        within(row).getByRole("button", { name: "Sign out" }),
      ).toBeEnabled();
    }
  });

  it("says it is loading until the server answers", async () => {
    const held = heldReply(twoSessions);
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSIONS}`]: held.answer,
    });
    renderSecurity(<ActiveSessionsSection enabled />);

    expect(screen.getByText("Loading active sessions…")).toBeInTheDocument();
    expect(screen.queryByTestId("active-sessions")).toBeNull();

    held.release();
    await screen.findByTestId("active-sessions");
    expect(screen.queryByText("Loading active sessions…")).toBeNull();
  });

  it("says the sign-in has ended when the server refuses the list, not that there are no sessions", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSIONS}`]: notAuthenticated,
    });
    renderSecurity(<ActiveSessionsSection enabled />);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Your sign-in has ended. Sign in again.",
    );
    expect(screen.queryByText("No active sessions were found.")).toBeNull();
    expect(screen.queryByTestId("active-sessions")).toBeNull();
  });

  it("says Weir cannot be reached when the request never arrives", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockRejectedValue(new TypeError("Failed to fetch")),
    );
    renderSecurity(<ActiveSessionsSection enabled />);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir. Check it's still running.",
    );
  });

  it("says no sessions were found when the server lists none, with nothing to sign out or arrange", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSIONS}`]: { status: 200, body: '{"items":[]}' },
    });
    renderSecurity(<ActiveSessionsSection enabled />);

    expect(
      await screen.findByText("No active sessions were found."),
    ).toBeInTheDocument();
    expect(signOutOthers()).toBeNull();
    expect(screen.queryByRole("button", { name: /columns/i })).toBeNull();
  });

  it("asks for nothing while the sign-in it belongs to is not known", () => {
    weir = serveRecordedReplies({});
    renderSecurity(<ActiveSessionsSection enabled={false} />);

    expect(weir.requests).toEqual([]);
  });

  it("is read again when the person comes back to the page, since sessions have no live update", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSIONS}`]: [threeSessions, oneSession],
    });
    const client = new QueryClient({
      defaultOptions: {
        queries: {
          ...appQueryClient.getDefaultOptions().queries,
          retry: false,
        },
      },
    });
    vi.useFakeTimers({ toFake: ["Date"] });
    renderSecurity(<ActiveSessionsSection enabled />, client);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));

    vi.setSystemTime(Date.now() + 31_000);
    await act(async () => {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
    });

    await waitFor(() => expect(bodyRows()).toHaveLength(1));
    expect(weir.sent("GET", SESSIONS)).toHaveLength(2);
  });
});

describe("Active sessions: signing out one browser", () => {
  it("sends that browser's request, says it was signed out and reads the list again", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${SESSIONS}`]: [threeSessions, twoSessions],
      [`POST ${REVOKE_NEWEST_OTHER}`]: sessionSignedOut,
    });
    renderSecurity(<ActiveSessionsSection enabled />);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));

    fireEvent.click(
      within(bodyRows()[0]).getByRole("button", { name: "Sign out" }),
    );

    expect(await screen.findByText("Session signed out.")).toHaveAttribute(
      "role",
      "status",
    );
    const sent = weir.sent("POST", REVOKE_NEWEST_OTHER);
    expect(sent).toHaveLength(1);
    expect(sent[0].headers["x-csrf-token"]).toBe(CSRF_TOKEN);
    await waitFor(() => expect(bodyRows()).toHaveLength(2));
    expect(weir.sent("GET", SESSIONS)).toHaveLength(2);
    expect(within(bodyRows()[1]).getByText("This browser")).toBeInTheDocument();
  });

  it("says the browser may already be signed out when the server no longer has that session", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${SESSIONS}`]: threeSessions,
      [`POST ${REVOKE_NEWEST_OTHER}`]: sessionAlreadyInactive,
    });
    renderSecurity(<ActiveSessionsSection enabled />);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));

    fireEvent.click(
      within(bodyRows()[0]).getByRole("button", { name: "Sign out" }),
    );

    expect(
      await screen.findByText(
        "Could not sign out that session. It may already be inactive.",
      ),
    ).toHaveAttribute("role", "status");
    expect(bodyRows()).toHaveLength(3);
  });

  it("shows Signing out… and holds every Sign out button while the request is out", async () => {
    const held = heldReply(sessionSignedOut);
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${SESSIONS}`]: [threeSessions, twoSessions],
      [`POST ${REVOKE_NEWEST_OTHER}`]: held.answer,
    });
    renderSecurity(<ActiveSessionsSection enabled />);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));

    fireEvent.click(
      within(bodyRows()[0]).getByRole("button", { name: "Sign out" }),
    );

    const busy = await screen.findAllByRole("button", {
      name: "Signing out…",
    });
    expect(busy).toHaveLength(2);
    for (const button of busy) expect(button).toBeDisabled();
    held.release();
    await waitFor(() => expect(bodyRows()).toHaveLength(2));
  });
});

describe("Active sessions: Sign out other sessions", () => {
  it("sends the request, says how many were signed out and leaves this browser listed and signed in", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${SESSIONS}`]: [threeSessions, oneSession],
      [`POST ${REVOKE_OTHERS}`]: otherSessionsSignedOut,
    });
    renderSecurity(<ActiveSessionsSection enabled />);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));

    fireEvent.click(signOutOthers()!);

    expect(
      await screen.findByText("Signed out 1 other session."),
    ).toHaveAttribute("role", "status");
    const sent = weir.sent("POST", REVOKE_OTHERS);
    expect(sent).toHaveLength(1);
    expect(sent[0].headers["x-csrf-token"]).toBe(CSRF_TOKEN);
    await waitFor(() => expect(bodyRows()).toHaveLength(1));
    expect(within(bodyRows()[0]).getByText("This browser")).toBeInTheDocument();
    expect(signOutOthers()).toBeNull();
    expect(
      weir.requests.filter((r) => r.method === "POST").map((r) => r.path),
    ).toEqual([REVOKE_OTHERS]);
  });

  it("is not offered when this is the only browser signed in", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${SESSIONS}`]: oneSession,
    });
    renderSecurity(<ActiveSessionsSection enabled />);

    await screen.findByTestId("active-sessions");

    expect(bodyRows()).toHaveLength(1);
    expect(signOutOthers()).toBeNull();
    expect(screen.queryByRole("button", { name: "Sign out" })).toBeNull();
  });

  it("says what went wrong and leaves the list as it was when the server refuses", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${SESSIONS}`]: threeSessions,
      [`POST ${REVOKE_OTHERS}`]: confirmationExpired,
    });
    renderSecurity(<ActiveSessionsSection enabled />);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));

    fireEvent.click(signOutOthers()!);

    expect(
      await screen.findByText(
        "Could not sign out the other sessions. Refresh and try again.",
      ),
    ).toHaveAttribute("role", "status");
    expect(bodyRows()).toHaveLength(3);
    expect(signOutOthers()).toBeEnabled();
  });

  it("shows Signing out… and holds the button while the request is out", async () => {
    const held = heldReply(otherSessionsSignedOut);
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${SESSIONS}`]: [threeSessions, oneSession],
      [`POST ${REVOKE_OTHERS}`]: held.answer,
    });
    renderSecurity(<ActiveSessionsSection enabled />);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));

    fireEvent.click(signOutOthers()!);

    expect(
      await screen.findByRole("button", { name: "Signing out…" }),
    ).toBeDisabled();
    held.release();
    await waitFor(() => expect(signOutOthers()).toBeNull());
  });

  it("says it signed out none when there was no other browser by then", async () => {
    weir = serveRecordedReplies({
      ...APP_SETTINGS,
      [`GET ${CSRF}`]: csrfToken,
      [`GET ${SESSIONS}`]: [threeSessions, oneSession],
      [`POST ${REVOKE_OTHERS}`]: noOtherSessionsToSignOut,
    });
    renderSecurity(<ActiveSessionsSection enabled />);
    await waitFor(() => expect(bodyRows()).toHaveLength(3));

    fireEvent.click(signOutOthers()!);

    expect(
      await screen.findByText("Signed out 0 other sessions."),
    ).toHaveAttribute("role", "status");
    await waitFor(() => expect(bodyRows()).toHaveLength(1));
  });
});
