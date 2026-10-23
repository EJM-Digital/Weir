import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { NextItem } from "./next-model";
import type { Filter } from "../processing-filter";
import { NextTile } from "./next-tile";

const NOW = new Date(2026, 7, 18, 10, 0, 0).getTime();

function item(key: string, secondsAway: number, label: string): NextItem {
  return {
    key,
    label,
    to: `/setup/performance/${key}`,
    at: NOW + secondsAway * 1000,
    intervalSeconds: 300,
  };
}

afterEach(() => vi.restoreAllMocks());

function renderTile(
  items: NextItem[],
  paused = false,
  across = true,
  filter: Filter = "all",
) {
  render(
    <MemoryRouter>
      <NextTile
        items={items}
        now={NOW}
        paused={paused}
        across={across}
        filter={filter}
      />
    </MemoryRouter>,
  );
  return screen.getByRole("region", { name: "Next" });
}

describe("the Next tile", () => {
  it("counts down to a thing that has no regular interval", () => {
    const tile = renderTile([
      {
        ...item("scan", 42, "Look for new downloads in TV"),
        intervalSeconds: null,
      },
    ]);

    expect(within(tile).getByTestId("live-next-figure")).toHaveTextContent(
      "42 s",
    );
  });

  it("counts down to the first thing, and lists what comes after as 'then' lines", () => {
    const tile = renderTile([
      item("scan", 42, "Look for new downloads in TV"),
      item("cleanup", 20 * 60, "Cleanup: Leftover work files"),
      item("clean", 3 * 3600, "Clean the Movies library"),
    ]);

    expect(within(tile).getByTestId("live-next-figure")).toHaveTextContent(
      "42 s",
    );
    expect(tile).toHaveTextContent("Look for new downloads in TV");
    const then = within(screen.getByTestId("live-next-then"));
    expect(
      then.getByRole("link", { name: "then Cleanup: Leftover work files" }),
    ).toHaveAttribute("href", "/setup/performance/cleanup");
    expect(then.getAllByRole("listitem")[0]).toHaveTextContent("20 min");
    expect(then.getAllByRole("listitem")[1]).toHaveTextContent("1:00 pm");
  });

  it("lists no more than three after the first", () => {
    renderTile([1, 2, 3, 4, 5].map((n) => item(`k${n}`, n * 60, `Thing ${n}`)));

    expect(
      within(screen.getByTestId("live-next-then")).getAllByRole("listitem"),
    ).toHaveLength(3);
  });

  it("says nothing is scheduled, and why, when nothing has a timer", () => {
    const tile = renderTile([]);

    expect(tile).toHaveTextContent("nothing scheduled");
    expect(tile).toHaveTextContent("Switch on a workflow or a cleanup job.");
  });

  it("says which kind of work has nothing scheduled when it is narrowed to one", () => {
    expect(renderTile([], false, true, "library")).toHaveTextContent(
      "No library cleaning is scheduled.",
    );
  });

  it("reads Paused, and that work already running finishes, while paused", () => {
    const tile = renderTile(
      [item("scan", 42, "Look for new downloads in TV")],
      true,
    );

    expect(tile).toHaveTextContent("Paused");
    expect(tile).toHaveTextContent("nothing new starts");
    expect(tile).not.toHaveTextContent("Look for new downloads");
    expect(tile).toHaveTextContent("Running files finish first");
  });

  it('lists only the "then" lines that fit the tile, whole ones', () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(
      function (this: HTMLElement) {
        if (this.classList.contains("mm-stat__body"))
          return { bottom: 50 } as DOMRect;
        const index = Array.from(this.parentElement?.children ?? []).indexOf(
          this,
        );
        return { bottom: (index + 1) * 30 } as DOMRect;
      },
    );

    renderTile([1, 2, 3, 4].map((n) => item(`k${n}`, n * 60, `Thing ${n}`)));

    const lines = within(screen.getByTestId("live-next-then")).getAllByRole(
      "listitem",
      { hidden: true },
    );
    expect(lines.map((line) => line.style.visibility)).toEqual([
      "",
      "hidden",
      "hidden",
    ]);
  });

  it('lists every "then" line when the tile grows with them', () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(
      function (this: HTMLElement) {
        return {
          bottom: this.classList.contains("mm-stat__body") ? 50 : 500,
        } as DOMRect;
      },
    );

    renderTile(
      [1, 2, 3, 4].map((n) => item(`k${n}`, n * 60, `Thing ${n}`)),
      false,
      false,
    );

    const lines = within(screen.getByTestId("live-next-then")).getAllByRole(
      "listitem",
      { hidden: true },
    );
    expect(lines.map((line) => line.style.visibility)).toEqual(["", "", ""]);
  });
});
