import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { stubWeirApi } from "../../../test/stub-weir-api";
import { useProcessingJobsInspectionQuery } from "./queries";
import type { ProcessingJobsInspectionFilter } from "./queries";

const INSPECTION = "GET /api/v1/processing/jobs/inspection";

afterEach(() => vi.unstubAllGlobals());

async function statusesAsked(filter: ProcessingJobsInspectionFilter) {
  const api = stubWeirApi({ [INSPECTION]: { items: [] } });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const { result } = renderHook(
    () => useProcessingJobsInspectionQuery(filter),
    {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
      ),
    },
  );
  await waitFor(() => expect(result.current.isSuccess).toBe(true));
  expect(api.unmatched).toEqual([]);
  return api.to(INSPECTION)[0].query.getAll("status");
}

describe("which jobs each filter asks the server for", () => {
  it("asks for every status when the filter is recent", async () => {
    expect(await statusesAsked("recent")).toEqual([]);
  });

  it("asks for the finished and the stuck ones when the filter is terminal", async () => {
    expect(await statusesAsked("terminal")).toEqual([
      "completed",
      "failed",
      "handler_ok_finalize_failed",
    ]);
  });

  it("asks for the queued and the running ones when the filter is active", async () => {
    expect(await statusesAsked("active")).toEqual(["pending", "leased"]);
  });

  it("asks for just that status for any other filter", async () => {
    expect(await statusesAsked("cancelled")).toEqual(["cancelled"]);
  });
});
