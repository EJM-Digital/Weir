import { act, render, renderHook, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { useAnimationFrame } from "./use-animation-frame";
import { useElementSize } from "./use-element-size";
import { useRemPx } from "./use-rem-px";

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.documentElement.style.fontSize = "";
});

describe("useElementSize", () => {
  class FakeObserver {
    static instances: FakeObserver[] = [];
    disconnected = false;
    constructor(readonly callback: () => void) {
      FakeObserver.instances.push(this);
    }
    observe() {}
    disconnect() {
      this.disconnected = true;
    }
  }

  function Probe() {
    const [ref, size] = useElementSize<HTMLDivElement>();
    return (
      <div ref={ref} data-testid="probe">
        {size.width}x{size.height}
      </div>
    );
  }

  function boxIs(width: number, height: number) {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({
      width,
      height,
    } as DOMRect);
  }

  it("measures in whole pixels before the first paint, and again whenever the element changes size", () => {
    FakeObserver.instances = [];
    vi.stubGlobal("ResizeObserver", FakeObserver);
    boxIs(300.4, 40.6);
    render(<Probe />);
    expect(screen.getByTestId("probe")).toHaveTextContent("300x41");

    boxIs(120.2, 80);
    act(() => FakeObserver.instances[0]!.callback());

    expect(screen.getByTestId("probe")).toHaveTextContent("120x80");
  });

  it("stops watching when the element goes", () => {
    FakeObserver.instances = [];
    vi.stubGlobal("ResizeObserver", FakeObserver);
    boxIs(10, 10);
    const { unmount } = render(<Probe />);

    unmount();

    expect(FakeObserver.instances[0]!.disconnected).toBe(true);
  });

  it("stays 0 by 0 where the browser cannot watch an element", () => {
    vi.stubGlobal("ResizeObserver", undefined);
    boxIs(300, 40);

    render(<Probe />);

    expect(screen.getByTestId("probe")).toHaveTextContent("0x0");
  });
});

describe("useAnimationFrame", () => {
  function frames() {
    const pending = new Map<number, (time: number) => void>();
    let next = 1;
    vi.stubGlobal(
      "requestAnimationFrame",
      (callback: (time: number) => void) => {
        pending.set(next, callback);
        return next++;
      },
    );
    vi.stubGlobal("cancelAnimationFrame", (handle: number) =>
      pending.delete(handle),
    );
    return {
      pending,
      tick(time: number) {
        const due = [...pending.values()];
        pending.clear();
        act(() => due.forEach((callback) => callback(time)));
      },
    };
  }

  it("calls with each frame's time and asks for the next, calling the newest callback without starting over", () => {
    const loop = frames();
    const first = vi.fn();
    const second = vi.fn();
    const { rerender } = renderHook(({ on }) => useAnimationFrame(on), {
      initialProps: { on: first },
    });

    loop.tick(16);
    rerender({ on: second });
    loop.tick(32);

    expect(first.mock.calls).toEqual([[16]]);
    expect(second.mock.calls).toEqual([[32]]);
    expect(loop.pending.size).toBe(1);
  });

  it("asks for no more frames once it is gone", () => {
    const loop = frames();
    const { unmount } = renderHook(() => useAnimationFrame(vi.fn()));
    expect(loop.pending.size).toBe(1);

    unmount();

    expect(loop.pending.size).toBe(0);
  });
});

describe("useRemPx", () => {
  it("reads the size of one rem from the page", () => {
    document.documentElement.style.fontSize = "18px";

    const { result } = renderHook(() => useRemPx());

    expect(result.current).toBe(18);
  });

  it("takes 16 when the page says nothing usable", () => {
    vi.spyOn(window, "getComputedStyle").mockReturnValue({
      fontSize: "",
    } as CSSStyleDeclaration);

    const { result } = renderHook(() => useRemPx());

    expect(result.current).toBe(16);
  });
});
