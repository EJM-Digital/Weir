import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { SortableColumnHeader } from "../../components/shared/sortable-column-header";
import type { TableColumnsConfig } from "./table-columns";
import { useTableColumns } from "./use-table-columns";

type Id = "name" | "size" | "when";

const config: TableColumnsConfig<Id> = {
  tableId: "dragged",
  sortable: true,
  defaultSort: { id: "name", direction: "asc" },
  columns: [
    { id: "name", label: "Name" },
    { id: "size", label: "Size" },
    { id: "when", label: "When" },
  ],
};

function Demo({ table = config }: { table?: TableColumnsConfig<Id> }) {
  const columns = useTableColumns(table);
  return (
    <table {...columns.tableProps}>
      <thead>
        <tr>
          {columns.order.map((id) => (
            <SortableColumnHeader key={id} heading={columns.heading(id)} />
          ))}
        </tr>
      </thead>
    </table>
  );
}

const headings = () =>
  screen.getAllByRole("columnheader").map((header) => header.textContent);

beforeEach(() => {
  localStorage.clear();
  vi.useFakeTimers();
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(
    function (this: HTMLElement) {
      const index = this.parentElement
        ? Array.from(this.parentElement.children).indexOf(this)
        : 0;
      const offset = /translateX\((-?[\d.]+)px\)/.exec(this.style.transform);
      const left = index * 100 + Number(offset?.[1] ?? 0);
      return {
        left,
        right: left + 100,
        top: 0,
        bottom: 20,
        width: 100,
        height: 20,
        x: left,
        y: 0,
        toJSON: () => ({}),
      };
    },
  );
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("moving columns with the pointer", () => {
  it.each([
    ["the main button", 0, ["Size", "When", "Name"]],
    ["the right button", 2, ["Name", "Size", "When"]],
  ])("carries a column with %s", (_name, button, expected) => {
    render(<Demo />);

    fireEvent.pointerDown(screen.getByRole("columnheader", { name: "Name" }), {
      button,
      clientX: 50,
      clientY: 10,
    });
    fireEvent.pointerMove(window, { clientX: 58, clientY: 10 });
    fireEvent.pointerMove(window, { clientX: 300, clientY: 10 });
    fireEvent.pointerUp(window);
    act(() => {
      vi.advanceTimersByTime(1000);
    });

    expect(headings()).toEqual(expected);
  });
});

describe("asking a table that cannot sort to sort", () => {
  function Ask() {
    const columns = useTableColumns({ ...config, sortable: false });
    return (
      <button type="button" onClick={columns.heading("name").onSort}>
        Sort
      </button>
    );
  }

  it("changes nothing", () => {
    render(<Ask />);

    fireEvent.click(screen.getByRole("button", { name: "Sort" }));

    expect(localStorage.getItem("weir-table:dragged")).toBeNull();
  });
});

describe("keys that are not a move", () => {
  it("leave the order alone, whatever the key", () => {
    render(<Demo />);

    fireEvent.keyDown(screen.getByRole("button", { name: "Size" }), {
      key: "Enter",
      altKey: true,
    });

    expect(headings()).toEqual(["Name", "Size", "When"]);
  });
});
