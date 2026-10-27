import { waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";

import { fetchMe } from "../lib/api/auth-api";
import { authKeys } from "../lib/auth/query-keys";
import type { UserPublic } from "../lib/api/types";
import { settingsKeys } from "../lib/settings/query-keys";
import { queryClient } from "./query-client";

const SIGNED_IN: UserPublic = {
  id: 1,
  role: "operator",
  username: "user1",
  app_theme: null,
};
const SETTINGS = { app_timezone: "UTC" };

vi.mock("../lib/api/auth-api", () => ({
  fetchMe: vi.fn(),
}));
vi.mock("../lib/settings/settings-api", () => ({
  fetchAppSettings: vi.fn().mockResolvedValue(SETTINGS),
}));

afterEach(() => {
  queryClient.clear();
  vi.mocked(fetchMe).mockReset();
  vi.restoreAllMocks();
});

it("never asks for settings when nobody is signed in, which the server would refuse", async () => {
  vi.mocked(fetchMe).mockResolvedValue(null);
  const spy = vi.spyOn(queryClient, "prefetchQuery");
  const { prefetchOnBoot } = await import("./prefetch-on-boot");

  prefetchOnBoot();

  await waitFor(() => {
    expect(queryClient.getQueryState(authKeys.me)?.status).toBe("success");
  });
  expect(spy).not.toHaveBeenCalled();
});

it("prefetches settings, under the key useAppSettingsQuery reads, once /auth/me confirms a signed-in user", async () => {
  vi.mocked(fetchMe).mockResolvedValue(SIGNED_IN);
  const { prefetchOnBoot } = await import("./prefetch-on-boot");

  prefetchOnBoot();

  await waitFor(() => {
    expect(queryClient.getQueryData(settingsKeys.app)).toEqual(SETTINGS);
  });
});

it("does not prefetch settings while /auth/me is still in flight", async () => {
  let resolveMe!: (user: UserPublic | null) => void;
  vi.mocked(fetchMe).mockReturnValue(
    new Promise((resolve) => {
      resolveMe = resolve;
    }),
  );
  const spy = vi.spyOn(queryClient, "prefetchQuery");
  const { prefetchOnBoot } = await import("./prefetch-on-boot");

  prefetchOnBoot();
  expect(spy).not.toHaveBeenCalled();

  resolveMe(SIGNED_IN);
  await waitFor(() => {
    expect(spy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: settingsKeys.app }),
    );
  });
});

it("leaves settings alone, and raises nothing, when /auth/me cannot be read", async () => {
  vi.mocked(fetchMe).mockRejectedValue(new Error("offline"));
  const spy = vi.spyOn(queryClient, "prefetchQuery");
  const { prefetchOnBoot } = await import("./prefetch-on-boot");

  prefetchOnBoot();

  await waitFor(() => {
    expect(queryClient.getQueryState(authKeys.me)?.status).toBe("error");
  });
  expect(spy).not.toHaveBeenCalled();
});
