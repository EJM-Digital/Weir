import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { stubActivityStream } from "../../test/fake-activity-stream";
import { activityKeys } from "./query-keys";
import {
  getServerLooks,
  subscribeLiveSignals,
  subscribeSystemChecks,
  useActivityStreamInvalidations,
  useLiveProgress,
} from "./use-activity-stream-invalidation";

function withQueryClient(qc: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

const KEYS = [activityKeys.recent] as const;

function watchActivity(throttleMs?: number) {
  const qc = new QueryClient();
  const invalidated = vi.spyOn(qc, "invalidateQueries");
  const view = renderHook(
    () => useActivityStreamInvalidations(KEYS, { throttleMs }),
    { wrapper: withQueryClient(qc) },
  );
  return { invalidated, view };
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("which activity messages are news", () => {
  it.each([
    [
      "a higher revision of the same entry",
      { latest_event_id: 5, activity_revision: 4 },
      2,
    ],
    [
      "a lower revision of the same entry",
      { latest_event_id: 5, activity_revision: 2 },
      1,
    ],
    ["the same entry with no revision", { latest_event_id: 5 }, 1],
    ["an older entry", { latest_event_id: 4, activity_revision: 9 }, 1],
  ])("treats %s", (_what, message, times) => {
    const stream = stubActivityStream();
    const { invalidated, view } = watchActivity();

    stream.frame("activity.latest", {
      latest_event_id: 5,
      activity_revision: 3,
    });
    stream.frame("activity.latest", message);

    expect(invalidated).toHaveBeenCalledTimes(times);
    view.unmount();
  });

  it("reads an entry with a revision after one without as new", () => {
    const stream = stubActivityStream();
    const { invalidated, view } = watchActivity();

    stream.frame("activity.latest", { latest_event_id: 5 });
    stream.frame("activity.latest", {
      latest_event_id: 5,
      activity_revision: 1,
    });

    expect(invalidated).toHaveBeenCalledTimes(2);
    view.unmount();
  });
});

describe("a throttled screen", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(10_000);
  });

  it("reads again at once, and drops its waiting read, when the wait has passed before its timer fired", () => {
    const stream = stubActivityStream();
    const { invalidated, view } = watchActivity(1_000);

    stream.frame("activity.latest", { latest_event_id: 1 });
    stream.frame("activity.latest", { latest_event_id: 2 });
    expect(invalidated).toHaveBeenCalledTimes(1);

    vi.setSystemTime(12_000);
    stream.frame("activity.latest", { latest_event_id: 3 });
    expect(invalidated).toHaveBeenCalledTimes(2);

    vi.advanceTimersByTime(5_000);
    expect(invalidated).toHaveBeenCalledTimes(2);
    view.unmount();
  });

  it("does not read after the screen has gone", () => {
    const stream = stubActivityStream();
    const { invalidated, view } = watchActivity(1_000);

    stream.frame("activity.latest", { latest_event_id: 1 });
    stream.frame("activity.latest", { latest_event_id: 2 });
    view.unmount();
    vi.advanceTimersByTime(5_000);

    expect(invalidated).toHaveBeenCalledTimes(1);
  });
});

describe("live progress frames it cannot use", () => {
  it("ignores a frame whose files are not a list", () => {
    const stream = stubActivityStream();
    const { result, unmount } = renderHook(() => useLiveProgress());

    stream.frame("processing.progress", { files: "none" });

    expect(result.current).toEqual({});
    unmount();
  });

  it("leaves out an entry that does not say which file it is", () => {
    const stream = stubActivityStream();
    const { result, unmount } = renderHook(() => useLiveProgress());

    act(() => {
      stream.frame("processing.progress", {
        files: [{ status: "processing" }, { relative_path: "A/B.mkv" }],
      });
    });

    expect(Object.keys(result.current)).toEqual(["A/B.mkv"]);
    expect(result.current["A/B.mkv"].status).toBe("processing");
    unmount();
  });
});

describe("what the server says about when it looked", () => {
  it("tells each listener, and keeps the newest times for one that comes later", () => {
    const stream = stubActivityStream();
    const heard = vi.fn();
    const stop = subscribeSystemChecks(heard);

    stream.frame("system.checks", {
      folders_checked_at: "2026-10-02T09:00:00Z",
      readiness_checked_at: null,
    });

    expect(heard).toHaveBeenCalledTimes(1);
    expect(getServerLooks().foldersCheckedAt).toBe(
      Date.parse("2026-10-02T09:00:00Z"),
    );
    expect(getServerLooks().readinessCheckedAt).toBeNull();
    stop();
  });

  it("ignores a frame it cannot read", () => {
    const stream = stubActivityStream();
    const heard = vi.fn();
    const stop = subscribeSystemChecks(heard);

    stream.frame("system.checks", "not an object");

    expect(heard).not.toHaveBeenCalled();
    stop();
  });
});

describe("a server that says hello without an id", () => {
  it("takes a hello with no run id for no news, and a different run id for a restart", () => {
    const stream = stubActivityStream();
    const signals = vi.fn();
    const stop = subscribeLiveSignals(signals);

    stream.frame("server.hello", { boot_id: "run-1" });
    stream.frame("server.hello", { boot_id: "" });
    expect(signals).not.toHaveBeenCalled();

    stream.frame("server.hello", { boot_id: "run-2" });
    expect(signals).toHaveBeenCalledWith({ type: "restarted" });
    stop();
  });
});

describe("a browser that goes offline and online with nobody watching", () => {
  it("opens no stream for nobody", () => {
    stubActivityStream();
    const { view } = watchActivity();
    view.unmount();
    const opened = vi.fn();
    vi.stubGlobal("EventSource", opened);

    window.dispatchEvent(new Event("offline"));
    window.dispatchEvent(new Event("online"));

    expect(opened).not.toHaveBeenCalled();
  });
});
