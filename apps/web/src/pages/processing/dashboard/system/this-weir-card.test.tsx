import { fireEvent, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { StatusMeaning } from "../../../../lib/ui/status-meaning";
import { testOverview, testStats } from "./test-stats";
import { ThisWeirCard } from "./this-weir-card";

type Query = { data?: unknown; isError?: boolean; error?: unknown };
let overview: Query;
let stats: Query;
let backups: Query;
let filesAtOnce: Query;
const showHealth = vi.fn();

vi.mock("../../../../lib/system/use-system-stats", () => ({
  useSystemOverviewQuery: () => overview,
  useSystemStatsQuery: () => stats,
}));
vi.mock("../../../../lib/settings/queries", () => ({
  useConfigurationBackupsQuery: () => backups,
}));
vi.mock("../../../../lib/processing/queries", () => ({
  useProcessingFilesAtOnceQuery: () => filesAtOnce,
}));

const checks = {
  passing: 9,
  total: 10,
  need: 1,
  meaning: "attention" as StatusMeaning,
};

function show(shown = checks) {
  return render(<ThisWeirCard checks={shown} onShowHealth={showHealth} />);
}

beforeEach(() => {
  overview = { data: testOverview };
  stats = { data: testStats };
  backups = { data: { items: [] } };
  filesAtOnce = { data: { waiting: 3 } };
  showHealth.mockClear();
});

describe("the This Weir card", () => {
  it("says how many checks pass, and how many need a person", () => {
    show();

    const card = screen.getByRole("region", { name: "This Weir" });
    expect(within(card).getByText("9")).toBeInTheDocument();
    expect(within(card).getByText(/of 10/)).toHaveTextContent(
      "of 10 checks pass",
    );
    expect(
      within(card).getByRole("button", { name: "1 need you →" }),
    ).toBeInTheDocument();
  });

  it("takes the person to Health from the pill", () => {
    show();

    fireEvent.click(screen.getByRole("button", { name: "1 need you →" }));

    expect(showHealth).toHaveBeenCalledTimes(1);
  });

  it("says all is good when nothing needs a person", () => {
    show({ passing: 10, total: 10, need: 0, meaning: "done" });

    expect(screen.getByText("All good")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /need you/ })).toBeNull();
  });

  it("colours the ring by the worst thing that needs a person", () => {
    const { container, rerender } = show();
    expect(container.querySelector(".mm-sy-ring")).toHaveAttribute(
      "data-status",
      "attention",
    );

    rerender(
      <ThisWeirCard
        checks={{ ...checks, meaning: "broken" }}
        onShowHealth={showHealth}
      />,
    );
    expect(container.querySelector(".mm-sy-ring")).toHaveAttribute(
      "data-status",
      "broken",
    );

    rerender(
      <ThisWeirCard
        checks={{ passing: 10, total: 10, need: 0, meaning: "done" }}
        onShowHealth={showHealth}
      />,
    );
    expect(container.querySelector(".mm-sy-ring")).toHaveAttribute(
      "data-status",
      "done",
    );
  });

  it("fills the ring by the share of checks that pass", () => {
    const { container } = show();

    const value =
      container.querySelector<SVGCircleElement>(".mm-sy-ring__value")!;
    const length = 2 * Math.PI * 32;
    expect(Number.parseFloat(value.style.strokeDashoffset)).toBeCloseTo(
      length * 0.1,
      1,
    );
  });

  it("shows a tile for each fact about Weir", () => {
    show();

    const facts = screen.getByRole("group", { name: "Facts about Weir" });
    for (const label of [
      "Version",
      "Uptime",
      "Files at once",
      "Jobs today",
      "Usage",
      "Waiting",
      "Response",
      "Last backup",
      "Runs as",
      "Address",
      "Data",
      "Browsers",
    ]) {
      expect(within(facts).getByText(label)).toBeInTheDocument();
    }
    expect(within(facts).getByText("3.2.16")).toBeInTheDocument();
    expect(within(facts).getByText("2 / 4")).toBeInTheDocument();
    expect(within(facts).getByText("1,200 run")).toBeInTheDocument();
  });

  it("shows the newest backup's size", () => {
    backups = {
      data: {
        items: [
          { created_at: "2026-10-01T03:00:00Z", size_bytes: 1024 },
          { created_at: "2026-10-02T09:00:00Z", size_bytes: 2048 },
        ],
      },
    };
    show();

    expect(screen.getByText("2.0 KB")).toBeInTheDocument();
  });

  it("says it is reading while the first reading is on its way", () => {
    overview = {};
    show();

    expect(
      screen.getByText("Reading how Weir is running…"),
    ).toBeInTheDocument();
  });

  it("says so when Weir's own reading could not be taken", () => {
    overview = { isError: true, error: new Error("down") };
    show();

    expect(screen.queryByText("Reading how Weir is running…")).toBeNull();
    expect(
      screen.queryByRole("group", { name: "Facts about Weir" }),
    ).toBeNull();
  });
});

describe("the tiles before the other readings arrive", () => {
  it("shows a dash for work, use of the computer, waiting files and backups that have not been read", () => {
    stats = {};
    backups = {};
    filesAtOnce = {};
    show();

    const tiles = screen.getByRole("group", { name: "Facts about Weir" });
    expect(tiles.querySelectorAll('[title^="Files at once: –"]').length).toBe(
      1,
    );
    expect(tiles.querySelectorAll('[title^="Waiting: –"]').length).toBe(1);
  });
});
