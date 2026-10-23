import {
  act,
  fireEvent,
  render,
  renderHook,
  screen,
} from "@testing-library/react";
import { useRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { SETTLE_MS } from "./drag-motion";
import { useDragReorder } from "./use-drag-reorder";

const ROW_HEIGHT = 100;

/**
 * Lays the list's items out one under another in the order the page draws them, each ROW_HEIGHT tall, and shifts each by
 * the offset it has been given, as a browser's measurement does.
 */
function layOutInDrawnOrder() {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(
    function (this: HTMLElement) {
      const index = this.parentElement
        ? Array.from(this.parentElement.children).indexOf(this)
        : 0;
      const offset = /translateY\((-?[\d.]+)px\)/.exec(this.style.transform);
      const top = 100 + index * ROW_HEIGHT + Number(offset?.[1] ?? 0);
      return {
        top,
        bottom: top + ROW_HEIGHT,
        left: 0,
        right: 300,
        width: 300,
        height: ROW_HEIGHT,
        x: 0,
        y: top,
        toJSON: () => ({}),
      };
    },
  );
}

function Rows({
  threshold = 0,
  onDrop,
}: {
  threshold?: number;
  onDrop: (order: number[]) => void;
}) {
  const rows = useRef(new Map<number, HTMLElement>());
  const drag = useDragReorder<number>({
    axis: "y",
    ids: [1, 2, 3],
    elementsOf: (id) => {
      const row = rows.current.get(id);
      return row ? [row] : [];
    },
    threshold,
    onDrop: (order, clear) => {
      onDrop(order);
      clear();
    },
  });
  return (
    <ul>
      {drag.order.map((id) => (
        <li
          key={id}
          data-testid={`row-${id}`}
          ref={(row) => {
            if (row) rows.current.set(id, row);
            else rows.current.delete(id);
          }}
        >
          <button
            type="button"
            aria-pressed={drag.heldId === id}
            onPointerDown={(event) => {
              event.preventDefault();
              drag.press(id, event);
            }}
          >
            Move {id}
          </button>
        </li>
      ))}
    </ul>
  );
}

function order() {
  return screen
    .getAllByRole("listitem")
    .map((row) => row.getAttribute("data-testid"));
}

function press(id: number, y: number) {
  fireEvent.pointerDown(screen.getByRole("button", { name: `Move ${id}` }), {
    button: 0,
    clientX: 10,
    clientY: y,
  });
}

function move(y: number) {
  fireEvent.pointerMove(window, { clientX: 10, clientY: y });
}

function release() {
  fireEvent.pointerUp(window);
  act(() => {
    vi.advanceTimersByTime(SETTLE_MS);
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  layOutInDrawnOrder();
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("dragging an item with the pointer", () => {
  it("follows the pointer exactly, within the list, and lifts the item while it is held", () => {
    render(<Rows onDrop={vi.fn()} />);

    press(1, 150);
    move(250);

    const row = screen.getByTestId("row-1");
    expect(row.style.transform).toBe("translateY(100px)");
    expect(row).toHaveAttribute("data-dragging");
    expect(document.documentElement).toHaveClass("mm-dragging");

    move(2000);
    expect(row.style.transform).toBe("translateY(0px)");
  });

  it("makes way for the held item as it passes the middle of the next one, and no sooner", () => {
    render(<Rows onDrop={vi.fn()} />);

    press(1, 150);
    move(190);
    expect(order()).toEqual(["row-1", "row-2", "row-3"]);

    move(260);
    expect(order()).toEqual(["row-2", "row-1", "row-3"]);

    move(120);
    expect(order()).toEqual(["row-1", "row-2", "row-3"]);
  });

  it("reports the new order once the item has settled, and clears the lift", () => {
    const onDrop = vi.fn();
    render(<Rows onDrop={onDrop} />);

    press(1, 150);
    move(360);
    fireEvent.pointerUp(window);
    expect(onDrop).not.toHaveBeenCalled();
    act(() => {
      vi.advanceTimersByTime(SETTLE_MS);
    });

    expect(onDrop).toHaveBeenCalledWith([2, 3, 1]);
    expect(screen.getByTestId("row-1")).not.toHaveAttribute("data-dragging");
    expect(screen.getByTestId("row-1").style.transform).toBe("");
    expect(document.documentElement).not.toHaveClass("mm-dragging");
  });

  it("reports nothing for an item dropped where it began", () => {
    const onDrop = vi.fn();
    render(<Rows onDrop={onDrop} />);

    press(2, 250);
    move(270);
    release();

    expect(onDrop).not.toHaveBeenCalled();
    expect(order()).toEqual(["row-1", "row-2", "row-3"]);
  });

  it("puts everything back on Escape and reports nothing", () => {
    const onDrop = vi.fn();
    render(<Rows onDrop={onDrop} />);

    press(1, 150);
    move(360);
    expect(order()).toEqual(["row-2", "row-3", "row-1"]);
    fireEvent.keyDown(window, { key: "Escape" });
    act(() => {
      vi.advanceTimersByTime(SETTLE_MS);
    });

    expect(order()).toEqual(["row-1", "row-2", "row-3"]);
    expect(onDrop).not.toHaveBeenCalled();
    expect(document.documentElement).not.toHaveClass("mm-dragging");
  });

  it("puts everything back when the browser takes the pointer away", () => {
    const onDrop = vi.fn();
    render(<Rows onDrop={onDrop} />);

    press(1, 150);
    move(360);
    fireEvent.pointerCancel(window);

    expect(order()).toEqual(["row-1", "row-2", "row-3"]);
    expect(onDrop).not.toHaveBeenCalled();
  });

  it("is not a drag until the pointer has travelled the distance asked for", () => {
    const onDrop = vi.fn();
    render(<Rows threshold={6} onDrop={onDrop} />);

    press(1, 150);
    move(153);
    expect(document.documentElement).not.toHaveClass("mm-dragging");
    fireEvent.pointerUp(window);

    expect(onDrop).not.toHaveBeenCalled();
    expect(screen.getByTestId("row-1")).not.toHaveAttribute("data-dragging");

    press(1, 150);
    move(160);
    expect(document.documentElement).toHaveClass("mm-dragging");
    release();
  });

  it("does not leave the grip focused or looking pressed after a drop", () => {
    render(<Rows onDrop={vi.fn()} />);
    const grip = screen.getByRole("button", { name: "Move 1" });
    grip.focus();

    press(1, 150);
    move(360);
    release();

    const dropped = screen.getByRole("button", { name: "Move 1" });
    expect(dropped).not.toHaveFocus();
    expect(dropped).toHaveAttribute("aria-pressed", "false");
  });

  it("swallows the click a drag ends with, so letting go over a button does not press it", () => {
    const pressed = vi.fn();
    document.addEventListener("click", pressed);
    render(<Rows onDrop={vi.fn()} />);

    press(1, 150);
    move(360);
    fireEvent.pointerUp(window);
    fireEvent.click(screen.getByRole("button", { name: "Move 1" }));
    act(() => {
      vi.advanceTimersByTime(SETTLE_MS);
    });

    document.removeEventListener("click", pressed);
    expect(pressed).not.toHaveBeenCalled();
  });
});

describe("an item that is not on screen", () => {
  it("is left out of the slide when the order changes around it", () => {
    const slideHome = vi.fn();
    const onScreen = document.createElement("li");
    const present = new Map<number, HTMLElement[]>([
      [1, [onScreen]],
      [2, [onScreen]],
    ]);
    const { result, rerender } = renderHook(
      ({ ids }: { ids: number[] }) =>
        useDragReorder<number>({
          axis: "y",
          ids,
          elementsOf: (id) => present.get(id) ?? [],
          threshold: 0,
          onDrop: slideHome,
        }),
      { initialProps: { ids: [1, 2, 3] } },
    );

    act(() => result.current.slide());
    present.delete(2);
    rerender({ ids: [3, 2, 1] });

    expect(slideHome).not.toHaveBeenCalled();
    expect(result.current.order).toEqual([3, 2, 1]);
  });
});
