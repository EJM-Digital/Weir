import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import type { ProcessingLibrary } from "../../../lib/processing/libraries-api";
import { fetchLibraryFolderChain } from "../../../lib/processing/library-folder-chain-api";
import { processingKeys } from "../../../lib/processing/query-keys";
import { useHealth } from "./use-health";

vi.mock(
  "../../../lib/processing/library-folder-chain-api",
  async (original) => ({
    ...(await original<
      typeof import("../../../lib/processing/library-folder-chain-api")
    >()),
    fetchLibraryFolderChain: vi.fn(),
  }),
);
let managerRows: { id: number }[] = [];
let clientRows: { id: number }[] = [];
vi.mock("../../../lib/media-managers/queries", () => ({
  useMediaManagerConnectionsQuery: () => ({ data: managerRows }),
}));
vi.mock("../../../lib/download-clients/queries", () => ({
  useDownloadClientConnectionsQuery: () => ({ data: clientRows }),
}));
let looks: {
  foldersCheckedAt: number | null;
  readinessCheckedAt: number | null;
} = {
  foldersCheckedAt: null,
  readinessCheckedAt: null,
};
vi.mock("../../../lib/system/use-server-looks", () => ({
  useServerLooks: () => looks,
}));
vi.mock("../../../lib/system/media-tools", () => ({
  useMediaToolsQuery: () => ({ data: undefined }),
}));

const movies = {
  id: 2,
  name: "Movies",
  enabled: true,
  media_type: "movie",
  watched_folder: "D:\\Watched",
  work_folder: "",
  output_folder: "D:\\Out",
} as ProcessingLibrary;

const answer = {
  library_id: 2,
  ready: true,
  local: { ready: true, lines: [] },
  managers: [],
  download_clients: [],
};

function renderHealth() {
  const client = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return { client, ...renderHook(() => useHealth([movies]), { wrapper }) };
}

describe("useHealth", () => {
  it("says a workflow was checked when the server last looked, not when this page last read", async () => {
    vi.mocked(fetchLibraryFolderChain).mockResolvedValue(answer);
    looks = { foldersCheckedAt: Date.now() + 60_000, readinessCheckedAt: null };
    const { result } = renderHealth();
    await waitFor(() =>
      expect(result.current.workflows[0].chain).toBeDefined(),
    );

    expect(result.current.workflows[0].checkedAt).toBe(looks.foldersCheckedAt);
    expect(result.current.foldersReadAt).toBeLessThan(
      looks.foldersCheckedAt ?? 0,
    );
    looks = { foldersCheckedAt: null, readinessCheckedAt: null };
  });

  it("has no time for a workflow whose folders have not answered, whatever the server last looked", () => {
    vi.mocked(fetchLibraryFolderChain).mockReturnValue(new Promise(() => {}));
    looks = { foldersCheckedAt: Date.now(), readinessCheckedAt: null };
    const { result } = renderHealth();

    expect(result.current.workflows[0].checkedAt).toBeNull();
    looks = { foldersCheckedAt: null, readinessCheckedAt: null };
  });

  it("does not check a workflow's folders on a timer: the server says on `folder_checks` when an answer changes", async () => {
    vi.mocked(fetchLibraryFolderChain).mockResolvedValue({
      library_id: 2,
      ready: true,
      local: { ready: true, lines: [] },
      managers: [],
      download_clients: [],
    });
    const client = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useHealth([movies]), { wrapper });
    await waitFor(() =>
      expect(result.current.workflows[0].chain).toBeDefined(),
    );

    const chains = client
      .getQueryCache()
      .findAll({ queryKey: processingKeys.libraryFolderChains });

    expect(chains).toHaveLength(1);
    expect(chains[0].observers[0].options.refetchInterval).toBeFalsy();
  });

  it("narrows the connections to the ones a workflow is linked to and the download clients its chain names", async () => {
    vi.mocked(fetchLibraryFolderChain).mockResolvedValue({
      ...answer,
      download_clients: [{ connection_id: 31, ready: true, lines: [] }],
    } as unknown as typeof answer);
    managerRows = [{ id: 11 }, { id: 12 }];
    clientRows = [{ id: 31 }, { id: 32 }];
    const client = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const linked = { ...movies, manager_connection_ids: [12] };
    const { result } = renderHook(() => useHealth([linked], 2), { wrapper });
    await waitFor(() =>
      expect(result.current.workflows[0].chain).toBeDefined(),
    );

    expect(result.current.managers).toEqual([{ id: 12 }]);
    expect(result.current.downloadClients).toEqual([{ id: 31 }]);
    managerRows = [];
    clientRows = [];
  });

  it("keeps no connection for a workflow whose chain has not answered", () => {
    vi.mocked(fetchLibraryFolderChain).mockReturnValue(new Promise(() => {}));
    managerRows = [{ id: 11 }];
    clientRows = [{ id: 31 }];
    const client = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useHealth([movies], 2), { wrapper });

    expect(result.current.managers).toEqual([]);
    expect(result.current.downloadClients).toEqual([]);
    managerRows = [];
    clientRows = [];
  });

  it("reads one workflow's chain again, and every chain again, when asked", async () => {
    vi.mocked(fetchLibraryFolderChain).mockResolvedValue(answer);
    const { result } = renderHealth();
    await waitFor(() =>
      expect(result.current.workflows[0].chain).toBeDefined(),
    );
    const asked = vi.mocked(fetchLibraryFolderChain).mock.calls.length;

    await result.current.workflows[0].recheck();
    expect(vi.mocked(fetchLibraryFolderChain).mock.calls.length).toBe(
      asked + 1,
    );

    await result.current.recheckFolders();
    expect(vi.mocked(fetchLibraryFolderChain).mock.calls.length).toBe(
      asked + 2,
    );
  });
});
