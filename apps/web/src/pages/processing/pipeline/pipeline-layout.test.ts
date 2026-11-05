import { describe, expect, it } from "vitest";

import {
  MIN_STATION_REM,
  PIPELINE_ROWS,
  boardMode,
  cardSize,
  cardsBudget,
  detailRoom,
  lanesHeight,
  stackedBoardBudget,
} from "./pipeline-layout";
import {
  FULL_CAPTION_PX,
  TINY_CAPTION_PX,
  FULL_DECORATIONS_MIN_PX,
  posterSize,
  SHELF_MIN_TILES,
  shelfFit,
  tilesAcross,
} from "./shelf-layout";

describe("the card size", () => {
  it("is sized from the lanes' height: three rows always, 60px cards at the least, a 130px step at the most", () => {
    expect(cardSize(undefined)).toEqual({
      card: 86,
      step: 94,
      tile: 69,
      oneLine: false,
    });
    // (lanes 208 - 22 for the "and N more" line + 8) / 3 = 64.67, so the least: step 68, card 60, a one-line title
    expect(cardSize(208)).toEqual({
      card: 60,
      step: 68,
      tile: 43,
      oneLine: true,
    });
    // 292px of lanes: step floor((292 - 22 + 8) / 3) = 92, card 84
    expect(cardSize(292)).toEqual({
      card: 84,
      step: 92,
      tile: 67,
      oneLine: false,
    });
    expect(cardSize(256)).toEqual({
      card: 72,
      step: 80,
      tile: 55,
      oneLine: true,
    });
    // a tall window: the step stops at 130, a 122px card
    expect(cardSize(900)).toEqual({
      card: 122,
      step: 130,
      tile: 105,
      oneLine: false,
    });
    expect(cardSize(40)).toMatchObject({ card: 60, step: 68 });
  });

  it("makes the tile 2:3 at every size: its width is (card - 17) / 1.5", () => {
    for (const room of [208, 256, 292, 380, 900]) {
      const { card, tile } = cardSize(room);
      expect(tile).toBe(card - 17);
    }
  });

  it("holds exactly three rows and the line under them in the room it was sized for", () => {
    expect(PIPELINE_ROWS).toBe(3);
    expect(lanesHeight(cardSize(undefined))).toBe(296);
    expect(lanesHeight(cardSize(0))).toBe(218);
    expect(lanesHeight(cardSize(292))).toBeLessThanOrEqual(292);
  });
});

describe("the board on a page that scrolls", () => {
  it("may be at most most of a window tall, and nothing without a measured window", () => {
    expect(stackedBoardBudget(1000)).toBe(700);
    expect(stackedBoardBudget(844)).toBe(590);
    expect(stackedBoardBudget(0)).toBeUndefined();
  });

  it("leaves the cards what the chrome around the lanes does not use", () => {
    expect(cardsBudget(500, 120)).toBe(380);
    expect(cardsBudget(undefined, 120)).toBeUndefined();
    expect(cardsBudget(100, 120)).toBe(0);
  });
});

describe("the board's mode", () => {
  it("keeps the stations side by side while each has room, and stacks them when it does not", () => {
    expect(boardMode(5 * MIN_STATION_REM * 16, 5, 16)).toBe("columns");
    expect(boardMode(5 * MIN_STATION_REM * 16 - 1, 5, 16)).toBe("stacked");
    expect(boardMode(390, 5, 16)).toBe("stacked");
    // Nothing measured yet: the columns.
    expect(boardMode(0, 5, 16)).toBe("columns");
  });
});

describe("the detail lines a card has room for", () => {
  it("counts whole 15px lines under the card's block, keeping 8px under them", () => {
    expect(detailRoom(419, 400)).toBe(0);
    expect(detailRoom(500, 440)).toBe(3);
    expect(detailRoom(100, 120)).toBe(0);
  });
});

describe("the shelf's tiles", () => {
  it("are sized from the row's height, exactly 2:3, with a caption only when the row is tall enough", () => {
    // 190px of row less 12px of padding and a 38px caption = 140px of art, 93px wide.
    expect(shelfFit(190)).toEqual({ caption: "compact", width: 93 });
    // 100px of row less 12px of padding and a 21px caption = 67px of art, 44px wide.
    expect(shelfFit(100)).toEqual({ caption: "tiny", width: 44 });
    // 60px of row less 12px of padding has no room for the least art and a line under it.
    expect(shelfFit(60)).toEqual({ caption: "none", width: 32 });
    expect(shelfFit(10).width).toBe(24);
    expect(shelfFit(2000).width).toBe(170);
  });

  it("shows at least five whole tiles across, by making the tiles narrower, never wider than their height allows", () => {
    expect(SHELF_MIN_TILES).toBe(5);
    // A tall row of 570px: 250px of row would give 133px tiles, but five must stand across.
    const tall = shelfFit(250, 570);
    expect(tall.width).toBe(102);
    expect(tilesAcross(570, tall.width)).toBeGreaterThanOrEqual(5);
    expect(shelfFit(250).width).toBe(133);
    // A short row already holds smaller tiles than five need: the height decides.
    expect(shelfFit(100, 570)).toEqual(shelfFit(100));
    // The tile stays exactly 2:3 and is never stretched: its height is one and a half times its width, within its row.
    expect(tall.width * 1.5).toBeLessThan(250 - 12);
    // Captions follow the row's height.
    expect(shelfFit(250, 570).caption).toBe("full");
    expect(shelfFit(200, 570).caption).toBe("compact");
    expect(shelfFit(100, 570).caption).toBe("tiny");
    expect(shelfFit(60, 570).caption).toBe("none");
  });

  it("have the tiny caption, a single status line, where the row has no room for the compact one", () => {
    expect(TINY_CAPTION_PX).toBe(21);
    // The compact caption needs 96px of art and 38px under it, 134px inside the padding: 146px of row.
    expect(shelfFit(146).caption).toBe("compact");
    expect(shelfFit(145).caption).toBe("tiny");
    // The tiny one needs the least art, 36px, and 21px under it, 57px inside the padding: 69px of row.
    expect(shelfFit(69).caption).toBe("tiny");
    expect(shelfFit(68).caption).toBe("none");
  });

  it("make the art a line shorter where the tiny caption stands under it", () => {
    // 100px of row less 12px of padding and 21px for the line: 67px of art. Without a line it would be 88px.
    expect(shelfFit(100).width).toBe(44);
    expect(shelfFit(60).width).toBe(32);
    expect(shelfFit(100, 570)).toEqual(shelfFit(100));
  });

  it("never makes a tile narrower than the least art, however narrow the row", () => {
    expect(shelfFit(250, 100).width).toBe(24);
  });

  it("have the full caption under tiles 64px wide and up, and not under narrower ones", () => {
    expect(FULL_CAPTION_PX).toBe(72);
    // 64px tiles take five across 368px: 5 * 64 + 4 * 12 + 10 = 378.
    expect(shelfFit(10_000, 378).caption).toBe("full");
    expect(shelfFit(10_000, 377).caption).toBe("compact");
  });

  it("dresses a poster fully from 64px wide, and thinly below that: a small one has no room for the tag", () => {
    expect(FULL_DECORATIONS_MIN_PX).toBe(64);
    expect(posterSize(64)).toBe("full");
    expect(posterSize(102)).toBe("full");
    expect(posterSize(155)).toBe("full");
    expect(posterSize(63)).toBe("small");
    expect(posterSize(42)).toBe("small");
    expect(posterSize(24)).toBe("small");
    // Until the shelf is measured, a tile is dressed fully.
    expect(posterSize(null)).toBe("full");
  });

  it("makes the posters of a short row small and those of a tall one full", () => {
    expect(posterSize(shelfFit(100, 570).width)).toBe("small");
    expect(posterSize(shelfFit(250, 570).width)).toBe("full");
  });

  it("draws whole tiles only", () => {
    // 96px tiles with a 12px gap in a 450px row (440px inside its padding): 4 whole ones, never a part of one.
    expect(tilesAcross(450, 96)).toBe(4);
    expect(tilesAcross(440, 96)).toBe(4);
    expect(tilesAcross(429, 96)).toBe(3);
    expect(tilesAcross(30, 96)).toBe(1);
  });
});
