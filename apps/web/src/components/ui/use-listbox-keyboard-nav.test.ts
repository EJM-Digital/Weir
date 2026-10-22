import { act, renderHook } from "@testing-library/react";
import type { KeyboardEvent } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { useListboxKeyboardNav } from "./use-listbox-keyboard-nav";

type Option = { label: string };

function options(...labels: string[]): Option[] {
  return labels.map((label) => ({ label }));
}

function setup(initialProps: {
  isOpen: boolean;
  initialIndex: number;
  opts: Option[];
}) {
  const onActivate = vi.fn();
  const hook = renderHook(
    (props: typeof initialProps) =>
      useListboxKeyboardNav(props.opts, {
        isOpen: props.isOpen,
        initialIndex: props.initialIndex,
        onActivate,
      }),
    { initialProps },
  );
  return { onActivate, ...hook };
}

describe("useListboxKeyboardNav", () => {
  it("starts roving focus at the initial index the moment the panel opens", () => {
    const { result, rerender } = setup({
      isOpen: false,
      initialIndex: 1,
      opts: options("Alpha", "Bravo", "Charlie"),
    });

    rerender({
      isOpen: true,
      initialIndex: 1,
      opts: options("Alpha", "Bravo", "Charlie"),
    });

    expect(result.current.activeIndex).toBe(1);
  });

  it("clamps an initial index past the end of the option list", () => {
    const { result, rerender } = setup({
      isOpen: false,
      initialIndex: 9,
      opts: options("Alpha", "Bravo"),
    });

    rerender({
      isOpen: true,
      initialIndex: 9,
      opts: options("Alpha", "Bravo"),
    });

    expect(result.current.activeIndex).toBe(1);
  });

  it("does not reset the active option while already open, even when options or the initial index change", () => {
    const { result, rerender } = setup({
      isOpen: true,
      initialIndex: 0,
      opts: options("Alpha", "Bravo", "Charlie"),
    });

    act(() => result.current.setActive(2));
    expect(result.current.activeIndex).toBe(2);

    // A filtered or lengthened option list while the panel stays open must not fight the person's
    // own navigation by jumping back to the initial index.
    rerender({
      isOpen: true,
      initialIndex: 0,
      opts: options("Alpha", "Bravo", "Charlie", "Delta"),
    });
    expect(result.current.activeIndex).toBe(2);

    rerender({
      isOpen: true,
      initialIndex: 2,
      opts: options("Alpha", "Bravo", "Charlie", "Delta"),
    });
    expect(result.current.activeIndex).toBe(2);
  });

  it("starts at the initial index again the next time it reopens", () => {
    const { result, rerender } = setup({
      isOpen: true,
      initialIndex: 0,
      opts: options("Alpha", "Bravo", "Charlie"),
    });

    act(() => result.current.setActive(2));
    rerender({
      isOpen: false,
      initialIndex: 0,
      opts: options("Alpha", "Bravo", "Charlie"),
    });
    rerender({
      isOpen: true,
      initialIndex: 1,
      opts: options("Alpha", "Bravo", "Charlie"),
    });

    expect(result.current.activeIndex).toBe(1);
  });
});

describe("useListboxKeyboardNav keys", () => {
  afterEach(() => vi.useRealTimers());

  function press(
    result: { current: ReturnType<typeof useListboxKeyboardNav> },
    key: string,
    over: Partial<{ ctrlKey: boolean; target: unknown }> = {},
  ) {
    const preventDefault = vi.fn();
    act(() =>
      result.current.onKeyDown({
        key,
        ctrlKey: false,
        metaKey: false,
        altKey: false,
        target: { getAttribute: () => "option" },
        preventDefault,
        ...over,
      } as unknown as KeyboardEvent),
    );
    return preventDefault;
  }

  function open(...labels: string[]) {
    return setup({ isOpen: true, initialIndex: 0, opts: options(...labels) });
  }

  it("moves one option with the arrow keys, Home and End, and stops at the ends", () => {
    const { result } = open("Alpha", "Bravo", "Charlie");

    press(result, "ArrowDown");
    expect(result.current.activeIndex).toBe(1);
    press(result, "End");
    expect(result.current.activeIndex).toBe(2);
    press(result, "ArrowDown");
    expect(result.current.activeIndex).toBe(2);
    press(result, "ArrowUp");
    expect(result.current.activeIndex).toBe(1);
    press(result, "Home");
    expect(result.current.activeIndex).toBe(0);
    press(result, "ArrowUp");
    expect(result.current.activeIndex).toBe(0);
  });

  it("keeps the keys for itself, so the page does not scroll under them", () => {
    const { result } = open("Alpha", "Bravo");

    expect(press(result, "ArrowDown")).toHaveBeenCalled();
    expect(press(result, "q")).toHaveBeenCalled();
  });

  it("jumps to the option whose label starts with what has been typed, and builds on it until 750 ms pass", () => {
    vi.useFakeTimers();
    const { result } = open("Apple", "Banana", "Blueberry", "Cherry");

    press(result, "b");
    expect(result.current.activeIndex).toBe(1);
    press(result, "l");
    expect(result.current.activeIndex).toBe(2);

    act(() => void vi.advanceTimersByTime(750));
    press(result, "c");
    expect(result.current.activeIndex).toBe(3);
  });

  it("does not forget what was typed before 750 ms have passed", () => {
    vi.useFakeTimers();
    const { result } = open("Apple", "Banana", "Blueberry");

    press(result, "b");
    act(() => void vi.advanceTimersByTime(749));
    press(result, "l");

    expect(result.current.activeIndex).toBe(2);
  });

  it("stays where it is when nothing starts with what was typed", () => {
    const { result } = open("Apple", "Banana");
    press(result, "ArrowDown");

    press(result, "z");

    expect(result.current.activeIndex).toBe(1);
  });

  it("leaves a key with Control held to the browser", () => {
    const { result } = open("Apple", "Banana");

    const prevented = press(result, "b", { ctrlKey: true });

    expect(prevented).not.toHaveBeenCalled();
    expect(result.current.activeIndex).toBe(0);
  });

  it("chooses the active option on Enter or Space from an option, and ignores them on the trigger", () => {
    const { result, onActivate } = open("Alpha", "Bravo");
    press(result, "ArrowDown");

    press(result, "Enter");
    press(result, " ");
    press(result, "Enter", { target: { getAttribute: () => "button" } });

    expect(onActivate).toHaveBeenCalledTimes(2);
    expect(onActivate).toHaveBeenCalledWith({ label: "Bravo" }, 1);
  });

  it("does nothing while the panel is shut or has no options", () => {
    const shut = setup({
      isOpen: false,
      initialIndex: 0,
      opts: options("Alpha", "Bravo"),
    });
    expect(press(shut.result, "ArrowDown")).not.toHaveBeenCalled();
    expect(shut.result.current.activeIndex).toBe(0);

    const empty = open();
    expect(press(empty.result, "ArrowDown")).not.toHaveBeenCalled();
  });

  it("does nothing for an arrow key when there are no options to move to", () => {
    const { result, onActivate } = open();

    press(result, "ArrowDown");

    expect(onActivate).not.toHaveBeenCalled();
  });
});
