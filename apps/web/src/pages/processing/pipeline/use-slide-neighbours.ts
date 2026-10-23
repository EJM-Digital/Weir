import { useLayoutEffect, useRef, type RefObject } from "react";

import { motionAllowed } from "../../../lib/ui/motion-allowed";
import { SLOTS_CHANGED_EVENT } from "./shelf-slots";
import { RESIZING_CLASS } from "./use-still-while-resizing";

/** Set by the shelf on each tile, to the tile's key: how a tile is told apart from the others. */
export const TILE_KEY_ATTRIBUTE = "data-shelf-key";

const SLIDE_MS = 300;
const SLIDE_EASING = "cubic-bezier(.2, .8, .2, 1)";
/** A tile that moved less than this has not moved. */
const MOVED_PX = 1;

function tilesOf(host: HTMLElement) {
  return host.querySelectorAll<HTMLElement>(`[${TILE_KEY_ATTRIBUTE}]`);
}

function keyOf(tile: HTMLElement): string {
  return tile.getAttribute(TILE_KEY_ATTRIBUTE) as string;
}

/** Where each tile of the row is, from the row's left edge. */
function leftsOf(host: HTMLElement): Map<string, number> {
  const rowLeft = host.getBoundingClientRect().left;
  const lefts = new Map<string, number>();
  for (const tile of tilesOf(host)) {
    lefts.set(keyOf(tile), tile.getBoundingClientRect().left - rowLeft);
  }
  return lefts;
}

/** Slides each tile that was somewhere else in `before` from there to where it is now. */
function slideFrom(host: HTMLElement, before: ReadonlyMap<string, number>) {
  if (!host.isConnected) return;
  const rowLeft = host.getBoundingClientRect().left;
  for (const tile of tilesOf(host)) {
    const was = before.get(keyOf(tile));
    const left = tile.getBoundingClientRect().left - rowLeft;
    if (
      was !== undefined &&
      Math.abs(was - left) > MOVED_PX &&
      typeof tile.animate === "function"
    ) {
      tile.animate(
        [{ transform: `translateX(${was - left}px)` }, { transform: "none" }],
        { duration: SLIDE_MS, easing: SLIDE_EASING },
      );
    }
  }
}

/**
 * When a tile is added to the shelf or taken from it, the tiles that stay slide to their new places instead of
 * jumping: each is moved back to where it was and let go. Nothing slides while the window is being resized, which
 * moves every tile at once, or when movement is not allowed. A tile added in a closed slot moves nothing, and one
 * whose slot opens moves the tiles beside it by layout, in step with the poster flying to it; where the tiles stand
 * is noted again as each slot closes, opens and settles, so the next change slides from where they really were.
 */
export function useSlideNeighbours(
  row: RefObject<HTMLElement | null>,
  order: string,
  /** The tiles' width: when it changes, every tile moves at once and none of them slides. */
  tileWidth: number | null,
): void {
  const lefts = useRef(new Map<string, number>());
  const lastOrder = useRef(order);
  useLayoutEffect(() => {
    const host = row.current;
    if (!host) return;
    const reordered = order !== lastOrder.current;
    const slide =
      reordered &&
      motionAllowed() &&
      !document.body.classList.contains(RESIZING_CLASS);
    const before = lefts.current;
    lefts.current = leftsOf(host);
    lastOrder.current = order;
    // The tiles are measured once the whole commit is done: a tile added for a file still on its way is put in a
    // closed slot by another effect of the same commit, and the tiles beside it must not slide for a slot that
    // takes no room.
    if (slide) queueMicrotask(() => slideFrom(host, before));
  }, [row, order, tileWidth]);
  useLayoutEffect(() => {
    const note = () => {
      if (row.current) lefts.current = leftsOf(row.current);
    };
    document.addEventListener(SLOTS_CHANGED_EVENT, note);
    return () => document.removeEventListener(SLOTS_CHANGED_EVENT, note);
  }, [row]);
}
