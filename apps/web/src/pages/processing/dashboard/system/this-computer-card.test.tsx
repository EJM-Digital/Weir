import { render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { testStats } from "./test-stats";
import { ThisComputerCard } from "./this-computer-card";

type Query = { data?: unknown; isError?: boolean; error?: unknown };
let query: Query;

vi.mock("../../../../lib/system/use-system-stats", () => ({
  useSystemStatsQuery: () => query,
}));
vi.mock("../../../../components/charts/live-trace", () => ({
  LiveTrace: ({
    series,
    top,
    floorTop,
    label,
  }: {
    series: { samples: unknown[] }[];
    top: number | null;
    floorTop?: number;
    label: string;
  }) => (
    <i data-testid="trace">{`${label} | lines ${series.length} | points ${series[0].samples.length} | top ${String(top)} | floor ${floorTop}`}</i>
  ),
}));

beforeEach(() => {
  query = { data: testStats };
});

describe("the This computer card", () => {
  it("says what it shows and over what time", () => {
    render(<ThisComputerCard />);

    const card = screen.getByRole("region", { name: "This computer" });
    expect(within(card).getByText("last 10 minutes")).toBeInTheDocument();
  });

  it("shows CPU, memory and disk with their figures and notes", () => {
    render(<ThisComputerCard />);

    expect(screen.getByText("CPU")).toBeInTheDocument();
    expect(screen.getByText("37")).toBeInTheDocument();
    expect(
      screen.getByText("16 cores · Weir 3% · ffmpeg 41%"),
    ).toBeInTheDocument();
    expect(screen.getByText("Memory")).toBeInTheDocument();
    expect(screen.getByText("11.0")).toBeInTheDocument();
    expect(screen.getByText("of 32.0 GB · Weir 255 MB")).toBeInTheDocument();
    expect(screen.getByText("Disk")).toBeInTheDocument();
    expect(screen.getByText("16")).toBeInTheDocument();
    expect(
      screen.getByText("read 4.0 MB/s · write 12 MB/s · 18% busy"),
    ).toBeInTheDocument();
  });

  it("draws a trace for each, a percentage on a scale to 100 and the disk on its own", () => {
    render(<ThisComputerCard />);

    const traces = screen
      .getAllByTestId("trace")
      .map((trace) => trace.textContent);
    expect(traces).toEqual([
      "CPU, last 10 minutes | lines 1 | points 2 | top 100 | floor 0",
      "Memory, last 10 minutes | lines 1 | points 2 | top 100 | floor 0",
      "Disk, last 10 minutes | lines 1 | points 2 | top null | floor 1048576",
    ]);
  });

  it("tags the machine, with a reboot waiting", () => {
    render(<ThisComputerCard />);

    expect(screen.getByText("Windows 11 Pro")).toBeInTheDocument();
    expect(screen.getByText("up 2 days")).toBeInTheDocument();
    expect(screen.getByText("reboot pending")).toBeInTheDocument();
  });

  it("shows a dash for a disk it cannot read", () => {
    query = {
      data: {
        ...testStats,
        now: {
          ...testStats.now,
          disk_read_bytes_per_sec: null,
          disk_write_bytes_per_sec: null,
        },
      },
    };
    render(<ThisComputerCard />);

    expect(screen.getByText("–")).toBeInTheDocument();
    expect(screen.getByText("not available")).toBeInTheDocument();
  });

  it("shows dashes, not zeros, in the first reading, which has no rates yet", () => {
    query = {
      data: {
        ...testStats,
        now: {
          ...testStats.now,
          cpu_percent: null,
          weir_cpu_percent: null,
          tools_cpu_percent: null,
          disk_read_bytes_per_sec: null,
          disk_write_bytes_per_sec: null,
          disk_busy_percent: null,
        },
      },
    };
    render(<ThisComputerCard />);

    expect(screen.getAllByText("–")).toHaveLength(2);
    expect(screen.getByText("16 cores")).toBeInTheDocument();
  });

  it("says it is reading while the first reading is on its way", () => {
    query = {};
    render(<ThisComputerCard />);

    expect(screen.getByText("Reading this computer…")).toBeInTheDocument();
  });

  it("says so when the reading could not be taken", () => {
    query = { isError: true, error: new Error("down") };
    render(<ThisComputerCard />);

    expect(screen.queryByText("Reading this computer…")).toBeNull();
    expect(
      screen.getByText(/this computer/i, { selector: "p" }),
    ).toBeInTheDocument();
  });
});
