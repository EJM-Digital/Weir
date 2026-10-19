import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { handedBack } from "../handed-back-model";
import type { Filter } from "../processing-filter";
import type { HandedBackSummary } from "../use-handed-back";
import { NEEDS_PANEL_ID } from "./show-needs-panel";
import { TodayTile } from "./today-tile";

const NOW = Date.parse("2026-08-18T10:00:00Z");
const stats: { files_processed: number; net_space_saved_bytes: number | null } =
  { files_processed: 38, net_space_saved_bytes: 44_236_078_284 };
const needsYou = { count: 0 };
/** The workflow the tile asked the figures and the count for, each time. */
const figuresAsked: (number | null)[] = [];
/** The kind of work it asked them for, each time. */
const filtersAsked: Filter[] = [];
const summary: { value: HandedBackSummary } = {
  value: { handed: handedBack([], NOW), total: 0, partial: false },
};

const settings = { timezone: "UTC" };
vi.mock("../../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => ({ data: { app_timezone: settings.timezone } }),
}));
vi.mock("./use-today-figures", () => ({
  useTodayFigures: (workflowId: number | null, filter: Filter) => {
    figuresAsked.push(workflowId);
    filtersAsked.push(filter);
    return {
      figures: {
        cleaned: stats.files_processed,
        savedBytes: stats.net_space_saved_bytes,
      },
      error: null,
    };
  },
}));
vi.mock("./use-needs-you", () => ({
  useNeedsYou: (workflowId: number | null, filter: Filter) => {
    figuresAsked.push(workflowId);
    filtersAsked.push(filter);
    return { groups: [], files: Array.from({ length: needsYou.count }) };
  },
}));
vi.mock("../use-handed-back", () => ({
  useHandedBack: () => summary.value,
}));

function finishedAt(
  minutesAgo: number,
  kind: "cleaned" | "already" | "failed" = "cleaned",
) {
  return {
    id: minutesAgo,
    source: "download" as const,
    kind,
    relativePath: "Heat.mkv",
    libraryId: 1,
    savedBytes: null,
    removedAudio: 0,
    removedSubtitles: 0,
    sentence: null,
    finishedAt: new Date(NOW - minutesAgo * 60_000).toISOString(),
  };
}

function renderTile(workflowId?: number | null, filter: Filter = "all") {
  render(
    <MemoryRouter>
      <TodayTile filter={filter} now={NOW} workflowId={workflowId} />
    </MemoryRouter>,
  );
  return screen.getByRole("region", { name: "Today" });
}

afterEach(() => {
  document.getElementById(NEEDS_PANEL_ID)?.remove();
});

beforeEach(() => {
  needsYou.count = 0;
  settings.timezone = "UTC";
  figuresAsked.length = 0;
  filtersAsked.length = 0;
  stats.net_space_saved_bytes = 44_236_078_284;
  summary.value = { handed: handedBack([], NOW), total: 0, partial: false };
});

describe("the Today tile", () => {
  it("asks for every workflow's figures unless it is given one", () => {
    renderTile();

    expect(new Set(figuresAsked)).toEqual(new Set([null]));
  });

  it("asks for one workflow's figures and need-a-look count when narrowed to it", () => {
    renderTile(3);

    expect(new Set(figuresAsked)).toEqual(new Set([3]));
  });

  it("asks for the figures and need-a-look count of the kind of work chosen, so they agree with the panels", () => {
    renderTile(null, "library");

    expect(new Set(filtersAsked)).toEqual(new Set(["library"]));
  });

  it("says nothing of space saved when none is recorded for the work shown", () => {
    stats.net_space_saved_bytes = null;
    const tile = renderTile(null, "library");

    expect(within(tile).getByTestId("live-done-today")).toHaveTextContent("38");
    expect(tile).not.toHaveTextContent("saved");
  });

  it("says how many were cleaned today and how much space that saved", () => {
    const tile = renderTile();

    expect(within(tile).getByTestId("live-done-today")).toHaveTextContent("38");
    expect(tile).toHaveTextContent("cleaned");
    expect(tile).toHaveTextContent("41.20 GB saved");
  });

  it("links to the files that need a look when there are any, and says nothing when there are none", () => {
    expect(renderTile()).not.toHaveTextContent("need a look");
  });

  it("names the number that need a look, and takes the person to the Needs you panel", () => {
    needsYou.count = 12;
    const panel = document.createElement("section");
    panel.id = NEEDS_PANEL_ID;
    panel.tabIndex = -1;
    const scrollIntoView = vi.fn();
    panel.scrollIntoView = scrollIntoView;
    document.body.append(panel);
    const tile = renderTile();

    fireEvent.click(
      within(tile).getByRole("button", { name: "12 need a look →" }),
    );

    expect(scrollIntoView).toHaveBeenCalled();
    expect(panel).toHaveFocus();
  });

  it("says nothing finished in the last 2 hours rather than drawing a chart of nothing", () => {
    const tile = renderTile();

    expect(within(tile).getByTestId("live-handed-back-sum")).toHaveTextContent(
      "Nothing finished in the last 2 hours.",
    );
  });

  it("says in the chart itself when nothing finished, and draws no such line when something did", () => {
    const tile = renderTile();
    expect(tile.querySelector(".mm-today-chart__empty")).toHaveTextContent(
      "Nothing in the last 2 hours",
    );

    cleanup();
    const handed = handedBack([finishedAt(3)], NOW);
    summary.value = { handed, total: handed.totals.all, partial: false };

    expect(renderTile().querySelector(".mm-today-chart__empty")).toBeNull();
  });

  it("describes the two hours in words, for a screen reader", () => {
    const handed = handedBack([finishedAt(3), finishedAt(8)], NOW);
    summary.value = { handed, total: handed.totals.all, partial: false };
    const tile = renderTile();

    expect(within(tile).getByTestId("live-handed-back-sum")).toHaveTextContent(
      "2 files finished in the last 2 hours: 2 cleaned.",
    );
  });

  it("lets the keyboard walk the chart and reads each five minutes of it", () => {
    const handed = handedBack(
      [finishedAt(3), finishedAt(3), finishedAt(8)],
      NOW,
    );
    summary.value = { handed, total: handed.totals.all, partial: false };
    const tile = renderTile();
    const chart = within(tile).getByRole("slider", {
      name: "Files finished, five minutes to a point",
    });

    chart.focus();
    expect(chart).toHaveAttribute(
      "aria-valuetext",
      expect.stringContaining("nothing"),
    );

    fireEvent.keyDown(chart, { key: "ArrowLeft" });

    expect(chart).toHaveAttribute(
      "aria-valuetext",
      expect.stringContaining("2 cleaned"),
    );
  });

  it("labels the 100% and 50% gridlines with whole numbers and the time span in one label", () => {
    const handed = handedBack(
      [finishedAt(3), finishedAt(3), finishedAt(8)],
      NOW,
    );
    summary.value = { handed, total: handed.totals.all, partial: false };
    const tile = renderTile();

    expect(tile).toHaveTextContent("4 files");
    expect(tile).toHaveTextContent("2 files");
    expect(tile).toHaveTextContent("2 h ago – now");
  });
});

describe("the split of the last two hours, under the chart", () => {
  const LEGEND = '[data-testid="today-legend"]';

  it("counts the files cleaned, already clean and needing a look, in that order", () => {
    const handed = handedBack(
      [
        finishedAt(3),
        finishedAt(4),
        finishedAt(5),
        finishedAt(6, "already"),
        finishedAt(7, "failed"),
        finishedAt(8, "failed"),
      ],
      NOW,
    );
    summary.value = { handed, total: handed.totals.all, partial: false };
    const tile = renderTile();

    const parts = Array.from(tile.querySelectorAll(`${LEGEND} > span`));

    expect(parts.map((part) => part.textContent)).toEqual([
      "3 cleaned",
      "1 already clean",
      "2 need a look",
    ]);
    expect(
      parts.map((part) =>
        part.querySelector(".mm-status-dot")?.getAttribute("data-status"),
      ),
    ).toEqual(["done", "done", "attention"]);
  });

  it("leaves out a way of ending that no file had", () => {
    const handed = handedBack([finishedAt(3), finishedAt(7, "failed")], NOW);
    summary.value = { handed, total: handed.totals.all, partial: false };
    const tile = renderTile();

    expect(tile.querySelector(LEGEND)).toHaveTextContent(
      "1 cleaned1 need a look",
    );
  });

  it("draws no legend when nothing finished", () => {
    expect(renderTile().querySelector(LEGEND)).toBeNull();
  });

  it("puts the same split in words in the chart's tooltip", () => {
    const handed = handedBack([finishedAt(3), finishedAt(7, "failed")], NOW);
    summary.value = { handed, total: handed.totals.all, partial: false };
    const tile = renderTile();

    expect(
      within(tile).getByRole("slider", {
        name: "Files finished, five minutes to a point",
      }),
    ).toHaveAttribute("title", "Last 2 hours: 1 cleaned, 1 need a look");
  });
});

describe("the Today chart's pointer readout", () => {
  const READOUT = ".cs-read";
  const CHART_BOX_WIDTH = 230;
  /** Over the second-newest of the 24 points, where the two files cleaned 3 minutes ago were counted. */
  const OVER_THE_CLEANED_POINT = (CHART_BOX_WIDTH * 22) / 23;

  function chartOf(tile: HTMLElement) {
    const chart = within(tile).getByRole("slider", {
      name: "Files finished, five minutes to a point",
    });
    chart.getBoundingClientRect = () => new DOMRect(0, 0, CHART_BOX_WIDTH, 60);
    return chart;
  }

  beforeEach(() => {
    const handed = handedBack(
      [finishedAt(3), finishedAt(3), finishedAt(8)],
      NOW,
    );
    summary.value = { handed, total: handed.totals.all, partial: false };
  });

  it("shows nothing until the pointer is on the chart", () => {
    const tile = renderTile();

    expect(tile.querySelector(READOUT)).toBeNull();
  });

  it("follows the pointer to the nearest five minutes and says what finished then", () => {
    const tile = renderTile();

    fireEvent.pointerMove(chartOf(tile), { clientX: OVER_THE_CLEANED_POINT });

    expect(tile.querySelector(READOUT)).toHaveTextContent("· 2 cleaned");
    expect(tile.querySelector(".cs-cursor")).not.toBeNull();
  });

  it("writes the time in the time zone chosen in Settings", () => {
    settings.timezone = "Australia/Brisbane";
    const tile = renderTile();

    fireEvent.pointerMove(chartOf(tile), { clientX: OVER_THE_CLEANED_POINT });

    expect(tile.querySelector(READOUT)).toHaveTextContent(/^7:55\spm/);
  });

  it("goes when the pointer leaves", () => {
    const tile = renderTile();
    const chart = chartOf(tile);
    fireEvent.pointerMove(chart, { clientX: OVER_THE_CLEANED_POINT });

    fireEvent.pointerLeave(chart);

    expect(tile.querySelector(READOUT)).toBeNull();
  });

  it("stays after a tap lifts, and goes when the person taps elsewhere", () => {
    const tile = renderTile();
    const chart = chartOf(tile);
    fireEvent.pointerDown(chart, {
      clientX: OVER_THE_CLEANED_POINT,
      pointerType: "touch",
    });

    fireEvent.pointerLeave(chart, { pointerType: "touch" });
    expect(tile.querySelector(READOUT)).not.toBeNull();

    fireEvent.mouseDown(document.body);
    expect(tile.querySelector(READOUT)).toBeNull();
  });

  it("shows the newest five minutes when the chart takes keyboard focus", () => {
    const tile = renderTile();

    fireEvent.focus(chartOf(tile));

    expect(tile.querySelector(READOUT)).toHaveTextContent("· nothing");
  });
});
