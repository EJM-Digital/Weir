import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { held } from "../../test/held";
import { stubWeirApi, type StubbedRequest } from "../../test/stub-weir-api";
import { useProcessingFilesQuery } from "./files-queries";

afterEach(() => vi.unstubAllGlobals());

describe("the files read when the workflow it is narrowed to changes", () => {
  it("keeps the page on screen until the new workflow's arrives", async () => {
    const next = held<unknown>();
    stubWeirApi({
      "GET /api/v1/processing/files": (request: StubbedRequest) =>
        request.query.get("library_id") === "1"
          ? { files: [], total: 0, status_counts: { unprocessed: 2 } }
          : next.promise,
    });
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const { result, rerender } = renderHook(
      ({ id }: { id: number }) => useProcessingFilesQuery({ library_id: id }),
      { wrapper, initialProps: { id: 1 } },
    );
    await waitFor(() => expect(result.current.data).toBeDefined());
    const first = result.current.data;

    rerender({ id: 2 });

    await waitFor(() => expect(result.current.isFetching).toBe(true));
    expect(result.current.data).toBe(first);
    next.resolve({ files: [], total: 0, status_counts: {} });
  });
});
