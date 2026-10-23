import { act, renderHook } from "@testing-library/react";
import { createRef } from "react";
import { describe, expect, it } from "vitest";

import {
  HELD_ATTRIBUTE,
  SLOTS_CHANGED_EVENT,
  closeSlot,
  openSlot,
  settleSlot,
  useOpenSlots,
} from "./shelf-slots";

describe("a shelf slot", () => {
  it("closes to no width, and gives its own width back when it settles", () => {
    const row = document.createElement("ul");
    row.innerHTML = "<li><span></span></li>";
    const slot = row.querySelector("li") as HTMLElement;
    const art = row.querySelector("span") as HTMLElement;

    closeSlot(art);

    expect(slot.style.width).toBe("0px");
    expect(slot.style.overflow).toBe("hidden");

    settleSlot(art);

    expect(slot.style.width).toBe("");
    expect(slot.style.overflow).toBe("");
  });

  it("leaves art that is in no slot as it is", () => {
    const art = document.createElement("span");

    openSlot(art, 0.5);
    settleSlot(art);

    expect(art.style.cssText).toBe("");
  });
});

describe("counting the tiles that are held or on their way", () => {
  it("counts none while the row is not on the page, and when the slots change", () => {
    const { result } = renderHook(() => useOpenSlots(createRef()));

    act(() => {
      document.dispatchEvent(new Event(SLOTS_CHANGED_EVENT));
    });

    expect(result.current).toBe(0);
  });

  it("counts the held tiles of a row that is on the page", () => {
    const row = document.createElement("ul");
    row.innerHTML = `<li ${HELD_ATTRIBUTE}></li><li></li>`;

    const { result } = renderHook(() => useOpenSlots({ current: row }));

    expect(result.current).toBe(1);
  });
});
