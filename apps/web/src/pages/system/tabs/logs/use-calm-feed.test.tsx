import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { useCalmFeed } from "./use-calm-feed";

/** The element at the top of the list, standing where the reader has scrolled it to. */
function listAt(top: number): HTMLElement {
  const list = document.createElement("div");
  list.getBoundingClientRect = () => ({ top }) as DOMRect;
  return list;
}

type Props = { live: string[] | undefined; key: string; holding: boolean };

function openFeed(props: Props, list: HTMLElement) {
  const view = renderHook(
    (current: Props) => useCalmFeed(current.live, current.key, current.holding),
    {
      initialProps: props,
    },
  );
  act(() => view.result.current.watchFeed(list));
  return view;
}

const FIRST = ["first"];
const SECOND = ["second"];
const THIRD = ["third"];

const scrollTo = (list: HTMLElement, top: number) => {
  list.getBoundingClientRect = () => ({ top }) as DOMRect;
  act(() => void window.dispatchEvent(new Event("scroll")));
};

describe("the log's calm feed", () => {
  it("shows the live data as it arrives while the reader is at the top with nothing held open", () => {
    const view = openFeed(
      { live: FIRST, key: "all", holding: false },
      listAt(0),
    );
    expect(view.result.current.shown).toBe(FIRST);

    view.rerender({ live: SECOND, key: "all", holding: false });

    expect(view.result.current.shown).toBe(SECOND);
  });

  it("keeps the list as it was while the reader has scrolled down, and shows the new data once they are back at the top", () => {
    const list = listAt(0);
    const view = openFeed({ live: FIRST, key: "all", holding: false }, list);
    scrollTo(list, -240);

    view.rerender({ live: SECOND, key: "all", holding: false });
    expect(view.result.current.shown).toBe(FIRST);

    scrollTo(list, 0);
    view.rerender({ live: THIRD, key: "all", holding: false });
    expect(view.result.current.shown).toBe(THIRD);
  });

  it("keeps the list as it was while something is held open, such as a row being read or older pages loaded", () => {
    const view = openFeed(
      { live: FIRST, key: "all", holding: false },
      listAt(0),
    );
    view.rerender({ live: FIRST, key: "all", holding: true });

    view.rerender({ live: SECOND, key: "all", holding: true });

    expect(view.result.current.shown).toBe(FIRST);
  });

  it("shows the new data at once when asked, wherever the reader is", () => {
    const list = listAt(0);
    const view = openFeed({ live: FIRST, key: "all", holding: false }, list);
    scrollTo(list, -240);
    view.rerender({ live: SECOND, key: "all", holding: false });

    act(() => view.result.current.show(SECOND));

    expect(view.result.current.shown).toBe(SECOND);
  });

  it("shows the live data of another list straight away, whatever was held for the last one", () => {
    const list = listAt(0);
    const view = openFeed({ live: FIRST, key: "all", holding: false }, list);
    scrollTo(list, -240);

    view.rerender({ live: THIRD, key: "errors", holding: false });

    expect(view.result.current.shown).toBe(THIRD);
  });

  it("shows nothing before the first data has arrived", () => {
    const view = openFeed(
      { live: undefined, key: "all", holding: false },
      listAt(0),
    );

    expect(view.result.current.shown).toBeUndefined();
  });
});
