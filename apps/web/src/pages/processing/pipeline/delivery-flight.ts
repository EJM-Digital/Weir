/**
 * The one movement on the Pipeline that is not a number changing: a file that has been delivered shows
 * it first (its card full and green for a moment), and then its poster flies from the card to the same
 * file on the Just finished shelf, where it lands in its place. The shelf's tile is kept out of sight
 * while the card still shows "Delivered", so the file is never in two places at once.
 *
 * Both the card's poster and the shelf's are 2:3, and the poster that flies is a copy of the card's, scaled
 * by one factor so it keeps its shape. It follows the shelf tile's place frame by frame, so it lands on the
 * tile even if the shelf slid or the page scrolled during the flight, and then gives way to the real tile
 * by fading rather than being swapped in a single frame (see flight-path for the path).
 *
 * With reduced motion, a page nobody is looking at, or a window being resized, nothing flies and the shelf
 * tile simply shows. The two components find each other in the document through the attributes below.
 */
import { motionAllowed } from "../../../lib/ui/motion-allowed";
import {
  FLIGHT_MS,
  LANDING_FADE_MS,
  easeInOut,
  poseAt,
  type Spot,
} from "./flight-path";
import {
  FLYING_ATTRIBUTE,
  HELD_ATTRIBUTE,
  announceSlotsChanged,
  closeSlot,
  openSlot,
  settleSlot,
} from "./shelf-slots";
import { RESIZING_CLASS } from "./use-still-while-resizing";

/** Set by the shelf on each tile's art, to the path of the file it shows. */
export const SHELF_TILE_ATTRIBUTE = "data-shelf-path";

const FLIGHT_Z_INDEX = "60";
/** A flight that has not finished this long after it should have (a page whose frames stopped) is ended anyway. */
const STALL_GRACE_MS = 500;

/** A delivered file's poster as it was drawn on its card, and what it needs to be drawn again in flight. */
export type Delivery = {
  path: string;
  /** A copy of the poster as drawn: the picture, or the initials, so the same thing flies to the shelf. */
  look: HTMLElement;
  /** Where the poster was on screen. */
  from: DOMRect;
};

function shelfTiles(): HTMLElement[] {
  return Array.from(
    document.querySelectorAll<HTMLElement>(`[${SHELF_TILE_ATTRIBUTE}]`),
  );
}

/** Keeps the tile out of sight and its slot closed, so the shelf does not change until the poster takes off. */
function hold(tile: HTMLElement): void {
  tile.setAttribute(HELD_ATTRIBUTE, "");
  tile.style.visibility = "hidden";
  closeSlot(tile);
  announceSlotsChanged();
}

/** Shows the tile under the poster that is landing on it, in a slot that has opened as far as it goes. */
function reveal(tile: HTMLElement): void {
  tile.removeAttribute(HELD_ATTRIBUTE);
  tile.style.visibility = "";
  settleSlot(tile);
}

function release(tile: HTMLElement): void {
  reveal(tile);
  tile.removeAttribute(FLYING_ATTRIBUTE);
  announceSlotsChanged();
}

/** Keeps the shelf's tiles for the files whose cards still say "Delivered" out of sight, and shows every other. */
export function holdShelfTiles(paths: ReadonlySet<string>): void {
  for (const tile of shelfTiles()) {
    if (tile.hasAttribute(FLYING_ATTRIBUTE)) continue;
    const wanted =
      motionAllowed() &&
      paths.has(tile.getAttribute(SHELF_TILE_ATTRIBUTE) as string);
    if (wanted && !tile.hasAttribute(HELD_ATTRIBUTE)) hold(tile);
    if (!wanted && tile.hasAttribute(HELD_ATTRIBUTE)) release(tile);
  }
}

function spotOf(element: HTMLElement): Spot {
  const { left, top, width } = element.getBoundingClientRect();
  return { left, top, width };
}

const radiusOf = (element: HTMLElement): number =>
  Number.parseFloat(getComputedStyle(element).borderTopLeftRadius) || 0;

/**
 * The copy of the card's poster, in a frame laid out at the card's size and moved only by its transform. The frame
 * stands in for the tile the poster sat in, so the poster's own sizes, which are shares of that tile, stay the same
 * once it is lifted out of the card.
 */
function ghostOf({ look, from }: Delivery): HTMLElement {
  const ghost = document.createElement("div");
  ghost.className = "mm-tile--flying";
  ghost.setAttribute("aria-hidden", "true");
  Object.assign(ghost.style, {
    position: "fixed",
    left: "0",
    top: "0",
    width: `${from.width}px`,
    height: `${from.height}px`,
    overflow: "hidden",
    zIndex: FLIGHT_Z_INDEX,
    pointerEvents: "none",
    transformOrigin: "top left",
    willChange: "transform",
  });
  ghost.appendChild(look);
  return ghost;
}

/**
 * Flies a copy of the delivered poster to the file's tile on the shelf. Without a shelf tile for the file, or
 * where movement is not allowed, there is nothing to fly to and the shelf's tile is simply shown.
 */
export function flyToShelf(delivery: Delivery): void {
  const target = shelfTiles().find(
    (tile) => tile.getAttribute(SHELF_TILE_ATTRIBUTE) === delivery.path,
  );
  if (!target) return;
  const { from } = delivery;
  const to = target.getBoundingClientRect();
  if (
    !motionAllowed() ||
    document.body.classList.contains(RESIZING_CLASS) ||
    to.width === 0 ||
    to.height === 0 ||
    from.width === 0 ||
    from.height === 0
  ) {
    release(target);
    return;
  }
  const ghost = ghostOf(delivery);
  document.body.appendChild(ghost);
  hold(target);
  target.setAttribute(FLYING_ATTRIBUTE, "");
  announceSlotsChanged();
  const fromRadius = radiusOf(delivery.look);
  const toRadius = radiusOf(target);
  const startedAt = performance.now();
  let frame = 0;
  let stall = 0;
  let landed = false;

  const end = () => {
    cancelAnimationFrame(frame);
    window.clearTimeout(stall);
    ghost.remove();
    release(target);
  };
  stall = window.setTimeout(end, FLIGHT_MS + LANDING_FADE_MS + STALL_GRACE_MS);

  const step = (now: number) => {
    if (!target.isConnected) {
      end();
      return;
    }
    const elapsed = Math.max(0, now - startedAt);
    const progress = easeInOut(Math.min(1, elapsed / FLIGHT_MS));
    // The slot opens by the flight's own curve, so the poster lands as it finishes opening.
    if (!landed) openSlot(target, progress);
    const pose = poseAt(progress, from, spotOf(target));
    ghost.style.transform = `translate(${pose.x}px, ${pose.y}px) scale(${pose.scale})`;
    ghost.style.borderRadius = `${(fromRadius + (toRadius - fromRadius) * progress) / pose.scale}px`;
    if (elapsed >= FLIGHT_MS && !landed) {
      landed = true;
      reveal(target);
    }
    const fade = Math.max(0, (elapsed - FLIGHT_MS) / LANDING_FADE_MS);
    if (fade >= 1) {
      end();
      return;
    }
    if (fade > 0) ghost.style.opacity = String(1 - fade);
    frame = requestAnimationFrame(step);
  };
  frame = requestAnimationFrame(step);
}
