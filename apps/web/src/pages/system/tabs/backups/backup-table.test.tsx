import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ColumnsMenu } from "../../../../components/shared/columns-menu";
import type { ConfigurationBackupItem } from "../../../../lib/settings/types";
import { useTableColumns } from "../../../../lib/ui/use-table-columns";
import { BACKUP_COLUMNS } from "./backup-columns";
import { BackupTable } from "./backup-table";

vi.mock("../../../../lib/ui/mm-format-date", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("../../../../lib/ui/mm-format-date")
  >()),
  useAppDateFormatter: () => (iso: string) => `at ${iso.slice(0, 10)}`,
}));

const BACKUPS: ConfigurationBackupItem[] = [
  {
    id: 1,
    created_at: "2026-10-02T02:00:00Z",
    file_name: "b1.json",
    size_bytes: 300,
    made_by: "schedule",
    secrets_sealed: false,
  },
  {
    id: 2,
    created_at: "2026-10-03T02:00:00Z",
    file_name: "b2.json",
    size_bytes: 100,
    made_by: "schedule",
    secrets_sealed: false,
  },
  {
    id: 3,
    created_at: "2026-10-01T02:00:00Z",
    file_name: "b3.json",
    size_bytes: 200,
    made_by: "schedule",
    secrets_sealed: false,
  },
];

function Backups() {
  const columns = useTableColumns(BACKUP_COLUMNS);
  return (
    <>
      <ColumnsMenu table={columns} />
      <BackupTable
        items={BACKUPS}
        columns={columns}
        disabled={false}
        onDownload={vi.fn()}
        onRestore={vi.fn()}
      />
    </>
  );
}

function taken() {
  return screen
    .getAllByRole("row")
    .slice(1)
    .map((row) => row.querySelector("th")?.textContent);
}

beforeEach(() => localStorage.clear());

describe("the backups table", () => {
  it("lists the newest backup first", () => {
    render(<Backups />);

    expect(taken()).toEqual([
      "at 2026-10-03",
      "at 2026-10-02",
      "at 2026-10-01",
    ]);
  });

  it("sorts by size, largest first, and reverses on the next click", () => {
    render(<Backups />);

    fireEvent.click(screen.getByRole("button", { name: "Size" }));
    expect(taken()).toEqual([
      "at 2026-10-02",
      "at 2026-10-01",
      "at 2026-10-03",
    ]);

    fireEvent.click(screen.getByRole("button", { name: "Size" }));
    expect(taken()[0]).toBe("at 2026-10-03");
  });

  it("puts the newest first again with Reset columns", () => {
    render(<Backups />);
    fireEvent.click(screen.getByRole("button", { name: "Taken" }));
    expect(taken()[0]).toBe("at 2026-10-01");

    fireEvent.click(screen.getByRole("button", { name: "Columns" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Reset columns" }));

    expect(taken()[0]).toBe("at 2026-10-03");
  });

  it("keeps the actions column last and offers no sort on it", () => {
    render(<Backups />);

    expect(
      screen.queryByRole("button", { name: "Actions" }),
    ).not.toBeInTheDocument();
    const headings = screen.getAllByRole("columnheader");
    expect(headings[headings.length - 1]).toHaveTextContent("Actions");
  });
});
