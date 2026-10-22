import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { SystemLogFrame } from "../../../../lib/system/system-log-frame";
import { useSystemLog } from "./use-system-log";

const NOW = Date.parse("2026-10-02T12:00:00Z");

type Entry = { timestamp: string; level: string; message: string };
const read =
  vi.fn<(filters: { level?: string }) => Promise<{ items: Entry[] }>>();
let deliver: ((frame: SystemLogFrame) => void) | null = null;

vi.mock("../../../../lib/settings/settings-api", () => ({
  fetchServerLogs: (filters: { level?: string }) => read(filters),
}));
vi.mock("../../../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => ({ data: { app_timezone: "UTC" } }),
}));
vi.mock("../../../../lib/activity/use-activity-stream-invalidation", () => ({
  subscribeSystemLog: (subscriber: (frame: SystemLogFrame) => void) => {
    deliver = subscriber;
    return () => {
      deliver = null;
    };
  },
}));

function wrapper(
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } }),
) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
  };
}

const ERROR: Entry = {
  timestamp: "2026-10-02T09:00:00Z",
  level: "ERROR",
  message: "Disk is full.",
};
const WARNING: Entry = {
  timestamp: "2026-10-02T10:00:00Z",
  level: "WARNING",
  message: "Radarr was slow.",
};
const INFO: Entry = {
  timestamp: "2026-10-02T11:00:00Z",
  level: "INFO",
  message: "Scan finished.",
};
const YESTERDAY: Entry = {
  timestamp: "2026-10-01T11:00:00Z",
  level: "ERROR",
  message: "Yesterday's failure.",
};

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(NOW);
  deliver = null;
  read.mockReset();
  read.mockImplementation(async ({ level }) => {
    const all = [INFO, WARNING, ERROR, YESTERDAY];
    return {
      items: level ? all.filter((entry) => entry.level === level) : all,
    };
  });
});

afterEach(() => {
  vi.useRealTimers();
});

describe("useSystemLog", () => {
  it("reads the log, newest first, and counts today's errors and warnings", async () => {
    const { result } = renderHook(() => useSystemLog(), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.loading).toBe(false));

    expect(result.current.lines.map((line) => line.message)).toEqual([
      "Scan finished.",
      "Radarr was slow.",
      "Disk is full.",
      "Yesterday's failure.",
    ]);
    expect(result.current.counts).toEqual({ errors: 1, warnings: 1 });
  });

  it("reads errors and warnings on their own, so their counts survive a log full of information", async () => {
    renderHook(() => useSystemLog(), { wrapper: wrapper() });

    await waitFor(() => expect(read).toHaveBeenCalledTimes(3));

    expect(read.mock.calls.map(([filters]) => filters.level)).toEqual(
      expect.arrayContaining([undefined, "ERROR", "WARNING"]),
    );
  });

  it("puts a line the stream pushes at the top, marked as just arrived, and counts it", async () => {
    const { result } = renderHook(() => useSystemLog(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => {
      deliver?.({
        at: "2026-10-02T11:59:59Z",
        level: "ERROR",
        message: "Another failure.",
      });
    });

    expect(result.current.lines[0]).toMatchObject({
      message: "Another failure.",
      level: "error",
      arrivedAt: NOW,
    });
    expect(result.current.counts.errors).toBe(2);
  });

  it("puts an information line the stream pushes at the top, as it does a warning", async () => {
    const { result } = renderHook(() => useSystemLog(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => {
      deliver?.({
        at: "2026-10-02T11:59:59Z",
        level: "INFO",
        message: "Scan of Movies finished.",
      });
    });

    expect(result.current.lines[0]).toMatchObject({
      message: "Scan of Movies finished.",
      level: "info",
      arrivedAt: NOW,
    });
    expect(result.current.counts).toEqual({ errors: 1, warnings: 1 });
  });

  it("does not read the log again on a timer: the stream carries what is new", async () => {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const { result } = renderHook(() => useSystemLog(), {
      wrapper: wrapper(client),
    });
    await waitFor(() => expect(result.current.loading).toBe(false));

    const intervals = client
      .getQueryCache()
      .findAll({ queryKey: ["system", "log"] })
      .map((query) => query.observers[0].options.refetchInterval);

    expect(intervals).toHaveLength(3);
    expect(intervals.every((interval) => !interval)).toBe(true);
  });

  it("leaves out a logged line whose time cannot be read", async () => {
    read.mockImplementation(async () => ({
      items: [INFO, { ...ERROR, timestamp: "no time" }],
    }));
    const { result } = renderHook(() => useSystemLog(), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.loading).toBe(false));

    expect(result.current.lines.map((line) => line.message)).toEqual([
      "Scan finished.",
    ]);
  });

  it("ignores a line the stream pushes with a time that cannot be read", async () => {
    const { result } = renderHook(() => useSystemLog(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.loading).toBe(false));
    const before = result.current.lines.length;

    act(() => {
      deliver?.({ at: "no time", level: "ERROR", message: "Lost." });
    });

    expect(result.current.lines).toHaveLength(before);
  });

  it("lists a line once when the log later holds the one the stream pushed", async () => {
    const { result } = renderHook(() => useSystemLog(), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => {
      deliver?.({
        at: ERROR.timestamp,
        level: "ERROR",
        message: ERROR.message,
      });
    });

    expect(
      result.current.lines.filter((line) => line.message === "Disk is full."),
    ).toHaveLength(1);
    expect(result.current.counts.errors).toBe(1);
  });

  it("says it failed when every read failed", async () => {
    read.mockRejectedValue(new Error("down"));
    const { result } = renderHook(() => useSystemLog(), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.failed).toBe(true));
    expect(result.current.lines).toEqual([]);
  });
});
