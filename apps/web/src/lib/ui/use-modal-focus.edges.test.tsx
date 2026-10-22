import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import { useModalFocus } from "./use-modal-focus";

function Layer({ name, onClose }: { name: string; onClose: () => void }) {
  const ref = useModalFocus<HTMLDivElement>({ onClose });
  return <div ref={ref} tabIndex={-1} role="dialog" aria-label={name} />;
}

function ModalLayer() {
  const ref = useModalFocus<HTMLDivElement>({ onClose: () => undefined });
  return (
    <>
      <div
        ref={ref}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-label="Layer"
      >
        <button type="button">First</button>
        <button type="button">Last</button>
      </div>
      <button type="button">Behind</button>
    </>
  );
}

function Bare() {
  const ref = useModalFocus<HTMLDivElement>({ onClose: () => undefined });
  return (
    <div
      ref={ref}
      tabIndex={-1}
      role="dialog"
      aria-modal="true"
      aria-label="Bare"
    />
  );
}

it("keeps focus on a modal layer that has nothing to tab to", () => {
  render(<Bare />);

  const allowed = fireEvent.keyDown(document, { key: "Tab" });

  expect(allowed).toBe(false);
  expect(screen.getByRole("dialog")).toHaveFocus();
});

it.each([
  ["Tab", false, "First"],
  ["Shift+Tab", true, "Last"],
])(
  "brings %s back into a modal layer from outside it",
  (_name, shiftKey, lands) => {
    render(<ModalLayer />);
    screen.getByRole("button", { name: "Behind" }).focus();

    fireEvent.keyDown(document, { key: "Tab", shiftKey });

    expect(screen.getByRole("button", { name: lands })).toHaveFocus();
  },
);

it("wraps Shift+Tab from the layer itself to the last control", () => {
  render(<ModalLayer />);
  expect(screen.getByRole("dialog")).toHaveFocus();

  fireEvent.keyDown(document, { key: "Tab", shiftKey: true });

  expect(screen.getByRole("button", { name: "Last" })).toHaveFocus();
});

it("forgets a layer whose element left the page without closing", () => {
  const closeGone = vi.fn();
  const closeOpen = vi.fn();
  const { unmount } = render(
    <>
      <Layer name="Gone" onClose={closeGone} />
      <Layer name="Open" onClose={closeOpen} />
    </>,
  );
  const gone = screen.getByRole("dialog", { name: "Open" });
  const parent = gone.parentElement!;
  gone.remove();

  fireEvent.keyDown(document, { key: "Escape" });

  expect(closeGone).toHaveBeenCalledTimes(1);
  expect(closeOpen).not.toHaveBeenCalled();
  parent.append(gone);
  unmount();
});

it("ignores focus landing on something that is not an element", () => {
  render(<Layer name="Panel" onClose={vi.fn()} />);

  expect(() => fireEvent.focusIn(document)).not.toThrow();
  expect(screen.getByRole("dialog")).toHaveFocus();
});
