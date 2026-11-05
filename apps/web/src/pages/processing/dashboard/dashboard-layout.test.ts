import { describe, expect, it } from "vitest";

import tokens from "../../../styles/weir-tokens.css?raw";
import {
  BOARD_CHROME_PX,
  BOARD_PX,
  GRID_AREAS,
  GRID_AREAS_SYSTEM,
  GRID_COLUMNS,
  LOW_COLUMNS,
  MIN_GRID_PX,
  gridRows,
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

  it("takes the grid's height and nothing else, so what a row holds never moves the others", () => {
    // Deluno's lower row needs 375px and Weir's 379px: both draw the same rows at the same height.
    expect(gridRows.length).toBe(1);
    expect(gridRows(1132).template).toBe("226px 506px 376px");
  });

  it("keeps the band to the grid's height: 20% of it between 144px and 240px, never spare", () => {
    expect(gridRows(780).band).toBe(156);
    expect(gridRows(1132).band).toBe(226);
    expect(gridRows(1200).band).toBe(240);
    expect(gridRows(2000).band).toBe(240);
    expect(gridRows(300).band).toBe(144);
  });

  it("grows the Pipeline's cards with spare height while the lower row keeps a third of the grid, at least 260px", () => {
    // 956px: 932px of rows; the band is 191px and the Pipeline 398px, so the lower row has 343px and keeps 308px.
    expect(gridRows(956)).toEqual({
      band: 191,
      board: 433,
      low: 308,
      template: "191px 433px 308px",
    });
    // 1132px: the Pipeline takes all the spare it can, the 130px step (36px a row).
    expect(gridRows(1132)).toEqual({
      band: 226,
      board: 506,
      low: 376,
      template: "226px 506px 376px",
    });
    expect(gridRows(2000).board).toBe(BOARD_PX + 3 * (130 - 94));
    expect(gridRows(2000)).toMatchObject({ band: 240, board: 506, low: 1230 });
  });

  it("gives the rows of a 1366 by 768 window's grid", () => {
    // The page there leaves the grid about 660px tall.
    expect(gridRows(660)).toEqual({
      band: 144,
      board: 328,
      low: 164,
      template: "144px 328px 164px",
    });
  });

  it("keeps the rows adding up to the grid's height with the gaps", () => {
    for (const height of [676, 956, 1050, 1132, 1315, 1800]) {
      const rows = gridRows(height);
      expect(rows.band + rows.board + rows.low + 24).toBe(height);
    }
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
