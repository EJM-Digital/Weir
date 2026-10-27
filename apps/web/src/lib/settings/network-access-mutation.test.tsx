import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { settingsKeys } from "./query-keys";
import { useNetworkAccessMutation } from "./queries";
import * as settingsApi from "./settings-api";
import type { NetworkAccessStatus } from "./types";

afterEach(() => vi.restoreAllMocks());

describe("useNetworkAccessMutation", () => {
  it("sends the choice and shows the answer under the network-access key at once", async () => {
    const answer = { state: "this_pc_only" } as unknown as NetworkAccessStatus;
    const put = vi
      .spyOn(settingsApi, "putNetworkAccess")
      .mockResolvedValue(answer);
    const client = new QueryClient({
      defaultOptions: { mutations: { retry: false } },
    });
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useNetworkAccessMutation(), {
      wrapper,
    });

    await act(() => result.current.mutateAsync({ scope: "network" }));

    expect(put).toHaveBeenCalledWith({ scope: "network" });
    expect(client.getQueryData(settingsKeys.networkAccess)).toBe(answer);
  });
});
