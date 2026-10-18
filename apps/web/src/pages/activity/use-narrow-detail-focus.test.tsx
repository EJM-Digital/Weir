import { render, screen } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

import { useNarrowDetailFocus } from "./use-narrow-detail-focus";

const NARROW = "(max-width: 1099.98px)";

function Detail({ id }: { id: number }) {
  const { sectionRef, titleRef } = useNarrowDetailFocus(id);
  return (
    <section ref={sectionRef} data-testid="detail">
      <h2 ref={titleRef} tabIndex={-1}>
        File {id}
      </h2>
    </section>
  );
}

const scrollIntoView = vi.fn();

function screenIs(narrow: boolean) {
  const asked: string[] = [];
  vi.stubGlobal("matchMedia", (query: string) => {
    asked.push(query);
    return { matches: narrow };
  });
  return asked;
}

beforeEach(() => {
  scrollIntoView.mockReset();
  Element.prototype.scrollIntoView = scrollIntoView;
});

afterEach(() => {
  vi.unstubAllGlobals();
});

it("leaves focus and scroll alone when the detail first appears, so loading the page never steals focus", () => {
  screenIs(true);

  render(<Detail id={1} />);

  expect(scrollIntoView).not.toHaveBeenCalled();
  expect(screen.getByRole("heading")).not.toHaveFocus();
});

it("scrolls the detail into view and focuses its title when another file is picked on a narrow screen", () => {
  const asked = screenIs(true);
  const { rerender } = render(<Detail id={1} />);

  rerender(<Detail id={2} />);

  expect(asked).toContain(NARROW);
  expect(scrollIntoView).toHaveBeenCalledTimes(1);
  expect(scrollIntoView).toHaveBeenCalledWith({ block: "start" });
  expect(screen.getByRole("heading", { name: "File 2" })).toHaveFocus();
});

it("does nothing when the detail sits beside the list on a wide screen", () => {
  screenIs(false);
  const { rerender } = render(<Detail id={1} />);

  rerender(<Detail id={2} />);

  expect(scrollIntoView).not.toHaveBeenCalled();
  expect(screen.getByRole("heading")).not.toHaveFocus();
});

it("does nothing when the same file is shown again", () => {
  screenIs(true);
  const { rerender } = render(<Detail id={1} />);

  rerender(<Detail id={1} />);

  expect(scrollIntoView).not.toHaveBeenCalled();
});
