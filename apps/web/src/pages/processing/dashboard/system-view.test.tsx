import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { GRID_AREAS_SYSTEM, GRID_COLUMNS } from "./dashboard-layout";
import { SystemView } from "./system-view";

type HealthState = {
  checks: { tone: string }[];
  checking: boolean;
  overviewChecks: null;
};

let libraries: {
  isPending: boolean;
  isError: boolean;
  error?: unknown;
  data: unknown[];
};
let health: HealthState;
let overviewChecks: { passing: number; total: number } | undefined;
const hearFrames = vi.fn();

vi.mock("../../../lib/processing/libraries-queries", () => ({
  useProcessingLibrariesQuery: () => libraries,
}));
vi.mock("../../../lib/system/use-system-stats", () => ({
  useSystemStatsFrames: () => hearFrames(),
  useSystemOverviewQuery: () => ({
    data: overviewChecks ? { checks: overviewChecks } : undefined,
  }),
}));
vi.mock("./system/use-health-checks", () => ({
  useHealthChecks: () => health,
}));
vi.mock("./system/health-card-model", () => ({
  healthSummary: (checks: { tone: string }[]) => ({
    pass: checks.filter((check) => check.tone === "ok").length,
    total: checks.length,
    need: checks.filter((check) => check.tone === "bad").length,
  }),
}));
vi.mock("./system/this-weir-card", () => ({
  ThisWeirCard: ({
    checks,
    onShowHealth,
  }: {
    checks: { passing: number; total: number; need: number };
    onShowHealth: () => void;
  }) => (
    <button type="button" onClick={onShowHealth}>
      {`ring ${checks.passing}/${checks.total} need ${checks.need}`}
    </button>
  ),
}));
vi.mock("./system/this-computer-card", () => ({
  ThisComputerCard: () => <p>computer</p>,
}));
vi.mock("./system/processing-card", () => ({
  ProcessingCard: () => <p>processing</p>,
}));
vi.mock("./system/storage-card", () => ({
  StorageCard: () => <p>storage</p>,
}));
vi.mock("./system/health-card", () => ({
  HealthCard: ({ jump }: { jump: number }) => <p>{`health jump ${jump}`}</p>,
}));
vi.mock("./system/connections-slot", () => ({
  ConnectionsSlot: () => <p>connections</p>,
}));
vi.mock("./system/tasks-card", () => ({ TasksCard: () => <p>tasks</p> }));
vi.mock("./system/log-card", () => ({ LogCard: () => <p>log</p> }));
vi.mock("./system/backups-card", () => ({
  BackupsCard: () => <p>backups</p>,
}));

const beside = { sideBySide: true, band: "across" } as const;
const narrow = { sideBySide: false, band: "stacked" } as const;

beforeEach(() => {
  libraries = { isPending: false, isError: false, data: [] };
  health = { checks: [], checking: false, overviewChecks: null };
  overviewChecks = undefined;
  hearFrames.mockClear();
});

describe("the System view's cards", () => {
  it("shows every card of the view", () => {
    render(<SystemView layout={beside} />);

    for (const words of [
      "computer",
      "processing",
      "storage",
      "connections",
      "tasks",
      "log",
      "backups",
    ]) {
      expect(screen.getByText(words)).toBeInTheDocument();
    }
    expect(screen.getByText(/health jump 0/)).toBeInTheDocument();
  });

  it("follows the stream's readings", () => {
    render(<SystemView layout={beside} />);

    expect(hearFrames).toHaveBeenCalled();
  });

  it("says Weir hit a problem, with Reload, and shows no card when the workflows cannot be read", () => {
    vi.stubEnv("DEV", false);
    libraries = {
      isPending: false,
      isError: true,
      error: new Error("down"),
      data: [],
    };
    render(<SystemView layout={beside} />);

    expect(
      screen.getByRole("heading", { name: "Weir hit a problem" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Reload" })).toBeInTheDocument();
    expect(screen.queryByText("computer")).toBeNull();
    vi.unstubAllEnvs();
  });

  it("waits for the workflows before it shows anything", () => {
    libraries = { isPending: true, isError: false, data: [] };
    render(<SystemView layout={beside} />);

    expect(screen.queryByText("computer")).toBeNull();
  });
});

describe("the ring's checks", () => {
  it("count the checks Health lists, so the two say the same", () => {
    health = {
      checks: [{ tone: "ok" }, { tone: "ok" }, { tone: "bad" }],
      checking: false,
      overviewChecks: null,
    };
    render(<SystemView layout={beside} />);

    expect(screen.getByText("ring 2/3 need 1")).toBeInTheDocument();
  });

  it("stand on Weir's own count while a folder check has not answered", () => {
    health = { checks: [{ tone: "ok" }], checking: true, overviewChecks: null };
    overviewChecks = { passing: 8, total: 10 };
    render(<SystemView layout={beside} />);

    expect(screen.getByText("ring 8/10 need 0")).toBeInTheDocument();
  });

  it("send the person to Health when they press that checks need them", () => {
    render(<SystemView layout={beside} />);

    fireEvent.click(screen.getByRole("button", { name: /^ring/ }));

    expect(screen.getByText(/health jump 1/)).toBeInTheDocument();
  });
});

describe("the System view's grid", () => {
  it("shares Live's columns, and names its own areas, where the right column sits beside the page", () => {
    render(<SystemView layout={beside} />);

    const grid = screen.getByTestId("dashboard-system");
    expect(grid).toHaveClass("mm-sy-grid--beside");
    expect(grid.style.gridTemplateColumns).toBe(GRID_COLUMNS);
    expect(grid.style.gridTemplateAreas).toBe(GRID_AREAS_SYSTEM);
    expect(grid.style.gridTemplateRows).toMatch(/^\d+px \d+px \d+px$/);
  });

  it("stacks its cards where the page is narrow", () => {
    render(<SystemView layout={narrow} />);

    const grid = screen.getByTestId("dashboard-system");
    expect(grid).not.toHaveClass("mm-sy-grid--beside");
    expect(grid.style.gridTemplateColumns).toBe("");
  });
});
