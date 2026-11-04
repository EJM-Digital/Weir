/**
 * The layout rules of the Dashboard's Live and System views, as plain functions of measured space. The page
 * asks its own main area, not the window, whether the right column sits beside it and whether the top band
 * keeps its three tiles across; the grid asks how tall it is and shares that height between the band, the
 * Pipeline and the lower row. The components measure; these decide.
 */
import {
  MAX_STEP_PX,
  MIN_CARD_PX,
  MORE_PX,
  PIPELINE_ROWS,
  ROW_GAP_PX,
  ROW_PX,
} from "../pipeline/pipeline-layout";
import { shelfRowNeed } from "../pipeline/shelf-layout";

/** The main area must be at least this wide, in rem, for the right column to sit beside the page. */
export const SIDE_BY_SIDE_REM = 66;
/** Below this main-area width, in rem, the top band stacks its three tiles. */
export const BAND_ACROSS_REM = 56.25;

export type PageLayout = {
  /** The right column sits beside the page, at its full height, rather than below it. */
  sideBySide: boolean;
  /** The top band keeps its three tiles across, or stacks them. */
  band: "across" | "stacked";
};

/** Unmeasured (0) is the roomy layout. */
export function pageLayout(width: number, remPx: number): PageLayout {
  if (!(width > 0)) return { sideBySide: true, band: "across" };
  return {
    sideBySide: width >= SIDE_BY_SIDE_REM * remPx,
    band: width >= BAND_ACROSS_REM * remPx ? "across" : "stacked",
  };
}

/** The gap between the grid's cards, in px: the --mm-panel-gap token (weir-tokens.css), which the page's own CSS uses. */
const GRID_GAP_PX = 12;
const GRID_GAPS_PX = 2 * GRID_GAP_PX;

/**
 * The one grid of the Live view beside the right column: the band, the Pipeline and the lower row are its rows,
 * Health spans the band and the Pipeline, and Needs you sits beside the lower row, so their tops and bottoms line
 * up across both columns. The cells name their areas in weir-processing-dashboard.css. System uses the same columns.
 */
export const GRID_COLUMNS = "minmax(0, 1fr) clamp(340px, 28%, 600px)";
export const GRID_AREAS = '"now health" "board health" "low needs"';
/**
 * The System view's grid on the same columns and rows: the band (This Weir, This computer and Processing) beside
 * Storage, Health beside Connections, and the lower row (Scheduled tasks and Log) beside Backups and tools.
 */
export const GRID_AREAS_SYSTEM = '"band store" "mid conn" "low upd"';
/** The lower row's own two columns: Just finished, and Activity. */
export const LOW_COLUMNS = "minmax(0, 1.15fr) minmax(0, 1fr)";

/**
 * What sits around the Pipeline's lanes, measured on the rendered board: the panel's top border (1px) and header
 * (48px, as tall as the shared shell standard's panel header), the ribbon with the space under it (50px), the space under the lanes
 * (10px) and the panel's bottom border (1px).
 */
export const BOARD_CHROME_PX = 110;
/**
 * The Pipeline's row at a comfortable size, Deluno's mockup's 398px (its JOURNEY_PX), kept as the same constant rather
 * than worked out from the chrome so the two products share their rows at every height.
 */
export const BOARD_PX = 398;
/** The least the Pipeline is given: its chrome, two rows of the smallest cards, one more, and the "and N more" line. */
const BOARD_MIN_PX =
  BOARD_CHROME_PX +
  PIPELINE_ROWS * (MIN_CARD_PX + ROW_GAP_PX) -
  ROW_GAP_PX +
  MORE_PX;
const BAND_MIN_PX = 144;
const BAND_MAX_PX = 200;
/** The band's share of the grid's height before its limits. */
const BAND_SHARE = 0.2;
/** Spare height goes to the band, up to this tall. */
const BAND_GROWN_MAX_PX = 240;
/**
 * A lower row that has what it needs gives the band spare height beyond BAND_GROWN_MAX_PX, once the Pipeline's cards
 * are at their tallest, up to this. The band's tiles list only whole rows, so they list more and the chart grows.
 */
export const BAND_TALL_MAX_PX = 360;
const LOW_MIN_PX = 180;
/**
 * Once the band and the Pipeline are at their least, the lower row gives way too, down to this: the window is that
 * short, and the page still fits it rather than scrolling. Set it to LOW_MIN_PX to hold the lower row's minimum
 * and let a short window scroll instead.
 */
const LOW_FLOOR_PX = 110;
/** The lower row is given room up to this before the spare goes to taller cards. */
const LOW_ROOMY_PX = 260;
/** The lower row takes at most this share of the grid, and at least LOW_ROOMY_PX. */
const LOW_SHARE_CAP = 0.33;
/** What Just finished's panel has around its row of tiles: its header, chips and padding, and the panel's borders, measured on the rendered panel. */
const SHELF_CHROME_PX = 60;
/** The panel's borders and the 14px the row's tiles keep either side: the tiles' box is the panel's width less this. */
const SHELF_ROW_MARGIN_PX = 30;

/**
 * What Just finished would report for `lowNeed`, worked out from the grid's width alone, for the System view, which has
 * no shelf of its own but shares Live's rows: its panel is the lower row's wider column (1.15 : 1 of the grid less the
 * right column and a gap), and the need is its chrome and the shelf's row at that width. Live's measured number and this
 * one agree, so no card moves when you switch between the two views.
 */
export function lowNeedOfGrid(gridWidth: number): number {
  const right = Math.min(600, Math.max(340, gridWidth * 0.28));
  const left = gridWidth - GRID_GAP_PX - right;
  const panel = ((left - GRID_GAP_PX) * 1.15) / 2.15;
  return (
    SHELF_CHROME_PX + shelfRowNeed(Math.round(panel - SHELF_ROW_MARGIN_PX))
  );
}

/** The height of the grid when nothing has been measured: a roomy page. */
const ROOMY_PX = 1000;

/** The least a page is tall while the grid fills the window: under it the page scrolls rather than crushing the rows. */
export const MIN_GRID_PX =
  BAND_MIN_PX + BOARD_MIN_PX + LOW_FLOOR_PX + GRID_GAPS_PX;

export type GridRows = {
  band: number;
  board: number;
  low: number;
  /** The grid's `grid-template-rows`. */
  template: string;
};

const clamp = (value: number, min: number, max: number) =>
  Math.min(max, Math.max(min, value));

export type GridNeeds = {
  /**
   * The height the lower row can use, in px: its panel's chrome and the shelf's posters at the width the
   * 5-poster minimum caps them to (see shelfRowNeed). Any height the lower row has beyond it is spare. Left out
   * until the shelf has measured itself.
   */
  lowNeed?: number;
};

/**
 * The rows of the grid, from the height it has. The band takes 20% of it between 144px and 200px, the Pipeline its
 * comfortable height (BOARD_PX) and the lower row at least 180px. When they do not fit they give way in that order:
 * the band first (never under 144px), then the Pipeline (never under what three rows of the smallest cards need),
 * then the lower row (never under LOW_FLOOR_PX).
 *
 * Spare height is shared once the lower row has what it needs. Told what the lower row can use (`lowNeed`, the
 * shelf's posters being capped by width rather than by height), the lower row keeps that much, at least 180px, and
 * gives the rest first to the Pipeline, whose cards grow up to the 130px step, then to the band, up to 360px; what
 * neither can take stays with the lower row. A lower row at or under its need gives nothing. Not told, the band
 * takes spare up to 240px while the lower row keeps 260px, and the lower row takes at most a third of the grid (at
 * least 260px): the rest grows the Pipeline's cards. Unmeasured, it is a roomy page.
 */
export function gridRows(
  available: number,
  { lowNeed }: GridNeeds = {},
): GridRows {
  const height = available > 0 ? available : ROOMY_PX;
  const avail = height - GRID_GAPS_PX;
  let band = clamp(Math.round(height * BAND_SHARE), BAND_MIN_PX, BAND_MAX_PX);
  let board = BOARD_PX;
  if (band + board + LOW_MIN_PX > avail) {
    band = Math.max(BAND_MIN_PX, avail - board - LOW_MIN_PX);
  }
  if (band + board + LOW_MIN_PX > avail) {
    board = Math.max(BOARD_MIN_PX, avail - band - LOW_MIN_PX);
  }
  let low = avail - band - board;
  const growBand = (keep: number, max: number) => {
    const grow = Math.min(low - keep, max - band);
    if (grow > 0) {
      band += grow;
      low -= grow;
    }
  };
  const growBoard = (keep: number) => {
    const grow = Math.min(low - keep, PIPELINE_ROWS * (MAX_STEP_PX - ROW_PX));
    if (grow > 0) {
      board += grow;
      low -= grow;
    }
  };
  if (lowNeed === undefined) {
    growBand(LOW_ROOMY_PX, BAND_GROWN_MAX_PX);
    growBoard(Math.max(LOW_ROOMY_PX, Math.round(avail * LOW_SHARE_CAP)));
  } else {
    const keep = Math.max(LOW_MIN_PX, lowNeed);
    growBoard(keep);
    growBand(keep, BAND_TALL_MAX_PX);
  }
  band = Math.round(band);
  board = Math.round(board);
  low = Math.max(LOW_FLOOR_PX, Math.round(low));
  return { band, board, low, template: `${band}px ${board}px ${low}px` };
}
