import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { jsonAnswer, stubWeirApi } from "../test/stub-weir-api";
import { held } from "../test/held";
import { queryClient } from "./query-client";
import { AppProviders } from "./providers";
import { AppRouter } from "./router";

/**
 * The app's router and its sign-in gate against a stubbed `fetch`, with the real routes, lazy pages and
 * queries. The router is made once, when the module loads, so each test moves it with the browser's history.
 */

const signedOut = () => jsonAnswer({ detail: "Not authenticated." }, 401);

function goTo(path: string) {
  act(() => {
    window.history.pushState(null, "", path);
    window.dispatchEvent(new PopStateEvent("popstate"));
  });
}

function openApp() {
  return render(
    <AppProviders>
      <AppRouter />
    </AppProviders>,
  );
}

// The page comes down first: clearing the shared query client or unstubbing fetch under a mounted page makes it ask for
// /auth/me again, and that request reaches the network guard in test/setup.ts.
afterEach(() => {
  cleanup();
  queryClient.clear();
  vi.unstubAllGlobals();
  window.history.pushState(null, "", "/");
});

describe("the router before anyone is signed in", () => {
  it("shows Loading while /auth/me has not answered, and does not move off the address", async () => {
    const meAnswers = held<Response>();
    stubWeirApi({ "GET /api/v1/auth/me": () => meAnswers.promise });

    goTo("/activity");
    openApp();

    expect(await screen.findByText("Loading…")).toBeInTheDocument();
    expect(window.location.pathname).toBe("/activity");
    meAnswers.resolve(signedOut());
  });

  it("sends a signed-out visitor to any address that is not a page to the sign-in page", async () => {
    const api = stubWeirApi({
      "GET /api/v1/auth/me": signedOut,
      "GET /api/v1/auth/bootstrap/status": {
        bootstrap_allowed: false,
        reason: "admin_already_exists",
        requires_setup_code: false,
      },
    });

    goTo("/no/such/page");
    openApp();

    expect(await screen.findByTestId("login-username")).toBeInTheDocument();
    expect(window.location.pathname).toBe("/login");
    expect(api.unmatched).toEqual([]);
  });

  it("opens the recovery page for a signed-out visitor without sending them to sign in", async () => {
    const api = stubWeirApi({
      "GET /api/v1/auth/me": signedOut,
      "GET /api/v1/auth/bootstrap/status": {
        bootstrap_allowed: false,
        reason: "admin_already_exists",
        requires_setup_code: false,
      },
    });

    goTo("/recover");
    openApp();

    expect(await screen.findByTestId("recover-code")).toBeInTheDocument();
    expect(window.location.pathname).toBe("/recover");
    expect(api.unmatched).toEqual([]);
  });
});
