/**
 * The shelf's tiles are sized from the height of the row they stand in, exactly 2:3, and only whole tiles
 * are drawn: a row too narrow for one more simply shows one fewer.
 */

/** The height of every line of a caption (weir-shelf.css: --shelf-line). */
export const CAPTION_LINE_PX = 15;
/** The space between a tile's poster and its title, and between slots. */
const CAPTION_GAP_PX = 6;
const SLOT_GAP_PX = 2;
/** The compact caption under a tile: the title and the status line, with 2px between them and 6px above them: 38px. */
export const CAPTION_PX = CAPTION_GAP_PX + 2 * CAPTION_LINE_PX + SLOT_GAP_PX;
/** The tiny caption under a tile: the status line alone, 6px under the poster: 21px. */
export const TINY_CAPTION_PX = CAPTION_GAP_PX + CAPTION_LINE_PX;
/** The full caption: the compact one and the detail and when lines, each 2px after the one before: 72px. */
export const FULL_CAPTION_PX = CAPTION_PX + 2 * (CAPTION_LINE_PX + SLOT_GAP_PX);
/** The space between tiles. */
export const TILE_GAP_PX = 12;
/** The padding round the row, which the tiles do not use (the row's own padding: 7 above, 5 below and either side). */
const ROW_PAD_Y = 12;
const ROW_PAD_X = 10;
/** The least art a tile has under a caption: 64px wide, the narrowest that wears the workflow's tag (FULL_DECORATIONS_MIN_PX). */
const CAPTIONED_ART_PX = 96;
/** A row at least this tall (inside its padding) has room for a caption under each tile, and the art above it. */
const CAPTION_MIN_ROW_PX = CAPTIONED_ART_PX + CAPTION_PX;
const MIN_ART_PX = 36;
/** A row at least this tall (inside its padding) has room for the tiny caption under the least art. */
const TINY_MIN_ROW_PX = MIN_ART_PX + TINY_CAPTION_PX;
/** The narrowest a tile gets, however many must stand in the row: 2:3 under the least art. */
const MIN_TILE_WIDTH_PX = Math.floor(MIN_ART_PX / 1.5);
/** No tile grows taller than this, however tall the row. */
const MAX_ART_PX = 255;

/**
 * What goes under each tile. The compact caption is the title and how the file came out; the full one adds what was
 * done and when, and is chosen only where the posters have all the height the five-tile rule lets them use and room
 * for it as well. Where the row is too short for the compact one, the tiny caption is how the file came out alone,
 * in the fewest words that fit under the poster. A row too short for even that shows tiles alone.
 */
export type CaptionTier = "none" | "tiny" | "compact" | "full";

/** The height a tier's caption takes under the poster. */
const CAPTION_HEIGHT_PX: Record<CaptionTier, number> = {
  none: 0,
  tiny: TINY_CAPTION_PX,
  compact: CAPTION_PX,
  full: FULL_CAPTION_PX,
};

/** The caption a row of `inner` px (inside its padding) can hold, short of the full one. */
function captionFor(inner: number): CaptionTier {
  if (inner >= CAPTION_MIN_ROW_PX) return "compact";
  return inner >= TINY_MIN_ROW_PX ? "tiny" : "none";
}

export type ShelfFit = {
  caption: CaptionTier;
  /** The width of a tile; its height is exactly one and a half times this. */
  width: number;
};

/** The fewest whole tiles the shelf shows across, however wide the window: the tiles narrow to fit them. */
export const SHELF_MIN_TILES = 5;

/** The widest tile a row of `rowWidth` px allows: `SHELF_MIN_TILES` whole tiles across, none taller than the tallest art. */
function widestTile(rowWidth: number): number {
  const forLeast =
    Math.floor((rowWidth - ROW_PAD_X + TILE_GAP_PX) / SHELF_MIN_TILES) -
    TILE_GAP_PX;
  return Math.max(
    MIN_TILE_WIDTH_PX,
    Math.min(Math.floor(MAX_ART_PX / 1.5), forLeast),
  );
}

/** The art of a tile `width` wide: exactly 2:3, taken up to a whole pixel. */
const artOf = (width: number) => Math.max(MIN_ART_PX, Math.ceil(width * 1.5));

/**
 * The tile size, from the row's own measured height, and no wider than lets `SHELF_MIN_TILES` whole tiles fit across
 * the row's measured width (when it is known). Tiles stay exactly 2:3: a narrower tile is also shorter, never
 * stretched, and none is narrower than the least art allows. A row with the height for the widest tile it allows,
 * the full caption under it, and a tile wide enough to wear one (FULL_DECORATIONS_MIN_PX) gets the full caption;
 * otherwise the compact one where the row is tall enough, the tiny one where it is tall enough for that, and none
 * where it is not.
 */
export function shelfFit(rowHeight: number, rowWidth = 0): ShelfFit {
  const inner = rowHeight - ROW_PAD_Y;
  const widest = rowWidth > 0 ? widestTile(rowWidth) : null;
  if (
    widest !== null &&
    widest >= FULL_DECORATIONS_MIN_PX &&
    inner >= artOf(widest) + FULL_CAPTION_PX
  ) {
    return { caption: "full", width: widest };
  }
  const caption = captionFor(inner);
  const art = Math.max(
    MIN_ART_PX,
    Math.min(MAX_ART_PX, inner - CAPTION_HEIGHT_PX[caption]),
  );
  const byHeight = Math.floor(art / 1.5);
  return {
    caption,
    width: widest === null ? byHeight : Math.min(byHeight, widest),
  };
}

/**
 * A poster narrower than this is too small for the workflow's tag, whose name would cover a third of the art or more
 * (the tag is 19px tall and as wide as its name, up to the poster's width less 12px).
 */
export const FULL_DECORATIONS_MIN_PX = 64;

export type PosterSize = "small" | "full";

/**
 * How a poster of this width is dressed. A "full" poster wears the workflow's name in a tag; a "small" one wears a
 * slim strip in its workflow's colour, and the name goes into the tile's tooltip and name. Not measured yet, it is
 * "full".
 */
export function posterSize(width: number | null): PosterSize {
  return width !== null && width < FULL_DECORATIONS_MIN_PX ? "small" : "full";
}

/** Whole tiles across a row of `rowWidth` px, at least one; never a part of one. */
export function tilesAcross(rowWidth: number, tileWidth: number): number {
  return Math.max(
    1,
    Math.floor(
      (rowWidth - ROW_PAD_X + TILE_GAP_PX) / (tileWidth + TILE_GAP_PX),
    ),
  );
}
