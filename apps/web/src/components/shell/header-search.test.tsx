import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { HeaderSearch } from "./header-search";

describe("HeaderSearch", () => {
  it("is one named search box, with its placeholder", () => {
    render(<HeaderSearch label="Find a file" placeholder="Find" />);

    const box = screen.getByRole("searchbox", { name: "Find a file" });
    expect(box).toHaveAttribute("placeholder", "Find");
  });

  it("is the same input whether or not it is collapsed to a mark, so what is typed stays", () => {
    const { rerender } = render(
      <HeaderSearch label="Find a file" defaultValue="metropolis" />,
    );
    const box = screen.getByRole("searchbox");

    rerender(
      <HeaderSearch label="Find a file" defaultValue="metropolis" collapsed />,
    );

    expect(screen.getByRole("searchbox")).toBe(box);
    expect(box).toHaveValue("metropolis");
    expect(box.closest("label")).toHaveAttribute("data-collapsed", "true");
  });

  it("tells a mark what it is by its tooltip, and the full box nothing", () => {
    const { rerender } = render(<HeaderSearch label="Find a file" collapsed />);
    expect(screen.getByRole("searchbox").closest("label")).toHaveAttribute(
      "title",
      "Find a file",
    );

    rerender(<HeaderSearch label="Find a file" />);
    expect(screen.getByRole("searchbox").closest("label")).not.toHaveAttribute(
      "title",
    );
  });

  it("lets go of the focus on Escape, and still tells the page about the key", () => {
    const keys: string[] = [];
    render(
      <HeaderSearch
        label="Find a file"
        onKeyDown={(event) => keys.push(event.key)}
      />,
    );
    const box = screen.getByRole("searchbox");
    box.focus();

    fireEvent.keyDown(box, { key: "Escape" });

    expect(box).not.toHaveFocus();
    expect(keys).toEqual(["Escape"]);
  });

  it("lets go of the focus on Escape, and passes every key on to the page", () => {
    const onKeyDown = vi.fn();
    render(<HeaderSearch label="Find a file" onKeyDown={onKeyDown} />);
    const box = screen.getByRole("searchbox");
    box.focus();

    fireEvent.keyDown(box, { key: "a" });
    expect(box).toHaveFocus();
    fireEvent.keyDown(box, { key: "Escape" });

    expect(box).not.toHaveFocus();
    expect(onKeyDown).toHaveBeenCalledTimes(2);
  });
});
