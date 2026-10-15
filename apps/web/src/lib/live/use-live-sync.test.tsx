import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { pauseKeys } from "../pause/query-keys";
import { processingKeys } from "../processing/query-keys";
import { STREAM_SILENCE_LIMIT_MS } from "../activity/use-activity-stream-invalidation";
import { authKeys } from "../auth/query-keys";
import { getLiveConnection } from "./live-connection";
import { useLiveSync } from "./use-live-sync";

class FakeEventSource {
  static instances: FakeEventSource[] = [];

  readyState = 0;
  closed = false;
  private listeners = new Map<
    string,
    Set<(ev: MessageEvent<string>) => void>
  >();

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, cb: (ev: MessageEvent<string>) => void): void {
    this.listeners.set(type, (this.listeners.get(type) ?? new Set()).add(cb));
  }

  close(): void {
    this.closed = true;
    this.readyState = 2;
  }

  emit(type: string, data = ""): void {
    const ev = { data } as MessageEvent<string>;
    this.listeners.get(type)?.forEach((cb) => cb(ev));
  }

  open(): void {
    this.readyState = 1;
    this.emit("open");
  }

  /** The browser keeps trying by itself. */
  drop(): void {
    this.readyState = 0;
    this.emit("error");
  }

  /** The server refused the stream: the browser gives up. */
  refused(): void {
    this.readyState = 2;
    this.emit("error");
  }

  hello(bootId: string): void {
    this.emit("server.hello", JSON.stringify({ boot_id: bootId }));
  }

  changed(topic: string): void {
    this.emit("data.changed", JSON.stringify({ topic }));
  }
}

const current = () => FakeEventSource.instances.at(-1)!;

function mountSync() {
  const qc = new QueryClient();
  const invalidate = vi.spyOn(qc, "invalidateQueries");
  const view = renderHook(() => useLiveSync(), {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    ),
  });
  return { qc, invalidate, ...view };
}

/** The query keys that were invalidated, the way a screen would see it. */
const invalidatedKeys = (spy: ReturnType<typeof mountSync>["invalidate"]) =>
  spy.mock.calls.map(([filters]) => filters?.queryKey);

beforeEach(() => {
  vi.stubGlobal("EventSource", FakeEventSource);
});

afterEach(() => {
  FakeEventSource.instances = [];
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("useLiveSync", () => {
  it("opens the one shared stream", () => {
    mountSync();

    expect(FakeEventSource.instances).toHaveLength(1);
    expect(current().url).toBe("/api/v1/activity/stream");
  });

  it("reads again the queries of a topic the server says changed, and no others", () => {
    const { invalidate } = mountSync();
    current().open();
    current().hello("boot-1");

    current().changed("pause");

    expect(invalidatedKeys(invalidate)).toEqual([
      pauseKeys.state,
      processingKeys.filesAtOnce,
    ]);
  });

  it("reads again each query a topic covers", () => {
    const { invalidate } = mountSync();
    current().open();

    current().changed("files_at_once");
    current().changed("maintenance");

    expect(invalidatedKeys(invalidate)).toEqual([
      processingKeys.filesAtOnce,
      processingKeys.maintenance,
    ]);
  });

  it("reads the playback devices and the files again when the chosen devices change in another browser", () => {
    const { invalidate } = mountSync();
    current().open();

    current().changed("direct_play_devices");

    expect(invalidatedKeys(invalidate)).toEqual([
      processingKeys.directPlayDevices,
      processingKeys.files,
    ]);
  });

  it("ignores a topic it does not know and a frame it cannot read", () => {
    const { invalidate } = mountSync();
    current().open();

    current().changed("from_a_newer_server");
    current().emit("data.changed", "not json");

    expect(invalidate).not.toHaveBeenCalled();
  });

  it("reads every query again when the connection comes back after being lost", () => {
    const { invalidate } = mountSync();
    current().open();
    current().hello("boot-1");
    current().drop();
    expect(invalidate).not.toHaveBeenCalled();

    current().open();

    expect(invalidatedKeys(invalidate)).toEqual([[]]);
  });

  it("makes every active query stale again, whatever its key", async () => {
    const { qc } = mountSync();
    const read = vi.fn().mockResolvedValue("ok");
    await qc.fetchQuery({ queryKey: ["anything", 1], queryFn: read });
    await qc.fetchQuery({ queryKey: ["something", "else"], queryFn: read });
    current().open();
    current().drop();

    act(() => current().open());

    expect(qc.getQueryState(["anything", 1])?.isInvalidated).toBe(true);
    expect(qc.getQueryState(["something", "else"])?.isInvalidated).toBe(true);
  });

  it("does nothing extra when the first connection simply opens", () => {
    const { invalidate } = mountSync();

    current().open();
    current().hello("boot-1");

    expect(invalidate).not.toHaveBeenCalled();
  });

  it("treats a different boot id after a reconnect as a restart, which checks the build but does not read everything a second time", async () => {
    const check = vi.fn(async () => new Response("", { status: 502 }));
    vi.stubGlobal("fetch", check);
    const { invalidate } = mountSync();
    current().open();
    current().hello("boot-1");
    current().drop();
    current().open();
    expect(invalidatedKeys(invalidate)).toEqual([[]]);

    current().hello("boot-2");

    await vi.waitFor(() => expect(check).toHaveBeenCalledTimes(1));
    expect(invalidatedKeys(invalidate)).toEqual([[]]);
  });

  it("does not call the same boot id a restart", () => {
    const { invalidate } = mountSync();
    current().open();
    current().hello("boot-1");
    current().drop();
    current().open();
    invalidate.mockClear();

    current().hello("boot-1");

    expect(invalidate).not.toHaveBeenCalled();
  });

  it("reloads the page on a restart only when the server now serves a different build", async () => {
    vi.useFakeTimers();
    const reload = vi.fn();
    vi.stubGlobal("location", { ...window.location, reload });
    document.head.innerHTML =
      '<script type="module" src="/assets/a.js"></script>';
    const serve = (src: string) =>
      vi.stubGlobal(
        "fetch",
        vi.fn(
          async () =>
            new Response(
              `<html><head><script type="module" src="${src}"></script></head></html>`,
            ),
        ),
      );
    mountSync();
    current().open();
    current().hello("boot-1");

    serve("/assets/a.js");
    current().hello("boot-2");
    await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
    await vi.advanceTimersByTimeAsync(0);
    expect(reload).not.toHaveBeenCalled();

    serve("/assets/b.js");
    current().hello("boot-3");
    await vi.waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    document.head.innerHTML = "";
  });

  it("opens the stream again after the browser gives up on it, and catches up", () => {
    vi.useFakeTimers();
    const { invalidate } = mountSync();
    current().open();

    current().refused();
    expect(getLiveConnection().status).toBe("lost");
    expect(FakeEventSource.instances).toHaveLength(1);

    vi.advanceTimersByTime(5_000);
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(current().closed).toBe(false);

    current().open();
    expect(getLiveConnection().status).toBe("live");
    expect(invalidatedKeys(invalidate)).toEqual([authKeys.me, []]);
  });

  it("asks whether the session has ended when the server refuses the stream, and stops trying once the shell is gone", () => {
    vi.useFakeTimers();
    const { invalidate, unmount } = mountSync();
    current().open();

    current().refused();
    expect(invalidatedKeys(invalidate)).toEqual([authKeys.me]);

    unmount();
    vi.advanceTimersByTime(60_000);

    expect(FakeEventSource.instances).toHaveLength(1);
  });

  it("does not ask about the session when the browser merely loses the connection", () => {
    const { invalidate } = mountSync();
    current().open();

    current().drop();

    expect(invalidate).not.toHaveBeenCalled();
  });

  describe("when the stream goes silent", () => {
    const hideTab = () =>
      vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    const showTab = () => {
      vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");
      act(() => void document.dispatchEvent(new Event("visibilitychange")));
    };

    afterEach(() => vi.restoreAllMocks());

    it("drops it once nothing has been heard for the limit, says so, and opens a fresh one", () => {
      vi.useFakeTimers();
      mountSync();
      const first = current();
      first.open();

      act(() => void vi.advanceTimersByTime(STREAM_SILENCE_LIMIT_MS - 5_000));
      expect(first.closed).toBe(false);
      expect(getLiveConnection().status).toBe("live");

      act(() => void vi.advanceTimersByTime(10_000));
      expect(first.closed).toBe(true);
      expect(getLiveConnection()).toEqual({
        status: "lost",
        hasBeenLive: true,
      });

      act(() => void vi.advanceTimersByTime(5_000));
      expect(FakeEventSource.instances).toHaveLength(2);
      expect(current().closed).toBe(false);
    });

    it("keeps a stream that keeps sending frames, whatever the frames are", () => {
      vi.useFakeTimers();
      mountSync();
      current().open();

      for (let i = 0; i < 20; i++) {
        act(() => void vi.advanceTimersByTime(STREAM_SILENCE_LIMIT_MS / 2));
        current().emit("system.stats", "not even json");
      }

      expect(FakeEventSource.instances).toHaveLength(1);
      expect(current().closed).toBe(false);
      expect(getLiveConnection().status).toBe("live");
    });

    it("does not judge a stream while the tab is hidden, and judges it the moment the tab is shown", () => {
      vi.useFakeTimers();
      mountSync();
      const first = current();
      first.open();
      hideTab();

      act(() => void vi.advanceTimersByTime(STREAM_SILENCE_LIMIT_MS * 3));
      expect(first.closed).toBe(false);
      expect(getLiveConnection().status).toBe("live");

      showTab();
      expect(first.closed).toBe(true);
      expect(getLiveConnection().status).toBe("lost");
    });
  });

  it("does not trust the stream once the browser says it is offline, and starts afresh when it is back", () => {
    const { invalidate } = mountSync();
    const first = current();
    first.open();
    first.hello("boot-1");

    act(() => void window.dispatchEvent(new Event("offline")));
    expect(first.closed).toBe(true);
    expect(getLiveConnection()).toEqual({ status: "lost", hasBeenLive: true });
    expect(FakeEventSource.instances).toHaveLength(1);

    act(() => void window.dispatchEvent(new Event("online")));
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(invalidate).not.toHaveBeenCalled();

    current().open();
    expect(getLiveConnection().status).toBe("live");
    expect(invalidatedKeys(invalidate)).toEqual([[]]);
  });

  it("lets go of the stream when the shell goes away", () => {
    const { unmount } = mountSync();
    const stream = current();

    unmount();

    expect(stream.closed).toBe(true);
    expect(getLiveConnection().status).toBe("connecting");
  });
});
