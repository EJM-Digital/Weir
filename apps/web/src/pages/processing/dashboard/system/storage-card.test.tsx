import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { StorageCard } from "./storage-card";
import { testStats } from "./test-stats";

type Query = { data?: unknown; isError?: boolean; error?: unknown };
let query: Query;

vi.mock("../../../../lib/system/use-system-stats", () => ({
  useSystemStatsQuery: () => query,
}));

function show(address = "/?view=system") {
  return render(
    <MemoryRouter initialEntries={[address]}>
      <StorageCard />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  query = { data: testStats };
});

describe("the Storage card", () => {
  it("shows the room left across the drives and links to where they are set", () => {
    show();

    const card = screen.getByTestId("system-storage");
    expect(within(card).getByText("400 GB free")).toBeInTheDocument();
    expect(
      within(card).getByRole("link", { name: "Storage: Storage" }),
    ).toHaveAttribute("href", "/setup/workflows");
  });

  it("shows a block for each drive: its room, when it fills, how busy it is and the room kept free", () => {
    show();

    const drive = screen.getByTestId("system-drive");
    expect(within(drive).getByText("D:")).toBeInTheDocument();
    expect(
      within(drive).getByText("400 GB free · full in ~12 days"),
    ).toBeInTheDocument();
    expect(
      within(drive).getByText("read 4.0 · write 12 MB/s · 18% busy"),
    ).toBeInTheDocument();
    expect(within(drive).getByText("keeps 20.00 GB free")).toBeInTheDocument();
  });

  it("marks a drive with less room than a workflow keeps free", () => {
    query = {
      data: {
        ...testStats,
        drives: [{ ...testStats.drives[0], free_bytes: 5 * 1024 ** 3 }],
      },
    };
    show();

    expect(screen.getByTestId("system-drive")).toHaveAttribute(
      "data-status",
      "attention",
    );
  });

  it("shows free space alone for a network share, which has no disk figures to read", () => {
    query = {
      data: {
        ...testStats,
        drives: [
          {
            ...testStats.drives[0],
            read_bytes_per_sec: null,
            write_bytes_per_sec: null,
            busy_percent: null,
            keep_free_bytes: 0,
          },
        ],
      },
    };
    show();

    const drive = screen.getByTestId("system-drive");
    expect(
      within(drive).getByText("400 GB free · full in ~12 days"),
    ).toBeInTheDocument();
    expect(within(drive).queryByText(/MB\/s/)).toBeNull();
  });

  it("titles a drive no workflow uses by its path alone", () => {
    query = {
      data: {
        ...testStats,
        drives: [{ ...testStats.drives[0], workflows: [] }],
      },
    };
    show();

    expect(screen.getByTestId("system-drive")).toHaveAttribute(
      "title",
      testStats.drives[0].path,
    );
  });

  it("lists every drive even when the address names a workflow that does not use it", () => {
    show("/?view=system&workflow=9");

    expect(screen.getByTestId("system-drive")).toBeInTheDocument();
    expect(screen.getByText("400 GB free")).toBeInTheDocument();
  });

  it("says so when no drive has been read", () => {
    query = { data: { ...testStats, drives: [] } };
    show();

    expect(screen.queryByTestId("system-drive")).toBeNull();
    expect(screen.getByText(/No drive has been read yet/)).toBeInTheDocument();
  });

  it("says so when the drives could not be read", () => {
    query = { isError: true, error: new Error("down") };
    show();

    expect(screen.queryByTestId("system-drive")).toBeNull();
    expect(screen.getByText(/drives/i, { selector: "p" })).toBeInTheDocument();
  });
});
