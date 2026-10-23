import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as authApi from "../api/auth-api";
import type { UserPublic } from "../api/types";
import { authKeys } from "./query-keys";
import { useSetThemeMutation } from "./queries";

afterEach(() => vi.restoreAllMocks());

async function chooseDark(cached: UserPublic | null | undefined) {
  vi.spyOn(authApi, "postSetTheme").mockResolvedValue({
    message: "Theme saved.",
    app_theme: "dark",
  });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  if (cached !== undefined) client.setQueryData(authKeys.me, cached);
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useSetThemeMutation(), { wrapper });
  await act(() => result.current.mutateAsync("dark"));
  return client;
}

describe("choosing a theme", () => {
  it("carries it to the signed-in account every screen reads", async () => {
    const me = {
      id: 1,
      username: "alice",
      role: "operator",
      app_theme: "light",
    } as UserPublic;

    const client = await chooseDark(me);

    expect(client.getQueryData(authKeys.me)).toEqual({
      ...me,
      app_theme: "dark",
    });
  });

  it("leaves the account alone when nobody is signed in", async () => {
    const client = await chooseDark(null);

    expect(client.getQueryData(authKeys.me)).toBeNull();
  });
});
