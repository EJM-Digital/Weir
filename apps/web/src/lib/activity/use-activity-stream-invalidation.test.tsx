import {
  QueryClient,
  QueryClientProvider,
  QueryObserver,
} from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { activityKeys } from "./query-keys";
import {
  invalidateLive,
  subscribeConnectionActivity,
  subscribeSystemLog,
  subscribeSystemTasks,
  useActivityStreamInvalidations,
  useLiveProgress,
} from "./use-activity-stream-invalidation";
import { processingKeys } from "../processing/query-keys";

class FakeEventSource {
  url: string;
  listeners = new Map<string, Set<(ev: MessageEvent<string>) => void>>();
  closed = false;

  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, cb: (ev: MessageEvent<string>) => void): void {
    const set = this.listeners.get(type) ?? new Set();
    set.add(cb);
    this.listeners.set(type, set);
  }

  removeEventListener(
    type: string,
    cb: (ev: MessageEvent<string>) => void,
  ): void {
    this.listeners.get(type)?.delete(cb);
  }

  close(): void {
    this.closed = true;
  }

  emit(type: string, data: string): void {
    const ev = { data } as MessageEvent<string>;
    this.listeners.get(type)?.forEach((cb) => cb(ev));
  }

  static instances: FakeEventSource[] = [];
}

function withQueryClient(qc: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

function setVisibility(state: DocumentVisibilityState): void {
  vi.spyOn(document, "visibilityState", "get").mockReturnValue(state);
}

const RECENT_KEYS = [activityKeys.recent] as const;
const CANCEL_REFETCH_FALSE = { cancelRefetch: false };

describe("useActivityStreamInvalidations", () => {
  afterEach(() => {
    FakeEventSource.instances = [];
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.dispatchEvent(new Event("visibilitychange"));
  });

  it("coalesces event bursts and invalidates only exact queries, without cancelling an in-flight fetch", () => {
    vi.useFakeTimers();
    vi.setSystemTime(10_000);
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");
    const keys = [processingKeys.overviewStats(), activityKeys.recent] as const;

    renderHook(
      () =>
        useActivityStreamInvalidations(keys, {
          exact: true,
          throttleMs: 1_000,
        }),
      { wrapper: withQueryClient(qc) },
    );

    const src = FakeEventSource.instances[0];
    src.emit("activity.latest", JSON.stringify({ latest_event_id: 1 }));
    src.emit("activity.latest", JSON.stringify({ latest_event_id: 2 }));
    src.emit("activity.latest", JSON.stringify({ latest_event_id: 3 }));

    expect(spy).toHaveBeenCalledTimes(2);
    expect(spy).toHaveBeenCalledWith(
      { queryKey: processingKeys.overviewStats(), exact: true },
      CANCEL_REFETCH_FALSE,
    );
    expect(spy).toHaveBeenCalledWith(
      { queryKey: activityKeys.recent, exact: true },
      CANCEL_REFETCH_FALSE,
    );

    vi.advanceTimersByTime(1_000);
    expect(spy).toHaveBeenCalledTimes(4);
  });

  it("invalidates activity recent query on activity.latest", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");

    renderHook(() => useActivityStreamInvalidations(RECENT_KEYS), {
      wrapper: withQueryClient(qc),
    });

    const src = FakeEventSource.instances[0];
    expect(src.url).toBe("/api/v1/activity/stream");
    src.emit("activity.latest", JSON.stringify({ latest_event_id: 12 }));

    expect(spy).toHaveBeenCalledWith(
      { queryKey: activityKeys.recent, exact: false },
      CANCEL_REFETCH_FALSE,
    );
  });

  it("invalidates when an existing activity row receives a newer revision, even at the same event id", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");

    renderHook(() => useActivityStreamInvalidations(RECENT_KEYS), {
      wrapper: withQueryClient(qc),
    });

    const src = FakeEventSource.instances[0];
    src.emit(
      "activity.latest",
      JSON.stringify({ latest_event_id: 12, activity_revision: 1 }),
    );
    src.emit(
      "activity.latest",
      JSON.stringify({ latest_event_id: 12, activity_revision: 2 }),
    );

    expect(spy).toHaveBeenCalledTimes(2);
  });

  it("refreshes the Processing file lists when a scan finds files, with no reload and no new activity row (#816)", () => {
    vi.useFakeTimers();
    vi.setSystemTime(10_000);
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");
    const lists = [processingKeys.fileList({ limit: 200 })] as const;

    renderHook(
      () => useActivityStreamInvalidations(lists, { throttleMs: 750 }),
      { wrapper: withQueryClient(qc) },
    );
    const src = FakeEventSource.instances[0];
    src.emit(
      "activity.latest",
      JSON.stringify({ latest_event_id: 5, activity_revision: 0 }),
    );
    vi.advanceTimersByTime(1_000);
    spy.mockClear();

    // A scan writes its files in batches, and each batch moves only the revision.
    for (let revision = 1; revision <= 100; revision += 1) {
      src.emit(
        "activity.latest",
        JSON.stringify({ latest_event_id: 5, activity_revision: revision }),
      );
    }

    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy).toHaveBeenCalledWith(
      { queryKey: lists[0], exact: false },
      CANCEL_REFETCH_FALSE,
    );
    vi.advanceTimersByTime(750);
    expect(spy).toHaveBeenCalledTimes(2);
  });

  it("ignores a message that repeats the same event id and revision already seen", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");

    renderHook(() => useActivityStreamInvalidations(RECENT_KEYS), {
      wrapper: withQueryClient(qc),
    });

    const src = FakeEventSource.instances[0];
    src.emit(
      "activity.latest",
      JSON.stringify({ latest_event_id: 12, activity_revision: 2 }),
    );
    src.emit(
      "activity.latest",
      JSON.stringify({ latest_event_id: 12, activity_revision: 2 }),
    );
    src.emit("activity.latest", JSON.stringify({ latest_event_id: 11 }));

    expect(spy).toHaveBeenCalledTimes(1);
  });

  it("ignores malformed stream messages instead of breaking live updates", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");

    renderHook(() => useActivityStreamInvalidations(RECENT_KEYS), {
      wrapper: withQueryClient(qc),
    });

    const src = FakeEventSource.instances[0];
    src.emit("activity.latest", "{");
    src.emit("activity.latest", JSON.stringify({ activity_revision: 1 }));
    src.emit(
      "activity.latest",
      JSON.stringify({ latest_event_id: 12, activity_revision: 2 }),
    );

    expect(spy).toHaveBeenCalledTimes(1);
  });

  it("invalidates the overview stats query on activity.latest", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");

    renderHook(
      () => useActivityStreamInvalidations([processingKeys.overviewStats()]),
      {
        wrapper: withQueryClient(qc),
      },
    );

    const src = FakeEventSource.instances[0];
    src.emit("activity.latest", JSON.stringify({ latest_event_id: 77 }));

    expect(spy).toHaveBeenCalledWith(
      { queryKey: processingKeys.overviewStats(), exact: false },
      CANCEL_REFETCH_FALSE,
    );
  });

  it("shares one EventSource across multiple query subscribers", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");

    const first = renderHook(
      () => useActivityStreamInvalidations(RECENT_KEYS),
      {
        wrapper: withQueryClient(qc),
      },
    );
    const second = renderHook(
      () => useActivityStreamInvalidations([processingKeys.overviewStats()]),
      {
        wrapper: withQueryClient(qc),
      },
    );

    expect(FakeEventSource.instances).toHaveLength(1);
    const src = FakeEventSource.instances[0];
    src.emit("activity.latest", JSON.stringify({ latest_event_id: 88 }));

    expect(spy).toHaveBeenCalledWith(
      { queryKey: activityKeys.recent, exact: false },
      CANCEL_REFETCH_FALSE,
    );
    expect(spy).toHaveBeenCalledWith(
      { queryKey: processingKeys.overviewStats(), exact: false },
      CANCEL_REFETCH_FALSE,
    );

    first.unmount();
    expect(src.closed).toBe(false);

    second.unmount();
    expect(src.closed).toBe(true);
  });

  it("opens a fresh EventSource after all subscribers unmount", async () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();

    const first = renderHook(
      () => useActivityStreamInvalidations(RECENT_KEYS),
      {
        wrapper: withQueryClient(qc),
      },
    );
    first.unmount();
    await waitFor(() => expect(FakeEventSource.instances[0].closed).toBe(true));

    renderHook(() => useActivityStreamInvalidations(RECENT_KEYS), {
      wrapper: withQueryClient(qc),
    });

    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].closed).toBe(false);
  });

  it("keeps the connection and keeps data current while the tab is hidden", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const spy = vi.spyOn(qc, "invalidateQueries");

    renderHook(() => useActivityStreamInvalidations(RECENT_KEYS), {
      wrapper: withQueryClient(qc),
    });
    const src = FakeEventSource.instances[0];

    setVisibility("hidden");
    document.dispatchEvent(new Event("visibilitychange"));
    src.emit("activity.latest", JSON.stringify({ latest_event_id: 7 }));

    expect(src.closed).toBe(false);
    expect(spy).toHaveBeenCalledWith(
      { queryKey: activityKeys.recent, exact: false },
      CANCEL_REFETCH_FALSE,
    );

    setVisibility("visible");
    document.dispatchEvent(new Event("visibilitychange"));

    expect(FakeEventSource.instances).toHaveLength(1);
  });

  it("opens the connection even when the tab starts hidden", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    setVisibility("hidden");

    renderHook(() => useActivityStreamInvalidations(RECENT_KEYS), {
      wrapper: withQueryClient(new QueryClient()),
    });

    expect(FakeEventSource.instances).toHaveLength(1);
    expect(FakeEventSource.instances[0].closed).toBe(false);
  });
});

describe("useLiveProgress", () => {
  afterEach(() => {
    FakeEventSource.instances = [];
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.dispatchEvent(new Event("visibilitychange"));
  });

  it("stores progress while the tab is hidden and shows the latest once it is visible", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    let renders = 0;
    const { result } = renderHook(() => {
      renders += 1;
      return useLiveProgress();
    });
    const src = FakeEventSource.instances[0];
    const frame = (percent: number) =>
      JSON.stringify({
        files: [{ relative_path: "Film/Film.mkv", percent }],
      });

    setVisibility("hidden");
    document.dispatchEvent(new Event("visibilitychange"));
    const rendersBeforeHiding = renders;
    act(() => {
      src.emit("processing.progress", frame(10));
      src.emit("processing.progress", frame(20));
      src.emit("processing.progress", frame(30));
    });
    expect(renders).toBe(rendersBeforeHiding);

    setVisibility("visible");
    act(() => {
      document.dispatchEvent(new Event("visibilitychange"));
    });

    expect(result.current["Film/Film.mkv"]?.percent).toBe(30);
    expect(renders).toBe(rendersBeforeHiding + 1);
  });

  it("starts empty and fills in from the stream's processing.progress frame", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const { result } = renderHook(() => useLiveProgress());
    expect(result.current).toEqual({});

    const src = FakeEventSource.instances[0];
    act(() => {
      src.emit(
        "processing.progress",
        JSON.stringify({
          files: [
            {
              relative_path: "Film/Film.mkv",
              status: "processing",
              stage: "writing",
              percent: 42.5,
              eta_seconds: 12,
              message: "Weir is writing the cleaned-up file.",
              speed: "148x",
              elapsed_seconds: 9,
              removed_audio: ["fre aac 2ch: removed"],
              removed_subtitles: [],
            },
          ],
        }),
      );
    });

    expect(result.current["Film/Film.mkv"]).toEqual({
      relativePath: "Film/Film.mkv",
      status: "processing",
      stage: "writing",
      percent: 42.5,
      etaSeconds: 12,
      message: "Weir is writing the cleaned-up file.",
      speed: "148x",
      elapsedSeconds: 9,
      removedAudio: ["fre aac 2ch: removed"],
      removedSubtitles: [],
    });
  });

  it("replaces the whole snapshot, so a file missing from a later frame disappears", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const { result } = renderHook(() => useLiveProgress());
    const src = FakeEventSource.instances[0];

    act(() => {
      src.emit(
        "processing.progress",
        JSON.stringify({
          files: [
            {
              relative_path: "Film/Film.mkv",
              status: "processing",
              percent: 10,
            },
          ],
        }),
      );
    });
    expect(result.current["Film/Film.mkv"]).toBeDefined();

    act(() => {
      src.emit("processing.progress", JSON.stringify({ files: [] }));
    });

    expect(result.current["Film/Film.mkv"]).toBeUndefined();
  });

  it("ignores a malformed frame instead of breaking live progress", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const { result } = renderHook(() => useLiveProgress());
    const src = FakeEventSource.instances[0];

    act(() => {
      src.emit("processing.progress", "not json");
    });

    expect(result.current).toEqual({});
  });

  it("shares its connection with an invalidation subscriber, and closes only once both are gone", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const qc = new QueryClient();
    const progress = renderHook(() => useLiveProgress());
    const invalidation = renderHook(
      () => useActivityStreamInvalidations([activityKeys.recent]),
      { wrapper: withQueryClient(qc) },
    );

    expect(FakeEventSource.instances).toHaveLength(1);
    const src = FakeEventSource.instances[0];

    invalidation.unmount();
    expect(src.closed).toBe(false);

    progress.unmount();
    expect(src.closed).toBe(true);
  });
});

describe("subscribeConnectionActivity", () => {
  afterEach(() => {
    FakeEventSource.instances = [];
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.dispatchEvent(new Event("visibilitychange"));
  });

  const frame = JSON.stringify({
    kind: "media_manager",
    id: 3,
    phase: "asked",
    direction: "outbound",
    at: "2026-10-02T12:00:00Z",
    ms: null,
  });

  it("hands every connection.activity frame to its subscriber, on the stream the others share", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const seen: unknown[] = [];
    const progress = renderHook(() => useLiveProgress());
    const stop = subscribeConnectionActivity((received) => seen.push(received));

    expect(FakeEventSource.instances).toHaveLength(1);
    FakeEventSource.instances[0].emit("connection.activity", frame);

    expect(seen).toEqual([
      expect.objectContaining({ kind: "media_manager", id: 3, phase: "asked" }),
    ]);
    stop();
    progress.unmount();
  });

  it("ignores a frame it cannot read", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const seen: unknown[] = [];
    const stop = subscribeConnectionActivity((received) => seen.push(received));

    FakeEventSource.instances[0].emit("connection.activity", "not json");

    expect(seen).toEqual([]);
    stop();
  });

  it("keeps the stream open for a subscriber alone, and closes it when the last one leaves", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const stop = subscribeConnectionActivity(() => undefined);
    const src = FakeEventSource.instances[0];

    expect(src.closed).toBe(false);
    stop();

    expect(src.closed).toBe(true);
  });
});

describe("subscribeSystemTasks and subscribeSystemLog", () => {
  afterEach(() => {
    FakeEventSource.instances = [];
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.dispatchEvent(new Event("visibilitychange"));
  });

  const tasksFrame = JSON.stringify([
    {
      key: "scan-1",
      label: "Scan Movies",
      running: true,
      last_run_at: null,
      last_ok: null,
      last_error: null,
      next_run_at: null,
      interval_seconds: null,
    },
  ]);
  const logFrame = JSON.stringify({
    at: "2026-10-02T12:00:00Z",
    level: "WARNING",
    message: "Radarr was slow.",
  });

  it("hands the task list of every system.tasks frame to its subscriber, on the one shared stream", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const seen: unknown[] = [];
    const connections = subscribeConnectionActivity(() => undefined);
    const stop = subscribeSystemTasks((tasks) => seen.push(tasks));

    expect(FakeEventSource.instances).toHaveLength(1);
    FakeEventSource.instances[0].emit("system.tasks", tasksFrame);

    expect(seen).toEqual([
      [expect.objectContaining({ key: "scan-1", running: true })],
    ]);
    stop();
    connections();
  });

  it("hands every system.log frame to its subscriber", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const seen: unknown[] = [];
    const stop = subscribeSystemLog((frame) => seen.push(frame));

    FakeEventSource.instances[0].emit("system.log", logFrame);

    expect(seen).toEqual([
      {
        at: "2026-10-02T12:00:00Z",
        level: "WARNING",
        message: "Radarr was slow.",
      },
    ]);
    stop();
  });

  it("ignores a frame it cannot read", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const tasks: unknown[] = [];
    const lines: unknown[] = [];
    const stopTasks = subscribeSystemTasks((value) => tasks.push(value));
    const stopLog = subscribeSystemLog((value) => lines.push(value));

    FakeEventSource.instances[0].emit("system.tasks", "not json");
    FakeEventSource.instances[0].emit("system.log", "{}");

    expect(tasks).toEqual([]);
    expect(lines).toEqual([]);
    stopTasks();
    stopLog();
  });

  it("keeps the stream open until the last of them leaves", () => {
    vi.stubGlobal(
      "EventSource",
      FakeEventSource as unknown as typeof EventSource,
    );
    const stopTasks = subscribeSystemTasks(() => undefined);
    const stopLog = subscribeSystemLog(() => undefined);
    const src = FakeEventSource.instances[0];

    stopTasks();
    expect(src.closed).toBe(false);
    stopLog();
    expect(src.closed).toBe(true);
  });
});

describe("invalidateLive", () => {
  function watchedQuery(queryFn: () => Promise<number>) {
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const observer = new QueryObserver(qc, { queryKey: ["live"], queryFn });
    const stop = observer.subscribe(() => {});
    return { qc, observer, stop };
  }

  it("reads again once a read already in flight lands, so a change that read began before is not lost", async () => {
    let reads = 0;
    let finishFirst: () => void = () => {};
    const firstRead = new Promise<void>((resolve) => (finishFirst = resolve));
    const { qc, observer, stop } = watchedQuery(async () => {
      reads += 1;
      if (reads === 1) await firstRead;
      return reads;
    });
    await waitFor(() => expect(reads).toBe(1));

    invalidateLive(qc, { queryKey: ["live"] });
    finishFirst();

    await waitFor(() => expect(observer.getCurrentResult().data).toBe(2));
    stop();
  });

  it("reads once when nothing is in flight", async () => {
    let reads = 0;
    const { qc, observer, stop } = watchedQuery(async () => (reads += 1));
    await waitFor(() => expect(observer.getCurrentResult().data).toBe(1));

    invalidateLive(qc, { queryKey: ["live"] });

    await waitFor(() => expect(observer.getCurrentResult().data).toBe(2));
    // A second read would have begun in the very step that landed the first one's answer.
    expect(qc.isFetching()).toBe(0);
    expect(reads).toBe(2);
    stop();
  });
});
