import { act, render, renderHook, screen } from "@testing-library/react";
import { useRef, useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { useElementSize } from "./use-element-size";
import { useFitLevels } from "./use-fit-levels";
import { useFitToScreen } from "./use-fit-to-screen";
import { useHoldFit } from "./fit-hold";
import { useNow } from "./use-now";

class FakeObserver {
  static instances: FakeObserver[] = [];
  constructor(readonly callback: () => void) {
    FakeObserver.instances.push(this);
  }
  observe() {}
  disconnect() {}
}

function setHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", {
    configurable: true,
    get: () => hidden,
  });
  document.dispatchEvent(new Event("visibilitychange"));
}

afterEach(() => {
  FakeObserver.instances = [];
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  setHidden(false);
});

describe("the clock while the tab comes and goes", () => {
  it.each<[string, boolean[]]>([
    ["starts hidden, then shown twice", [true, false, false]],
    ["is hidden twice", [false, true, true]],
  ])("keeps one clock when it %s", (_name, visits) => {
    vi.useFakeTimers();
    setHidden(visits[0]);
    const { result, unmount } = renderHook(() => useNow(1000));
    const first = result.current;
    visits.slice(1).forEach((hidden) => act(() => setHidden(hidden)));
    act(() => {
      vi.advanceTimersByTime(3000);
    });
    const running = !visits[visits.length - 1];
    expect(result.current > first).toBe(running);
    unmount();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("does not start a clock for a tab nobody is looking at, and stops cleanly", () => {
    vi.useFakeTimers();
    setHidden(true);
    const { unmount } = renderHook(() => useNow(1000));
    expect(vi.getTimerCount()).toBe(0);
    unmount();
  });
});

describe("the size of an element", () => {
  function Probe() {
    const [ref, size] = useElementSize<HTMLDivElement>();
    return (
      <div ref={ref} data-testid="probe">
        {size.width}x{size.height}
      </div>
    );
  }

  it("ignores a change that arrives after the element has gone, and a change that is no change", () => {
    vi.stubGlobal("ResizeObserver", FakeObserver);
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({
      width: 50,
      height: 20,
    } as DOMRect);
    const { unmount } = render(<Probe />);
    act(() => FakeObserver.instances[0].callback());
    expect(screen.getByTestId("probe")).toHaveTextContent("50x20");
    unmount();
    expect(() => FakeObserver.instances[0].callback()).not.toThrow();
  });
});

describe("a page fitted to the window", () => {
  function Page({ inMain }: { inMain: boolean }) {
    const ref = useRef<HTMLDivElement>(null);
    useFitToScreen(ref, true, 100);
    const page = <div ref={ref} data-testid="page" />;
    return inMain ? <main>{page}</main> : page;
  }

  it.each([true, false])(
    "is fitted, and left alone once gone, with a main area %s",
    async (inMain) => {
      vi.stubGlobal("ResizeObserver", FakeObserver);
      let ready: () => void = () => undefined;
      Object.defineProperty(document, "fonts", {
        configurable: true,
        value: { ready: new Promise<void>((resolve) => (ready = resolve)) },
      });
      const { unmount } = render(<Page inMain={inMain} />);
      expect(FakeObserver.instances).toHaveLength(1);
      const page = screen.getByTestId("page");
      expect(page.style.height).not.toBe("");
      unmount();
      ready();
      await Promise.resolve();
      expect(page.style.height).toBe("");
      Reflect.deleteProperty(document, "fonts");
    },
  );
});

describe("a row that refits to its room", () => {
  function Row({ holding = false }: { holding?: boolean }) {
    const [row, setRow] = useState<HTMLDivElement | null>(null);
    useHoldFit(holding);
    const level = useFitLevels(row, 3, "", true);
    return (
      <header>
        <div ref={setRow} data-testid="row">
          {level}
        </div>
      </header>
    );
  }

  it("leaves the level alone when the room reports the width it already had", () => {
    vi.stubGlobal("ResizeObserver", FakeObserver);
    render(<Row />);
    act(() => FakeObserver.instances[0].callback());
    expect(screen.getByTestId("row")).toHaveTextContent("0");
  });

  it("has nothing to refit when a popover closes and the room never changed", () => {
    vi.stubGlobal("ResizeObserver", FakeObserver);
    const { rerender } = render(<Row holding />);
    rerender(<Row />);
    expect(screen.getByTestId("row")).toHaveTextContent("0");
  });
});
