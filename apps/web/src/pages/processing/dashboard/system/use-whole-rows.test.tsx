import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { useWholeRows } from "./use-whole-rows";

const TILE_PX = 42;
const GAP_PX = 5;

function Tiles({ tiles }: { tiles: number }) {
  const { roomRef, gridRef, height, hidden } = useWholeRows(
    tiles,
    TILE_PX,
    GAP_PX,
  );
  return (
    <div ref={roomRef} data-testid="room">
      <div ref={gridRef} data-testid="grid" style={{ height }} />
      <p data-testid="hidden">{hidden}</p>
    </div>
  );
}

/** A room `roomPx` tall whose grid has `columns` columns, in an environment that measures neither. */
function measuring(roomPx: number, columns: number) {
  vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(
    roomPx,
  );
  vi.spyOn(window, "getComputedStyle").mockReturnValue({
    gridTemplateColumns: Array(columns).fill("80px").join(" "),
  } as CSSStyleDeclaration);
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("the tiles' rows", () => {
  it("makes the grid as tall as the whole rows its room holds", () => {
    measuring(100, 2);
    render(<Tiles tiles={10} />);

    expect(screen.getByTestId("grid").style.height).toBe("89px");
  });

  it("counts the tiles left beyond those rows", () => {
    measuring(100, 2);
    render(<Tiles tiles={10} />);

    expect(screen.getByTestId("hidden")).toHaveTextContent("6");
  });

  it("has none beyond when they all fit", () => {
    measuring(200, 2);
    render(<Tiles tiles={6} />);

    expect(screen.getByTestId("hidden")).toHaveTextContent("0");
  });

  it("keeps one row however little room there is", () => {
    measuring(10, 1);
    render(<Tiles tiles={4} />);

    expect(screen.getByTestId("grid").style.height).toBe("42px");
    expect(screen.getByTestId("hidden")).toHaveTextContent("3");
  });
});

describe("the tiles' rows by the grid's own style", () => {
  it("uses the height and gap the grid's style says", () => {
    vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(100);
    vi.spyOn(window, "getComputedStyle").mockReturnValue({
      gridAutoRows: "30px",
      rowGap: "10px",
      gridTemplateColumns: "80px 80px",
    } as CSSStyleDeclaration);
    render(<Tiles tiles={10} />);

    // Two rows of 30px and a 10px gap fit in 100px; six of the ten tiles are beyond them.
    expect(screen.getByTestId("grid").style.height).toBe("70px");
    expect(screen.getByTestId("hidden")).toHaveTextContent("6");
  });

  it("uses its own sizes where the style says a length that is not in px", () => {
    vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(100);
    vi.spyOn(window, "getComputedStyle").mockReturnValue({
      gridAutoRows: "auto",
      rowGap: "normal",
      gridTemplateColumns: "80px 80px",
    } as CSSStyleDeclaration);
    render(<Tiles tiles={10} />);

    expect(screen.getByTestId("grid").style.height).toBe("89px");
  });

  it("measures nothing when the grid is not on the page", () => {
    function Bare() {
      const { height, hidden } = useWholeRows(3, TILE_PX, GAP_PX);
      return <p data-testid="bare">{`${height}/${hidden}`}</p>;
    }
    render(<Bare />);

    expect(screen.getByTestId("bare")).toHaveTextContent("undefined/0");
  });
});

describe("the tiles' rows as the room changes", () => {
  it("measures again when the room changes size or the fonts arrive, and stops when the page is left", async () => {
    let resize: () => void = () => undefined;
    const disconnect = vi.fn();
    vi.stubGlobal(
      "ResizeObserver",
      class {
        constructor(callback: () => void) {
          resize = callback;
        }
        observe() {}
        disconnect = disconnect;
      },
    );
    let fontsLoaded: () => void = () => undefined;
    Object.defineProperty(document, "fonts", {
      configurable: true,
      value: {
        ready: new Promise<void>((resolve) => {
          fontsLoaded = resolve;
        }),
      },
    });
    const room = vi.spyOn(HTMLElement.prototype, "clientHeight", "get");
    room.mockReturnValue(100);
    vi.spyOn(window, "getComputedStyle").mockReturnValue({
      gridTemplateColumns: "80px 80px",
    } as CSSStyleDeclaration);
    const { unmount } = render(<Tiles tiles={10} />);
    expect(screen.getByTestId("grid").style.height).toBe("89px");

    room.mockReturnValue(200);
    act(() => resize());
    expect(screen.getByTestId("grid").style.height).toBe("183px");

    act(() => resize());
    expect(screen.getByTestId("grid").style.height).toBe("183px");

    room.mockReturnValue(100);
    await act(async () => {
      fontsLoaded();
    });
    expect(screen.getByTestId("grid").style.height).toBe("89px");

    unmount();
    expect(disconnect).toHaveBeenCalled();
    Reflect.deleteProperty(document, "fonts");
  });

  it("does not measure when the fonts arrive after the page has been left", async () => {
    let fontsLoaded: () => void = () => undefined;
    Object.defineProperty(document, "fonts", {
      configurable: true,
      value: {
        ready: new Promise<void>((resolve) => {
          fontsLoaded = resolve;
        }),
      },
    });
    vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(100);
    const style = vi
      .spyOn(window, "getComputedStyle")
      .mockReturnValue({ gridTemplateColumns: "80px" } as CSSStyleDeclaration);
    const { unmount } = render(<Tiles tiles={4} />);
    unmount();
    style.mockClear();

    await act(async () => {
      fontsLoaded();
    });

    expect(style).not.toHaveBeenCalled();
    Reflect.deleteProperty(document, "fonts");
  });
});
