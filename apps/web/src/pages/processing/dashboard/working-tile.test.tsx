import { fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ProcessingFile } from "../../../lib/processing/files-api";
import type { WorkingItem } from "../processing-model";
import { WorkingTile } from "./working-tile";

const file = { id: 5 } as ProcessingFile;

function item(overrides: Partial<WorkingItem>): WorkingItem {
  return {
    key: "file-5",
    source: "download",
    name: "Glass Orchard S01E04",
    path: "Glass.Orchard.S01E04.mkv",
    facts: "",
    libraryName: "TV",
    step: "write",
    percent: 46.4,
    etaSeconds: null,
    speed: null,
    removedAudio: 0,
    removedSubtitles: 0,
    file,
    ...overrides,
  };
}

function renderTile(
  props: Partial<React.ComponentProps<typeof WorkingTile>> = {},
) {
  const onOpen = vi.fn();
  render(
    <MemoryRouter>
      <WorkingTile
        working={[]}
        filesAtOnce={2}
        waitSeconds={60}
        onOpen={onOpen}
        {...props}
      />
    </MemoryRouter>,
  );
  return { onOpen };
}

afterEach(() => vi.restoreAllMocks());

/** Lays the tile out: its body ends at `bodyBottom`, and each row is `rowHeight` tall, one under another. */
function layOut(bodyBottom: number, rowHeight: number) {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(
    function (this: HTMLElement) {
      if (this.classList.contains("mm-stat__body")) {
        return { bottom: bodyBottom } as DOMRect;
      }
      const index = Array.from(
        this.parentElement?.parentElement?.children ?? [],
      ).indexOf(this.parentElement as Element);
      return { bottom: (index + 1) * rowHeight } as DOMRect;
    },
  );
}

const rowsOf = () =>
  within(screen.getByTestId("live-working")).getAllByRole("listitem");
const many = (count: number) =>
  Array.from({ length: count }, (_, index) =>
    item({ key: `file-${index}`, name: `File ${index}` }),
  );

describe("the Working on now tile", () => {
  it("says how many are being worked on out of how many may be at once", () => {
    renderTile({ working: [item({})] });

    expect(screen.getByTestId("live-working-count")).toHaveTextContent("1");
    expect(
      screen.getByRole("region", { name: "Working on now" }),
    ).toHaveTextContent("of 2 at once");
  });

  it("says only 'at once' while the limit is not known", () => {
    renderTile({ filesAtOnce: null });

    expect(
      screen.getByRole("region", { name: "Working on now" }),
    ).toHaveTextContent("0at once");
  });

  it("shows each file with its step and its percent, rounded", () => {
    renderTile({
      working: [
        item({}),
        item({
          key: "file-6",
          name: "Sintel (2010)",
          step: "verify",
          percent: 100,
        }),
      ],
    });

    const rows = within(screen.getByTestId("live-working")).getAllByRole(
      "listitem",
    );
    expect(rows[0]).toHaveTextContent("Glass Orchard S01E04 · Writing46%");
    expect(rows[1]).toHaveTextContent("Sintel (2010) · Verifying100%");
  });

  it("shows a step with no percent yet without one", () => {
    renderTile({ working: [item({ step: "checking", percent: null })] });

    expect(screen.getByTestId("live-working")).toHaveTextContent(
      "Glass Orchard S01E04 · Checking",
    );
    expect(screen.getByTestId("live-working")).not.toHaveTextContent("%");
  });

  it("opens a file's story from its row", () => {
    const { onOpen } = renderTile({ working: [item({})] });

    fireEvent.click(screen.getByRole("button"));

    expect(onOpen).toHaveBeenCalledWith(file);
  });

  it("shows a library clean, which has no story, as a row that is not a button", () => {
    renderTile({ working: [item({ file: null, source: "library" })] });

    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.getByTestId("live-working")).toHaveTextContent(
      "Glass Orchard S01E04",
    );
  });

  it("says nothing is being cleaned when nothing is", () => {
    renderTile();

    expect(screen.getByText("Nothing being cleaned.")).toBeInTheDocument();
    expect(screen.getByTestId("live-working")).not.toBeVisible();
  });

  it("names the wait for a new download beside the figure, where the tile's width never hides it", () => {
    renderTile();

    const tile = screen.getByRole("region", { name: "Working on now" });
    expect(tile.querySelector(".mm-stat__figure")).toHaveTextContent(
      "new downloads wait 60s",
    );
    expect(tile.querySelector(".mm-stat__note")).toHaveAttribute(
      "title",
      "New downloads wait 60 seconds after they stop changing.",
    );
    expect(tile.querySelector(".mm-stat__aside")).not.toHaveTextContent("wait");
    expect(within(tile).getByRole("link", { name: "Change" })).toHaveAttribute(
      "href",
      "/setup/performance",
    );
  });

  it("keeps the link when the workflows disagree about the wait", () => {
    renderTile({ waitSeconds: null });

    const tile = screen.getByRole("region", { name: "Working on now" });
    expect(tile).not.toHaveTextContent("new downloads wait");
    expect(
      within(tile).getByRole("link", { name: "Change" }),
    ).toBeInTheDocument();
  });

  it("makes the tile's label the link to every running file, whatever width the tile is", () => {
    renderTile({ working: many(2) });

    expect(
      screen.getByRole("link", {
        name: "Working on now: every file in Activity",
      }),
    ).toHaveAttribute("href", "/activity?show=working");
  });

  describe("with more files than the tile has room for", () => {
    it("lists at most four, and says how many more there are", () => {
      renderTile({ working: many(6) });

      expect(rowsOf()).toHaveLength(4);
      const more = screen.getByRole("link", { name: "2 more in Activity" });
      expect(more).toHaveAttribute("href", "/activity?show=working");
      expect(more).toHaveTextContent("2 more");
    });

    it("shows only the whole rows that fit the tile, and counts the rest as more", () => {
      layOut(100, 40);

      renderTile({ working: many(5) });

      const rows = rowsOf().map((row) =>
        within(row).getByText(/File/).closest("li"),
      );
      expect(
        rows.map(
          (row) =>
            row?.querySelector<HTMLElement>("[data-fit]")?.style.visibility,
        ),
      ).toEqual(["", "", "hidden", "hidden"]);
      expect(
        screen.getByRole("link", { name: "3 more in Activity" }),
      ).toBeInTheDocument();
    });

    it("lists every row of a tile that grows with them, up to four", () => {
      layOut(100, 40);

      renderTile({ working: many(5), across: false });

      expect(
        rowsOf().map(
          (row) =>
            row.querySelector<HTMLElement>("[data-fit]")?.style.visibility,
        ),
      ).toEqual(["", "", "", ""]);
      expect(
        screen.getByRole("link", { name: "1 more in Activity" }),
      ).toBeInTheDocument();
    });

    it("says nothing about more when every file shows", () => {
      renderTile({ working: many(2) });

      expect(
        screen.queryByRole("link", { name: /more in Activity/ }),
      ).toBeNull();
    });
  });
});

describe("the Working on now tile when its list empties and refills", () => {
  function tileOf(working: WorkingItem[]) {
    return (
      <MemoryRouter>
        <WorkingTile
          working={working}
          filesAtOnce={2}
          waitSeconds={60}
          onOpen={vi.fn()}
        />
      </MemoryRouter>
    );
  }

  it("keeps its list element, showing it only while there is something to list", () => {
    const { rerender } = render(tileOf([item({})]));
    const list = screen.getByTestId("live-working");
    expect(list).toBeVisible();

    rerender(tileOf([]));
    expect(screen.getByTestId("live-working")).toBe(list);
    expect(list).not.toBeVisible();
    expect(screen.getByText("Nothing being cleaned.")).toBeVisible();

    rerender(tileOf([item({})]));
    expect(screen.getByTestId("live-working")).toBe(list);
    expect(list).toBeVisible();
    expect(screen.getByText("Nothing being cleaned.")).not.toBeVisible();
  });
});
