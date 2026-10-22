import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { activityPage } from "../../test/dashboard-fixtures";
import {
  stubWeirApi,
  type StubbedApi,
  type StubbedRequest,
} from "../../test/stub-weir-api";
import type { ActivityEventItem } from "../api/types";
import { useActivityWindowQuery } from "./queries";

const RECENT = "GET /api/v1/activity/recent";
const PAGE_SIZE = 100;
const PASSES = {
  event_type: "processing.file_remux_pass_completed",
  library_id: 3,
};

let api: StubbedApi;

function entry(id: number): ActivityEventItem {
  return {
    id,
    created_at: "2026-10-15T10:00:00+00:00",
    module: "processing",
    event_type: PASSES.event_type,
    title: `Entry ${id}`,
  };
}

/**
 * A server holding `count` matching entries numbered 1..count, which answers as Activity does: newest first, one page
 * of at most `limit`, `has_more` while older ones remain, and the total on the first page only.
 */
function serverHolding(count: number) {
  api = stubWeirApi({
    [RECENT]: (request: StubbedRequest) => {
      const limit = Number(request.query.get("limit"));
      const before = request.query.get("before_id");
      const newest = before === null ? count : Number(before) - 1;
      const ids = Array.from(
        { length: Math.max(0, Math.min(limit, newest)) },
        (_, step) => newest - step,
      );
      return activityPage(ids.map(entry), {
        has_more: newest - ids.length > 0,
        total: before === null ? count : 0,
      });
    },
  });
}

function windowOf(pages?: number, options?: { enabled?: boolean }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return renderHook(() => useActivityWindowQuery(PASSES, pages, options), {
    wrapper,
  });
}

const askedFor = () => api.to(RECENT).map((request) => request.query);

afterEach(() => {
  expect(api.unmatched).toEqual([]);
  vi.unstubAllGlobals();
});

describe("the Activity window the Today figures count from", () => {
  it("reads one page of 100 with the window's filters when everything fits on it", async () => {
    serverHolding(40);

    const { result } = windowOf();

    await waitFor(() => expect(result.current.data?.complete).toBe(true));
    expect(result.current.data?.items).toHaveLength(40);
    expect(result.current.data?.total).toBe(40);
    const asked = askedFor();
    expect(asked).toHaveLength(1);
    expect(asked[0].get("limit")).toBe("100");
    expect(asked[0].get("event_type")).toBe(PASSES.event_type);
    expect(asked[0].get("library_id")).toBe("3");
    expect(asked[0].has("before_id")).toBe(false);
  });

  it("walks back from the oldest id of each page until the server says no more", async () => {
    serverHolding(250);

    const { result } = windowOf();

    await waitFor(() => expect(result.current.data?.complete).toBe(true));
    const asked = askedFor();
    expect(asked.map((query) => query.get("before_id"))).toEqual([
      null,
      "151",
      "51",
    ]);
    const ids = result.current.data?.items.map((item) => item.id) ?? [];
    expect(ids).toHaveLength(250);
    expect(ids[0]).toBe(250);
    expect(ids.at(-1)).toBe(1);
    expect(new Set(ids).size).toBe(250);
  });

  it("takes the total from the first page, since later pages leave it out", async () => {
    serverHolding(250);

    const { result } = windowOf();

    await waitFor(() => expect(result.current.data?.complete).toBe(true));
    expect(result.current.data?.total).toBe(250);
  });

  it("is complete when the last page is exactly full and the server has nothing older", async () => {
    serverHolding(PAGE_SIZE * 2);

    const { result } = windowOf();

    await waitFor(() => expect(result.current.data?.complete).toBe(true));
    expect(result.current.data?.items).toHaveLength(PAGE_SIZE * 2);
    expect(askedFor()).toHaveLength(2);
  });

  it("stops after five pages and says the window is not complete", async () => {
    serverHolding(PAGE_SIZE * 7);

    const { result } = windowOf();

    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(askedFor()).toHaveLength(5);
    expect(result.current.data?.complete).toBe(false);
    expect(result.current.data?.items).toHaveLength(PAGE_SIZE * 5);
    expect(result.current.data?.total).toBe(PAGE_SIZE * 7);
  });

  it("is complete when the window holds exactly as many pages as the cap", async () => {
    serverHolding(PAGE_SIZE * 5);

    const { result } = windowOf();

    await waitFor(() => expect(result.current.data?.complete).toBe(true));
    expect(askedFor()).toHaveLength(5);
  });

  it("reads only one page when asked for a count alone, and still reports the whole count", async () => {
    serverHolding(PAGE_SIZE * 3);

    const { result } = windowOf(1);

    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(askedFor()).toHaveLength(1);
    expect(result.current.data?.total).toBe(PAGE_SIZE * 3);
    expect(result.current.data?.complete).toBe(false);
  });

  it("stops at an empty page even when the server still says there is more", async () => {
    api = stubWeirApi({
      [RECENT]: (request: StubbedRequest) =>
        request.query.has("before_id")
          ? activityPage([], { has_more: true, total: 0 })
          : activityPage([entry(9)], { has_more: true, total: 9 }),
    });

    const { result } = windowOf();

    await waitFor(() => expect(result.current.data?.complete).toBe(true));
    expect(askedFor()).toHaveLength(2);
    expect(result.current.data?.items.map((item) => item.id)).toEqual([9]);
  });

  it("reads nothing while it is switched off", () => {
    serverHolding(10);

    const { result } = windowOf(undefined, { enabled: false });

    expect(result.current.fetchStatus).toBe("idle");
    expect(api.requests).toEqual([]);
  });
});

describe("a window when the server lists no entries", () => {
  it("is empty, with the total the server gave, when the reply has no list at all", async () => {
    api = stubWeirApi({ [RECENT]: { total: 0, has_more: false } });

    const { result } = windowOf();

    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(result.current.data).toEqual({
      items: [],
      total: 0,
      complete: true,
    });
  });
});
