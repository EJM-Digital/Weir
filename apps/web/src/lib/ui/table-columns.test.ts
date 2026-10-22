import { describe, expect, it } from "vitest";

import {
  columnOf,
  meaningRank,
  movableOrder,
  readLayout,
  shiftedOrder,
  sortRows,
  withMovableOrder,
  type TableColumnsConfig,
} from "./table-columns";

type Id = "pick" | "name" | "size" | "when" | "act";

const config: TableColumnsConfig<Id> = {
  tableId: "things",
  sortable: true,
  defaultSort: { id: "name", direction: "asc" },
  columns: [
    { id: "pick", label: "Pick", movable: false, sortable: false },
    { id: "name", label: "Name" },
    { id: "size", label: "Size", firstDirection: "desc" },
    { id: "when", label: "When" },
    { id: "act", label: "Actions", movable: false, sortable: false },
  ],
};

describe("moving columns", () => {
  it("leaves a column that cannot move in its place while the others are shuffled around it", () => {
    const next = withMovableOrder(config, ["when", "name", "size"]);

    expect(next).toEqual(["pick", "when", "name", "size", "act"]);
  });

  it("works on the movable columns only", () => {
    expect(
      movableOrder(config, ["act", "size", "pick", "name", "when"]),
    ).toEqual(["size", "name", "when"]);
  });

  it("moves a column one place and says nothing when it is already at that end", () => {
    const start = ["pick", "name", "size", "when", "act"] as Id[];

    expect(shiftedOrder(config, start, "size", -1)).toEqual([
      "pick",
      "size",
      "name",
      "when",
      "act",
    ]);
    expect(shiftedOrder(config, start, "name", -1)).toBeNull();
    expect(shiftedOrder(config, start, "when", 1)).toBeNull();
  });
});

describe("reading what a browser remembered", () => {
  it("starts from the table's own layout when nothing was saved or what was saved is not a layout", () => {
    const start = {
      order: config.columns.map((c) => c.id),
      sort: config.defaultSort,
    };

    expect(readLayout(config, null)).toEqual(start);
    expect(readLayout(config, "nonsense")).toEqual(start);
    expect(readLayout(config, { order: 7, sort: 3 })).toEqual(start);
  });

  it("keeps a saved order and sort", () => {
    const layout = readLayout(config, {
      order: ["pick", "when", "size", "name", "act"],
      sort: { id: "size", direction: "desc" },
    });

    expect(layout.order).toEqual(["pick", "when", "size", "name", "act"]);
    expect(layout.sort).toEqual({ id: "size", direction: "desc" });
  });

  it("drops columns that no longer exist, and puts new ones in their usual place", () => {
    const layout = readLayout(config, {
      order: ["pick", "gone", "when", "name", "act"],
    });

    expect(layout.order).toEqual(["pick", "when", "name", "size", "act"]);
  });

  it("never lets a column appear twice, or a fixed column leave its place", () => {
    const layout = readLayout(config, {
      order: ["act", "name", "name", "size", "pick", "when"],
    });

    expect(layout.order).toEqual(["pick", "name", "size", "when", "act"]);
  });

  it("forgets a sort on a column that cannot sort, or a direction nobody knows", () => {
    expect(
      readLayout(config, { sort: { id: "pick", direction: "asc" } }).sort,
    ).toEqual(config.defaultSort);
    expect(
      readLayout(config, { sort: { id: "size", direction: "sideways" } }).sort,
    ).toEqual(config.defaultSort);
  });

  it("forgets every sort for a table that cannot be sorted", () => {
    const fixedOrder = { ...config, sortable: false, defaultSort: null };

    expect(
      readLayout(fixedOrder, { sort: { id: "size", direction: "asc" } }).sort,
    ).toBeNull();
  });
});

describe("sorting rows", () => {
  type Row = { name: string | null; size: number | null; at: number };
  const rows: Row[] = [
    { name: "episode 10", size: 30, at: 3 },
    { name: "Episode 2", size: 5, at: 1 },
    { name: null, size: null, at: 2 },
    { name: "episode 1", size: 5, at: 4 },
  ];
  const values = {
    name: (row: Row) => row.name,
    size: (row: Row) => row.size,
    when: (row: Row) => row.at,
  };

  it("puts words in the order a person reads them, with numbers inside them counted as numbers", () => {
    const sorted = sortRows(rows, { id: "name", direction: "asc" }, values);

    expect(sorted.map((row) => row.name)).toEqual([
      "episode 1",
      "Episode 2",
      "episode 10",
      null,
    ]);
  });

  it("puts numbers in numeric order, not as text", () => {
    const sorted = sortRows(rows, { id: "size", direction: "desc" }, values);

    expect(sorted.map((row) => row.size)).toEqual([30, 5, 5, null]);
  });

  it("keeps rows with no value last whichever way it runs", () => {
    const up = sortRows(rows, { id: "name", direction: "asc" }, values);
    const down = sortRows(rows, { id: "name", direction: "desc" }, values);

    expect(up.at(-1)?.name).toBeNull();
    expect(down.at(-1)?.name).toBeNull();
  });

  it("keeps rows that tie in the order they came in, in both directions", () => {
    const up = sortRows(rows, { id: "size", direction: "asc" }, values);
    const down = sortRows(rows, { id: "size", direction: "desc" }, values);

    expect(up.filter((row) => row.size === 5).map((row) => row.at)).toEqual([
      1, 4,
    ]);
    expect(down.filter((row) => row.size === 5).map((row) => row.at)).toEqual([
      1, 4,
    ]);
  });

  it("leaves the rows alone for no sort, or a column with nothing to sort by", () => {
    expect(sortRows(rows, null, values)).toEqual(rows);
    expect(
      sortRows<Row, string>(rows, { id: "unknown", direction: "asc" }, values),
    ).toEqual(rows);
  });

  it("does not change the rows it was given", () => {
    const before = [...rows];

    sortRows(rows, { id: "when", direction: "desc" }, values);

    expect(rows).toEqual(before);
  });
});

describe("a status's place in a sort", () => {
  it("follows the order the meanings are listed in, so what is done and what is broken each sit together", () => {
    const ranks = (
      ["idle", "broken", "done", "doing", "todo", "attention"] as const
    )
      .map(meaningRank)
      .sort((a, b) => a - b);

    expect(ranks).toEqual([0, 1, 2, 3, 4, 5]);
    expect(meaningRank("done")).toBeLessThan(meaningRank("todo"));
    expect(meaningRank("broken")).toBeLessThan(meaningRank("idle"));
  });
});

describe("looking up a column", () => {
  it("finds a column by its id", () => {
    expect(columnOf(config, "size").label).toBe("Size");
  });

  it("names the table and the column when there is no such column", () => {
    expect(() => columnOf(config, "missing" as Id)).toThrow(
      "The table things has no column missing.",
    );
  });
});
