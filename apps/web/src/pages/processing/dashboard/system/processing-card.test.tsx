import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { NETWORK_UNREACHABLE_MESSAGE } from "../../../../lib/api/api-error-text";
import { ApiHttpError } from "../../../../lib/api/client";
import { ProcessingCard } from "./processing-card";
import { testStats } from "./test-stats";

type Query = { data?: unknown; isError?: boolean; error?: unknown };
let query: Query;
let recent: { done: number; savedBytes: number } | undefined;

vi.mock("../../../../lib/system/use-system-stats", () => ({
  useSystemStatsQuery: () => query,
}));
vi.mock("./use-recent-work", () => ({ useRecentWork: () => recent }));
vi.mock("../../../../components/charts/live-trace", () => ({
  LiveTrace: ({
    series,
    label,
  }: {
    series: { key: string }[];
    label: string;
  }) => (
    <i data-testid="trace">{`${label} | ${series.map((line) => line.key).join("+")}`}</i>
  ),
}));

beforeEach(() => {
  query = { data: testStats };
  recent = { done: 14, savedBytes: 3 * 1024 ** 3 };
});

describe("the Processing card", () => {
  it("shows the disk work, with what is read and what the last ten minutes saved beside it", () => {
    render(<ProcessingCard />);

    expect(screen.getByText("Disk work")).toBeInTheDocument();
    expect(screen.getByText("6.0")).toBeInTheDocument();
    expect(screen.getByText("reading 20 · 3.00 GB saved")).toBeInTheDocument();
  });

  it("shows the speed, how many passes run of the slots, and what the last ten minutes finished", () => {
    render(<ProcessingCard />);

    expect(screen.getByText("Speed")).toBeInTheDocument();
    expect(screen.getByText("148")).toBeInTheDocument();
    expect(screen.getByText("2 of 4 running · 14 done")).toBeInTheDocument();
  });

  it("draws reads and writes as two lines on one trace, and the speed as one", () => {
    render(<ProcessingCard />);

    expect(
      screen.getAllByTestId("trace").map((trace) => trace.textContent),
    ).toEqual([
      "Disk work, last 10 minutes | read+write",
      "Speed, last 10 minutes | speed",
    ]);
  });

  it("shows only the running passes while the finished work is still being read", () => {
    recent = undefined;
    render(<ProcessingCard />);

    expect(screen.getByText("2 of 4 running")).toBeInTheDocument();
    expect(screen.queryByText(/done/)).toBeNull();
  });

  it("says it is reading while the first reading is on its way", () => {
    query = {};
    render(<ProcessingCard />);

    expect(screen.getByText("Reading Weir's work…")).toBeInTheDocument();
  });

  it("says Weir couldn't load its work, not that it is still reading, when the reading failed", () => {
    query = { isError: true, error: new Error("down") };
    render(<ProcessingCard />);

    expect(
      screen.getByText(
        "Weir couldn't load Weir's work. Reload the page to try again.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText("Reading Weir's work…")).toBeNull();
  });

  it("says Weir cannot be reached when the failure was the server not answering", () => {
    query = {
      isError: true,
      error: new ApiHttpError(
        "/api/v1/system/stats",
        0,
        "x",
        undefined,
        false,
        true,
      ),
    };
    render(<ProcessingCard />);

    expect(screen.getByText(NETWORK_UNREACHABLE_MESSAGE)).toBeInTheDocument();
  });

  it("keeps showing the last reading when a later one fails", () => {
    query = { data: testStats, isError: true, error: new Error("down") };
    render(<ProcessingCard />);

    expect(screen.getByText("Disk work")).toBeInTheDocument();
    expect(screen.queryByText(/couldn't load/)).toBeNull();
  });
});
