import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ConnectionActivityFrame } from "./connection-activity";
import { connectionRowMeaning } from "./connection-lights";

const stopListening = vi.fn();
const subscribeConnectionActivity = vi.fn(() => stopListening);
const motionAllowed = vi.fn(() => true);

vi.mock("../activity/use-activity-stream-invalidation", () => ({
  subscribeConnectionActivity: () => subscribeConnectionActivity(),
}));
vi.mock("../ui/motion-allowed", () => ({
  motionAllowed: () => motionAllowed(),
}));

const NOW = Date.parse("2026-10-02T12:00:00Z");
const KEY = "media_manager:3";

const frame = (
  overrides: Partial<ConnectionActivityFrame> = {},
): ConnectionActivityFrame => ({
  kind: "media_manager",
  id: 3,
  phase: "asked",
  direction: "outbound",
  at: new Date(NOW).toISOString(),
  ms: null,
  ...overrides,
});

/** A fresh store for each test: the lights live in the module. */
async function openStore() {
  vi.resetModules();
  const store = await import("./connection-lights");
  const hook = renderHook(() => store.useConnectionActivity());
  const send = (overrides?: Partial<ConnectionActivityFrame>) => {
    act(() => {
      store.noteConnectionActivity(frame(overrides));
    });
  };
  return { store, hook, send };
}

function advance(ms: number) {
  act(() => {
    vi.advanceTimersByTime(ms);
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
  motionAllowed.mockReturnValue(true);
  subscribeConnectionActivity.mockClear();
  stopListening.mockClear();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("a connection's light", () => {
  it("is on while Weir's call is out", async () => {
    const { hook, send } = await openStore();

    send();

    expect(hook.result.current.lights.get(KEY)).toBe("asking");
  });

  it("flashes green when the call is answered, then goes out", async () => {
    const { store, hook, send } = await openStore();
    send();

    send({ phase: "answered", ms: 84 });
    expect(hook.result.current.lights.get(KEY)).toBe("answered");
    advance(store.FLASH_MS);

    expect(hook.result.current.lights.has(KEY)).toBe(false);
  });

  it("flashes red when the call fails", async () => {
    const { hook, send } = await openStore();

    send({ phase: "failed", ms: 1200 });

    expect(hook.result.current.lights.get(KEY)).toBe("failed");
  });

  it("flashes green when a connection calls Weir", async () => {
    const { hook, send } = await openStore();

    send({ phase: "answered", direction: "inbound" });

    expect(hook.result.current.lights.get(KEY)).toBe("answered");
  });

  it("stays blue when a connection calls Weir in the middle of Weir's own call", async () => {
    const { hook, send } = await openStore();
    send();

    send({ phase: "answered", direction: "inbound" });

    expect(hook.result.current.lights.get(KEY)).toBe("asking");
  });

  it("is one light however many calls there are: the newest replaces it, and one timer ends it", async () => {
    const { store, hook, send } = await openStore();

    send();
    send({ phase: "answered", ms: 50 });
    advance(store.FLASH_MS - 1);
    send({ phase: "failed", ms: 900 });
    advance(store.FLASH_MS - 1);

    expect(hook.result.current.lights.get(KEY)).toBe("failed");
    expect(vi.getTimerCount()).toBe(1);
    advance(1);
    expect(hook.result.current.lights.size).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("gives up on a call that never says how it ended", async () => {
    const { store, hook, send } = await openStore();

    send();
    advance(store.ASKING_GIVES_UP_MS);

    expect(hook.result.current.lights.has(KEY)).toBe(false);
  });

  it("is never lit while the tab is hidden or motion is reduced, and puts a light out", async () => {
    const { hook, send } = await openStore();
    send();
    motionAllowed.mockReturnValue(false);

    send({ phase: "answered", ms: 84 });

    expect(hook.result.current.lights.size).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe("the answers the stream gave", () => {
  it("are kept when a call ends, with how long it took", async () => {
    const { hook, send } = await openStore();

    send({ phase: "answered", ms: 84 });

    expect(hook.result.current.answers.get(KEY)).toEqual({
      at: NOW,
      ms: 84,
      ok: true,
    });
  });

  it("say a failed call did not get an answer", async () => {
    const { hook, send } = await openStore();

    send({ phase: "failed", ms: 1200 });

    expect(hook.result.current.answers.get(KEY)?.ok).toBe(false);
  });

  it("say nothing about Weir's own calls when a connection called Weir", async () => {
    const { hook, send } = await openStore();

    send({ phase: "answered", direction: "inbound" });

    expect(hook.result.current.answers.get(KEY)).toEqual({
      at: NOW,
      ms: null,
      ok: null,
    });
  });

  it("are not made by a call that has only begun", async () => {
    const { hook, send } = await openStore();

    send();

    expect(hook.result.current.answers.size).toBe(0);
  });
});

describe("listening to the stream", () => {
  it("starts with the first screen that shows a connection and stops with the last, taking every light with it", async () => {
    const { hook, send } = await openStore();
    send();
    expect(subscribeConnectionActivity).toHaveBeenCalledTimes(1);

    hook.unmount();

    expect(stopListening).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe("what a connection's row shows", () => {
  it("is what its state means while no light is on", () => {
    expect(connectionRowMeaning("ok", null)).toBe("done");
    expect(connectionRowMeaning("down", null)).toBe("broken");
  });

  it("is what the light means while one is on, whatever the state", () => {
    expect(connectionRowMeaning("down", "asking")).toBe("doing");
    expect(connectionRowMeaning("ok", "failed")).toBe("broken");
  });
});

describe("lights that were never on, and screens that share the stream", () => {
  it("shows no light, and starts no timer, for a call that comes while motion is reduced", async () => {
    motionAllowed.mockReturnValue(false);
    const { hook, send } = await openStore();

    send();

    expect(hook.result.current.lights.size).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it("keeps listening to the stream while another screen still shows a connection", async () => {
    const { store, hook } = await openStore();
    const second = renderHook(() => store.useConnectionActivity());

    second.unmount();
    expect(stopListening).not.toHaveBeenCalled();

    hook.unmount();
    expect(stopListening).toHaveBeenCalledTimes(1);
  });
});
