import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { SETTLE_MS } from "../../lib/ui/drag-motion";
import { ReorderHandle } from "./reorder-handle";
import { useRowReorder } from "./use-row-reorder";

const ROW_HEIGHT = 100;
const NAMES: Record<number, string> = { 1: "Movies", 2: "TV", 3: "Kids" };

/** Lays the rows out one under another in the order the page draws them, shifted by the offset they have been given. */
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

function Rows({ onCommit }: { onCommit: (ids: number[]) => Promise<unknown> }) {
  const reorder = useRowReorder({
    ids: [1, 2, 3],
    nameOf: (id) => NAMES[id],
    onCommit,
  });
  return (
    <>
      <p role="status">{reorder.announcement}</p>
      <table>
        <tbody>
          {reorder.orderedIds.map((id) => (
            <tr key={id} ref={reorder.rowRef(id)}>
              <td>
                <ReorderHandle
                  label={`Move ${NAMES[id]}`}
                  describedBy="hint"
                  handle={reorder.handleProps(id)}
                />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

function names() {
  return screen.getAllByRole("button").map((button) => button.ariaLabel);
}

beforeEach(() => {
  vi.useFakeTimers();
  layOutInDrawnOrder();
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("dragging a row by its grip", () => {
  it("saves the new order once the row has settled where it was dropped", async () => {
    const onCommit = vi.fn().mockResolvedValue(undefined);
    render(<Rows onCommit={onCommit} />);

    fireEvent.pointerDown(screen.getByRole("button", { name: "Move Movies" }), {
      button: 0,
      clientY: 150,
    });
    fireEvent.pointerMove(window, { clientY: 360 });
    expect(names()).toEqual(["Move TV", "Move Kids", "Move Movies"]);
    expect(onCommit).not.toHaveBeenCalled();
    fireEvent.pointerUp(window);
    await act(async () => {
      vi.advanceTimersByTime(SETTLE_MS);
    });

    expect(onCommit).toHaveBeenCalledWith([2, 3, 1]);
  });

  it("puts the row back on Escape and saves nothing", () => {
    const onCommit = vi.fn().mockResolvedValue(undefined);
    render(<Rows onCommit={onCommit} />);

    fireEvent.pointerDown(screen.getByRole("button", { name: "Move Movies" }), {
      button: 0,
      clientY: 150,
    });
    fireEvent.pointerMove(window, { clientY: 360 });
    fireEvent.keyDown(window, { key: "Escape" });

    expect(names()).toEqual(["Move Movies", "Move TV", "Move Kids"]);
    expect(onCommit).not.toHaveBeenCalled();
  });

  it("leaves the grip neither focused nor pressed after a drop with the pointer", async () => {
    render(<Rows onCommit={vi.fn().mockResolvedValue(undefined)} />);
    screen.getByRole("button", { name: "Move Movies" }).focus();

    fireEvent.pointerDown(screen.getByRole("button", { name: "Move Movies" }), {
      button: 0,
      clientY: 150,
    });
    fireEvent.pointerMove(window, { clientY: 360 });
    fireEvent.pointerUp(window);
    await act(async () => {
      vi.advanceTimersByTime(SETTLE_MS);
    });

    const grip = screen.getByRole("button", { name: "Move Movies" });
    expect(grip).not.toHaveFocus();
    expect(grip).toHaveAttribute("aria-pressed", "false");
  });

  it("does not take focus when it is pressed", () => {
    render(<Rows onCommit={vi.fn().mockResolvedValue(undefined)} />);

    fireEvent.pointerDown(screen.getByRole("button", { name: "Move Movies" }), {
      button: 0,
      clientY: 150,
    });

    expect(
      screen.getByRole("button", { name: "Move Movies" }),
    ).not.toHaveFocus();
  });
});

describe("moving a row from the keyboard", () => {
  const grip = (name: string) =>
    screen.getByRole("button", { name: `Move ${name}` });
  const announced = () => screen.getByRole("status").textContent;
  const flush = () => act(async () => undefined);

  it("moves a row one place and saves at once with Alt and an arrow, keeping the grip focused", async () => {
    const onCommit = vi.fn().mockResolvedValue(undefined);
    render(<Rows onCommit={onCommit} />);
    grip("Movies").focus();

    fireEvent.keyDown(grip("Movies"), { key: "ArrowDown", altKey: true });
    await flush();

    expect(onCommit).toHaveBeenCalledWith([2, 1, 3]);
    expect(announced()).toBe("Movies moved to position 2 of 3.");
    expect(grip("Movies")).toHaveFocus();
  });

  it.each([
    ["Movies", "ArrowUp", "Movies is already at the top, position 1 of 3."],
    ["Kids", "ArrowDown", "Kids is already at the bottom, position 3 of 3."],
  ])(
    "says %s cannot go further when Alt and %s are pressed at the end",
    async (name, key, words) => {
      const onCommit = vi.fn().mockResolvedValue(undefined);
      render(<Rows onCommit={onCommit} />);

      fireEvent.keyDown(grip(name), { key, altKey: true });
      await flush();

      expect(announced()).toBe(words);
      expect(onCommit).not.toHaveBeenCalled();
    },
  );

  it("picks a row up, moves it with the arrows, and saves it where it is dropped", async () => {
    const onCommit = vi.fn().mockResolvedValue(undefined);
    render(<Rows onCommit={onCommit} />);

    fireEvent.keyDown(grip("Movies"), { key: " " });
    expect(grip("Movies")).toHaveAttribute("aria-pressed", "true");
    expect(announced()).toBe("Movies picked up at position 1 of 3.");

    fireEvent.keyDown(grip("Movies"), { key: "ArrowDown" });
    expect(announced()).toBe("Movies moved to position 2 of 3.");
    expect(names()).toEqual(["Move TV", "Move Movies", "Move Kids"]);
    expect(onCommit).not.toHaveBeenCalled();

    fireEvent.keyDown(grip("Movies"), { key: "Enter" });
    await flush();

    expect(announced()).toBe("Movies dropped at position 2 of 3.");
    expect(onCommit).toHaveBeenCalledWith([2, 1, 3]);
    expect(grip("Movies")).toHaveAttribute("aria-pressed", "false");
  });

  it("says a held row cannot go past the end", () => {
    render(<Rows onCommit={vi.fn().mockResolvedValue(undefined)} />);
    fireEvent.keyDown(grip("Movies"), { key: "Enter" });

    fireEvent.keyDown(grip("Movies"), { key: "ArrowUp" });

    expect(announced()).toBe("Movies is already at the top, position 1 of 3.");
    expect(names()).toEqual(["Move Movies", "Move TV", "Move Kids"]);
  });

  it("saves nothing when a row is dropped where it was picked up", async () => {
    const onCommit = vi.fn().mockResolvedValue(undefined);
    render(<Rows onCommit={onCommit} />);

    fireEvent.keyDown(grip("TV"), { key: " " });
    fireEvent.keyDown(grip("TV"), { key: " " });
    await flush();

    expect(announced()).toBe("TV dropped at position 2 of 3.");
    expect(onCommit).not.toHaveBeenCalled();
    expect(grip("TV")).toHaveAttribute("aria-pressed", "false");
  });

  it("puts a held row back on Escape, and when the grip loses focus", async () => {
    const onCommit = vi.fn().mockResolvedValue(undefined);
    render(<Rows onCommit={onCommit} />);

    fireEvent.keyDown(grip("Movies"), { key: " " });
    fireEvent.keyDown(grip("Movies"), { key: "ArrowDown" });
    fireEvent.keyDown(grip("Movies"), { key: "Escape" });
    expect(announced()).toBe("Movies put back.");
    expect(names()).toEqual(["Move Movies", "Move TV", "Move Kids"]);

    fireEvent.keyDown(grip("Movies"), { key: " " });
    fireEvent.keyDown(grip("Movies"), { key: "ArrowDown" });
    fireEvent.blur(grip("Movies"));
    expect(grip("Movies")).toHaveAttribute("aria-pressed", "false");
    expect(names()).toEqual(["Move Movies", "Move TV", "Move Kids"]);
    expect(onCommit).not.toHaveBeenCalled();
  });

  it.each([
    ["Escape", { key: "Escape" }],
    ["another key", { key: "a" }],
    ["an arrow without Alt", { key: "ArrowDown" }],
  ])("ignores %s on a row that is not held", (_name, event) => {
    render(<Rows onCommit={vi.fn().mockResolvedValue(undefined)} />);

    const unhandled = fireEvent.keyDown(grip("Movies"), event);

    expect(unhandled).toBe(true);
    expect(announced()).toBe("");
  });

  it("ignores leaving a grip that was not holding a row", () => {
    render(<Rows onCommit={vi.fn().mockResolvedValue(undefined)} />);

    fireEvent.blur(grip("Movies"));

    expect(announced()).toBe("");
  });

  it("ignores every other mouse button than the main one", () => {
    render(<Rows onCommit={vi.fn().mockResolvedValue(undefined)} />);

    const unhandled = fireEvent.pointerDown(grip("Movies"), {
      button: 2,
      clientY: 150,
    });
    fireEvent.pointerMove(window, { clientY: 360 });

    expect(unhandled).toBe(true);
    expect(names()).toEqual(["Move Movies", "Move TV", "Move Kids"]);
  });

  it("takes no new move while the last one is still being saved", async () => {
    let finish: () => void = () => undefined;
    const onCommit = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    );
    render(<Rows onCommit={onCommit} />);
    fireEvent.keyDown(grip("Movies"), { key: "ArrowDown", altKey: true });

    fireEvent.keyDown(grip("TV"), { key: "ArrowDown", altKey: true });
    const pressed = fireEvent.pointerDown(grip("Kids"), {
      button: 0,
      clientY: 350,
    });
    expect(onCommit).toHaveBeenCalledTimes(1);
    expect(pressed).toBe(true);

    finish();
    await flush();
  });
});
