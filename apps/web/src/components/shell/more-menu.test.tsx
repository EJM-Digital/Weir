import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { MoreMenu } from "./more-menu";

const FOLDED = [
  { id: "a", label: "Alpha" },
  { id: "b", label: "Beta" },
  { id: "c", label: "Gamma" },
] as const;

function renderMenu(onChoose = vi.fn()) {
  render(
    <MoreMenu
      menuLabel="More tabs"
      folded={FOLDED}
      onChoose={onChoose}
      buttonClassName="tab"
    />,
  );
  return { onChoose, button: screen.getByRole("button", { name: /More/ }) };
}

const item = (name: string) => screen.getByRole("menuitem", { name });

describe("the More menu", () => {
  it("opens from the arrow key on its button, with the first entry in focus, and stays as it is on any other key", () => {
    const { button } = renderMenu();

    fireEvent.keyDown(button, { key: "a" });
    expect(screen.queryByRole("menu")).toBeNull();

    fireEvent.keyDown(button, { key: "ArrowDown" });
    expect(screen.getByRole("menu", { name: "More tabs" })).toBeVisible();
    expect(item("Alpha")).toHaveFocus();
  });

  it("moves through the entries with the arrow keys, Home and End, going round at either end", () => {
    const { button } = renderMenu();
    fireEvent.click(button);
    const menu = screen.getByRole("menu");

    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(item("Beta")).toHaveFocus();
    fireEvent.keyDown(menu, { key: "End" });
    expect(item("Gamma")).toHaveFocus();
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(item("Alpha")).toHaveFocus();
    fireEvent.keyDown(menu, { key: "ArrowUp" });
    expect(item("Gamma")).toHaveFocus();
    fireEvent.keyDown(menu, { key: "Home" });
    expect(item("Alpha")).toHaveFocus();
  });

  it("leaves the focus where it is for a key it has no use for", () => {
    const { button } = renderMenu();
    fireEvent.click(button);

    fireEvent.keyDown(screen.getByRole("menu"), { key: "x" });

    expect(item("Alpha")).toHaveFocus();
  });

  it("closes on Tab, and on Escape with the focus back on its button", () => {
    const { button } = renderMenu();
    fireEvent.click(button);

    fireEvent.keyDown(screen.getByRole("menu"), { key: "Tab" });
    expect(screen.queryByRole("menu")).toBeNull();

    fireEvent.click(button);
    fireEvent.keyDown(screen.getByRole("menu"), { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
    expect(button).toHaveFocus();
  });

  it("chooses an entry and closes", () => {
    const { button, onChoose } = renderMenu();
    fireEvent.click(button);

    fireEvent.click(item("Beta"));

    expect(onChoose).toHaveBeenCalledWith("b");
    expect(screen.queryByRole("menu")).toBeNull();
  });
});
