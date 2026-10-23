import { render, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { fitRows, useFittingRows } from "./fit-rows";

afterEach(() => vi.restoreAllMocks());

/** A host that ends at `bottom` with rows that end at the given positions (each keeping `padding` under its content). */
function hostWithRows(bottom: number, ends: number[], padding = 0) {
  const host = document.createElement("div");
  host.getBoundingClientRect = () => ({ bottom }) as DOMRect;
  for (const end of ends) {
    const row = document.createElement("div");
    row.setAttribute("data-fit", "");
    row.style.paddingBottom = `${padding}px`;
    row.getBoundingClientRect = () => ({ bottom: end }) as DOMRect;
    host.appendChild(row);
  }
  return { host, rows: Array.from(host.children) as HTMLElement[] };
}

describe("fitting a tile's rows to its height", () => {
  it("counts the whole rows that fit and hides the rest, never cutting a row through", () => {
    const { host, rows } = hostWithRows(100, [40, 80, 120, 160]);

    expect(fitRows(host)).toBe(2);
    expect(rows.map((row) => row.style.visibility)).toEqual([
      "",
      "",
      "hidden",
      "hidden",
    ]);
  });

  it("lets a row's own padding run past the tile, but not its content", () => {
    const { host } = hostWithRows(100, [40, 108], 10);

    expect(fitRows(host)).toBe(2);
    expect(fitRows(hostWithRows(100, [40, 112], 10).host)).toBe(1);
  });

  it("keeps a tinted row whole, its padding included, since the tint would show cut through", () => {
    const { host, rows } = hostWithRows(100, [40, 108], 10);
    rows[1].style.backgroundColor = "rgb(20, 30, 40)";

    expect(fitRows(host)).toBe(1);
  });

  it("allows half a pixel of rounding", () => {
    expect(fitRows(hostWithRows(100, [100.4]).host)).toBe(1);
    expect(fitRows(hostWithRows(100, [100.6]).host)).toBe(0);
  });

  it("shows every row of a tile that grows with its rows", () => {
    const { host, rows } = hostWithRows(10, [40, 80]);
    rows[1].style.visibility = "hidden";

    expect(fitRows(host, false)).toBe(2);
    expect(rows.map((row) => row.style.visibility)).toEqual(["", ""]);
  });

  it("leaves out a heading whose first row does not fit, so a heading is never the last thing shown", () => {
    const { host, rows } = hostWithRows(100, [40, 80, 120]);
    rows[1].setAttribute("data-fit", "with-next");

    expect(fitRows(host)).toBe(1);
    expect(rows.map((row) => row.style.visibility)).toEqual([
      "",
      "hidden",
      "hidden",
    ]);
  });

  it("shows a heading with the row after it when that fits", () => {
    const { host, rows } = hostWithRows(100, [40, 80, 90]);
    rows[1].setAttribute("data-fit", "with-next");

    expect(fitRows(host)).toBe(3);
  });

  it("shows a hidden row again when the tile has grown", () => {
    const { host, rows } = hostWithRows(100, [40, 160]);
    fitRows(host);
    expect(rows[1].style.visibility).toBe("hidden");

    host.getBoundingClientRect = () => ({ bottom: 200 }) as DOMRect;

    expect(fitRows(host)).toBe(2);
    expect(rows[1].style.visibility).toBe("");
  });
});

describe("the hook", () => {
  function Tile({ enabled }: { enabled: boolean }) {
    const [ref, fits] = useFittingRows(enabled);
    return (
      <div ref={ref} data-testid="tile">
        <span data-fit="">one</span>
        <span data-fit="">two</span>
        <output>{fits}</output>
      </div>
    );
  }

  it("says every row fits until the tile has been measured, and counts them once it has", () => {
    const { container } = render(<Tile enabled />);

    // jsdom lays nothing out, so everything ends where the tile does: all of them fit.
    expect(container.querySelector("output")?.textContent).toBe("2");
  });

  it("says every row fits while no tile has been given the host", () => {
    const { result } = renderHook(() => useFittingRows());

    expect(result.current[1]).toBe(Number.MAX_SAFE_INTEGER);
  });
});
