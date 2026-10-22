import { describe, expect, it } from "vitest";

import type { FinishedFile } from "../../../lib/activity/processing-outcome";
import { NOW } from "./pipeline-fixtures";
import {
  SHELF_LIMIT,
  shelfOf,
  todayWords,
  type ShelfScope,
} from "./shelf-model";

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

const NAMES = new Map([
  [1, "Movies"],
  [2, "TV"],
]);
const EVERYTHING: ShelfScope = { filter: "all", workflowId: null, now: NOW };
const yesterday = () => new Date(NOW - 36 * 3_600_000).toISOString();

function shelf(items: FinishedFile[], scope: ShelfScope = EVERYTHING) {
  return shelfOf(items, scope, NAMES);
}

describe("the shelf's tiles", () => {
  it("say the title, how the file came out, what was done and how long ago", () => {
    const [tile] = shelf([finished(1)]).tiles;

    expect(tile).toMatchObject({
      title: "The Quiet Harbour S01E01",
      status: {
        meaning: "done",
        payoff: true,
        words: ["318 MB saved", "318 MB"],
      },
      detail: ["−2 audio · −4 subs", "−6 tracks"],
      ago: "3 min ago",
      workflow: "TV",
      workflowKnown: true,
    });
  });

  it("give each outcome the meaning it has, gold for a saving, and say it in a word where there is no saving", () => {
    const { tiles } = shelf([
      finished(1),
      finished(2, { savedBytes: null, removedAudio: 0, removedSubtitles: 0 }),
      finished(3, { kind: "already", savedBytes: null }),
      finished(4, { kind: "rejected", savedBytes: null }),
      finished(5, { kind: "passed", savedBytes: null }),
      finished(6, { kind: "failed", savedBytes: null }),
    ]);

    expect(
      tiles.map((tile) => [
        tile.status.meaning,
        tile.status.payoff === true,
        tile.status.words,
      ]),
    ).toEqual([
      ["done", true, ["318 MB saved", "318 MB"]],
      ["done", false, ["Cleaned"]],
      ["done", false, ["Already clean"]],
      ["attention", false, ["Rejected"]],
      ["attention", false, ["Passed through"]],
      ["broken", false, ["Couldn't finish"]],
    ]);
  });

  it("have the briefest words for a poster with no room for a title, the last of them always a word", () => {
    const { tiles } = shelf([
      finished(1, { savedBytes: 1.24 * 1024 ** 3 }),
      finished(2, { savedBytes: null, removedAudio: 0, removedSubtitles: 0 }),
      finished(3, { kind: "already", savedBytes: null }),
      finished(4, { kind: "rejected", savedBytes: null }),
      finished(5, { kind: "passed", savedBytes: null }),
      finished(6, { kind: "failed", savedBytes: null }),
    ]);

    expect(tiles.map((tile) => tile.status.short)).toEqual([
      ["1.2 GB"],
      ["Cleaned", "Done"],
      ["Clean"],
      ["Rejected"],
      ["Passed"],
      ["Failed"],
    ]);
  });

  it("say a saving under a megabyte in whole KB, never rounded up to a megabyte it did not save", () => {
    const saved = (bytes: number) =>
      shelf([finished(1, { savedBytes: bytes })]).tiles[0].status.words;

    expect(saved(1.1 * 1024)).toEqual(["1 KB saved", "1 KB"]);
    expect(saved(318.4 * 1024)).toEqual(["318 KB saved", "318 KB"]);
    expect(saved(700)).toEqual(["1 KB saved", "1 KB"]);
    expect(saved(1023.6 * 1024)).toEqual(["1 MB saved", "1 MB"]);
  });

  it("say a saving with one decimal for GB and whole MB, and no sign", () => {
    const saved = (bytes: number) =>
      shelf([finished(1, { savedBytes: bytes })]).tiles[0].status.words;

    expect(saved(1.24 * 1024 ** 3)).toEqual(["1.2 GB saved", "1.2 GB"]);
    expect(saved(221.4 * 1024 ** 2)).toEqual(["221 MB saved", "221 MB"]);
    expect(saved(12.6 * 1024 ** 2)).toEqual(["13 MB saved", "13 MB"]);
    expect(saved(1023.9 * 1024 ** 2)).toEqual(["1.0 GB saved", "1.0 GB"]);
    expect(saved(10 * 1024 ** 3)).toEqual(["10.0 GB saved", "10.0 GB"]);
  });

  it("say what was removed from a cleaned file, a minus meaning removed, and nothing where nothing was", () => {
    const detail = (overrides: Partial<FinishedFile>) =>
      shelf([finished(1, overrides)]).tiles[0].detail;

    expect(detail({ removedAudio: 1, removedSubtitles: 7 })).toEqual([
      "−1 audio · −7 subs",
      "−8 tracks",
    ]);
    expect(detail({ removedAudio: 0, removedSubtitles: 1 })).toEqual([
      "−1 sub",
      "−1 track",
    ]);
    expect(detail({ removedAudio: 3, removedSubtitles: 0 })).toEqual([
      "−3 audio",
      "−3 tracks",
    ]);
    expect(detail({ removedAudio: 0, removedSubtitles: 0 })).toBeNull();
    expect(
      detail({ source: "library", removedAudio: 0, removedSubtitles: 0 }),
    ).toEqual(["Cleaned in place"]);
  });

  it("leave the detail slot empty for a file that was not cleaned", () => {
    const { tiles } = shelf([
      finished(1, { kind: "already", savedBytes: null }),
      finished(2, { kind: "rejected", savedBytes: null }),
      finished(3, { kind: "passed", savedBytes: null }),
      finished(4, { kind: "failed", savedBytes: null }),
    ]);

    expect(tiles.map((tile) => tile.detail)).toEqual([null, null, null, null]);
  });

  it("say the saving once, in the status line", () => {
    const [tile] = shelf([finished(1)]).tiles;

    expect(tile.status.words).toEqual(["318 MB saved", "318 MB"]);
    expect(tile.detail?.join(" ")).not.toMatch(/MB/);
    expect(tile.ago).not.toMatch(/MB/);
  });

  it("say how long ago the file finished", () => {
    const [tile] = shelf([
      finished(1, { finishedAt: new Date(NOW - 10_000).toISOString() }),
    ]).tiles;

    expect(tile.ago).toBe("just now");
  });

  it("say what became of a file that nothing was removed from, and claim no saving that was not recorded", () => {
    const { tiles } = shelf([
      finished(1, { removedAudio: 0, removedSubtitles: 0, savedBytes: null }),
      finished(2, {
        kind: "already",
        savedBytes: null,
        removedAudio: 0,
        removedSubtitles: 0,
      }),
      finished(3, {
        source: "library",
        removedAudio: 0,
        removedSubtitles: 0,
        savedBytes: null,
      }),
    ]);

    expect(tiles.map((tile) => tile.what)).toEqual([
      "Cleaned",
      "Already clean",
      "Cleaned in place",
    ]);
    expect(tiles.every((tile) => tile.savedAmount === null)).toBe(true);
  });

  it("do not name a workflow that is not known", () => {
    const [tile] = shelf([finished(1, { libraryId: 99 })]).tiles;

    expect(tile.workflowKnown).toBe(false);
  });

  it("do not name a workflow for a file that belongs to none", () => {
    const [tile] = shelf([finished(1, { libraryId: null })]).tiles;

    expect(tile.workflowKnown).toBe(false);
  });

  it("honour the page filter", () => {
    const items = [finished(1), finished(2, { source: "library" })];
    const keys = (filter: ShelfScope["filter"]) =>
      shelf(items, { ...EVERYTHING, filter }).tiles.map((tile) => tile.key);

    expect(keys("download")).toEqual(["1"]);
    expect(keys("library")).toEqual(["2"]);
    expect(keys("all")).toEqual(["1", "2"]);
  });

  it("honour the workflow the shelf is narrowed to", () => {
    const items = [
      finished(1, { libraryId: 2 }),
      finished(2, { libraryId: 1 }),
    ];

    const narrowed = shelf(items, { ...EVERYTHING, workflowId: 1 });

    expect(narrowed.tiles.map((tile) => tile.key)).toEqual(["2"]);
    expect(narrowed.today).toBe(1);
  });

  it("are at most as many as the shelf holds, newest first", () => {
    const items = Array.from({ length: SHELF_LIMIT + 5 }, (_, i) =>
      finished(i + 1),
    );

    const { tiles } = shelf(items);

    expect(tiles).toHaveLength(SHELF_LIMIT);
    expect(tiles[0].key).toBe("1");
  });
});

describe("what the shelf adds up to", () => {
  it("counts what finished today and what it saved", () => {
    const result = shelf([
      finished(1),
      finished(2, { savedBytes: 2 * 1024 ** 2 }),
      finished(3, { kind: "rejected", savedBytes: null }),
      finished(4, { kind: "failed", savedBytes: null }),
      finished(5, { finishedAt: yesterday() }),
    ]);

    expect(result).toMatchObject({
      today: 4,
      savedBytes: 320 * 1024 ** 2,
      latest: false,
    });
    expect(result.tiles.map((tile) => tile.key)).toEqual(["1", "2", "3", "4"]);
    expect(todayWords(result)).toBe("4 today · 320 MB saved");
  });

  it("shows the latest from before when nothing finished today", () => {
    const result = shelf([finished(5, { finishedAt: yesterday() })]);

    expect(result).toMatchObject({ today: 0, latest: true });
    expect(result.tiles.map((tile) => tile.key)).toEqual(["5"]);
    expect(todayWords(result)).toBe("Nothing yet today · latest arrivals");
  });

  it("is empty, and says nothing finished today, when there are no files at all", () => {
    expect(shelf([])).toMatchObject({ today: 0, latest: true, tiles: [] });
  });
});
