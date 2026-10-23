import { render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { apiFetch, resetUnauthorizedHandlingForTests } from "../lib/api/client";
import { clearSignedIn, markSignedIn } from "../lib/auth/signed-in-before";
import { AppProviders } from "./providers";

describe("a 401 from the server", () => {
  beforeEach(() => {
    clearSignedIn();
    window.history.replaceState(null, "", "/");
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(new Response("", { status: 401 })),
    );
  });

  afterEach(() => {
    resetUnauthorizedHandlingForTests();
    vi.unstubAllGlobals();
    clearSignedIn();
  });

  async function answered401() {
    render(<AppProviders>{null}</AppProviders>);
    await apiFetch("/api/v1/auth/me");
  }

  it("sends a browser that never had a session to sign-in without saying a session expired", async () => {
    await answered401();

    expect(window.location.pathname).toBe("/login");
    expect(window.location.search).toBe("");
  });

  it.each(["/login", "/setup"])(
    "leaves the address alone on %s, where the person already is",
    async (path) => {
      markSignedIn();
      window.history.replaceState(null, "", path);

      await answered401();

      expect(window.location.pathname).toBe(path);
      expect(window.location.search).toBe("");
    },
  );

  it("tells a browser that did have one that its session expired", async () => {
    markSignedIn();

    await answered401();

    expect(window.location.pathname).toBe("/login");
    expect(window.location.search).toBe("?session=expired");
  });
});
