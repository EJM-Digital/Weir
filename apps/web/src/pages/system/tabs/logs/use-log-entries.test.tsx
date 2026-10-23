import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { SystemLogPage } from "../../../../lib/system/system-log-api";
import { EVENT_ROW, JOB_ROW, logPage } from "../../../../test/log-test-support";
import { useLogEntries } from "./use-log-entries";

const mocks = vi.hoisted(() => ({
  fetchSystemLog: vi.fn(),
  deliver: { current: null as (() => void) | null },
  unsubscribe: vi.fn(),
}));

vi.mock("../../../../lib/system/system-log-api", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/system/system-log-api")
  >()),
  fetchSystemLog: mocks.fetchSystemLog,
}));

vi.mock(
  "../../../../lib/activity/use-activity-stream-invalidation",
  async (importOriginal) => ({
    ...(await importOriginal<
      typeof import("../../../../lib/activity/use-activity-stream-invalidation")
    >()),
    useActivityStreamInvalidations: () => undefined,
    subscribeSystemLog: (subscriber: () => void) => {
      mocks.deliver.current = subscriber;
      return mocks.unsubscribe;
    },
  }),
);

const QUERY = {};

function open() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return renderHook(() => useLogEntries(QUERY, false), { wrapper });
}

const page = (over: Partial<SystemLogPage> = {}) =>
  logPage([EVENT_ROW, JOB_ROW], over);

beforeEach(() => {
  mocks.fetchSystemLog.mockReset();
  mocks.unsubscribe.mockReset();
  mocks.deliver.current = null;
});

afterEach(() => vi.useRealTimers());

describe("the log's older pages", () => {
  it("asks for nothing more when the oldest row is already in", async () => {
    mocks.fetchSystemLog.mockResolvedValue(page({ next_cursor: null }));
    const { result } = open();
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => result.current.loadMore());

    expect(result.current.hasMore).toBe(false);
    expect(mocks.fetchSystemLog).toHaveBeenCalledTimes(1);
  });

  it("asks once for the next page when there is one, and not again while it is on its way", async () => {
    mocks.fetchSystemLog.mockResolvedValueOnce(page({ next_cursor: "c1" }));
    mocks.fetchSystemLog.mockReturnValueOnce(new Promise(() => undefined));
    const { result } = open();
    await waitFor(() => expect(result.current.hasMore).toBe(true));

    act(() => result.current.loadMore());
    await waitFor(() => expect(result.current.loadingMore).toBe(true));
    act(() => result.current.loadMore());

    expect(mocks.fetchSystemLog).toHaveBeenCalledTimes(2);
  });
});

describe("reading the newest page again", () => {
  it("keeps the list it has when the new reading fails", async () => {
    mocks.fetchSystemLog.mockResolvedValueOnce(page());
    const { result } = open();
    await waitFor(() => expect(result.current.rows).toHaveLength(2));
    mocks.fetchSystemLog.mockRejectedValue(new Error("down"));

    await act(async () => {
      await result.current.reload();
    });

    expect(result.current.rows).toHaveLength(2);
  });
});

describe("the log before its first page has been read", () => {
  it("has no arrivals to show yet", () => {
    mocks.fetchSystemLog.mockReturnValue(new Promise(() => undefined));
    const { result } = open();

    act(() => result.current.showArrived());

    expect(result.current.rows).toEqual([]);
    expect(result.current.loading).toBe(true);
  });

  it("stays empty when it is read again and the reading fails", async () => {
    mocks.fetchSystemLog.mockRejectedValue(new Error("down"));
    const { result } = open();
    await waitFor(() => expect(result.current.error).not.toBeNull());

    await act(async () => {
      await result.current.reload();
    });

    expect(result.current.rows).toEqual([]);
  });
});

describe("the log following the stream", () => {
  it("stops waiting to read the log again once the screen is gone", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    mocks.fetchSystemLog.mockResolvedValue(page());
    const { unmount } = open();

    act(() => mocks.deliver.current?.());
    act(() => mocks.deliver.current?.());
    const waiting = vi.getTimerCount();
    const cleared = vi.spyOn(window, "clearTimeout");
    unmount();

    expect(mocks.unsubscribe).toHaveBeenCalled();
    expect(cleared).toHaveBeenCalledWith(expect.anything());
    expect(waiting).toBeGreaterThan(0);
  });
});
