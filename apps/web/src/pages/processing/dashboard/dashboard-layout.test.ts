import { describe, expect, it } from "vitest";

import tokens from "../../../styles/weir-tokens.css?raw";
import {
  BOARD_CHROME_PX,
  BAND_TALL_MAX_PX,
  BOARD_PX,
  GRID_AREAS,
  GRID_AREAS_SYSTEM,
  GRID_COLUMNS,
  LOW_COLUMNS,
  MIN_GRID_PX,
  gridRows,
  lowNeedOfGrid,
  pageLayout,
} from "./dashboard-layout";

describe("page layout from the measured main area", () => {
  it("puts the right column beside the page from 66rem of main area", () => {
    expect(pageLayout(1055, 16).sideBySide).toBe(false);
    expect(pageLayout(1056, 16).sideBySide).toBe(true);
    expect(pageLayout(2200, 16).sideBySide).toBe(true);
  });

  it("keeps the top band three across down to 56.25rem, then stacks it", () => {
    expect(pageLayout(900, 16).band).toBe("across");
    expect(pageLayout(899, 16).band).toBe("stacked");
    expect(pageLayout(352, 16).band).toBe("stacked");
  });

  it("uses the roomy layout until measured", () => {
    expect(pageLayout(0, 16)).toEqual({ sideBySide: true, band: "across" });
  });

  it("scales the thresholds with the type size", () => {
    expect(pageLayout(1150, 20).sideBySide).toBe(false);
    expect(pageLayout(990, 15).sideBySide).toBe(true);
    expect(pageLayout(989, 15).sideBySide).toBe(false);
  });
});

describe("the shared page grid", () => {
  it("has Health spanning the band and the Pipeline, and Needs you beside the lower row", () => {
    expect(GRID_COLUMNS).toBe("minmax(0, 1fr) clamp(340px, 28%, 600px)");
    expect(GRID_AREAS).toBe('"now health" "board health" "low needs"');
    expect(GRID_AREAS_SYSTEM).toBe('"band store" "mid conn" "low upd"');
    expect(LOW_COLUMNS).toBe("minmax(0, 1.15fr) minmax(0, 1fr)");
  });

  it("measures the Pipeline's chrome and keeps its comfortable row to three rows of 86px cards", () => {
    expect(BOARD_CHROME_PX).toBe(110);
    // Deluno's mockup's comfortable journey row, the same constant in both products.
    expect(BOARD_PX).toBe(398);
  });

  it("gives a window with room for the band's 20% and the Pipeline's comfortable height what they ask for", () => {
    // 780px of grid, less two 12px gaps: the band's 20% is 156px, the Pipeline's 398px, and the lower row has 202px.
    expect(gridRows(780)).toEqual({
      band: 156,
      board: 398,
      low: 202,
      template: "156px 398px 202px",
    });
  });

  it("gives way band first (never under 144px), then the Pipeline (never under three rows of 60px cards)", () => {
    // 663px: the band keeps 144px, the Pipeline goes from 398px to 328px, the lower row gets what is left.
    expect(gridRows(663)).toEqual({
      band: 144,
      board: 328,
      low: 167,
      template: "144px 328px 167px",
    });
    expect(gridRows(700).band).toBeGreaterThanOrEqual(144);
  });

  it("gives the lower row way last, down to its floor", () => {
    expect(gridRows(615)).toMatchObject({ band: 144, board: 328, low: 119 });
    expect(gridRows(300)).toMatchObject({ band: 144, board: 328, low: 110 });
  });

  it("holds a page as tall as the three least rows and the gaps between them", () => {
    expect(MIN_GRID_PX).toBe(144 + 328 + 110 + 24);
    const least = gridRows(MIN_GRID_PX);
    expect(least.band + least.board + least.low + 24).toBe(MIN_GRID_PX);
  });

  it("shares spare height: the band up to 240px, the lower row a third at most, the rest grows the cards", () => {
    // 956px: the band's 20% is 191px and takes spare up to 240px; the lower row keeps what is left.
    expect(gridRows(956)).toEqual({
      band: 240,
      board: 398,
      low: 294,
      template: "240px 398px 294px",
    });
    // A tall window: the lower row takes a third at most and the cards grow to the 130px step (36px a row).
    expect(gridRows(2000).board).toBe(BOARD_PX + 3 * (130 - 94));
    expect(gridRows(1315)).toEqual({
      band: 240,
      board: 506,
      low: 545,
      template: "240px 506px 545px",
    });
  });

  describe("when told what the lower row can use", () => {
    it("gives a tall window's spare lower-row height to the Pipeline first, growing its cards", () => {
      // 956px: the band's 20% is 191px, the Pipeline 398px and the lower row has 343px, 103px more than its 240px.
      expect(gridRows(956, { lowNeed: 240 })).toEqual({
        band: 191,
        board: 501,
        low: 240,
        template: "191px 501px 240px",
      });
    });

    it("gives what the Pipeline cannot take, past its 130px step, to the band, up to 240px", () => {
      // 1050px: 1026px of rows, the lower row has 128px beyond its 300px. The Pipeline takes 108px and the band 20px.
      expect(gridRows(1050, { lowNeed: 300 })).toEqual({
        band: 220,
        board: BOARD_PX + 3 * (130 - 94),
        low: 300,
        template: "220px 506px 300px",
      });
    });

    it("gives the band spare height beyond 240px, once the Pipeline's cards are at their tallest", () => {
      expect(BAND_TALL_MAX_PX).toBe(360);
      // 1315px: the Pipeline takes its 108px, the band 160px (200 to 360px), and the lower row keeps 425px.
      expect(gridRows(1315, { lowNeed: 300 })).toEqual({
        band: 360,
        board: 506,
        low: 425,
        template: "360px 506px 425px",
      });
      // Not told, the band stops at 240px as it did.
      expect(gridRows(1315)).toMatchObject({ band: 240, board: 506, low: 545 });
    });

    it("leaves what neither takes with the lower row", () => {
      expect(gridRows(1800, { lowNeed: 300 })).toEqual({
        band: 360,
        board: 506,
        low: 910,
        template: "360px 506px 910px",
      });
    });

    it("changes nothing for a lower row already at or under its need", () => {
      // None of these has spare height, so the rows are the ones the give-way order gives, told or not.
      for (const height of [300, 615, 663, 676, 780]) {
        expect(gridRows(height, { lowNeed: gridRows(height).low })).toEqual(
          gridRows(height),
        );
        expect(gridRows(height, { lowNeed: 5000 })).toEqual(gridRows(height));
      }
    });

    it("keeps the 1538 by 784 window's rows: 144, 328 and 180", () => {
      // The grid there is 676px tall, and the shelf's posters are held down by its height, so it needs more than it has.
      expect(gridRows(676, { lowNeed: 294 })).toEqual({
        band: 144,
        board: 328,
        low: 180,
        template: "144px 328px 180px",
      });
    });

    it("never gives the lower row less than 180px, however little it says it needs", () => {
      expect(gridRows(800, { lowNeed: 20 })).toMatchObject({
        board: 436,
        low: 180,
      });
      expect(gridRows(800, { lowNeed: 0 }).low).toBe(180);
    });

    it("keeps the rows adding up to the grid's height with the gaps", () => {
      for (const height of [676, 956, 1050, 1315, 1800]) {
        const rows = gridRows(height, { lowNeed: 300 });
        expect(rows.band + rows.board + rows.low + 24).toBe(height);
      }
    });
  });

  it("is a roomy page until the grid is measured", () => {
    expect(gridRows(0)).toEqual(gridRows(1000));
    expect(gridRows(-1)).toEqual(gridRows(1000));
  });

  it("uses the same gap as the page's --mm-panel-gap token", () => {
    const gap = /--mm-panel-gap:\s*(\d+)px/.exec(tokens);
    // The rows leave two gaps between the three of them.
    expect(gap).not.toBeNull();
    const rows = gridRows(1000);
    expect(rows.band + rows.board + rows.low + 2 * Number(gap?.[1])).toBe(1000);
  });
});

describe("what the lower row needs, from the grid's width alone", () => {
  it("gives the System view the rows Live has at the same size, as measured on the rendered Live view", () => {
    const rowsAt = (width: number, height: number) =>
      gridRows(height, { lowNeed: lowNeedOfGrid(width) }).template;

    expect(rowsAt(1226.78, 676)).toBe("144px 328px 180px");
    expect(rowsAt(1600.53, 964)).toBe("193px 448px 299px");
    expect(rowsAt(2230, 1323)).toBe("360px 506px 433px");
  });

  it("grows with the room the lower row's wider panel has", () => {
    expect(lowNeedOfGrid(2230)).toBeGreaterThan(lowNeedOfGrid(1600));
    expect(lowNeedOfGrid(1600)).toBeGreaterThan(lowNeedOfGrid(1227));
  });
});
