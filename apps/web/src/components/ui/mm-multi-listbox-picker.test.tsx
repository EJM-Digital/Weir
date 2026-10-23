import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";

import type { MmListboxOption } from "./mm-listbox-picker";
import { MmMultiListboxPicker } from "./mm-multi-listbox-picker";

const OPTIONS: MmListboxOption[] = [
  { value: "a", label: "Alpha" },
  { value: "b", label: "Bravo" },
  { value: "c", label: "Charlie" },
];

function renderPicker(
  overrides: Partial<Parameters<typeof MmMultiListboxPicker>[0]> = {},
) {
  const onChange = vi.fn();
  render(
    <MmMultiListboxPicker
      options={OPTIONS}
      values={[]}
      onChange={onChange}
      {...overrides}
    />,
  );
  return { onChange };
}

it("shows every chosen label in full for a long list of selections", () => {
  const manyOptions: MmListboxOption[] = Array.from(
    { length: 12 },
    (_, index) => ({
      value: `lang-${index}`,
      label: `Language number ${index} (${index})`,
    }),
  );
  renderPicker({
    options: manyOptions,
    values: manyOptions.map((o) => o.value),
  });

  const trigger = screen.getByRole("button");
  expect(trigger.querySelector("span")?.className).not.toMatch(/\btruncate\b/);
  manyOptions.forEach((option) => {
    expect(trigger).toHaveTextContent(option.label);
  });
});

it("toggles an option on click and keeps the panel open", () => {
  const { onChange } = renderPicker({ values: ["a"] });
  fireEvent.click(screen.getByRole("button"));
  fireEvent.click(screen.getByRole("option", { name: "Bravo" }));

  expect(onChange).toHaveBeenCalledWith(["a", "b"]);
  expect(screen.getByRole("listbox")).toBeInTheDocument();
});

it("removes an already-chosen option on click", () => {
  const { onChange } = renderPicker({ values: ["a", "b"] });
  fireEvent.click(screen.getByRole("button"));
  fireEvent.click(screen.getByRole("option", { name: "Bravo" }));

  expect(onChange).toHaveBeenCalledWith(["a"]);
});

it("closes when a click lands outside the picker", () => {
  render(
    <div>
      <MmMultiListboxPicker options={OPTIONS} values={[]} onChange={vi.fn()} />
      <button type="button">Outside</button>
    </div>,
  );
  fireEvent.click(screen.getByRole("button", { name: "Select one or more…" }));
  fireEvent.mouseDown(screen.getByRole("button", { name: "Outside" }));

  expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
});

it("closes on Escape", () => {
  renderPicker();
  fireEvent.click(screen.getByRole("button"));
  fireEvent.keyDown(document, { key: "Escape" });

  expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
});

it("never opens while disabled", () => {
  renderPicker({ disabled: true });
  fireEvent.click(screen.getByRole("button"));

  expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
});

it("moves the active option with ArrowDown and ArrowUp", () => {
  renderPicker();
  const trigger = screen.getByRole("button");
  fireEvent.click(trigger);
  const options = screen.getAllByRole("option");

  fireEvent.keyDown(trigger, { key: "ArrowDown" });
  expect(options[1]).toHaveFocus();

  fireEvent.keyDown(options[1], { key: "ArrowDown" });
  expect(options[2]).toHaveFocus();

  fireEvent.keyDown(options[2], { key: "ArrowUp" });
  expect(options[1]).toHaveFocus();
});

it("jumps to the first and last option with Home and End", () => {
  renderPicker();
  const trigger = screen.getByRole("button");
  fireEvent.click(trigger);
  const options = screen.getAllByRole("option");

  fireEvent.keyDown(trigger, { key: "End" });
  expect(options[2]).toHaveFocus();

  fireEvent.keyDown(options[2], { key: "Home" });
  expect(options[0]).toHaveFocus();
});

it("jumps to the option matching a typed letter", () => {
  renderPicker();
  const trigger = screen.getByRole("button");
  fireEvent.click(trigger);
  const options = screen.getAllByRole("option");

  fireEvent.keyDown(trigger, { key: "c" });
  expect(options[2]).toHaveFocus();
});

it("toggles the focused option with Enter and keeps the panel open", () => {
  const { onChange } = renderPicker();
  const trigger = screen.getByRole("button");
  fireEvent.click(trigger);
  const options = screen.getAllByRole("option");

  fireEvent.keyDown(trigger, { key: "ArrowDown" });
  fireEvent.keyDown(options[1], { key: "Enter" });

  expect(onChange).toHaveBeenCalledWith(["b"]);
  expect(screen.getByRole("listbox")).toBeInTheDocument();
});

it("toggles the focused option with Space and keeps the panel open", () => {
  const { onChange } = renderPicker();
  const trigger = screen.getByRole("button");
  fireEvent.click(trigger);
  const options = screen.getAllByRole("option");

  fireEvent.keyDown(trigger, { key: "ArrowDown" });
  fireEvent.keyDown(options[1], { key: " " });

  expect(onChange).toHaveBeenCalledWith(["b"]);
  expect(screen.getByRole("listbox")).toBeInTheDocument();
});

it("takes a chosen option out with Enter", () => {
  const { onChange } = renderPicker({ values: ["a", "b"] });
  fireEvent.click(screen.getByRole("button"));
  const options = screen.getAllByRole("option");

  fireEvent.keyDown(options[0], { key: "Enter" });

  expect(onChange).toHaveBeenCalledWith(["b"]);
});

it("keeps focus where it is when the mouse goes down on an option", () => {
  renderPicker();
  fireEvent.click(screen.getByRole("button"));

  const notPrevented = fireEvent.mouseDown(
    screen.getByRole("option", { name: "Bravo" }),
  );

  expect(notPrevented).toBe(false);
});
