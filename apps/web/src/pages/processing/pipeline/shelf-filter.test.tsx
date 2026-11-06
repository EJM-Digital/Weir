import { fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ShelfFilter } from "./shelf-filter";

const CHOICES = [
  { id: null, name: "All" },
  { id: 1, name: "Movies" },
  { id: 2, name: "TV" },
  { id: 3, name: "Kids" },
];

afterEach(() => vi.restoreAllMocks());

/** jsdom has no layout: the chips' box is too wide for as long as it holds more than `most` chips. */
function roomFor(most: number) {
  vi.spyOn(Element.prototype, "scrollWidth", "get").mockImplementation(
    function (this: Element) {
      return this.classList.contains("mm-shelf-filter") &&
        this.querySelectorAll("[aria-pressed]").length > most
        ? 500
        : 0;
    },
  );
}

const shown = () =>
  within(screen.getByRole("group", { name: "Workflows" }))
    .getAllByRole("button")
    .map((button) => button.textContent);

describe("ShelfFilter", () => {
  it("shows every workflow's chip when the header has the room", () => {
    roomFor(9);
    render(<ShelfFilter choices={CHOICES} chosen={null} onChoose={vi.fn()} />);

    expect(shown()).toEqual(["All", "Movies", "TV", "Kids"]);
  });

  it("is the boxed switch, and keeps its buttons' elements when the choice changes", () => {
    roomFor(9);
    const view = render(
      <ShelfFilter choices={CHOICES} chosen={null} onChoose={vi.fn()} />,
    );
    const group = screen.getByRole("group", { name: "Workflows" });
    expect(group).toHaveClass("mm-segmented");
    expect(group.querySelectorAll("button.mm-segmented__option")).toHaveLength(
      4,
    );
    const before = within(group).getAllByRole("button");

    view.rerender(
      <ShelfFilter choices={CHOICES} chosen={2} onChoose={vi.fn()} />,
    );

    const after = within(
      screen.getByRole("group", { name: "Workflows" }),
    ).getAllByRole("button");
    expect(after).toHaveLength(4);
    after.forEach((button, index) => expect(button).toBe(before[index]));
    expect(screen.getByRole("button", { name: "TV" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(screen.getByRole("button", { name: "All" })).toHaveAttribute(
      "aria-pressed",
      "false",
    );
  });

  it("folds the chips that do not fit into More, whole, and chooses from there", () => {
    roomFor(2);
    const onChoose = vi.fn();
    render(<ShelfFilter choices={CHOICES} chosen={null} onChoose={onChoose} />);

    expect(shown()).toEqual(["All", "Movies", "More"]);
    fireEvent.click(screen.getByRole("button", { name: "More" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Kids" }));
    expect(onChoose).toHaveBeenCalledWith(3);
  });

  it("keeps the chosen workflow's chip in the row", () => {
    roomFor(2);
    const onChoose = vi.fn();
    render(<ShelfFilter choices={CHOICES} chosen={3} onChoose={onChoose} />);

    expect(shown()).toEqual(["All", "Kids", "More"]);
  });

  it("puts All in More when only the chosen workflow has room, and shows every workflow from there", () => {
    roomFor(1);
    const onChoose = vi.fn();
    render(<ShelfFilter choices={CHOICES} chosen={3} onChoose={onChoose} />);

    expect(shown()).toEqual(["Kids", "More"]);
    fireEvent.click(screen.getByRole("button", { name: "More" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "All" }));
    expect(onChoose).toHaveBeenCalledWith(null);
  });
});
