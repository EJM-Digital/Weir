import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ConnectionEntry } from "../../../../lib/connections/connection-model";
import type { ProcessingLibrary } from "../../../../lib/processing/libraries-api";
import type { SystemDrive } from "../../../../lib/system/system-stats-types";
import type { Health } from "../use-health";
import type { HealthCheck } from "./health-checks";
import { useHealthChecks } from "./use-health-checks";

const NOW = Date.parse("2026-10-02T12:00:00Z");
const movies = { id: 2, name: "Movies" } as ProcessingLibrary;
const tv = { id: 3, name: "TV" } as ProcessingLibrary;

const recheckMovies = vi.fn(async () => undefined);
const health: Health = {
  workflows: [],
  managers: [],
  downloadClients: [],
  tools: null,
  foldersReadAt: null,
  recheckFolders: vi.fn(),
};
const testConnection = vi.fn(async () => undefined);
const refetch = () => vi.fn(async () => undefined);
const queries = {
  tools: { dataUpdatedAt: NOW, refetch: refetch() },
  settings: {
    data: undefined as Record<string, unknown> | undefined,
    dataUpdatedAt: NOW,
    refetch: refetch(),
  },
  backups: {
    data: undefined as { items: { created_at: string }[] } | undefined,
    refetch: refetch(),
  },
  readiness: {
    data: undefined as Record<string, unknown> | undefined,
    dataUpdatedAt: NOW,
    refetch: refetch(),
  },
  update: {
    data: undefined as Record<string, unknown> | undefined,
    refetch: refetch(),
  },
  overview: {
    data: undefined as
      { checks: { passing: number; total: number } } | undefined,
  },
};
let entries: ConnectionEntry[] = [];
let drives: SystemDrive[] = [];
const readStats = vi.fn();

const useHealth = vi.fn<(workflows: unknown) => Health>(() => health);
vi.mock("../use-health", () => ({
  useHealth: (workflows: unknown) => useHealth(workflows),
}));
vi.mock("../../../../lib/connections/use-connections", () => ({
  useConnections: () => ({ entries, lights: new Map() }),
}));
vi.mock("../use-connection-testing", () => ({
  useConnectionTesting: () => ({ test: testConnection }),
}));
vi.mock("../../../../lib/system/media-tools", () => ({
  useMediaToolsQuery: () => queries.tools,
}));
vi.mock("../../../../lib/settings/queries", () => ({
  useAppSettingsQuery: () => queries.settings,
  useConfigurationBackupsQuery: () => queries.backups,
  useUpdateStatusQuery: () => queries.update,
}));
vi.mock("../../../../lib/system/readiness-queries", () => ({
  useSystemReadinessQuery: () => queries.readiness,
}));
let looks: {
  foldersCheckedAt: number | null;
  readinessCheckedAt: number | null;
} = {
  foldersCheckedAt: null,
  readinessCheckedAt: null,
};
vi.mock("../../../../lib/system/use-server-looks", () => ({
  useServerLooks: () => looks,
}));
vi.mock("../../../../lib/system/use-system-stats", () => ({
  useSystemOverviewQuery: () => queries.overview,
}));
vi.mock("../../../../lib/system/system-stats-api", () => ({
  fetchSystemStats: async () => {
    readStats();
    return { drives };
  },
}));

function wrapper() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
  };
}

function drive(name: string, workflowIds: number[]): SystemDrive {
  return {
    name,
    path: `${name}\\`,
    total_bytes: 2_000_000_000_000,
    free_bytes: 1_000_000_000_000,
    weir_bytes: 0,
    keep_free_bytes: 0,
    full_in_days: null,
    read_bytes_per_sec: null,
    write_bytes_per_sec: null,
    busy_percent: null,
    workflows: workflowIds.map((id) => ({ id, name: `w${id}`, roles: [] })),
  };
}

function renderChecks() {
  return renderHook(() => useHealthChecks([movies, tv]), {
    wrapper: wrapper(),
  });
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(NOW);
  health.workflows = [
    {
      workflow: movies,
      verdict: { words: "In sync", meaning: "done", readiness: "ready" },
      why: null,
      chain: undefined,
      checkedAt: NOW,
      recheck: recheckMovies,
    },
  ];
  health.tools = null;
  looks = { foldersCheckedAt: null, readinessCheckedAt: null };
  entries = [];
  drives = [];
  queries.settings.data = undefined;
  queries.backups.data = undefined;
  queries.readiness.data = undefined;
  queries.update.data = undefined;
  queries.overview.data = undefined;
  queries.tools.dataUpdatedAt = NOW;
  queries.settings.dataUpdatedAt = NOW;
  queries.readiness.dataUpdatedAt = NOW;
  for (const mock of [
    readStats,
    recheckMovies,
    testConnection,
    queries.tools.refetch,
    queries.settings.refetch,
    queries.backups.refetch,
    queries.readiness.refetch,
    queries.update.refetch,
  ]) {
    mock.mockClear();
  }
});

afterEach(() => {
  vi.useRealTimers();
});

describe("useHealthChecks", () => {
  it("makes a check of each switched-on workflow", () => {
    const { result } = renderChecks();

    expect(result.current.checks.map((check) => check.id)).toEqual([
      "workflow:2",
    ]);
  });

  it("says it is checking while a folder check has not answered", () => {
    health.workflows = [
      {
        ...health.workflows[0],
        verdict: { words: "Checking…", meaning: "doing", readiness: null },
      },
    ];
    queries.overview.data = { checks: { passing: 9, total: 10 } };
    const { result } = renderChecks();

    expect(result.current.checking).toBe(true);
    expect(result.current.overviewChecks).toEqual({ passing: 9, total: 10 });
  });

  it("judges the backup from the schedule and the newest backup, made by hand or by itself", () => {
    queries.settings.data = {
      configuration_backup_enabled: true,
      configuration_backup_interval_hours: 24,
      configuration_backup_last_run_at: "2026-09-20T03:00:00Z",
    };
    queries.backups.data = { items: [{ created_at: "2026-10-02T03:00:00Z" }] };
    const { result } = renderChecks();

    const backup = result.current.checks.find(
      (check) => check.area === "backups",
    );
    expect(backup?.meaning).toBe("done");
  });

  it("reads Weir's workers and a waiting update from readiness and the update status", () => {
    queries.readiness.data = {
      worker_health: [
        { stopped_workers: 1, stale_workers: 0, detail: "Cleanup stopped." },
        { stopped_workers: 0, stale_workers: 0, detail: "Fine." },
      ],
    };
    queries.update.data = {
      status: "update_available",
      latest_version: "3.3.0",
    };
    const { result } = renderChecks();

    const weir = result.current.checks.filter((check) => check.area === "weir");
    expect(weir.map((check) => [check.id, check.meaning])).toEqual([
      ["weir:workers", "broken"],
      ["weir:update", "todo"],
    ]);
  });

  it("says Weir was checked when the server last looked, not when this page last read", () => {
    queries.readiness.data = { worker_health: [] };
    queries.readiness.dataUpdatedAt = NOW - 60_000;
    looks = { foldersCheckedAt: null, readinessCheckedAt: NOW - 4_000 };
    const { result } = renderChecks();

    const workers = result.current.checks.find(
      (check) => check.id === "weir:workers",
    );
    expect(workers?.checkedAt).toBe(NOW - 4_000);
    expect(result.current.readAt).toBe(NOW);
  });

  it("checks every drive any workflow uses, and asks for every workflow's folders", async () => {
    drives = [drive("D:", [2]), drive("E:", [3])];
    const { result } = renderChecks();

    await waitFor(() =>
      expect(
        result.current.checks.filter((check) => check.area === "storage"),
      ).toHaveLength(2),
    );
    expect(
      result.current.checks
        .filter((check) => check.area === "storage")
        .map((check) => check.title),
    ).toEqual(["D:", "E:"]);
    expect(useHealth).toHaveBeenLastCalledWith([movies, tv]);
  });

  it("looks at a workflow again by reading its folder chain again", async () => {
    const { result } = renderChecks();
    const target = result.current.checks[0];

    await act(async () => {
      await result.current.again(target);
    });

    expect(recheckMovies).toHaveBeenCalledTimes(1);
  });

  it("looks at a connection again by testing it", async () => {
    const entry = {
      key: "media_manager:1",
      enabled: true,
      state: "ok",
    } as ConnectionEntry;
    entries = [entry];
    const { result } = renderChecks();
    const target = result.current.checks.find(
      (check) => check.area === "connections",
    ) as HealthCheck;

    await act(async () => {
      await result.current.again(target);
    });

    expect(testConnection).toHaveBeenCalledWith(entry);
  });

  it("looks at the backups and at Weir again by reading them again", async () => {
    const { result } = renderChecks();
    const again = (area: HealthCheck["again"]["area"]) =>
      act(async () => {
        await result.current.again({
          again: { area, key: null },
          id: area,
        } as HealthCheck);
      });

    await again("backups");
    await again("weir");
    await again("tools");

    expect(queries.settings.refetch).toHaveBeenCalled();
    expect(queries.backups.refetch).toHaveBeenCalled();
    expect(queries.readiness.refetch).toHaveBeenCalled();
    expect(queries.update.refetch).toHaveBeenCalled();
    expect(queries.tools.refetch).toHaveBeenCalled();
  });

  it("marks a check as being looked at while it is, and clears it after", async () => {
    let finish: () => void = () => undefined;
    recheckMovies.mockImplementationOnce(
      () =>
        new Promise<undefined>((resolve) => {
          finish = () => resolve(undefined);
        }),
    );
    const { result } = renderChecks();
    const target = result.current.checks[0];

    let pending: Promise<void> = Promise.resolve();
    act(() => {
      pending = result.current.again(target);
    });
    expect(result.current.busy.has("workflow:2")).toBe(true);

    await act(async () => {
      finish();
      await pending;
    });
    expect(result.current.busy.size).toBe(0);
  });

  it.each([
    {
      name: "no backup has ever run",
      settings: { last_run_at: null },
      backups: undefined,
      words: "No backup yet",
    },
    {
      name: "the backup list has not loaded and the last run is unreadable",
      settings: { last_run_at: "not a time" },
      backups: undefined,
      words: "No backup yet",
    },
    {
      name: "the only backup has no readable time",
      settings: { last_run_at: null },
      backups: { items: [{ created_at: "not a time" }] },
      words: "No backup yet",
    },
    {
      name: "the last run is old and the newest backup is recent",
      settings: { last_run_at: "2026-09-01T00:00:00Z" },
      backups: { items: [{ created_at: "2026-10-02T09:00:00Z" }] },
      words: "On schedule",
    },
  ])("judges the backup when $name", ({ settings, backups, words }) => {
    queries.settings.data = {
      configuration_backup_enabled: true,
      configuration_backup_interval_hours: 24,
      configuration_backup_last_run_at: settings.last_run_at,
    };
    queries.backups.data = backups;
    const { result } = renderChecks();

    expect(
      result.current.checks.find((check) => check.area === "backups")?.words,
    ).toBe(words);
  });

  it("does not say when the settings were read before they have been", () => {
    queries.settings.data = {
      configuration_backup_enabled: false,
      configuration_backup_interval_hours: 24,
      configuration_backup_last_run_at: null,
    };
    queries.settings.dataUpdatedAt = 0;
    const { result } = renderChecks();

    expect(
      result.current.checks.find((check) => check.area === "backups")
        ?.checkedAt,
    ).toBeNull();
  });

  it("says every worker is running when readiness lists none, and names no update the server did not name", () => {
    queries.readiness.data = {};
    queries.readiness.dataUpdatedAt = 0;
    queries.update.data = { status: "update_available" };
    const { result } = renderChecks();

    const weir = result.current.checks.filter((check) => check.area === "weir");
    expect(weir.map((check) => [check.id, check.words])).toEqual([
      ["weir:workers", "All running"],
    ]);
    expect(weir[0].checkedAt).toBeNull();
  });

  it("does not say when the tools were read before they have been, nor when anything was read", () => {
    health.tools = [
      {
        key: "ffmpeg",
        name: "FFmpeg",
        version: "7.1",
        banner: "ffmpeg 7.1",
        meaning: "done",
      },
    ];
    queries.tools.dataUpdatedAt = 0;
    queries.settings.dataUpdatedAt = 0;
    queries.readiness.dataUpdatedAt = 0;
    const { result } = renderChecks();

    expect(result.current.checks[1].checkedAt).toBeNull();
    expect(result.current.readAt).toBeNull();
  });

  it("looks at the drives again by reading them again", async () => {
    drives = [drive("D:", [2])];
    const { result } = renderChecks();
    await waitFor(() => expect(readStats).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(
        result.current.checks.some((check) => check.area === "storage"),
      ).toBe(true),
    );
    const target = result.current.checks.find(
      (check) => check.area === "storage",
    ) as HealthCheck;

    await act(async () => {
      await result.current.again(target);
    });

    expect(readStats).toHaveBeenCalledTimes(2);
  });

  it.each([
    ["a workflow that is no longer listed", "workflows", "99"],
    ["a connection that is no longer listed", "connections", "gone"],
  ] as const)(
    "does nothing when asked to look again at %s",
    async (_name, area, key) => {
      const { result } = renderChecks();

      await act(async () => {
        await result.current.again({
          again: { area, key },
          id: "x",
        } as HealthCheck);
      });

      expect(recheckMovies).not.toHaveBeenCalled();
      expect(testConnection).not.toHaveBeenCalled();
      expect(result.current.busy.size).toBe(0);
    },
  );
});
