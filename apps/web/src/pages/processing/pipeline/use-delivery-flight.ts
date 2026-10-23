import { useLayoutEffect, useRef, type RefObject } from "react";

import type { PipelineCard } from "./pipeline-card-types";
import { flyToShelf, holdShelfTiles, type Delivery } from "./delivery-flight";

/** Set on each card's tile, to the card's key: where a tile is found to read its place on screen. */
export const TILE_ATTRIBUTE = "data-pipeline-tile";

function tilesByKey(root: HTMLElement): Map<string, HTMLElement> {
  const tiles = new Map<string, HTMLElement>();
  for (const tile of root.querySelectorAll<HTMLElement>(
    `[${TILE_ATTRIBUTE}]`,
  )) {
    tiles.set(tile.getAttribute(TILE_ATTRIBUTE) as string, tile);
  }
  return tiles;
}

/**
 * Hands each delivered card's tile to `onDelivered` the moment its "Delivered" hold ends, and keeps the
 * shelf's tile for the same file out of sight until then. `endedKeys` are the keys of every ended card
 * before the page's filter, so a card hidden by the filter is not mistaken for one whose hold ended.
 */
export function useDeliveryFlight(
  root: RefObject<HTMLElement | null>,
  cards: readonly PipelineCard[],
  endedKeys: ReadonlySet<string>,
  onDelivered: (delivery: Delivery) => void = flyToShelf,
): void {
  const shown = useRef(new Map<string, Delivery>());
  useLayoutEffect(() => {
    const host = root.current;
    if (!host) return undefined;
    const tiles = tilesByKey(host);
    const delivered = cards.filter((card) => card.end === "delivered");
    const now = new Map<string, Delivery>();
    for (const card of delivered) {
      const tile = tiles.get(card.key);
      const look = tile?.firstElementChild?.cloneNode(true);
      if (tile && look instanceof HTMLElement) {
        now.set(card.key, {
          path: card.path,
          look,
          from: tile.getBoundingClientRect(),
        });
      }
    }
    for (const [key, delivery] of shown.current) {
      if (!now.has(key) && !endedKeys.has(key)) onDelivered(delivery);
    }
    shown.current = now;
    holdShelfTiles(new Set(delivered.map((card) => card.path)));
  });
  useLayoutEffect(() => () => holdShelfTiles(new Set()), []);
}
