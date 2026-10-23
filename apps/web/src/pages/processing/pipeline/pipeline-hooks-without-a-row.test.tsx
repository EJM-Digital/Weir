import { act, renderHook } from "@testing-library/react";
import { createRef } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { PipelineCard } from "./pipeline-card-types";
import { SLOTS_CHANGED_EVENT } from "./shelf-slots";
import { TILE_ATTRIBUTE, useDeliveryFlight } from "./use-delivery-flight";
import { TILE_KEY_ATTRIBUTE, useSlideNeighbours } from "./use-slide-neighbours";
import { useTileArrivals } from "./use-tile-arrivals";

afterEach(() => {
  Reflect.deleteProperty(Element.prototype, "animate");
  vi.restoreAllMocks();
});

const delivered = {
  key: "file-1",
  path: "TV/one.mkv",
  end: "delivered",
} as PipelineCard;

function deliveryOn(board: HTMLElement, onDelivered: () => void) {
  return renderHook(
    ({ cards }: { cards: PipelineCard[] }) =>
      useDeliveryFlight({ current: board }, cards, new Set(), onDelivered),
    { initialProps: { cards: [delivered] } },
  );
}

describe("the board and shelf hooks", () => {
  it("do nothing before their row is on the page", () => {
    const onDelivered = vi.fn();
    const animate = vi.fn();
    Element.prototype.animate = animate;

    renderHook(() => {
      useDeliveryFlight(createRef(), [delivered], new Set(), onDelivered);
      useSlideNeighbours(createRef(), "a|b", 100);
      useTileArrivals(createRef(), "a|b");
    });
    act(() => {
      document.dispatchEvent(new Event(SLOTS_CHANGED_EVENT));
    });

    expect(onDelivered).not.toHaveBeenCalled();
    expect(animate).not.toHaveBeenCalled();
  });

  it("do not hand over a delivered card whose tile is not on the board", () => {
    const onDelivered = vi.fn();
    const { rerender } = deliveryOn(document.createElement("div"), onDelivered);

    rerender({ cards: [] });

    expect(onDelivered).not.toHaveBeenCalled();
  });

  it("hand over a delivered card whose tile is on the board", () => {
    const onDelivered = vi.fn();
    const board = document.createElement("div");
    board.innerHTML = `<div ${TILE_ATTRIBUTE}="file-1"><span>poster</span></div>`;
    const { rerender } = deliveryOn(board, onDelivered);

    rerender({ cards: [] });

    expect(onDelivered).toHaveBeenCalledTimes(1);
  });

  it("do not slide a row that is not on the page when the slide is due", async () => {
    const animate = vi.fn();
    Element.prototype.animate = animate;
    const row = document.createElement("ul");
    row.innerHTML = `<li ${TILE_KEY_ATTRIBUTE}="a"></li><li ${TILE_KEY_ATTRIBUTE}="b"></li>`;
    const { rerender } = renderHook(
      ({ order }: { order: string }) =>
        useSlideNeighbours({ current: row }, order, 100),
      { initialProps: { order: "a|b" } },
    );

    rerender({ order: "b|a" });
    await act(async () => {});

    expect(animate).not.toHaveBeenCalled();
  });
});
