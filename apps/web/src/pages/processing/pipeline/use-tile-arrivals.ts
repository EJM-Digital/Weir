import { useLayoutEffect, useRef, type RefObject } from "react";

import { motionAllowed } from "../../../lib/ui/motion-allowed";
import { HELD_ATTRIBUTE } from "./shelf-slots";
import { TILE_KEY_ATTRIBUTE } from "./use-slide-neighbours";
import { RESIZING_CLASS } from "./use-still-while-resizing";

const ARRIVE_MS = 800;
const ARRIVE_EASING = "cubic-bezier(.2, .8, .2, 1)";
const ARRIVE_FRAMES: Keyframe[] = [
  { opacity: 0, transform: "translateY(14px) scale(0.92)" },
  { opacity: 1, transform: "none" },
];

/**
 * A tile that is new to the shelf arrives: it fades and rises into place. A tile the shelf only now has room for
 * (the window grew) is not new, so it is simply there; nothing arrives while the window is being resized, or when
 * movement is not allowed; and a tile held for the poster that is about to fly to it has no arrival of its own,
 * the flight is its arrival. `filed` is the key of every tile the shelf has, joined, shown or not.
 */
export function useTileArrivals(
  row: RefObject<HTMLElement | null>,
  filed: string,
): void {
  const before = useRef<ReadonlySet<string> | null>(null);
  useLayoutEffect(() => {
    const host = row.current;
    if (!host) return;
    const had = before.current;
    before.current = new Set(filed.split("|"));
    if (!motionAllowed() || document.body.classList.contains(RESIZING_CLASS))
      return;
    for (const tile of host.querySelectorAll<HTMLElement>(
      `[${TILE_KEY_ATTRIBUTE}]`,
    )) {
      if (had?.has(tile.getAttribute(TILE_KEY_ATTRIBUTE) as string)) continue;
      if (tile.querySelector(`[${HELD_ATTRIBUTE}]`)) continue;
      if (typeof tile.animate === "function") {
        tile.animate(ARRIVE_FRAMES, {
          duration: ARRIVE_MS,
          easing: ARRIVE_EASING,
        });
      }
    }
  }, [row, filed]);
}
