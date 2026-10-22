import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ProcessingLibrary } from "../../lib/processing/libraries-api";
import { LibraryPicker } from "./library-picker";

function library(id: number, name: string): ProcessingLibrary {
  return { id, name, media_type: "tv" } as ProcessingLibrary;
}

const many = Array.from({ length: 8 }, (_, i) =>
  library(i + 1, `Lib ${i + 1}`),
);

describe("LibraryPicker by keyboard and mouse", () => {
  it("names the library when the chosen one is not in the list", () => {
    const { rerender } = render(
      <LibraryPicker libraries={[]} chosenId={9} onPick={vi.fn()} />,
    );
    expect(screen.getByTestId("library-picker")).toHaveTextContent("—");

    rerender(
      <LibraryPicker
        libraries={[library(1, "TV"), library(2, "Movies")]}
        chosenId={9}
        onPick={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Choose" }));
    expect(document.activeElement).toBe(screen.getAllByRole("option")[0]);
  });

  it("shows how many files a library holds, and which kind it is", () => {
    render(
      <LibraryPicker
        libraries={[
          library(1, "TV"),
          { ...library(2, "Movies"), media_type: "movie" },
        ]}
        chosenId={1}
        onPick={vi.fn()}
        countFor={(id) => (id === 2 ? 1234 : null)}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /TV/ }));

    expect(screen.getByRole("option", { name: /Movies/ })).toHaveTextContent(
      "Movies · 1,234 files",
    );
    expect(screen.getByRole("option", { name: /^TV/ })).toHaveTextContent(
      "TV episodes",
    );
  });

  it("goes to either end with Home and End, and wraps round with the arrows", () => {
    render(
      <LibraryPicker
        libraries={[library(1, "TV"), library(2, "Movies"), library(3, "Docs")]}
        chosenId={1}
        onPick={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /TV/ }));
    const options = screen.getAllByRole("option");
    const menu = screen.getByRole("listbox");

    fireEvent.keyDown(menu, { key: "End" });
    expect(document.activeElement).toBe(options[2]);
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement).toBe(options[0]);
    fireEvent.keyDown(menu, { key: "ArrowUp" });
    expect(document.activeElement).toBe(options[2]);
    fireEvent.keyDown(menu, { key: "Home" });
    expect(document.activeElement).toBe(options[0]);
  });

  it("leaves other keys alone", () => {
    render(
      <LibraryPicker
        libraries={[library(1, "TV"), library(2, "Movies")]}
        chosenId={1}
        onPick={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /TV/ }));
    const first = document.activeElement;

    const kept = fireEvent.keyDown(screen.getByRole("listbox"), { key: "a" });

    expect(kept).toBe(true);
    expect(document.activeElement).toBe(first);
  });

  it("goes from the search box to the first library on Down and the last on Up", () => {
    render(<LibraryPicker libraries={many} chosenId={1} onPick={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /Lib 1$/ }));
    const search = screen.getByLabelText("Find a workflow");
    const options = screen.getAllByRole("option");
    expect(document.activeElement).toBe(search);

    fireEvent.keyDown(search, { key: "ArrowUp" });
    expect(document.activeElement).toBe(options[7]);

    search.focus();
    fireEvent.keyDown(search, { key: "ArrowDown" });
    expect(document.activeElement).toBe(options[0]);
  });

  it("keeps Home and End for the caret while typing in the search box", () => {
    render(<LibraryPicker libraries={many} chosenId={1} onPick={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /Lib 1$/ }));
    const search = screen.getByLabelText("Find a workflow");

    const home = fireEvent.keyDown(search, { key: "Home" });
    const end = fireEvent.keyDown(search, { key: "End" });

    expect([home, end]).toEqual([true, true]);
    expect(document.activeElement).toBe(search);
  });

  it("says so when no workflow matches the search, and has nothing to move to", () => {
    render(<LibraryPicker libraries={many} chosenId={1} onPick={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /Lib 1$/ }));
    const search = screen.getByLabelText("Find a workflow");
    fireEvent.change(search, { target: { value: "nothing like it" } });

    expect(screen.getByText("No workflow by that name.")).toBeInTheDocument();
    expect(fireEvent.keyDown(search, { key: "ArrowDown" })).toBe(true);
  });

  it("closes when the mouse goes down outside it, but not inside it", () => {
    render(
      <div>
        <p>Elsewhere</p>
        <LibraryPicker
          libraries={[library(1, "TV"), library(2, "Movies")]}
          chosenId={1}
          onPick={vi.fn()}
        />
      </div>,
    );
    fireEvent.click(screen.getByRole("button", { name: /TV/ }));

    fireEvent.mouseDown(screen.getByRole("listbox"));
    expect(screen.getByRole("listbox")).toBeInTheDocument();

    fireEvent.mouseDown(screen.getByText("Elsewhere"));
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
  });

  it("ignores other keys pressed anywhere while it is open", () => {
    render(
      <LibraryPicker
        libraries={[library(1, "TV"), library(2, "Movies")]}
        chosenId={1}
        onPick={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /TV/ }));

    fireEvent.keyDown(document, { key: "Tab" });

    expect(screen.getByRole("listbox")).toBeInTheDocument();
  });
});
