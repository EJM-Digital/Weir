import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { WithWorkflows } from "../../../test/with-workflows";
import type { FinishedFile } from "../../../lib/activity/processing-outcome";
import { JustFinishedShelf } from "./just-finished-shelf";
import { NOW } from "./pipeline-fixtures";

function finished(
  id: number,
  overrides: Partial<FinishedFile> = {},
): FinishedFile {
  return {
    id,
    source: "download",
    kind: "cleaned",
    relativePath: `The.Quiet.Harbour.S01E0${id}.1080p.WEB-DL.mkv`,
    libraryId: 2,
    savedBytes: 318 * 1024 ** 2,
    removedAudio: 2,
    removedSubtitles: 4,
    sentence: null,
    finishedAt: new Date(NOW - 3 * 60_000).toISOString(),
    ...overrides,
  };
}

const WORKFLOW_NAMES = new Map([
  [1, "Movies"],
  [2, "TV"],
  [3, "Kids"],
]);

function shelf(
  items: FinishedFile[],
  props: Partial<React.ComponentProps<typeof JustFinishedShelf>> = {},
) {
  return (
    <WithWorkflows>
      <MemoryRouter>
        <JustFinishedShelf
          items={items}
          filter="all"
          now={NOW}
          workflowNames={WORKFLOW_NAMES}
          onOpen={vi.fn()}
          {...props}
        />
      </MemoryRouter>
    </WithWorkflows>
  );
}

const tiles = () =>
  screen
    .getAllByRole("listitem")
    .map((item) => within(item).getByRole("button").getAttribute("aria-label"));

describe("Just finished", () => {
  afterEach(() => {
    Reflect.deleteProperty(document, "fonts");
    vi.restoreAllMocks();
  });

  it("does not measure its status line again when the fonts arrive after it has gone", async () => {
    let fontsArrive: () => void = () => undefined;
    Object.defineProperty(document, "fonts", {
      configurable: true,
      value: { ready: new Promise<void>((resolve) => (fontsArrive = resolve)) },
    });
    const { unmount } = render(shelf([finished(1)]));
    unmount();
    const measured = vi.spyOn(window, "getComputedStyle");

    await act(async () => fontsArrive());

    expect(measured).not.toHaveBeenCalled();
  });

  it("names a tile without a time when the time the file finished cannot be read", () => {
    render(shelf([finished(1, { finishedAt: "not a time" })]));

    const [label] = tiles();
    expect(label).toContain("The Quiet Harbour");
    expect(label).not.toMatch(/ago|just now/);
  });

  it("says what has not finished when the work it is narrowed to has nothing", () => {
    render(shelf([finished(1)], { filter: "library" }));

    expect(
      screen.getByText("No library cleaning has finished yet."),
    ).toBeVisible();
  });

  it("shows each file as a tile with its title and how it came out, and names what was removed and how long ago", () => {
    render(shelf([finished(1)]));

    const tile = screen.getByRole("button", {
      name: "The Quiet Harbour S01E01 (TV): 2 audio, 4 subtitles removed, saved 318 MB, 3 min ago",
    });
    expect(
      within(tile).getByText("The Quiet Harbour S01E01"),
    ).toBeInTheDocument();
    expect(within(tile).getByText("318 MB saved")).toBeInTheDocument();
  });

  it("tags each tile with its workflow, and the tag goes to that workflow in the library", () => {
    render(shelf([finished(1, { libraryId: 2 })]));

    const list = screen.getByRole("list");
    expect(within(list).getByRole("link", { name: "TV" })).toHaveAttribute(
      "href",
      "/library?library=2",
    );
  });

  it("keeps the tag beside the poster's button, so a click on the tag does not open the story", () => {
    const onOpen = vi.fn();
    render(shelf([finished(1, { libraryId: 2 })], { onOpen }));

    const tag = screen.getByRole("link", { name: "TV" });

    expect(tag).toHaveClass("lsh-tag");
    expect(screen.getByRole("button", { name: /S01E01/ })).not.toContainElement(
      tag,
    );
    fireEvent.click(tag);
    expect(onOpen).not.toHaveBeenCalled();
  });

  it("shows the title's poster on its tile, and the initials where the file has none", () => {
    render(
      shelf([
        finished(1, { posterUrl: "/api/v1/artwork/posters/tv-harbour-2019" }),
        finished(2, { posterUrl: null }),
      ]),
    );

    const [withPoster, without] = screen.getAllByRole("listitem");
    expect(within(withPoster).getByRole("img")).toHaveAttribute(
      "src",
      "/api/v1/artwork/posters/tv-harbour-2019",
    );
    expect(within(without).queryByRole("img")).toBeNull();
  });

  it("opens the file's story when a tile is clicked", () => {
    const onOpen = vi.fn();
    const item = finished(1);
    render(shelf([item], { onOpen }));

    fireEvent.click(screen.getByRole("button", { name: /S01E01/ }));

    expect(onOpen).toHaveBeenCalledWith(item);
  });

  it("honours the filter", () => {
    render(
      shelf([finished(1), finished(2, { source: "library", libraryId: 1 })], {
        filter: "library",
      }),
    );

    expect(tiles()).toEqual([expect.stringContaining("S01E02")]);
  });

  it("marks each tile with the path of its file, so the board can fly a delivered tile to it", () => {
    render(shelf([finished(1)]));

    expect(document.querySelector("[data-shelf-path]")).toHaveAttribute(
      "data-shelf-path",
      "The.Quiet.Harbour.S01E01.1080p.WEB-DL.mkv",
    );
  });

  it("links on to Activity", () => {
    render(shelf([finished(1)]));

    expect(
      screen.getByRole("link", { name: "Activity: Just finished" }),
    ).toHaveAttribute("href", "/activity");
  });
});

describe("Just finished's workflow chips", () => {
  afterEach(() => localStorage.clear());

  it("offers All and a chip for each workflow, with All pressed", () => {
    render(shelf([finished(1)]));

    const group = screen.getByRole("group", { name: "Workflows" });
    expect(
      within(group)
        .getAllByRole("button")
        .map((chip) => [chip.textContent, chip.getAttribute("aria-pressed")]),
    ).toEqual([
      ["All", "true"],
      ["Movies", "false"],
      ["TV", "false"],
      ["Kids", "false"],
    ]);
  });

  it("offers a chip only to the workflows that are on, when told which", () => {
    render(shelf([finished(1)], { enabledWorkflowIds: new Set([1, 2]) }));

    const group = screen.getByRole("group", { name: "Workflows" });
    expect(within(group).queryByRole("button", { name: "Kids" })).toBeNull();
  });

  it("narrows the shelf and its counts to the chip pressed, and back again with All", () => {
    render(
      shelf([
        finished(1, { libraryId: 2 }),
        finished(2, { libraryId: 1, kind: "failed" }),
      ]),
    );

    fireEvent.click(screen.getByRole("button", { name: "Movies" }));

    expect(tiles()).toEqual([expect.stringContaining("S01E02")]);
    expect(screen.getByRole("button", { name: "Movies" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(
      screen.getByRole("region", { name: "Just finished" }),
    ).toHaveAccessibleDescription("1 today · 318 MB saved");

    fireEvent.click(screen.getByRole("button", { name: "All" }));

    expect(tiles()).toHaveLength(2);
  });

  it("leaves only the dashboard's workflow, pressed, when the page narrows to one", () => {
    render(
      shelf([finished(1, { libraryId: 2 }), finished(2, { libraryId: 1 })], {
        workflowId: 1,
      }),
    );

    const group = screen.getByRole("group", { name: "Workflows" });
    const chips = within(group).getAllByRole("button");
    expect(chips.map((chip) => chip.textContent)).toEqual(["Movies"]);
    expect(chips[0]).toHaveAttribute("aria-pressed", "true");
    expect(tiles()).toEqual([expect.stringContaining("S01E02")]);
  });

  it("sits the chips in the panel's header, between the title and the Activity link", () => {
    render(shelf([finished(1)]));

    const header = screen
      .getByRole("heading", { name: "Just finished" })
      .closest("header");
    expect(header).not.toBeNull();
    expect(
      within(header as HTMLElement).getByRole("group", { name: "Workflows" }),
    ).toBeInTheDocument();
    expect(
      within(header as HTMLElement).getByRole("link", {
        name: "Activity: Just finished",
      }),
    ).toBeInTheDocument();
  });

  it("describes what the page counts for the day across every workflow, rather than showing it in the header", () => {
    render(
      shelf([finished(1, { kind: "failed" })], {
        count: "38 cleaned today · 41.20 GB saved",
      }),
    );

    expect(
      screen.getByRole("region", { name: "Just finished" }),
    ).toHaveAccessibleDescription("38 cleaned today · 41.20 GB saved");
  });

  it("says nothing finished yet today, and shows the latest from before", () => {
    render(
      shelf(
        [
          finished(1, {
            finishedAt: new Date(NOW - 30 * 3_600_000).toISOString(),
          }),
        ],
        { count: "0 cleaned today · 0 B saved" },
      ),
    );

    expect(
      screen.getByRole("region", { name: "Just finished" }),
    ).toHaveAccessibleDescription("Nothing yet today · latest arrivals");
    expect(tiles()).toHaveLength(1);
  });

  it("remembers the chip pressed, and opens on it the next time", () => {
    const first = render(shelf([finished(1, { libraryId: 2 })]));
    fireEvent.click(screen.getByRole("button", { name: "TV" }));
    first.unmount();

    render(shelf([finished(1, { libraryId: 2 })]));

    expect(screen.getByRole("button", { name: "TV" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(localStorage.getItem("weir.live.shelfWorkflow")).toBe("2");
  });

  it("opens on All when the workflow it remembered is no longer there", () => {
    localStorage.setItem("weir.live.shelfWorkflow", "9");

    render(shelf([finished(1)]));

    expect(screen.getByRole("button", { name: "All" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
  });

  it("lets the dashboard's workflow win over the one remembered", () => {
    localStorage.setItem("weir.live.shelfWorkflow", "2");

    render(
      shelf([finished(1, { libraryId: 2 }), finished(2, { libraryId: 1 })], {
        workflowId: 1,
      }),
    );

    expect(tiles()).toEqual([expect.stringContaining("S01E02")]);
  });
});

describe("the size of the tiles", () => {
  afterEach(() => vi.restoreAllMocks());

  function rowOf(height: number, width: number) {
    vi.spyOn(Element.prototype, "clientHeight", "get").mockReturnValue(height);
    vi.spyOn(Element.prototype, "clientWidth", "get").mockReturnValue(width);
  }
  const files = Array.from({ length: 9 }, (_, index) => finished(index + 1));

  it("makes the tiles no wider than lets five stand across, and shows at least five", () => {
    rowOf(250, 570);

    render(shelf(files));

    // 250px of row would give 135px tiles, three across; five must stand across, so they are 102px.
    expect(tiles()).toHaveLength(5);
    expect(
      document
        .querySelector<HTMLElement>(".mm-shelf__row")
        ?.style.getPropertyValue("--shelf-tile-w"),
    ).toBe("102px");
  });

  it("fits the tiles to a short row's height, and shows every one that stands across", () => {
    rowOf(100, 570);

    render(shelf(files));

    // 100px of row less 12px of padding and the 21px line under each tile gives 44px tiles: nine stand across 570px.
    expect(tiles()).toHaveLength(9);
    expect(
      document
        .querySelector<HTMLElement>(".mm-shelf__row")
        ?.style.getPropertyValue("--shelf-tile-w"),
    ).toBe("44px");
  });

  it("dresses a full-size poster with the workflow's tag, and nothing else on the art", () => {
    rowOf(200, 570);

    render(shelf(files));

    const first = screen.getAllByRole("listitem")[0];
    expect(first).toHaveAttribute("data-size", "full");
    expect(within(first).getByRole("link", { name: "TV" })).toHaveClass(
      "lsh-tag",
    );
    expect(first.querySelector(".mm-shelf__art")?.children).toHaveLength(1);
  });

  describe("the caption's lines", () => {
    const captionOf = (index: number) =>
      screen
        .getAllByRole("listitem")
        [index].querySelector<HTMLElement>(".mm-shelf__caption");
    const linesOf = (index: number) =>
      [...(captionOf(index)?.querySelectorAll(".mm-shelf__line") ?? [])].map(
        (line) => line.textContent,
      );

    it("is the full caption where the posters have the height the five-tile rule lets them use, and the room for it", () => {
      // 570px wide: 102px posters, 153px of art, 72px of caption and 12px of padding: 237px.
      rowOf(237, 570);

      render(shelf(files));

      expect(captionOf(0)).toHaveAttribute("data-tier", "full");
      expect(linesOf(0)).toEqual([
        "The Quiet Harbour S01E01",
        "318 MB saved",
        "−2 audio · −4 subs",
        "3 min ago",
      ]);
    });

    it("is the compact caption, the title and how it came out, a pixel short of that, with the tiles the same size", () => {
      rowOf(236, 570);

      render(shelf(files));

      expect(captionOf(0)).toHaveAttribute("data-tier", "compact");
      expect(linesOf(0)).toEqual(["The Quiet Harbour S01E01", "318 MB saved"]);
      expect(
        document
          .querySelector<HTMLElement>(".mm-shelf__row")
          ?.style.getPropertyValue("--shelf-tile-w"),
      ).toBe("102px");
    });

    it("says the saving exactly once", () => {
      rowOf(237, 570);

      render(shelf(files));

      expect(screen.getAllByText(/318 MB/)).toHaveLength(5);
      expect(screen.queryByText("318 MB")).not.toBeInTheDocument();
    });

    it("keeps four slots on every tile, an empty one where there is nothing to say, so the lines stand level across the row", () => {
      rowOf(237, 570);

      render(
        shelf([
          finished(1),
          finished(2, { kind: "already", savedBytes: null }),
          finished(3, { kind: "failed", savedBytes: null }),
        ]),
      );

      expect([0, 1, 2].map(linesOf)).toEqual([
        [
          "The Quiet Harbour S01E01",
          "318 MB saved",
          "−2 audio · −4 subs",
          "3 min ago",
        ],
        ["The Quiet Harbour S01E02", "Already clean", "", "3 min ago"],
        ["The Quiet Harbour S01E03", "Couldn't finish", "", "3 min ago"],
      ]);
    });

    it("draws the status line by what the outcome means: done, done, needs a look, broken", () => {
      rowOf(237, 570);

      render(
        shelf([
          finished(1),
          finished(2, { kind: "already", savedBytes: null }),
          finished(3, { kind: "rejected", savedBytes: null }),
          finished(4, { kind: "failed", savedBytes: null }),
        ]),
      );

      const meaningOf = (index: number) =>
        captionOf(index)
          ?.querySelector(".mm-shelf__status")
          ?.getAttribute("data-status");
      expect(meaningOf(0)).toBe("done");
      expect(meaningOf(1)).toBe("done");
      expect(meaningOf(2)).toBe("attention");
      expect(meaningOf(3)).toBe("broken");
    });

    it("says 'Already clean' in the name and tooltip of a file nothing was changed in, and on its status line", () => {
      rowOf(237, 570);

      render(
        shelf([
          finished(1, {
            kind: "already",
            savedBytes: null,
            removedAudio: 0,
            removedSubtitles: 0,
          }),
        ]),
      );

      expect(
        screen.getByRole("button", {
          name: "The Quiet Harbour S01E01 (TV): Already clean, 3 min ago",
        }),
      ).toHaveAttribute("title", expect.stringContaining("Already clean"));
      expect(linesOf(0)[1]).toBe("Already clean");
    });

    it("keeps the bare saving where 'saved' does not fit after the dot", () => {
      rowOf(237, 570);

      // 8px a character: "318 MB saved" is 96px, wider than the 90px the 102px poster leaves after the dot.
      render(shelf(files, { measureText: (text) => text.length * 8 }));

      expect(linesOf(0)[1]).toBe("318 MB");
    });

    it("counts the tracks removed where the two kinds do not fit the poster's width", () => {
      rowOf(237, 570);

      // 8px a character: "−2 audio · −4 subs" is 144px, wider than the 102px poster.
      render(shelf(files, { measureText: (text) => text.length * 8 }));

      expect(linesOf(0)[2]).toBe("−6 tracks");
    });

    it("is the compact caption on posters too small for the tag, however much room there is", () => {
      // 300px wide holds five 48px posters, narrower than a poster that can wear a full caption.
      rowOf(400, 300);

      render(shelf(files));

      expect(captionOf(0)).toHaveAttribute("data-tier", "compact");
      expect(linesOf(0)).toHaveLength(2);
    });

    it("is the status line alone, in the shortest words that fit, where the row has room for one line and no more", () => {
      // 100px of row: 44px posters, with 21px under each for the line.
      rowOf(100, 570);

      // 4px a character: "318 MB" is 24px, which fits the 33px a 44px poster leaves after the dot.
      render(shelf(files, { measureText: (text) => text.length * 4 }));

      expect(captionOf(0)).toHaveAttribute("data-tier", "tiny");
      expect(linesOf(0)).toEqual(["318 MB"]);
    });

    it("leaves the dot out and keeps the word where the word fits only without it", () => {
      rowOf(100, 570);

      // 7px a character: "318 MB" is 42px and "Saved" 35px, wider than the 33px a 44px poster leaves after the dot, and under the 44px without it.
      render(shelf(files, { measureText: (text) => text.length * 7 }));

      const status = captionOf(0)?.querySelector(".mm-shelf__status");
      expect(linesOf(0)).toEqual(["318 MB"]);
      expect(status).toHaveAttribute("data-bare");
      expect(status).toHaveAttribute("data-status", "done");
    });

    it("keeps the dot beside a word that fits with it, and never says nothing", () => {
      rowOf(100, 570);

      render(shelf(files, { measureText: (text) => text.length * 4 }));
      expect(
        captionOf(0)?.querySelector(".mm-shelf__status"),
      ).not.toHaveAttribute("data-bare");
    });

    it("says a word for a cleaned file with no saving where its own word does not fit", () => {
      rowOf(100, 570);

      // 5px a character: "Cleaned" is 35px and "Done" 20px, against the 33px a 44px poster leaves after the dot.
      render(
        shelf(
          [
            finished(1, {
              savedBytes: null,
              removedAudio: 0,
              removedSubtitles: 0,
            }),
          ],
          { measureText: (text) => text.length * 5 },
        ),
      );

      expect(linesOf(0)).toEqual(["Done"]);
    });

    it("keeps the title and the full words in the tooltip and name of a tile with the line alone", () => {
      rowOf(100, 570);

      render(
        shelf(
          [
            finished(1, {
              kind: "rejected",
              savedBytes: null,
              removedAudio: 0,
              removedSubtitles: 0,
            }),
          ],
          {
            measureText: (text) => text.length * 4,
          },
        ),
      );

      expect(linesOf(0)).toEqual(["Rejected"]);
      expect(
        screen.getByRole("button", {
          name: "The Quiet Harbour S01E01 (TV): Rejected, 3 min ago",
        }),
      ).toHaveAttribute("title", expect.stringContaining("The Quiet Harbour"));
    });

    it("leaves the tiles bare where the row is too short for even the line", () => {
      rowOf(60, 570);

      render(shelf(files));

      expect(captionOf(0)).toBeNull();
    });
  });

  describe("on posters too small for the tag", () => {
    it("puts the workflow in the tile's name and tooltip, with the saving, and draws a strip in its colour", () => {
      rowOf(100, 570);

      render(shelf(files));

      const first = screen.getAllByRole("listitem")[0];
      expect(first).toHaveAttribute("data-size", "small");
      const open = within(first).getByRole("button");
      expect(open).toHaveAccessibleName(
        "The Quiet Harbour S01E01 (TV): 2 audio, 4 subtitles removed, saved 318 MB, 3 min ago",
      );
      expect(open).toHaveAttribute("title", expect.stringContaining("318 MB"));
      expect(within(first).getByRole("link", { name: "TV" })).toHaveClass(
        "lsh-tag",
      );
    });

    it("keeps the workflow's tag as a link to its library, named and tinted for the workflow", () => {
      rowOf(100, 570);

      render(shelf(files));

      const tag = within(screen.getAllByRole("listitem")[0]).getByRole("link", {
        name: "TV",
      });
      expect(tag).toHaveClass("lsh-tag");
      expect(tag).toHaveAttribute("href", "/library?library=2");
      expect(tag).toHaveAttribute("title", "Open TV in the library");
      expect(tag.style.getPropertyValue("--tile-hue")).toBe("265");
    });

    it("leaves a file that saved nothing with a name that says nothing of it", () => {
      rowOf(100, 570);

      render(shelf([finished(1, { savedBytes: 0 })]));

      expect(
        screen.getByRole("button", { name: /S01E01/ }),
      ).toHaveAccessibleName(
        "The Quiet Harbour S01E01 (TV): 2 audio, 4 subtitles removed, 3 min ago",
      );
    });
  });
});

describe("what a screen reader is told when a file finishes", () => {
  const announcement = () => screen.getByTestId("shelf-announcement");

  it("says nothing for the files already there when the page first loads", () => {
    render(shelf([finished(2), finished(1)]));

    expect(announcement()).toHaveTextContent("");
    expect(announcement()).toHaveAttribute("aria-live", "polite");
  });

  it("names the file and how it came out the moment a new one lands", () => {
    const { rerender } = render(shelf([finished(1)]));

    rerender(shelf([finished(2), finished(1)]));

    expect(announcement()).toHaveTextContent(
      "The Quiet Harbour S01E02 finished: Saved 318 MB · removed 2 audio, 4 subtitles",
    );
  });

  it("says nothing again for the same newest file, and speaks again for the next", () => {
    const { rerender } = render(shelf([finished(1)]));
    rerender(shelf([finished(2), finished(1)]));
    const first = announcement().textContent;

    rerender(shelf([finished(2), finished(1)], { now: NOW + 60_000 }));
    expect(announcement()).toHaveTextContent(first ?? "");

    rerender(
      shelf([finished(3, { kind: "already" }), finished(2), finished(1)]),
    );
    expect(announcement()).toHaveTextContent(
      "The Quiet Harbour S01E03 finished: Already clean",
    );
  });

  it("announces the first file of a page that opened with an empty shelf", () => {
    const { rerender } = render(shelf([]));
    expect(announcement()).toHaveTextContent("");

    rerender(shelf([finished(1)]));

    expect(announcement()).toHaveTextContent(
      "The Quiet Harbour S01E01 finished:",
    );
  });
});
