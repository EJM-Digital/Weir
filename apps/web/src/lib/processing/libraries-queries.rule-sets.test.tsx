import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { useCreateProcessingRuleSet } from "./libraries-queries";
import { processingKeys } from "./query-keys";
import * as ruleSetsApi from "./rule-sets-api";
import type {
  ProcessingRuleSet,
  ProcessingRuleSetWrite,
} from "./rule-sets-api";

const created = { id: 7, name: "Anime" } as ProcessingRuleSet;
const other = { id: 2, name: "Movies" } as ProcessingRuleSet;

afterEach(() => vi.restoreAllMocks());

async function create(cached: ProcessingRuleSet[] | undefined) {
  vi.spyOn(ruleSetsApi, "createProcessingRuleSet").mockResolvedValue(created);
  vi.spyOn(ruleSetsApi, "fetchProcessingRuleSets").mockResolvedValue([]);
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  if (cached) client.setQueryData(processingKeys.ruleSets, cached);
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useCreateProcessingRuleSet(), {
    wrapper,
  });
  await act(() => result.current.mutateAsync({} as ProcessingRuleSetWrite));
  return client;
}

describe("creating a rules profile", () => {
  it("adds it to the profiles already read, so the page can select it at once", async () => {
    const client = await create([other]);

    expect(client.getQueryData(processingKeys.ruleSets)).toEqual([
      other,
      created,
    ]);
  });

  it("does not add it twice when the list already holds it", async () => {
    const client = await create([other, created]);

    expect(client.getQueryData(processingKeys.ruleSets)).toEqual([
      other,
      created,
    ]);
  });

  it("leaves the list to be read when none has been read yet", async () => {
    const client = await create(undefined);

    expect(client.getQueryData(processingKeys.ruleSets)).toBeUndefined();
  });
});
