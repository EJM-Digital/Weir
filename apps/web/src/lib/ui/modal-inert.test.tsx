import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { afterEach, expect, it, vi } from "vitest";

import { ModalDialog } from "../../components/ui/modal-dialog";

afterEach(() => {
  vi.restoreAllMocks();
});

function inert(element: Element): boolean {
  return element.closest("[inert]") !== null;
}

/** A side menu, and a page with a button that opens a dialog, as the shell lays them out. */
function Shell({ companion = false }: { companion?: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <nav aria-label="Menu">
        <button type="button">Menu item</button>
      </nav>
      <main>
        <button type="button" onClick={() => setOpen(true)}>
          Open
        </button>
        <p>Page text</p>
        {open ? (
          <>
            {companion ? (
              <button type="button" data-modal-companion="">
                Backdrop
              </button>
            ) : null}
            <ModalDialog
              title="Sure?"
              testId="dialog"
              onClose={() => setOpen(false)}
            >
              <button type="button">Yes</button>
            </ModalDialog>
          </>
        ) : null}
      </main>
    </>
  );
}

it("makes everything outside an open dialog inert, and the dialog and the page around it still reachable", () => {
  render(<Shell />);
  fireEvent.click(screen.getByRole("button", { name: "Open" }));

  expect(inert(screen.getByRole("button", { name: "Menu item" }))).toBe(true);
  expect(inert(screen.getByRole("button", { name: "Open" }))).toBe(true);
  expect(inert(screen.getByText("Page text"))).toBe(true);
  expect(inert(screen.getByTestId("dialog"))).toBe(false);
  expect(inert(screen.getByRole("button", { name: "Yes" }))).toBe(false);
  expect(screen.getByRole("main")).not.toHaveAttribute("inert");
});

it("frees everything again when the dialog closes, before it hands focus back to the button that opened it", () => {
  const focused: { element: Element; inert: boolean }[] = [];
  // The original is called back with the element it was called on, so it is never separated from its `this`.
  // eslint-disable-next-line @typescript-eslint/unbound-method
  const focus = HTMLElement.prototype.focus;
  vi.spyOn(HTMLElement.prototype, "focus").mockImplementation(function (
    this: HTMLElement,
    options?: FocusOptions,
  ) {
    focused.push({ element: this, inert: inert(this) });
    focus.call(this, options);
  });
  render(<Shell />);
  const opener = screen.getByRole("button", { name: "Open" });
  opener.focus();
  fireEvent.click(opener);
  expect(inert(opener)).toBe(true);

  fireEvent.keyDown(document, { key: "Escape" });

  expect(screen.queryByTestId("dialog")).not.toBeInTheDocument();
  expect(document.querySelector("[inert]")).toBeNull();
  expect(opener).toHaveFocus();
  const handedBack = focused.filter((call) => call.element === opener).at(-1);
  expect(handedBack?.inert).toBe(false);
});

it("leaves a companion of the dialog, such as its click-away backdrop, working", () => {
  render(<Shell companion />);
  fireEvent.click(screen.getByRole("button", { name: "Open" }));

  expect(inert(screen.getByRole("button", { name: "Backdrop" }))).toBe(false);
  expect(inert(screen.getByRole("button", { name: "Menu item" }))).toBe(true);
});

it("keeps what was inert before inert after the dialog closes", () => {
  render(<Shell />);
  screen.getByRole("navigation").setAttribute("inert", "");
  fireEvent.click(screen.getByRole("button", { name: "Open" }));

  fireEvent.keyDown(document, { key: "Escape" });

  expect(screen.getByRole("navigation")).toHaveAttribute("inert");
  expect(screen.getByRole("main")).not.toHaveAttribute("inert");
});

function Stacked() {
  const [outer, setOuter] = useState(true);
  const [inner, setInner] = useState(true);
  return (
    <>
      <nav aria-label="Menu">
        <button type="button">Menu item</button>
      </nav>
      <main>
        {outer ? (
          <ModalDialog
            title="First"
            testId="first"
            onClose={() => setOuter(false)}
          >
            <button type="button">In first</button>
            {inner ? (
              <ModalDialog
                title="Second"
                testId="second"
                onClose={() => setInner(false)}
              >
                <button type="button">In second</button>
              </ModalDialog>
            ) : null}
          </ModalDialog>
        ) : null}
      </main>
    </>
  );
}

it("keeps the page behind inert until the last of two stacked dialogs has closed, and the first one reachable again after the second", () => {
  render(<Stacked />);
  expect(inert(screen.getByRole("button", { name: "In first" }))).toBe(true);
  expect(inert(screen.getByRole("button", { name: "In second" }))).toBe(false);

  fireEvent.keyDown(document, { key: "Escape" });

  expect(screen.queryByTestId("second")).not.toBeInTheDocument();
  expect(inert(screen.getByRole("button", { name: "In first" }))).toBe(false);
  expect(inert(screen.getByRole("button", { name: "Menu item" }))).toBe(true);

  fireEvent.keyDown(document, { key: "Escape" });

  expect(document.querySelector("[inert]")).toBeNull();
});
