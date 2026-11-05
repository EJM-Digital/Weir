import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { activityPage, passFinished } from "../../../test/dashboard-fixtures";
import { held } from "../../../test/held";
import { liveViewRoutes, quietWorld } from "../../../test/live-view-server";
import { stubWeirApi } from "../../../test/stub-weir-api";
import { HANDED_BACK_BUCKET_MS } from "../handed-back-model";
import { useHandedBack } from "../use-handed-back";
import { useTodayFigures } from "./use-today-figures";

const RECENT = "GET /api/v1/activity/recent";
const START = Date.parse("2026-10-15T10:00:00Z");

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(START);
});
afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function wrapper() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
  };
}

describe("the rolling five-minute window", () => {
  it("keeps the Today figures on screen while the next window loads", async () => {
    const next = held<unknown>();
    let reads = 0;
    stubWeirApi({
      ...liveViewRoutes(quietWorld()),
      [RECENT]: () =>
        ++reads === 1
          ? activityPage([passFinished(1, "A.mkv", { libraryId: 3 })])
          : next.promise,
    });
    const { result, rerender } = renderHook(
      () => useTodayFigures(3, "download", Date.now()),
      { wrapper: wrapper() },
    );
    await waitFor(() => expect(result.current.figures).toBeDefined());
    const before = result.current.figures;

    vi.setSystemTime(START + HANDED_BACK_BUCKET_MS);
    rerender();

    await waitFor(() => expect(reads).toBe(2));
    expect(before?.cleaned).toBe(1);
    expect(result.current.figures).toEqual(before);
    next.resolve(activityPage([]));
  });

  it("keeps the two-hour chart's files on screen while the next window loads", async () => {
    const next = held<unknown>();
    let reads = 0;
    stubWeirApi({
      ...liveViewRoutes(quietWorld()),
      [RECENT]: () =>
        ++reads <= 2
          ? activityPage([
              passFinished(1, "A.mkv", {
                createdAt: "2026-10-15T09:55:00+00:00",
              }),
            ])
          : next.promise,
    });
    const { result, rerender } = renderHook(
      () => useHandedBack("all", Date.now()),
      { wrapper: wrapper() },
    );
    await waitFor(() => expect(reads).toBe(2));
    await waitFor(() => expect(result.current.partial).toBe(false));
    const before = result.current.handed;
    expect(before.totals.all).toBeGreaterThan(0);

    vi.setSystemTime(START + HANDED_BACK_BUCKET_MS);
    rerender();

    await waitFor(() => expect(reads).toBe(4));
    expect(result.current.handed.buckets.length).toBe(before.buckets.length);
    expect(result.current.handed.totals.all).toBe(before.totals.all);
    next.resolve(activityPage([]));
  });
});
