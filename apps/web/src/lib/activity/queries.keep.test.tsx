import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { activityPage } from "../../test/dashboard-fixtures";
import { held } from "../../test/held";
import { stubWeirApi } from "../../test/stub-weir-api";
import type { ActivityEventItem } from "../api/types";
import { useActivityRecentQuery, useActivityWindowQuery } from "./queries";

const RECENT = "GET /api/v1/activity/recent";
const ENTRY = {
  id: 1,
  created_at: "2026-10-15T10:00:00+00:00",
  module: "processing",
  event_type: "processing.file_remux_pass_completed",
  title: "Entry 1",
} as ActivityEventItem;

afterEach(() => vi.unstubAllGlobals());

function client() {
  const queries = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queries}>{children}</QueryClientProvider>
    );
  };
}

describe("a window that rolls forward", () => {
  it("keeps the window it holds while the next one loads, and the same one at the same workflow", async () => {
    const next = held<unknown>();
    let reads = 0;
    stubWeirApi({
      [RECENT]: () => (++reads === 1 ? activityPage([ENTRY]) : next.promise),
    });
    const { result, rerender } = renderHook(
      ({ from }: { from: string }) =>
        useActivityWindowQuery({ library_id: 3, date_from: from }),
      {
        wrapper: client(),
        initialProps: { from: "2026-10-15T10:00:00+00:00" },
      },
    );
    await waitFor(() => expect(result.current.data).toBeDefined());
    const first = result.current.data;

    rerender({ from: "2026-10-15T10:05:00+00:00" });

    await waitFor(() => expect(reads).toBe(2));
    expect(result.current.data).toBe(first);
    expect(result.current.isPlaceholderData).toBe(true);
    next.resolve(activityPage([]));
    await waitFor(() => expect(result.current.isPlaceholderData).toBe(false));
    expect(result.current.data).not.toBe(first);
  });

  it("shows nothing of another workflow's window while its own loads", async () => {
    const next = held<unknown>();
    let reads = 0;
    stubWeirApi({
      [RECENT]: () => (++reads === 1 ? activityPage([ENTRY]) : next.promise),
    });
    const { result, rerender } = renderHook(
      ({ id }: { id: number }) =>
        useActivityWindowQuery({ library_id: id, date_from: "2026-10-15" }),
      { wrapper: client(), initialProps: { id: 3 } },
    );
    await waitFor(() => expect(result.current.data).toBeDefined());

    rerender({ id: 4 });

    await waitFor(() => expect(reads).toBe(2));
    expect(result.current.data).toBeUndefined();
    next.resolve(activityPage([]));
  });
});

describe("a list whose filter changes", () => {
  it("keeps the rows it holds until the new filter's arrive", async () => {
    const next = held<unknown>();
    let reads = 0;
    stubWeirApi({
      [RECENT]: () => (++reads === 1 ? activityPage([ENTRY]) : next.promise),
    });
    const { result, rerender } = renderHook(
      ({ id }: { id: number }) => useActivityRecentQuery({ library_id: id }),
      { wrapper: client(), initialProps: { id: 3 } },
    );
    await waitFor(() => expect(result.current.data).toBeDefined());
    const first = result.current.data;

    rerender({ id: 4 });

    await waitFor(() => expect(reads).toBe(2));
    expect(result.current.data).toBe(first);
    next.resolve(activityPage([]));
    await waitFor(() => expect(result.current.isPlaceholderData).toBe(false));
  });

  it("can ask for the rows of one subject only, when another's would be wrong", async () => {
    const next = held<unknown>();
    let reads = 0;
    stubWeirApi({
      [RECENT]: () => (++reads === 1 ? activityPage([ENTRY]) : next.promise),
    });
    const { result, rerender } = renderHook(
      ({ file }: { file: string }) =>
        useActivityRecentQuery({ file }, { keepWhileLoading: false }),
      { wrapper: client(), initialProps: { file: "a.mkv" } },
    );
    await waitFor(() => expect(result.current.data).toBeDefined());

    rerender({ file: "b.mkv" });

    await waitFor(() => expect(reads).toBe(2));
    expect(result.current.data).toBeUndefined();
    next.resolve(activityPage([]));
  });
});
