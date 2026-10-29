import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { fetchMe } from "../api/auth-api";
import { useLoginMutation, useLogoutMutation } from "./queries";
import {
  putRecoveryPromptAside,
  recoveryPromptPutAside,
} from "./recovery-prompt";
import { useSignOutFailed } from "./sign-out-failed";
import {
  clearSignedIn,
  markSignedIn,
  signedOutPath,
  wasSignedIn,
} from "./signed-in-before";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });

describe("being signed in before", () => {
  beforeEach(() => {
    clearSignedIn();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    clearSignedIn();
  });

  it("is noted whenever Weir answers as a signed-in user, however the page was first filled", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(json({ user: { id: 1, username: "alice" } })),
    );

    await fetchMe();

    expect(wasSignedIn()).toBe(true);
  });

  it("is not noted by a signed-out answer", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({}, 401)));

    await fetchMe();

    expect(wasSignedIn()).toBe(false);
  });
});

describe("signing out on purpose", () => {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={new QueryClient()}>
      {children}
    </QueryClientProvider>
  );

  beforeEach(() => {
    markSignedIn();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    clearSignedIn();
  });

  it("sends the person to a sign-in with no expired message while it happens, then forgets the session", async () => {
    let pathWhileSigningOut = "";
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation((url: string) => {
        if (String(url).endsWith("/auth/csrf")) {
          return Promise.resolve(json({ csrf_token: "t" }));
        }
        pathWhileSigningOut = signedOutPath();
        return Promise.resolve(new Response(null, { status: 204 }));
      }),
    );
    const { result } = renderHook(() => useLogoutMutation(), { wrapper });

    await act(() => result.current.mutateAsync());

    expect(pathWhileSigningOut).toBe("/login");
    expect(wasSignedIn()).toBe(false);
  });

  it("keeps the session on record when signing out fails, and says expired again if it later ends", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockImplementation((url: string) =>
          Promise.resolve(
            String(url).endsWith("/auth/csrf")
              ? json({ csrf_token: "t" })
              : json({ detail: "Could not sign out" }, 500),
          ),
        ),
    );
    const { result } = renderHook(() => useLogoutMutation(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync().catch(() => undefined);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(wasSignedIn()).toBe(true);
    expect(signedOutPath()).toBe("/login?session=expired");
  });

  it("records that signing out failed, and forgets it once the next sign-out goes through", async () => {
    const answer = (status: number) =>
      vi
        .fn()
        .mockImplementation((url: string) =>
          Promise.resolve(
            String(url).endsWith("/auth/csrf")
              ? json({ csrf_token: "t" })
              : json({ detail: "Could not sign out" }, status),
          ),
        );
    vi.stubGlobal("fetch", answer(500));
    const { result } = renderHook(
      () => ({ logout: useLogoutMutation(), failed: useSignOutFailed() }),
      { wrapper },
    );

    await act(async () => {
      await result.current.logout.mutateAsync().catch(() => undefined);
    });
    await waitFor(() => expect(result.current.failed).toBe(true));

    vi.stubGlobal("fetch", answer(200));
    await act(async () => {
      await result.current.logout.mutateAsync().catch(() => undefined);
    });
    await waitFor(() => expect(result.current.failed).toBe(false));
  });
});

describe("signing in after the recovery-codes prompt was put aside", () => {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={new QueryClient()}>
      {children}
    </QueryClientProvider>
  );

  afterEach(() => {
    vi.unstubAllGlobals();
    window.sessionStorage.clear();
    clearSignedIn();
  });

  it("brings the prompt back for the new sign-in", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockImplementation((url: string) =>
          Promise.resolve(
            String(url).endsWith("/auth/csrf")
              ? json({ csrf_token: "t" })
              : json({ user: { id: 1, username: "alice", role: "admin" } }),
          ),
        ),
    );
    putRecoveryPromptAside();
    const { result } = renderHook(() => useLoginMutation(), { wrapper });

    await act(() =>
      result.current.mutateAsync({
        username: "alice",
        password: "a-password",
        trustedDevice: false,
      }),
    );

    expect(recoveryPromptPutAside()).toBe(false);
  });
});
